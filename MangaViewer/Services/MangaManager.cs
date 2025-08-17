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
    /// 폴더 내 이미지(페이지) 목록 로드 및 페이지 계산/상태 관리.
    /// Cover 단독 여부, RTL 여부에 따라 페이지 인덱스 <-> 이미지 인덱스 매핑 제공.
    /// </summary>
    public class MangaManager
    {
        public event Action? MangaLoaded;   // 최초 목록 로드(첫 항목) 시 신호
        public event Action? PageChanged;   // 현재 페이지/표시 상태 변화 시 신호

        private static readonly HashSet<string> s_imageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp" };
        private static readonly Regex s_digitsRegex = new(@"\d+", RegexOptions.Compiled);

        private CancellationTokenSource? _loadCts;

        private readonly ObservableCollection<MangaPageViewModel> _pages = new();
        public ReadOnlyObservableCollection<MangaPageViewModel> Pages { get; }

        public int CurrentPageIndex { get; private set; }
        public bool IsRightToLeft { get; private set; }
        public bool IsCoverSeparate { get; private set; } = true; // true: 첫 장(표지) 단독 페이지

        public int TotalImages => _pages.Count;
        public int TotalPages => GetMaxPageIndex() + 1;

        public MangaManager() => Pages = new ReadOnlyObservableCollection<MangaPageViewModel>(_pages);

        /// <summary>
        /// 지정 폴더에서 이미지 파일들을 자연 정렬 후 비동기 배치 로드.
        /// 첫 이미지는 즉시 UI 컬렉션에 추가하여 빠른 반응 제공.
        /// </summary>
        public async Task LoadFolderAsync(StorageFolder folder)
        {
            _loadCts?.Cancel();
            _loadCts = new CancellationTokenSource();
            var token = _loadCts.Token;

            _pages.Clear();
            CurrentPageIndex = 0;

            var dispatcher = DispatcherQueue.GetForCurrentThread();

            // 파일 나열 및 정렬 (백그라운드)
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

            // 첫 이미지 즉시 추가 (빠른 표시)
            _pages.Add(new MangaPageViewModel { FilePath = imageFiles[0].Path });
            MangaLoaded?.Invoke();
            PageChanged?.Invoke();

            // 나머지는 배치로 천천히 추가 (UI 응답성 확보)
            _ = Task.Run(async () =>
            {
                const int batchSize = 50;
                int addedInBatch = 0;
                for (int i = 1; i < imageFiles.Count; i++)
                {
                    if (_loadCts!.IsCancellationRequested) return;
                    string path = imageFiles[i].Path;

                    dispatcher.TryEnqueue(() =>
                    {
                        if (_loadCts.IsCancellationRequested) return;
                        _pages.Add(new MangaPageViewModel { FilePath = path });
                    });

                    if (++addedInBatch >= batchSize)
                    {
                        addedInBatch = 0;
                        try { await Task.Delay(10, _loadCts.Token); } catch { return; }
                    }
                }
            }, token);
        }

        /// <summary>이전 페이지로 이동.</summary>
        public void GoToPreviousPage()
        {
            if (CurrentPageIndex <= 0) return;
            CurrentPageIndex--;
            PageChanged?.Invoke();
        }

        /// <summary>다음 페이지로 이동.</summary>
        public void GoToNextPage()
        {
            if (CurrentPageIndex >= GetMaxPageIndex()) return;
            CurrentPageIndex++;
            PageChanged?.Invoke();
        }

        /// <summary>지정 페이지로 이동 (범위 Clamp)</summary>
        public void GoToPage(int pageIndex)
        {
            CurrentPageIndex = Math.Clamp(pageIndex, 0, GetMaxPageIndex());
            PageChanged?.Invoke();
        }

        /// <summary>썸네일(이미지) 인덱스로부터 현재 페이지 계산 후 이동.</summary>
        public void SetCurrentPageFromImageIndex(int imageIndex)
        {
            if (imageIndex < 0 || imageIndex >= _pages.Count) return;
            CurrentPageIndex = GetPageIndexFromImageIndex(imageIndex);
            PageChanged?.Invoke();
        }

        /// <summary>읽기 방향 토글(RTL/LTR)</summary>
        public void ToggleDirection()
        {
            IsRightToLeft = !IsRightToLeft;
            PageChanged?.Invoke();
        }

        /// <summary>표지 단독 여부 토글. 현재 표지가 포함된 페이지 유지되도록 재계산.</summary>
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

        /// <summary>현재 설정(IsCoverSeparate)에 따른 최대 페이지 인덱스.</summary>
        private int GetMaxPageIndex()
        {
            if (_pages.Count == 0) return 0;
            int pageCount = IsCoverSeparate
                ? (_pages.Count > 1 ? 1 + (int)Math.Ceiling((_pages.Count - 1) / 2.0) : 1)
                : (int)Math.Ceiling(_pages.Count / 2.0);
            return Math.Max(0, pageCount - 1);
        }

        /// <summary>페이지 인덱스로부터 실제 이미지 경로 목록 (1 또는 2개) 반환.</summary>
        public List<string> GetImagePathsForPage(int pageIndex)
        {
            var paths = new List<string>();
            if (_pages.Count == 0 || pageIndex < 0 || pageIndex > GetMaxPageIndex()) return paths;

            if (IsCoverSeparate)
            {
                if (pageIndex == 0)
                {
                    var fp0 = _pages[0].FilePath; if (fp0 != null) paths.Add(fp0);
                }
                else
                {
                    int actualIndex = 1 + (pageIndex - 1) * 2;
                    if (actualIndex < _pages.Count) { var fp = _pages[actualIndex].FilePath; if (fp != null) paths.Add(fp); }
                    if (actualIndex + 1 < _pages.Count) { var fp2 = _pages[actualIndex + 1].FilePath; if (fp2 != null) paths.Add(fp2); }
                }
            }
            else
            {
                int actualIndex = pageIndex * 2;
                if (actualIndex < _pages.Count) { var fp = _pages[actualIndex].FilePath; if (fp != null) paths.Add(fp); }
                if (actualIndex + 1 < _pages.Count) { var fp2 = _pages[actualIndex + 1].FilePath; if (fp2 != null) paths.Add(fp2); }
            }
            return paths;
        }

        /// <summary>페이지 내 대표(첫) 이미지 인덱스 반환.</summary>
        public int GetPrimaryImageIndexForPage(int pageIndex)
        {
            if (_pages.Count == 0 || pageIndex < 0 || pageIndex > GetMaxPageIndex()) return -1;
            return IsCoverSeparate
                ? (pageIndex == 0 ? 0 : 1 + (pageIndex - 1) * 2)
                : pageIndex * 2;
        }

        /// <summary>이미지 인덱스로부터 페이지 인덱스 계산.</summary>
        private int GetPageIndexFromImageIndex(int imageIndex)
        {
            if (imageIndex < 0 || imageIndex >= _pages.Count) return 0;
            return IsCoverSeparate
                ? (imageIndex == 0 ? 0 : 1 + (imageIndex - 1) / 2)
                : imageIndex / 2;
        }

        /// <summary>숫자 패딩을 통한 자연 정렬 키 생성.</summary>
        private static string ToNaturalSortKey(string name) =>
            s_digitsRegex.Replace(name, m => m.Value.PadLeft(10, '0'));
    }
}