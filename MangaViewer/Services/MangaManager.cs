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
    /// ��ȭ �̹��� �ε� �� ������(1~2�� ����) ����/������̼� ���� ����.
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
        public bool IsCoverSeparate { get; private set; } = true; // true: ǥ�� �ܵ�

        public int TotalImages => _pages.Count;
        public int TotalPages => GetMaxPageIndex() + 1;

        public MangaManager() => Pages = new ReadOnlyObservableCollection<MangaPageViewModel>(_pages);

        /// <summary>���� ���� ��ü �̹��� �ε� (�ʱ� �߰�)</summary>
        public async Task LoadFolderAsync(StorageFolder folder)
        {
            _loadCts?.Cancel();
            _loadCts = new CancellationTokenSource();
            var token = _loadCts.Token;

            _pages.Clear();
            CurrentPageIndex = 0;
            var dispatcher = DispatcherQueue.GetForCurrentThread();

            // ���� ���� + �ڿ� ���� (��׶���)
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

            // ù �̹��� ��� �߰� (ǥ��)
            _pages.Add(new MangaPageViewModel { FilePath = imageFiles[0].Path });
            MangaLoaded?.Invoke();
            PageChanged?.Invoke();

            // ������ �񵿱� ���� (UI ���� ��ȭ)
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
        /// ��Ʈ����/�ٿ�ε�� ���� ��� �߰�. mem: Ű ��� ���� ����, �ߺ�/��ٿ�ε� ����.
        /// </summary>
        public void AddDownloadedFiles(IEnumerable<string> filePaths)
        {
            var incoming = filePaths?.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new();
            if (incoming.Count == 0) return;

            // ���� ����: �̸��� ���Ե� ���� (mem:gid:####.ext �� ####) ����
            foreach (var path in incoming.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                bool isMem = path.StartsWith("mem:", StringComparison.OrdinalIgnoreCase);
                string name = System.IO.Path.GetFileNameWithoutExtension(path) ?? string.Empty;
                // mem:gid:####.ext ���� -> ������ �ݷ� �� 4�ڸ� ���� ����
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

                // �ʿ� �� placeholder Ȯ��
                if (index >= _pages.Count)
                {
                    SetExpectedTotal(index + 1);
                }

                var vm = _pages[index];
                if (vm.FilePath == null)
                {
                    vm.FilePath = path; // ���� ä��
                }
                else
                {
                    // �̹� ������ ä���� ������ ��Ģ:
                    // 1) �� �� mem: �̸� �̹� �ٿ�ε�� ������ ���� -> skip (�ߺ� ����)
                    if (isMem && vm.FilePath.StartsWith("mem:", StringComparison.OrdinalIgnoreCase)) continue;
                    // 2) ������ mem: �̰� ���ο� ���� ���� �����̸� ��ü (ǰ�� ��� ���̽� ����)
                    if (!isMem && vm.FilePath.StartsWith("mem:", StringComparison.OrdinalIgnoreCase)) { vm.FilePath = path; }
                    // 3) �� �� (���� �ٸ� ���� ���� �浹) -> ���� (���� ����)
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
            // Cover �и�: ��ü ������ = 1(ǥ��) + floor((N-1)/2 + 1) = 1 + (N-1)/2
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