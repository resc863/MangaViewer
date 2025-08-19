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
    /// 만화 이미지 로드 및 페이지(1~2장 묶음) 계산/내비게이션 관리.
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

        /// <summary>폴더 내 지원 이미지 로드 (지연 추가)</summary>
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

            // 나머지 배치 추가 (UI 부하 감소)
            _ = Task.Run(async () =>
            {
                const int batchSize = 64; // 한번에 UI 추가할 최대 항목
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
            // Cover 분리: 총 페이지 = 1(표지) + floor((N-1+1)/2) = 1 + N/2 (정수 나눗셈)
            int pageCount = IsCoverSeparate ? (1 + _pages.Count / 2) : ((_pages.Count + 1) / 2);
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
    }
}