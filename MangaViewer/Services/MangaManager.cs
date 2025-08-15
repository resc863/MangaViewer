using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using MangaViewer.ViewModels;

namespace MangaViewer.Services
{
    public class MangaManager
    {
        public event Action MangaLoaded;
        public event Action PageChanged;

        private static readonly HashSet<string> s_imageExtensions =
            new(StringComparer.OrdinalIgnoreCase)
            { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp" };

        private static readonly Regex s_digitsRegex = new(@"\d+", RegexOptions.Compiled);

        private CancellationTokenSource _loadCts;

        public ReadOnlyObservableCollection<MangaPageViewModel> Pages { get; private set; }
        private readonly ObservableCollection<MangaPageViewModel> _pages = new();

        public int CurrentPageIndex { get; private set; } = 0;
        public bool IsRightToLeft { get; private set; } = false;
        public bool IsCoverSeparate { get; private set; } = true;

        public int TotalImages => _pages.Count;
        public int TotalPages => GetMaxPageIndex() + 1;

        public MangaManager()
        {
            Pages = new ReadOnlyObservableCollection<MangaPageViewModel>(_pages);
        }

        public async Task LoadFolderAsync(StorageFolder folder)
        {
            _loadCts?.Cancel();
            _loadCts = new CancellationTokenSource();
            var token = _loadCts.Token;

            _pages.Clear();
            CurrentPageIndex = 0;

            var files = await folder.GetFilesAsync().AsTask(token);

            var imageFiles = files
                .Where(f => s_imageExtensions.Contains(f.FileType))
                .Select(f => new { f.Path, SortKey = ToNaturalSortKey(System.IO.Path.GetFileNameWithoutExtension(f.Name)) })
                .OrderBy(x => x.SortKey, StringComparer.Ordinal)
                .ToList();

            if (token.IsCancellationRequested) return;

            foreach (var file in imageFiles)
            {
                if (token.IsCancellationRequested) return;
                _pages.Add(new MangaPageViewModel { FilePath = file.Path });
            }

            MangaLoaded?.Invoke();
            PageChanged?.Invoke();
        }

        public void GoToPreviousPage()
        {
            if (CurrentPageIndex > 0)
            {
                CurrentPageIndex--;
                PageChanged?.Invoke();
            }
        }

        public void GoToNextPage()
        {
            if (CurrentPageIndex < GetMaxPageIndex())
            {
                CurrentPageIndex++;
                PageChanged?.Invoke();
            }
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

            var primaryImageIndex = GetPrimaryImageIndexForPage(CurrentPageIndex);
            
            IsCoverSeparate = !IsCoverSeparate;

            if (primaryImageIndex >= 0)
            {
                CurrentPageIndex = GetPageIndexFromImageIndex(primaryImageIndex);
            }
            
            PageChanged?.Invoke();
        }

        public List<string> GetImagePathsForCurrentPage()
        {
            return GetImagePathsForPage(CurrentPageIndex);
        }

        private int GetMaxPageIndex()
        {
            if (_pages.Count <= 0) return 0;
            int pageCount = IsCoverSeparate
                ? (_pages.Count > 1 ? 1 + (int)Math.Ceiling((_pages.Count - 1) / 2.0) : 1)
                : (int)Math.Ceiling(_pages.Count / 2.0);
            return Math.Max(0, pageCount - 1);
        }

        private List<string> GetImagePathsForPage(int pageIndex)
        {
            var paths = new List<string>();
            if (_pages.Count == 0 || pageIndex < 0 || pageIndex > GetMaxPageIndex()) return paths;

            if (IsCoverSeparate)
            {
                if (pageIndex == 0)
                {
                    paths.Add(_pages[0].FilePath);
                }
                else
                {
                    int actualIndex = 1 + (pageIndex - 1) * 2;
                    if (actualIndex < _pages.Count) paths.Add(_pages[actualIndex].FilePath);
                    if (actualIndex + 1 < _pages.Count) paths.Add(_pages[actualIndex + 1].FilePath);
                }
            }
            else
            {
                int actualIndex = pageIndex * 2;
                if (actualIndex < _pages.Count) paths.Add(_pages[actualIndex].FilePath);
                if (actualIndex + 1 < _pages.Count) paths.Add(_pages[actualIndex + 1].FilePath);
            }
            return paths;
        }

        public int GetPrimaryImageIndexForPage(int pageIndex)
        {
            if (_pages.Count == 0 || pageIndex < 0 || pageIndex > GetMaxPageIndex()) return -1;
            if (IsCoverSeparate)
            {
                return (pageIndex == 0) ? 0 : 1 + (pageIndex - 1) * 2;
            }
            return pageIndex * 2;
        }

        private int GetPageIndexFromImageIndex(int imageIndex)
        {
            if (imageIndex < 0 || imageIndex >= _pages.Count) return 0;
            if (IsCoverSeparate)
            {
                return (imageIndex == 0) ? 0 : 1 + (imageIndex - 1) / 2;
            }
            return imageIndex / 2;
        }

        private static string ToNaturalSortKey(string name)
        {
            return s_digitsRegex.Replace(name, m => m.Value.PadLeft(10, '0'));
        }
    }
}