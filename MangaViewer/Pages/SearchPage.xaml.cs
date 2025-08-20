using MangaViewer.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Microsoft.UI.Xaml.Navigation; // NavigationCacheMode

namespace MangaViewer.Pages
{
    public sealed partial class SearchPage : Page
    {
        private sealed class TagItem
        {
            public string Value { get; set; } = string.Empty;
            public string Query { get; set; } = string.Empty;
            public double Width { get; set; } // adaptive width
        }

        internal static SearchPage? LastInstance; // �ܺ� ���������� ����

        public SearchViewModel ViewModel { get; } = new();
        private MangaViewModel? _mangaViewModel;
        private CancellationTokenSource? _streamCts;
        private FrameworkElement? _lastContextTarget; // remembers last right-click target
        private bool _isStreamingNavigate; // �ߺ� / ������ ����
        private Task _uiBatchChain = Task.CompletedTask; // UI ��ġ ����ȭ

        public SearchPage()
        {
            this.InitializeComponent();
            NavigationCacheMode = NavigationCacheMode.Required; // ������ ĳ�� ����
            DataContext = ViewModel;
            ViewModel.GalleryOpenRequested += OnGalleryOpenRequested;
            Loaded += SearchPage_Loaded;
            LastInstance = this;
        }

        private void SearchPage_Loaded(object sender, RoutedEventArgs e)
        {
            if (ResultsScroll != null)
            {
                ResultsScroll.ViewChanged -= ResultsScroll_ViewChanged;
                ResultsScroll.ViewChanged += ResultsScroll_ViewChanged;
            }
        }

        private async void ResultsScroll_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            try
            {
                if (ResultsScroll.VerticalOffset + ResultsScroll.ViewportHeight + 150 >= ResultsScroll.ExtentHeight)
                {
                    await ViewModel.LoadMoreAsync();
                }
            }
            catch (Exception ex) { Debug.WriteLine("[SearchPage] InfiniteScroll error: " + ex.Message); }
        }

        protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is MangaViewModel vm) _mangaViewModel = vm;

            // ���ƿ��� �� ���� ����� �ִٸ� ItemsSource ����
            if (ResultsList.ItemsSource == null && ViewModel.Results.Count > 0)
            {
                ResultsList.ItemsSource = ViewModel.Results;
            }
        }

        internal async Task ExternalSearchAsync(string query)
        {
            QueryBox.Text = query;
            await RunSearchFromExternalAsync();
        }

        private async void OnSearchClick(object sender, RoutedEventArgs e) => await RunSearch();
        private async void OnQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args) => await RunSearch();

        private async Task RunSearch()
        {
            BusyRing.IsActive = true;
            try
            {
                await ViewModel.SearchAsync(QueryBox.Text ?? string.Empty);
                ResultsList.ItemsSource = ViewModel.Results; // �׻� ���� �÷��� ���ε�
            }
            finally { BusyRing.IsActive = false; }
        }

        internal Task RunSearchFromExternalAsync() => RunSearch();
        internal AutoSuggestBox QueryInput => QueryBox; // expose for external windows

        private async void OnGalleryOpenRequested(object? sender, GalleryItemViewModel item)
        {
            if (_mangaViewModel == null || item.GalleryUrl == null) return;
            if (_isStreamingNavigate) return; // �̹� ������
            BusyRing.IsActive = true;

            try { _streamCts?.Cancel(); } catch { }
            _streamCts?.Dispose();
            _streamCts = new CancellationTokenSource();
            var token = _streamCts.Token;
            bool navigated = false;

            try
            {
                var service = new Services.EhentaiService();
                var cached = Services.EhentaiService.TryGetCachedGallery(item.GalleryUrl);
                if (cached != null && cached.Count > 0)
                {
                    await _mangaViewModel.LoadLocalFilesAsync(cached);
                    SafeNavigateToReader();
                    return;
                }

                var (pageUrls, _) = await service.GetAllPageUrlsAsync(item.GalleryUrl, token);
                _mangaViewModel.BeginStreamingGallery();
                if (pageUrls.Count > 0) _mangaViewModel.SetExpectedTotalPages(pageUrls.Count);

                await foreach (var batch in service.DownloadPagesStreamingOrderedAsync(item.GalleryUrl, pageUrls, 32, s => Debug.WriteLine("[Stream] " + s), token).WithCancellation(token))
                {
                    if (token.IsCancellationRequested) break;
                    if (batch.Files.Any())
                    {
                        _uiBatchChain = _uiBatchChain.ContinueWith(_ => DispatcherQueue.TryEnqueue(() =>
                        {
                            try { _mangaViewModel.AddDownloadedFiles(batch.Files); }
                            catch (Exception ex) { Debug.WriteLine("[SearchPage] AddDownloadedFiles error: " + ex.Message); }
                        }), TaskScheduler.Default);

                        if (!navigated && batch.Completed > 0)
                        {
                            navigated = true;
                            _isStreamingNavigate = true;
                            // ù �̹��� Ȯ�� �� ������ �̵������� ��Ʈ������ ��� ����
                            DispatcherQueue.TryEnqueue(SafeNavigateToReader);
                            // ���/Ż�� ���� -> �ܿ� ������ ��� �ٿ�ε�
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Debug.WriteLine("[SearchPage] Streaming error: " + ex);
            }
            finally
            {
                BusyRing.IsActive = false;
                _isStreamingNavigate = false;
            }
        }

        private void SafeNavigateToReader()
        {
            try
            {
                if (_mangaViewModel == null) return;
                if (Frame.Content?.GetType() != typeof(MangaReaderPage))
                {
                    MainWindow.TryNavigate(typeof(MangaReaderPage), _mangaViewModel);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[SearchPage] Navigate error: " + ex.Message);
            }
            finally { BusyRing.IsActive = false; }
        }

        private async void MetaFlyout_Opening(object sender, object e)
        {
            if (sender is not MenuFlyout mf) return;
            if (mf.Target is not FrameworkElement fe) return;
            if (fe.DataContext is not GalleryItemViewModel item) return;
            _lastContextTarget = fe;
            try { await ViewModel.LoadItemDetailsAsync(item); } catch (Exception ex) { Debug.WriteLine("[SearchPage] Details load error: " + ex.Message); }

            var detailItem = mf.Items.OfType<MenuFlyoutItem>().FirstOrDefault(i => (string)i.Text == "�� ����");
            if (detailItem != null) detailItem.Tag = item;

            FillSubMenu(mf, "�۰�", item.Artists);
            FillSubMenu(mf, "�׷�", item.Groups);
            FillSubMenu(mf, "�±�", item.SearchableTags);
        }

        private void FillSubMenu(MenuFlyout root, string header, System.Collections.IEnumerable values)
        {
            var sub = root.Items.OfType<MenuFlyoutSubItem>().FirstOrDefault(i => (string)i.Text == header);
            if (sub == null) return;
            sub.Items.Clear();
            foreach (var v in values)
                sub.Items.Add(CreateSearchItem(v?.ToString() ?? string.Empty));
        }

        private MenuFlyoutItem CreateSearchItem(string text)
        {
            var mi = new MenuFlyoutItem { Text = text };
            mi.Click += async (s, e) => { QueryBox.Text = text; await RunSearch(); };
            return mi;
        }

        private async void OnDetailsMenuClick(object sender, RoutedEventArgs e)
        {
            GalleryItemViewModel? item = null;
            if (sender is MenuFlyoutItem mi && mi.Tag is GalleryItemViewModel tagged) item = tagged;
            else if (_lastContextTarget?.DataContext is GalleryItemViewModel ctxItem) item = ctxItem;
            if (item == null) return;
            try { await ViewModel.LoadItemDetailsAsync(item); } catch { }
            SafeNavigateToDetails(item);
        }

        private void SafeNavigateToDetails(GalleryItemViewModel item)
        {
            try
            {
                if (Frame.Content?.GetType() != typeof(GalleryDetailsPage))
                    MainWindow.TryNavigate(typeof(GalleryDetailsPage), item);
            }
            catch (Exception ex) { Debug.WriteLine("[SearchPage] Details navigate error: " + ex.Message); }
        }

        private sealed class WrapPanelSimple : Panel
        {
            protected override Size MeasureOverride(Size availableSize)
            {
                double lineH = 0, x = 0, y = 0;
                double width = double.IsInfinity(availableSize.Width) || availableSize.Width <= 0 ? 900 : availableSize.Width;
                foreach (var c in Children)
                {
                    c.Measure(new Size(width, availableSize.Height));
                    var sz = c.DesiredSize;
                    if (x > 0 && x + sz.Width > width)
                    {
                        x = 0; y += lineH + 2; lineH = 0;
                    }
                    x += sz.Width + 6;
                    if (sz.Height > lineH) lineH = sz.Height;
                }
                return new Size(width, y + lineH);
            }
            protected override Size ArrangeOverride(Size finalSize)
            {
                double lineH = 0, x = 0, y = 0;
                double width = finalSize.Width <= 0 ? 900 : finalSize.Width;
                foreach (var c in Children)
                {
                    var sz = c.DesiredSize;
                    if (x > 0 && x + sz.Width > width)
                    {
                        x = 0; y += lineH + 2; lineH = 0;
                    }
                    c.Arrange(new Rect(new Point(x, y), sz));
                    x += sz.Width + 6;
                    if (sz.Height > lineH) lineH = sz.Height;
                }
                return new Size(width, y + lineH);
            }
        }
    }
}
