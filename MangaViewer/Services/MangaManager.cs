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
    /// 만화 이미지 로드 및 페이지(1~2장 표시) 계산/탐색 관리.
    /// 지정 폴더의 '최상위 파일'만 검사하며 하위 폴더는 재귀적으로 탐색하지 않는다.
    /// 허용 확장자: .jpg, .jpeg, .png, .bmp, .webp, .avif, .gif (대소문자 무시)
    /// </summary>
    public class MangaManager
    {
        public event Action? MangaLoaded;
        public event Action? PageChanged;

        // Allow-list (case-insensitive). Sub‑folders are ignored because we only call folder.GetFilesAsync().
        private static readonly HashSet<string> s_imageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".avif", ".gif" };
        private static readonly Regex s_digitsRegex = new(@"\d+", RegexOptions.Compiled);

        private CancellationTokenSource? _loadCts;

        private readonly ObservableCollection<MangaPageViewModel> _pages = new();
        public ReadOnlyObservableCollection<MangaPageViewModel> Pages { get; }

        public int CurrentPageIndex { get; private set; }
        public bool IsRightToLeft { get; private set; }
        public bool IsCoverSeparate { get; private set; } = true; // true: 표지 분리

        public int TotalImages => _pages.Count;
        public int TotalPages => GetMaxPageIndex() + 1;

        public MangaManager() => Pages = new ReadOnlyObservableCollection<MangaPageViewModel>(_pages);

        /// <summary>선택 폴더 내 허용된 이미지 전체 로드 (초기 추가)</summary>
        public async Task LoadFolderAsync(StorageFolder folder)
        {
            _loadCts?.Cancel();
            _loadCts = new CancellationTokenSource();
            var token = _loadCts.Token;

            _pages.Clear();
            CurrentPageIndex = 0;
            var dispatcher = DispatcherQueue.GetForCurrentThread();

            // 파일 수집 + 자연 정렬 (비동기)
            List<(string Path, string SortKey)> imageFiles = await Task.Run(async () =>
            {
                var files = await folder.GetFilesAsync(); // top-level only, no subfolders
                return files
                    .Where(f => !string.IsNullOrEmpty(f.FileType) && s_imageExtensions.Contains(f.FileType))
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

            // 첫 이미지 바로 추가 (표지)
            _pages.Add(new MangaPageViewModel { FilePath = imageFiles[0].Path });
            MangaLoaded?.Invoke();
            PageChanged?.Invoke();

            // 나머지 배치 추가 (UI 끊김 방지)
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
        /// 스트리밍/다운로드된 파일 추가. mem: 키 기반 이미지 포함, 중복/역다운로드 처리.
        /// </summary>
        public void AddDownloadedFiles(IEnumerable<string> filePaths)
        {
            var incoming = filePaths?.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new();
            if (incoming.Count == 0) return;

            // 위치 추출: 이름에 포함된 숫자(mem:gid:####.ext 는 ####) 기반
            foreach (var path in incoming.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                bool isMem = path.StartsWith("mem:", StringComparison.OrdinalIgnoreCase);
                string name = System.IO.Path.GetFileNameWithoutExtension(path) ?? string.Empty;
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

                if (index >= _pages.Count)
                {
                    SetExpectedTotal(index + 1);
                }

                var vm = _pages[index];
                if (vm.FilePath == null)
                {
                    vm.FilePath = path; // 채움
                }
                else
                {
                    if (isMem && vm.FilePath.StartsWith("mem:", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!isMem && vm.FilePath.StartsWith("mem:", StringComparison.OrdinalIgnoreCase)) { vm.FilePath = path; }
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