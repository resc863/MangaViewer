// Project: MangaViewer
// File: Services/MangaManager.cs
// Purpose: Loads images from a folder (top-level only), maintains logical page layout (single/two-page),
// and exposes navigation and mapping between image indices and page indices.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage; // retained only for picker entry point
using MangaViewer.ViewModels;
using Microsoft.UI.Dispatching;
using System.IO; // new for non-WinRT enumeration

namespace MangaViewer.Services
{
    /// <summary>
    /// MangaManager
    /// Responsibilities:
    ///  - Collect eligible image files from a folder (non-recursive, natural sort).
    ///  - Maintain Page vs Image index mapping supporting cover-separated mode and RTL direction toggling.
    ///  - Support streaming additions (mem: keys) with numeric ordering heuristic.
    ///  - Provide navigation helpers (next/prev/page jump) updating CurrentPageIndex.
    ///  - Deliver observable collection of MangaPageViewModel objects (placeholders allowed for streaming).
    /// Performance Strategies:
    ///  - Batch adds (64 items) with short delay to reduce UI thread blocking when large folder loads.
    ///  - Natural sort converts numeric substrings to fixed-width to produce intuitive ordering.
    /// Streaming Support:
    ///  - SetExpectedTotal creates placeholder VMs enabling layout before actual files arrive.
    ///  - AddDownloadedFiles merges mem: images and later real disk paths replacing placeholders.
    /// Threading: Background file enumeration + batching via Task.Run; UI updates marshaled through DispatcherQueue.
    /// </summary>
    public class MangaManager
    {
        public event Action? MangaLoaded;
        public event Action? PageChanged;

        private readonly DispatcherQueue _dispatcher;
        private static readonly HashSet<string> s_imageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".avif", ".gif" };

        private CancellationTokenSource? _loadCts;
        private readonly ObservableCollection<MangaPageViewModel> _pages = new();
        public ReadOnlyObservableCollection<MangaPageViewModel> Pages { get; }

        public int CurrentPageIndex { get; private set; }
        public bool IsRightToLeft { get; private set; }
        public bool IsCoverSeparate { get; private set; } = true; // true: cover separate

        public int TotalImages => _pages.Count;
        public int TotalPages => GetMaxPageIndex() + 1;

        public string? CurrentFolderPath { get; private set; }

        public MangaManager()
        {
            _dispatcher = DispatcherQueue.GetForCurrentThread();
            Pages = new ReadOnlyObservableCollection<MangaPageViewModel>(_pages);
        }

        /// <summary>
        /// Load folder via StorageFolder (legacy); internally uses path-based method.
        /// </summary>
        public async Task LoadFolderAsync(StorageFolder folder)
        {
            if (folder == null) { Clear(); return; }
            await LoadFolderAsync(folder.Path);
        }

        /// <summary>
        /// Load images from physical folder (top-level only). Clears existing pages.
        /// </summary>
        public async Task LoadFolderAsync(string folderPath)
        {
            _loadCts?.Cancel();
            _loadCts = new CancellationTokenSource();
            var token = _loadCts.Token;

            _pages.Clear();
            CurrentPageIndex = 0;
            CurrentFolderPath = folderPath;
            var dispatcher = _dispatcher ?? DispatcherQueue.GetForCurrentThread();

            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            {
                RaiseMangaLoaded();
                RaisePageChanged();
                return;
            }

            // 파일 수집 + 자연 정렬 (비동기)
            List<(string Path, string SortKey)> imageFiles = await Task.Run(() =>
            {
                var result = new List<(string Path, string SortKey)>();
                try
                {
                    var files = Directory.GetFiles(folderPath, "*.*", SearchOption.TopDirectoryOnly);
                    foreach (var f in files)
                    {
                        string ext = Path.GetExtension(f);
                        if (s_imageExtensions.Contains(ext))
                        {
                            string name = Path.GetFileNameWithoutExtension(f);
                            string sortKey = ToNaturalSortKey(name);
                            result.Add((f, sortKey));
                        }
                    }
                    result.Sort((a, b) => StringComparer.Ordinal.Compare(a.SortKey, b.SortKey));
                }
                catch { }
                return result;
            }, token);

            if (token.IsCancellationRequested) return;

            if (imageFiles.Count == 0)
            {
                RaiseMangaLoaded();
                RaisePageChanged();
                return;
            }

            // 첫 이미지 바로 추가 (표지)
            _pages.Add(new MangaPageViewModel { FilePath = imageFiles[0].Path });
            RaiseMangaLoaded();
            RaisePageChanged();

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
                            RaisePageChanged();
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
            CurrentFolderPath = null;
            RaiseMangaLoaded();
            RaisePageChanged();
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
                RaiseMangaLoaded();
                RaisePageChanged();
            }
        }

        /// <summary>
        /// Merge streaming downloaded files (mem: or disk paths) into placeholder pages; apply replacement logic.
        /// </summary>
        public void AddDownloadedFiles(IEnumerable<string> filePaths)
        {
            if (filePaths == null) return;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<string>();
            foreach (var p in filePaths)
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                if (seen.Add(p)) list.Add(p);
            }
            if (list.Count == 0) return;
            list.Sort(StringComparer.OrdinalIgnoreCase);

            foreach (var path in list)
            {
                bool isMem = path.StartsWith("mem:", StringComparison.OrdinalIgnoreCase);
                string name = System.IO.Path.GetFileNameWithoutExtension(path) ?? string.Empty;
                int index = isMem ? ExtractMemIndex(path) : (ExtractNumericIndex(name) - 1);
                if (index < 0) continue;
                if (index >= _pages.Count) SetExpectedTotal(index + 1);
                var vm = _pages[index];
                if (vm.FilePath == null) vm.FilePath = path;
                else
                {
                    if (isMem && vm.FilePath.StartsWith("mem:", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!isMem && vm.FilePath.StartsWith("mem:", StringComparison.OrdinalIgnoreCase)) vm.FilePath = path;
                }
            }
            RaisePageChanged();
        }

        private static int ExtractMemIndex(string memPath)
        {
            int lastColon = memPath.LastIndexOf(':');
            if (lastColon < 0) return -1;
            int start = lastColon + 1;
            int len = 0;
            while (start + len < memPath.Length && len < 4 && char.IsDigit(memPath[start + len])) len++;
            if (len == 0) return -1;
            if (int.TryParse(memPath.AsSpan(start, len), out int v)) return v - 1;
            return -1;
        }

        private static int ExtractNumericIndex(string name)
        {
            if (string.IsNullOrEmpty(name)) return -1;
            int i = 0;
            while (i < name.Length && !char.IsDigit(name[i])) i++;
            if (i >= name.Length) return -1;
            int start = i;
            while (i < name.Length && char.IsDigit(name[i])) i++;
            if (int.TryParse(name.AsSpan(start, i - start), out int v)) return v;
            return -1;
        }

        // Navigation helpers
        public void GoToPreviousPage()
        {
            if (CurrentPageIndex <= 0) return;
            CurrentPageIndex--;
            RaisePageChanged();
        }
        public void GoToNextPage()
        {
            if (CurrentPageIndex >= GetMaxPageIndex()) return;
            CurrentPageIndex++;
            RaisePageChanged();
        }
        public void GoToPage(int pageIndex)
        {
            CurrentPageIndex = Math.Clamp(pageIndex, 0, GetMaxPageIndex());
            RaisePageChanged();
        }
        public void SetCurrentPageFromImageIndex(int imageIndex)
        {
            if (imageIndex < 0 || imageIndex >= _pages.Count) return;
            CurrentPageIndex = GetPageIndexFromImageIndex(imageIndex);
            RaisePageChanged();
        }
        public void ToggleDirection()
        {
            IsRightToLeft = !IsRightToLeft;
            RaisePageChanged();
        }
        public void ToggleCover()
        {
            if (_pages.Count == 0) return;
            int primaryImageIndex = GetPrimaryImageIndexForPage(CurrentPageIndex);
            IsCoverSeparate = !IsCoverSeparate;
            if (primaryImageIndex >= 0)
                CurrentPageIndex = GetPageIndexFromImageIndex(primaryImageIndex);
            RaisePageChanged();
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

        public int FindImageIndexByPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return -1;
            for (int i = 0; i < _pages.Count; i++)
            {
                if (string.Equals(_pages[i].FilePath, path, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return -1;
        }

        private static string ToNaturalSortKey(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            var sb = new System.Text.StringBuilder(name.Length + 16);
            int i = 0;
            while (i < name.Length)
            {
                char c = name[i];
                if (!char.IsDigit(c)) { sb.Append(c); i++; continue; }
                int start = i;
                while (i < name.Length && char.IsDigit(name[i])) i++;
                int len = i - start;
                // pad numeric part to fixed width (10) for lexical ordering
                for (int pad = 10 - len; pad > 0; pad--) sb.Append('0');
                sb.Append(name, start, len);
            }
            return sb.ToString();
        }

        public void CreatePlaceholders(int count)
        {
            if (count <= 0) return;
            if (_pages.Count > 0) return; // only if empty
            for (int i = 0; i < count; i++)
                _pages.Add(new MangaPageViewModel());
            RaiseMangaLoaded();
            RaisePageChanged();
        }

        public void ReplaceFileAtIndex(int index, string path)
        {
            if (index < 0 || string.IsNullOrWhiteSpace(path)) return;
            if (index >= _pages.Count) SetExpectedTotal(index + 1);
            _pages[index].FilePath = path;
            RaisePageChanged();
        }

        private void RaiseMangaLoaded()
        {
            if (_dispatcher != null)
            {
                _dispatcher.TryEnqueue(() => MangaLoaded?.Invoke());
                return;
            }
            MangaLoaded?.Invoke();
        }
        private void RaisePageChanged()
        {
            if (_dispatcher != null)
            {
                _dispatcher.TryEnqueue(() => PageChanged?.Invoke());
                return;
            }
            PageChanged?.Invoke();
        }
    }
}