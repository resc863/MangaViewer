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
using MangaViewer.Services;
using MangaViewer.Services.Thumbnails; // ensure thumbnails namespace available
using Microsoft.UI.Input; // Keyboard state
using Microsoft.UI.Xaml.Input; // PointerRoutedEventArgs
using Microsoft.UI.Composition; // animations
using Microsoft.UI.Xaml.Hosting; // ElementCompositionPreview
using System.Numerics; // Vector3, Vector2
using Microsoft.UI.Xaml.Media; // CompositionTarget.Rendering

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

        private sealed class TileAnimState
        {
            public double LastWidth;
            public double LastHeight;
        }

        internal static SearchPage? LastInstance; // 최근 인스턴스

        public SearchViewModel ViewModel { get; } = new();
        private MangaViewModel? _mangaViewModel;
        private CancellationTokenSource? _streamCts;
        private FrameworkElement? _lastContextTarget; // remembers last right-click target
        private Task _uiBatchChain = Task.CompletedTask; // UI batch serialization

        // NEW: streaming generation & active gallery tracking
        private int _streamGeneration;
        private string? _activeGalleryUrl;

        // Zoom settings
        private const double ZoomMin = 120;
        private const double ZoomMax = 420;

        // Dynamic tile size properties (default matching previous 180x240)
        public double TileWidth
        {
            get => (double)GetValue(TileWidthProperty);
            set => SetValue(TileWidthProperty, value);
        }
        public static readonly DependencyProperty TileWidthProperty =
            DependencyProperty.Register(nameof(TileWidth), typeof(double), typeof(SearchPage), new PropertyMetadata(180d));

        public double TileHeight
        {
            get => (double)GetValue(TileHeightProperty);
            set => SetValue(TileHeightProperty, value);
        }
        public static readonly DependencyProperty TileHeightProperty =
            DependencyProperty.Register(nameof(TileHeight), typeof(double), typeof(SearchPage), new PropertyMetadata(240d));

        public SearchPage()
        {
            this.InitializeComponent();
            NavigationCacheMode = NavigationCacheMode.Required;
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
                ResultsList.ItemsSource = ViewModel.Results;
            }
            finally { BusyRing.IsActive = false; }
        }

        internal Task RunSearchFromExternalAsync() => RunSearch();
        internal AutoSuggestBox QueryInput => QueryBox; // expose for external windows

        private async void OnGalleryOpenRequested(object? sender, GalleryItemViewModel item)
        {
            if (_mangaViewModel == null || item.GalleryUrl == null) return;

            // streaming state
            _streamGeneration++;
            int localGen = _streamGeneration;
            string newGalleryUrl = item.GalleryUrl;

            BusyRing.IsActive = true;

            if (_activeGalleryUrl != null && !string.Equals(_activeGalleryUrl, newGalleryUrl, StringComparison.OrdinalIgnoreCase))
            {
                try { _streamCts?.Cancel(); } catch { }
                try { Services.EhentaiService.CancelDownload(_activeGalleryUrl); } catch { }
            }
            _activeGalleryUrl = newGalleryUrl;

            try { _streamCts?.Cancel(); } catch { }
            _streamCts?.Dispose();
            _streamCts = new CancellationTokenSource();
            var token = _streamCts.Token;

            try
            {
                var service = new Services.EhentaiService();
                var cached = Services.EhentaiService.TryGetCachedGallery(newGalleryUrl);
                if (cached != null && cached.Count > 0)
                {
                    if (localGen == _streamGeneration)
                    {
                        await _mangaViewModel.LoadLocalFilesAsync(cached);
                        SafeNavigateToReader();
                    }
                    return;
                }

                var (pageUrls, _) = await service.GetAllPageUrlsAsync(newGalleryUrl, token);
                if (localGen != _streamGeneration || token.IsCancellationRequested) return;

                _mangaViewModel.BeginStreamingGallery();
                if (pageUrls.Count > 0) _mangaViewModel.SetExpectedTotalPages(pageUrls.Count);

                bool navigated = false;

                await foreach (var batch in service.DownloadPagesStreamingOrderedAsync(newGalleryUrl, pageUrls, 32, s => Debug.WriteLine("[Stream] " + s), token).WithCancellation(token))
                {
                    if (localGen != _streamGeneration || token.IsCancellationRequested) break; // stale
                    if (batch.Files.Any())
                    {
                        var files = batch.Files; // capture
                        _uiBatchChain = _uiBatchChain.ContinueWith(_ => DispatcherQueue.TryEnqueue(() =>
                        {
                            if (localGen != _streamGeneration) return; // stale
                            try { _mangaViewModel.AddDownloadedFiles(files); }
                            catch (Exception ex) { Debug.WriteLine("[SearchPage] AddDownloadedFiles error: " + ex.Message); }
                        }), TaskScheduler.Default);

                        if (!navigated && batch.Completed > 0)
                        {
                            navigated = true;
                            DispatcherQueue.TryEnqueue(() =>
                            {
                                if (localGen == _streamGeneration) SafeNavigateToReader();
                            });
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
                if (localGen == _streamGeneration)
                {
                    BusyRing.IsActive = false;
                }
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

            var detailItem = mf.Items.OfType<MenuFlyoutItem>().FirstOrDefault(i => (string)i.Text == "상세 정보");
            if (detailItem != null) detailItem.Tag = item;

            FillSubMenu(mf, "작가", item.Artists);
            FillSubMenu(mf, "그룹", item.Groups);
            FillSubMenu(mf, "태그", item.SearchableTags);
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

        // Ctrl + Mouse Wheel to adjust tile size (instant, no per-tile scale animation)
        private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            try
            {
                var props = e.GetCurrentPoint(this).Properties;
                bool ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
                if (!ctrl) return;

                int delta = props.MouseWheelDelta; // positive up, negative down
                double factor = delta > 0 ? 1.08 : 1.0 / 1.08; // gentle zoom
                double target = Math.Clamp(TileWidth * factor, ZoomMin, ZoomMax);

                // apply instantly; layout will animate tiles' positions via implicit Offset animations
                TileWidth = target;
                TileHeight = target * (4.0 / 3.0);

                e.Handled = true;
            }
            catch { }
        }

        // Animate tile: implicit translation with light opacity during reflow
        private void TileRoot_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is not FrameworkElement fe) return;
                fe.Unloaded -= TileRoot_Unloaded;
                fe.Unloaded += TileRoot_Unloaded;

                var visual = ElementCompositionPreview.GetElementVisual(fe);
                var comp = visual.Compositor;

                // Implicit offset animation when tile is re-laid out (position changes)
                var offsetAnim = comp.CreateVector3KeyFrameAnimation();
                offsetAnim.Duration = TimeSpan.FromMilliseconds(160);
                offsetAnim.Target = "Offset";
                var ease = comp.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.0f), new Vector2(0.0f, 1.0f));
                offsetAnim.InsertExpressionKeyFrame(1f, "this.FinalValue", ease);

                // Subtle opacity tween during reflow
                var opacityAnim = comp.CreateScalarKeyFrameAnimation();
                opacityAnim.Duration = TimeSpan.FromMilliseconds(140);
                opacityAnim.InsertKeyFrame(0f, 0.96f, ease);
                opacityAnim.InsertKeyFrame(1f, 1f, ease);

                visual.CenterPoint = new Vector3((float)fe.ActualWidth / 2f, (float)fe.ActualHeight / 2f, 0f);
                var coll = comp.CreateImplicitAnimationCollection();
                var group = comp.CreateAnimationGroup();
                group.Add(offsetAnim);
                group.Add(opacityAnim);
                coll["Offset"] = group; // when position changes, run both
                visual.ImplicitAnimations = coll;
            }
            catch { }
        }

        private void TileRoot_Unloaded(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe)
            {
                fe.Unloaded -= TileRoot_Unloaded;
            }
        }
    }
}
