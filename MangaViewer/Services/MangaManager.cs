using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using MangaViewer.ViewModels;
using Microsoft.UI.Dispatching;

namespace MangaViewer.Services
{
    /// <summary>
    /// 만화 이미지 로딩 및 페이지(1~2장 단위) 구성/내비게이션 로직 관리.
    /// </summary>
    public class MangaManager
    {
        public event Action? MangaLoaded;
        public event Action? PageChanged;

        private static readonly HashSet<string> s_imageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp" };
        private static readonly Regex s_digitsRegex = new(@"\d+", RegexOptions.Compiled);

        private CancellationTokenSource? _loadCts;

        private readonly ObservableCollection<MangaPageViewModel> _pages = new();
        public ReadOnlyObservableCollection<MangaPageViewModel> Pages { get; }

        public int CurrentPageIndex { get; private set; }
        public bool IsRightToLeft { get; private set; }
        public bool IsCoverSeparate { get; private set; } = true; // true: 표지 단독

        public int TotalImages => _pages.Count;
        public int TotalPages => GetMaxPageIndex() + 1;

        public MangaManager() => Pages = new ReadOnlyObservableCollection<MangaPageViewModel>(_pages);

        /// <summary>로컬 폴더 전체 이미지 로드 (초기 추가)</summary>
        public async Task LoadFolderAsync(StorageFolder folder)
        {
            _loadCts?.Cancel();
            _loadCts = new CancellationTokenSource();
            var token = _loadCts.Token;

            _pages.Clear();
            CurrentPageIndex = 0;
            var dispatcher = DispatcherQueue.GetForCurrentThread();

            // 파일 수집 + 자연 정렬 (백그라운드)
            List<(string Path, string SortKey)> imageFiles = await Task.Run(async () =>
            {
                var files = await folder.GetFilesAsync();
                return files
                    .Where(f => s_imageExtensions.Contains(f.FileType))
                    .Select(f => (f.Path, SortKey: ToNaturalSortKey(System.IO.Path.GetFileNameWithoutExtension(f.Name))))
                    .OrderBy(x => x.SortKey, StringComparer.Ordinal)
                    .ToList();
            }, token);

            if (token.IsCancellationRequested) return;

            if (imageFiles.Count == 0)
            {
                MangaLoaded?.Invoke();
                PageChanged?.Invoke();
                return;
            }

            // 첫 이미지 즉시 추가 (표지)
            _pages.Add(new MangaPageViewModel { FilePath = imageFiles[0].Path });
            MangaLoaded?.Invoke();
            PageChanged?.Invoke();

            // 나머지 비동기 삽입 (UI 부하 완화)
            _ = Task.Run(async () =>
            {
                const int batchSize = 64;
                var batch = new List<MangaPageViewModel>(batchSize);
                for (int i = 1; i < imageFiles.Count; i++)
                {
                    if (_loadCts!.IsCancellationRequested) return;
                    batch.Add(new MangaPageViewModel { FilePath = imageFiles[i].Path });

                    bool flush = batch.Count >= batchSize || i == imageFiles.Count - 1;
                    if (flush)
                    {
                        var toAdd = batch.ToArray();
                        batch.Clear();
                        dispatcher.TryEnqueue(() =>
                        {
                            if (_loadCts.IsCancellationRequested) return;
                            foreach (var vm in toAdd) _pages.Add(vm);
                        });
                        try { await Task.Delay(8, _loadCts.Token); } catch { return; }
                    }
                }
            }, token);
        }

        public void Clear()
        {
            _pages.Clear();
            CurrentPageIndex = 0;
            MangaLoaded?.Invoke();
            PageChanged?.Invoke();
        }
        private int _expectedTotal = 0;
        public void SetExpectedTotal(int total)
        {
            if (total <= 0) return;
            _expectedTotal = total;
            if (_pages.Count < total)
            {
                for (int i = _pages.Count; i < total; i++)
                    _pages.Add(new MangaPageViewModel()); // placeholder (FilePath null)
                MangaLoaded?.Invoke();
                PageChanged?.Invoke();
            }
        }

        /// <summary>
        /// 스트리밍/다운로드된 파일 경로 추가. mem: 키 기반 순서 고정, 중복/재다운로드 방지.
        /// </summary>
        public void AddDownloadedFiles(IEnumerable<string> filePaths)
        {
            var incoming = filePaths?.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new();
            if (incoming.Count == 0) return;

            // 사전 정렬: 이름에 포함된 숫자 (mem:gid:####.ext 의 ####) 기준
            foreach (var path in incoming.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                bool isMem = path.StartsWith("mem:", StringComparison.OrdinalIgnoreCase);
                string name = System.IO.Path.GetFileNameWithoutExtension(path) ?? string.Empty;
                // mem:gid:####.ext 형태 -> 마지막 콜론 뒤 4자리 숫자 추출
                int index = -1;
                if (isMem)
                {
                    int lastColon = path.LastIndexOf(':');
                    if (lastColon >= 0 && lastColon + 5 <= path.Length)
                    {
                        var numSpan = path.AsSpan(lastColon + 1, Math.Min(4, path.Length - (lastColon + 1)));
                        if (int.TryParse(numSpan, out int parsed)) index = parsed - 1;
                    }
                }
                else
                {
                    index = ExtractNumericIndex(name) - 1; // zero-based
                }

                if (index < 0) continue;

                // 필요 시 placeholder 확장
                if (index >= _pages.Count)
                {
                    SetExpectedTotal(index + 1);
                }

                var vm = _pages[index];
                if (vm.FilePath == null)
                {
                    vm.FilePath = path; // 최초 채움
                }
                else
                {
                    // 이미 슬롯이 채워져 있으면 규칙:
                    // 1) 둘 다 mem: 이면 이미 다운로드된 것으로 간주 -> skip (중복 방지)
                    if (isMem && vm.FilePath.StartsWith("mem:", StringComparison.OrdinalIgnoreCase)) continue;
                    // 2) 기존이 mem: 이고 새로운 것이 로컬 파일이면 교체 (품질 향상 케이스 가정)
                    if (!isMem && vm.FilePath.StartsWith("mem:", StringComparison.OrdinalIgnoreCase)) { vm.FilePath = path; }
                    // 3) 그 외 (서로 다른 실제 파일 충돌) -> 무시 (순서 보존)
                }
            }
            PageChanged?.Invoke();
        }

        private static int ExtractNumericIndex(string name)
        {
            if (string.IsNullOrEmpty(name)) return -1;
            var m = s_digitsRegex.Match(name);
            if (!m.Success) return -1;
            if (int.TryParse(m.Value, out int v)) return v;
            return -1;
        }

        public void GoToPreviousPage()
        {
            if (CurrentPageIndex <= 0) return;
            CurrentPageIndex--;
            PageChanged?.Invoke();
        }
        public void GoToNextPage()
        {
            if (CurrentPageIndex >= GetMaxPageIndex()) return;
            CurrentPageIndex++;
            PageChanged?.Invoke();
        }
        public void GoToPage(int pageIndex)
        {
            CurrentPageIndex = Math.Clamp(pageIndex, 0, GetMaxPageIndex());
            PageChanged?.Invoke();
        }
        public void SetCurrentPageFromImageIndex(int imageIndex)
        {
            if (imageIndex < 0 || imageIndex >= _pages.Count) return;
            CurrentPageIndex = GetPageIndexFromImageIndex(imageIndex);
            PageChanged?.Invoke();
        }
        public void ToggleDirection()
        {
            IsRightToLeft = !IsRightToLeft;
            PageChanged?.Invoke();
        }
        public void ToggleCover()
        {
            if (_pages.Count == 0) return;
            int primaryImageIndex = GetPrimaryImageIndexForPage(CurrentPageIndex);
            IsCoverSeparate = !IsCoverSeparate;
            if (primaryImageIndex >= 0)
                CurrentPageIndex = GetPageIndexFromImageIndex(primaryImageIndex);
            PageChanged?.Invoke();
        }

        public List<string> GetImagePathsForCurrentPage() => GetImagePathsForPage(CurrentPageIndex);

        private int GetMaxPageIndex()
        {
            if (_pages.Count == 0) return 0;
            // Cover 분리: 전체 페이지 = 1(표지) + floor((N-1)/2 + 1) = 1 + (N-1)/2
            int pageCount = IsCoverSeparate ? (1 + (_pages.Count - 1 + 1) / 2) : ((_pages.Count + 1) / 2);
            return pageCount - 1;
        }

        public List<string> GetImagePathsForPage(int pageIndex)
        {
            var paths = new List<string>(2);
            if (_pages.Count == 0 || pageIndex < 0 || pageIndex > GetMaxPageIndex()) return paths;

            if (IsCoverSeparate)
            {
                if (pageIndex == 0)
                {
                    var fp0 = _pages[0].FilePath; if (fp0 != null) paths.Add(fp0);
                }
                else
                {
                    int baseIdx = 1 + (pageIndex - 1) * 2;
                    if (baseIdx < _pages.Count) { var fp = _pages[baseIdx].FilePath; if (fp != null) paths.Add(fp); }
                    int second = baseIdx + 1;
                    if (second < _pages.Count) { var fp2 = _pages[second].FilePath; if (fp2 != null) paths.Add(fp2); }
                }
            }
            else
            {
                int baseIdx = pageIndex * 2;
                if (baseIdx < _pages.Count) { var fp = _pages[baseIdx].FilePath; if (fp != null) paths.Add(fp); }
                int second = baseIdx + 1;
                if (second < _pages.Count) { var fp2 = _pages[second].FilePath; if (fp2 != null) paths.Add(fp2); }
            }
            return paths;
        }

        public int GetPrimaryImageIndexForPage(int pageIndex)
        {
            if (_pages.Count == 0 || pageIndex < 0 || pageIndex > GetMaxPageIndex()) return -1;
            return IsCoverSeparate ? (pageIndex == 0 ? 0 : 1 + (pageIndex - 1) * 2) : pageIndex * 2;
        }
        private int GetPageIndexFromImageIndex(int imageIndex)
        {
            if (imageIndex < 0 || imageIndex >= _pages.Count) return 0;
            return IsCoverSeparate ? (imageIndex == 0 ? 0 : 1 + (imageIndex - 1) / 2) : imageIndex / 2;
        }

        private static string ToNaturalSortKey(string name) =>
            s_digitsRegex.Replace(name, m => m.Value.PadLeft(10, '0'));
        public void CreatePlaceholders(int count)
        {
            if (count <= 0) return;
            if (_pages.Count > 0) return; // only if empty
            for (int i = 0; i < count; i++)
                _pages.Add(new MangaPageViewModel());
            MangaLoaded?.Invoke();
            PageChanged?.Invoke();
        }
    }
}