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
    /// ���� �� �̹���(������) ��� �ε� �� ������ ���/���� ����.
    /// Cover �ܵ� ����, RTL ���ο� ���� ������ �ε��� <-> �̹��� �ε��� ���� ����.
    /// </summary>
    public class MangaManager
    {
        public event Action? MangaLoaded;   // ���� ��� �ε�(ù �׸�) �� ��ȣ
        public event Action? PageChanged;   // ���� ������/ǥ�� ���� ��ȭ �� ��ȣ

        private static readonly HashSet<string> s_imageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp" };
        private static readonly Regex s_digitsRegex = new(@"\d+", RegexOptions.Compiled);

        private CancellationTokenSource? _loadCts;

        private readonly ObservableCollection<MangaPageViewModel> _pages = new();
        public ReadOnlyObservableCollection<MangaPageViewModel> Pages { get; }

        public int CurrentPageIndex { get; private set; }
        public bool IsRightToLeft { get; private set; }
        public bool IsCoverSeparate { get; private set; } = true; // true: ù ��(ǥ��) �ܵ� ������

        public int TotalImages => _pages.Count;
        public int TotalPages => GetMaxPageIndex() + 1;

        public MangaManager() => Pages = new ReadOnlyObservableCollection<MangaPageViewModel>(_pages);

        /// <summary>
        /// ���� �������� �̹��� ���ϵ��� �ڿ� ���� �� �񵿱� ��ġ �ε�.
        /// ù �̹����� ��� UI �÷��ǿ� �߰��Ͽ� ���� ���� ����.
        /// </summary>
        public async Task LoadFolderAsync(StorageFolder folder)
        {
            _loadCts?.Cancel();
            _loadCts = new CancellationTokenSource();
            var token = _loadCts.Token;

            _pages.Clear();
            CurrentPageIndex = 0;

            var dispatcher = DispatcherQueue.GetForCurrentThread();

            // ���� ���� �� ���� (��׶���)
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

            // ù �̹��� ��� �߰� (���� ǥ��)
            _pages.Add(new MangaPageViewModel { FilePath = imageFiles[0].Path });
            MangaLoaded?.Invoke();
            PageChanged?.Invoke();

            // �������� ��ġ�� õõ�� �߰� (UI ���伺 Ȯ��)
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

        /// <summary>���� �������� �̵�.</summary>
        public void GoToPreviousPage()
        {
            if (CurrentPageIndex <= 0) return;
            CurrentPageIndex--;
            PageChanged?.Invoke();
        }

        /// <summary>���� �������� �̵�.</summary>
        public void GoToNextPage()
        {
            if (CurrentPageIndex >= GetMaxPageIndex()) return;
            CurrentPageIndex++;
            PageChanged?.Invoke();
        }

        /// <summary>���� �������� �̵� (���� Clamp)</summary>
        public void GoToPage(int pageIndex)
        {
            CurrentPageIndex = Math.Clamp(pageIndex, 0, GetMaxPageIndex());
            PageChanged?.Invoke();
        }

        /// <summary>�����(�̹���) �ε����κ��� ���� ������ ��� �� �̵�.</summary>
        public void SetCurrentPageFromImageIndex(int imageIndex)
        {
            if (imageIndex < 0 || imageIndex >= _pages.Count) return;
            CurrentPageIndex = GetPageIndexFromImageIndex(imageIndex);
            PageChanged?.Invoke();
        }

        /// <summary>�б� ���� ���(RTL/LTR)</summary>
        public void ToggleDirection()
        {
            IsRightToLeft = !IsRightToLeft;
            PageChanged?.Invoke();
        }

        /// <summary>ǥ�� �ܵ� ���� ���. ���� ǥ���� ���Ե� ������ �����ǵ��� ����.</summary>
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

        /// <summary>���� ����(IsCoverSeparate)�� ���� �ִ� ������ �ε���.</summary>
        private int GetMaxPageIndex()
        {
            if (_pages.Count == 0) return 0;
            int pageCount = IsCoverSeparate
                ? (_pages.Count > 1 ? 1 + (int)Math.Ceiling((_pages.Count - 1) / 2.0) : 1)
                : (int)Math.Ceiling(_pages.Count / 2.0);
            return Math.Max(0, pageCount - 1);
        }

        /// <summary>������ �ε����κ��� ���� �̹��� ��� ��� (1 �Ǵ� 2��) ��ȯ.</summary>
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

        /// <summary>������ �� ��ǥ(ù) �̹��� �ε��� ��ȯ.</summary>
        public int GetPrimaryImageIndexForPage(int pageIndex)
        {
            if (_pages.Count == 0 || pageIndex < 0 || pageIndex > GetMaxPageIndex()) return -1;
            return IsCoverSeparate
                ? (pageIndex == 0 ? 0 : 1 + (pageIndex - 1) * 2)
                : pageIndex * 2;
        }

        /// <summary>�̹��� �ε����κ��� ������ �ε��� ���.</summary>
        private int GetPageIndexFromImageIndex(int imageIndex)
        {
            if (imageIndex < 0 || imageIndex >= _pages.Count) return 0;
            return IsCoverSeparate
                ? (imageIndex == 0 ? 0 : 1 + (imageIndex - 1) / 2)
                : imageIndex / 2;
        }

        /// <summary>���� �е��� ���� �ڿ� ���� Ű ����.</summary>
        private static string ToNaturalSortKey(string name) =>
            s_digitsRegex.Replace(name, m => m.Value.PadLeft(10, '0'));
    }
}