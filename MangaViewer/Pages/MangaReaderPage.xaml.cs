using MangaViewer.ViewModels;
using Microsoft.UI; // Colors
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Microsoft.UI.Xaml.Input; // Tapped
using Microsoft.UI.Xaml.Controls.Primitives; // DragDeltaEventArgs
using MangaViewer.Services.Thumbnails;
using System.Collections.Generic;
using Microsoft.UI.Xaml.Media.Imaging;
using MangaViewer.Services;

namespace MangaViewer.Pages
{
    public sealed partial class MangaReaderPage : Page
    {
        public MangaViewModel ViewModel { get; private set; } = null!;
        private Storyboard? _pageSlideStoryboard;
        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _thumbRefreshUiTimer;
        private bool _isUnloaded;
        private ScrollViewer? _thumbScrollViewer;
        private bool? _preferredPaneOpen; // persisted preferred state
        private DispatcherTimer? _resizeDebounceTimer; // debounce resize
        private bool _suppressPersistDuringResize; // suppress persisting IsPaneOpen while resizing
        private const double PaneMinWidth = 120;
        private const double PaneMaxWidth = 420;

        private const double BookmarkPaneMinWidth = 120;
        private const double BookmarkPaneMaxWidth = 420;

        private int _prefetchRadius = 24;
        private int _prefetchRadiusIdle = 48;
        private DateTime _lastScrollTime = DateTime.MinValue;

        private readonly Queue<Rectangle> _rectPool = new();

        private readonly ClipboardService _clipboard = ClipboardService.Instance;
        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _viewportRedecodeTimer;

        private SolidColorBrush? _ocrLeftStrokeBrush;
        private SolidColorBrush? _ocrLeftFillBrush;
        private SolidColorBrush? _ocrRightStrokeBrush;
        private SolidColorBrush? _ocrRightFillBrush;

        public MangaReaderPage()
        {
            InitializeComponent();
            Loaded += MangaReaderPage_Loaded;
            Unloaded += MangaReaderPage_Unloaded;

            _resizeDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _resizeDebounceTimer.Tick += (_, __) =>
            {
                _resizeDebounceTimer!.Stop();
                _suppressPersistDuringResize = false;
                if (_preferredPaneOpen.HasValue && ViewModel != null)
                {
                    if (ViewModel.IsPaneOpen != _preferredPaneOpen.Value)
                        ViewModel.IsPaneOpen = _preferredPaneOpen.Value;
                }
            };

            _viewportRedecodeTimer = DispatcherQueue.CreateTimer();
            _viewportRedecodeTimer.Interval = TimeSpan.FromMilliseconds(120);
            _viewportRedecodeTimer.IsRepeating = false;
            _viewportRedecodeTimer.Tick += async (_, __) =>
            {
                if (_isUnloaded || ViewModel == null) return;
                try { await ViewModel.RefreshCurrentPageImagesForViewportAsync(); } catch { }
            };
        }

        private Grid SingleWrapperGrid => SingleWrapper;
        private Canvas SingleOverlayCanvas => SingleOverlay;
        private Grid LeftWrapperGrid => LeftWrapper;
        private Canvas LeftOverlayCanvas => LeftOverlay;
        private Grid RightWrapperGrid => RightWrapper;
        private Canvas RightOverlayCanvas => RightOverlay;
        private TranslateTransform PageTranslateTransform => PageTranslate;
        private Grid PageSlideHostGrid => PageSlideHost;

        private void MangaReaderPage_Loaded(object sender, RoutedEventArgs e)
        {
            if (DataContext is MangaViewModel vm)
                HookVm(vm);

            HookThumbScrollViewer();

            try
            {
                var w = SettingsProvider.Get("ReaderPaneWidth", double.NaN);
                if (!double.IsNaN(w))
                    ReaderSplitView.OpenPaneLength = Math.Clamp(w, PaneMinWidth, PaneMaxWidth);

                var bw = SettingsProvider.Get("BookmarkPaneWidth", double.NaN);
                if (!double.IsNaN(bw) && BookmarksList?.Parent is Grid g)
                    g.ColumnDefinitions[1].Width = new GridLength(Math.Clamp(bw, BookmarkPaneMinWidth, BookmarkPaneMaxWidth));
            }
            catch { }

            // 초기 로드: 뷰포트 렌더링 완료 후 큐 구축
            _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                // 짧은 지연 후 뷰포트가 구성되면 큐 재구축
                var initialTimer = DispatcherQueue.CreateTimer();
                initialTimer.Interval = TimeSpan.FromMilliseconds(100);
                initialTimer.Tick += (_, __) =>
                {
                    initialTimer.Stop();
                    RebuildViewportThumbnailQueue(radius: _prefetchRadiusIdle);
                };
                initialTimer.Start();
            });

            // 페이지 로드 완료 후 포커스 설정 (렌더링 완료 후 충분한 지연)
            SetFocusAfterDelay();
        }

        protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is MangaViewModel vm)
            {
                DataContext = vm;
                HookVm(vm);
            }
            HookThumbScrollViewer();
            SetFocusAfterDelay();
        }

        private void SetFocusAfterDelay()
        {
            // UI 렌더링이 완전히 완료될 때까지 충분한 시간 대기 후 포커스 설정
            _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, async () =>
            {
                await System.Threading.Tasks.Task.Delay(150);
                if (!_isUnloaded && this.XamlRoot != null)
                {
                    this.Focus(FocusState.Programmatic);
                }
            });
        }

        protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            Cleanup();
        }

        private void MangaReaderPage_Unloaded(object sender, RoutedEventArgs e) => Cleanup();

        private void Cleanup()
        {
            if (_isUnloaded) return;
            _isUnloaded = true;

            _thumbRefreshUiTimer?.Stop();
            _thumbRefreshUiTimer = null;

            if (_thumbScrollViewer != null)
            {
                _thumbScrollViewer.ViewChanged -= OnThumbsViewChanged;
                _thumbScrollViewer = null;
            }

            _resizeDebounceTimer?.Stop();
            _resizeDebounceTimer = null;

            _viewportRedecodeTimer?.Stop();
            _viewportRedecodeTimer = null;

            UnhookVm(ViewModel);
        }

        private void HookVm(MangaViewModel vm)
        {
            if (ReferenceEquals(ViewModel, vm)) return;

            // Unhook previous VM to avoid duplicate subscriptions.
            if (ViewModel != null)
                UnhookVm(ViewModel);

            ViewModel = vm;

            ViewModel.PageViewChanged += OnVmRedrawRequested;
            ViewModel.OcrCompleted += OnVmRedrawRequested;
            ViewModel.PageSlideRequested += OnPageSlideRequested;
            ViewModel.MangaFolderLoaded += OnMangaFolderLoaded;
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;

            // Load preferred pane state from SettingsProvider
            try
            {
                bool pref = SettingsProvider.Get("ReaderPaneOpenPreferred", ViewModel.IsPaneOpen);
                _preferredPaneOpen = pref;
                if (ViewModel.IsPaneOpen != pref)
                    ViewModel.IsPaneOpen = pref;
            }
            catch { _preferredPaneOpen = ViewModel.IsPaneOpen; }

            if (ViewModel.Thumbnails is INotifyCollectionChanged incc)
                incc.CollectionChanged += OnThumbsChanged;

            foreach (var page in ViewModel.Thumbnails)
                SubscribePage(page);

            if (ViewModel.Bookmarks is INotifyCollectionChanged bincc)
                bincc.CollectionChanged += OnBookmarksChanged;

            foreach (var bm in ViewModel.Bookmarks)
            {
                bm.PropertyChanged += OnPagePropertyChanged;
                if (!string.IsNullOrEmpty(bm.FilePath) && !bm.HasThumbnail && !bm.IsThumbnailLoading)
                    ThumbnailDecodeScheduler.Instance.EnqueueBookmark(bm, bm.FilePath, DispatcherQueue);
            }

            StartThumbnailAutoRefresh();
            RedrawAllOcr();
        }

        private void UnhookVm(MangaViewModel? vm)
        {
            if (vm == null) return;

            vm.PageViewChanged -= OnVmRedrawRequested;
            vm.OcrCompleted -= OnVmRedrawRequested;
            vm.PageSlideRequested -= OnPageSlideRequested;
            vm.MangaFolderLoaded -= OnMangaFolderLoaded;
            vm.PropertyChanged -= OnViewModelPropertyChanged;

            if (vm.Thumbnails is INotifyCollectionChanged incc)
                incc.CollectionChanged -= OnThumbsChanged;

            foreach (var p in vm.Thumbnails)
                p.PropertyChanged -= OnPagePropertyChanged;

            if (vm.Bookmarks is INotifyCollectionChanged bincc)
                bincc.CollectionChanged -= OnBookmarksChanged;

            foreach (var b in vm.Bookmarks)
                b.PropertyChanged -= OnPagePropertyChanged;
        }

        private void OnVmRedrawRequested(object? sender, EventArgs e) => RedrawAllOcr();

        private void OnMangaFolderLoaded(object? sender, EventArgs e) => SetFocusAfterDelay();

        private void OnBookmarksChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
            {
                foreach (var item in e.NewItems)
                {
                    if (item is MangaPageViewModel m)
                    {
                        m.PropertyChanged -= OnPagePropertyChanged;
                        m.PropertyChanged += OnPagePropertyChanged;
                        if (!string.IsNullOrEmpty(m.FilePath) && !m.HasThumbnail && !m.IsThumbnailLoading)
                            ThumbnailDecodeScheduler.Instance.EnqueueBookmark(m, m.FilePath, DispatcherQueue);
                    }
                }
            }

            if (e.OldItems != null)
            {
                foreach (var item in e.OldItems)
                {
                    if (item is MangaPageViewModel m)
                        m.PropertyChanged -= OnPagePropertyChanged;
                }
            }
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is not MangaViewModel vm) return;

            if (e.PropertyName == nameof(MangaViewModel.IsPaneOpen))
            {
                if (_suppressPersistDuringResize) return;

                _preferredPaneOpen = vm.IsPaneOpen;
                try { SettingsProvider.Set("ReaderPaneOpenPreferred", _preferredPaneOpen.Value); } catch { }
                return;
            }

            if (e.PropertyName == nameof(MangaViewModel.SelectedThumbnailIndex))
                RebuildViewportThumbnailQueue(radius: 48);
        }

        private void HookThumbScrollViewer()
        {
            try
            {
                if (ThumbnailsList != null && _thumbScrollViewer == null)
                {
                    _thumbScrollViewer = FindDescendant<ScrollViewer>(ThumbnailsList);
                    if (_thumbScrollViewer != null)
                    {
                        _thumbScrollViewer.ViewChanged -= OnThumbsViewChanged;
                        _thumbScrollViewer.ViewChanged += OnThumbsViewChanged;
                    }
                }
            }
            catch { }
        }

        private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null) return null;
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T t) return t;
                var r = FindDescendant<T>(child);
                if (r != null) return r;
            }
            return null;
        }

        private bool TryGetRealizedIndexRange(out int minIndex, out int maxIndex)
        {
            minIndex = int.MaxValue;
            maxIndex = -1;

            if (ThumbnailsList?.ItemsPanelRoot is not Panel panel || panel.Children.Count == 0)
                return false;

            for (int i = 0; i < panel.Children.Count; i++)
            {
                if (panel.Children[i] is not ListViewItem lvi) continue;
                int idx = ThumbnailsList.IndexFromContainer(lvi);
                if (idx < 0) continue;
                if (idx < minIndex) minIndex = idx;
                if (idx > maxIndex) maxIndex = idx;
            }

            return minIndex != int.MaxValue && maxIndex >= 0;
        }

        private int GetCurrentViewportPivot()
        {
            if (!TryGetRealizedIndexRange(out int minIndex, out int maxIndex))
                return ViewModel?.SelectedThumbnailIndex ?? -1;

            return (minIndex + maxIndex) / 2;
        }

        private void StartThumbnailAutoRefresh()
        {
            _thumbRefreshUiTimer?.Stop();

            _thumbRefreshUiTimer = DispatcherQueue.CreateTimer();
            _thumbRefreshUiTimer.Interval = TimeSpan.FromMilliseconds(400);
            _thumbRefreshUiTimer.IsRepeating = true;
            _thumbRefreshUiTimer.Tick += (_, __) =>
            {
                if (_isUnloaded) return;
                try { UpdatePriorityByViewport(); KickVisibleThumbnailDecode(); } catch { }
            };
            _thumbRefreshUiTimer.Start();
        }

        private void OnThumbsViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
        {
            if (_isUnloaded) return;
            _lastScrollTime = DateTime.Now;
            UpdatePriorityByViewport();
            KickVisibleThumbnailDecode();
            if (!e.IsIntermediate)
                RebuildViewportThumbnailQueue(radius: _prefetchRadiusIdle);
        }

        private void UpdatePriorityByViewport()
        {
            if (ThumbnailsList == null || ViewModel == null) return;
            if (!TryGetRealizedIndexRange(out int minIndex, out int maxIndex)) return;

            int pivot = (minIndex + maxIndex) / 2;
            ThumbnailDecodeScheduler.Instance.UpdateSelectedIndex(pivot);

            int radius = (DateTime.Now - _lastScrollTime).TotalMilliseconds > 500 ? _prefetchRadiusIdle : _prefetchRadius;
            int start = Math.Max(0, pivot - radius);
            int end = Math.Min(ViewModel.Thumbnails.Count - 1, pivot + radius);

            for (int i = start; i <= end; i++)
            {
                var vm = ViewModel.Thumbnails[i];
                if (vm.FilePath != null && !vm.HasThumbnail && !vm.IsThumbnailLoading)
                    ThumbnailDecodeScheduler.Instance.Enqueue(vm, vm.FilePath, i, pivot, DispatcherQueue);
            }
        }

        private void KickVisibleThumbnailDecode()
        {
            if (_isUnloaded) return;
            if (ThumbnailsList == null || ViewModel == null || ViewModel.Thumbnails.Count == 0) return;
            if (!DispatcherQueue.HasThreadAccess) return;

            var panel = ThumbnailsList.ItemsPanelRoot as Panel;
            if (panel == null || panel.Children.Count == 0) return;

            int pivot = GetCurrentViewportPivot();
            ThumbnailDecodeScheduler.Instance.UpdateSelectedIndex(pivot);

            for (int i = 0; i < panel.Children.Count; i++)
            {
                if (panel.Children[i] is not ListViewItem lvi) continue;

                int index = ThumbnailsList.IndexFromContainer(lvi);
                if (index < 0 || index >= ViewModel.Thumbnails.Count) continue;

                var vm = ViewModel.Thumbnails[index];
                if (vm.FilePath != null && !vm.HasThumbnail && !vm.IsThumbnailLoading)
                    ThumbnailDecodeScheduler.Instance.Enqueue(vm, vm.FilePath, index, pivot, DispatcherQueue);
            }
        }

        private void OnThumbsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
            {
                foreach (var item in e.NewItems)
                {
                    if (item is MangaPageViewModel m) SubscribePage(m);
                }
            }
            RebuildViewportThumbnailQueue(radius: 48);
        }

        private void SubscribePage(MangaPageViewModel page)
        {
            page.PropertyChanged -= OnPagePropertyChanged;
            page.PropertyChanged += OnPagePropertyChanged;
        }

        private void OnPagePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is not MangaPageViewModel vm) return;
            if (e.PropertyName != nameof(MangaPageViewModel.FilePath)) return;

            if (string.IsNullOrEmpty(vm.FilePath) || ViewModel == null) return;

            int index = ViewModel.Thumbnails.IndexOf(vm);
            if (index < 0) return;

            int pivot = GetCurrentViewportPivot();
            ThumbnailDecodeScheduler.Instance.UpdateSelectedIndex(pivot);
            ThumbnailDecodeScheduler.Instance.Enqueue(vm, vm.FilePath, index, pivot, DispatcherQueue);
            KickVisibleThumbnailDecode();
        }

        private void ThumbnailsList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (ViewModel == null) return;
            if (args.Item is not MangaPageViewModel vm) return;

            int index = sender.IndexFromContainer(args.ItemContainer);
            if (args.InRecycleQueue)
            {
                if (index != ViewModel.SelectedThumbnailIndex) vm.UnloadThumbnail();
                return;
            }

            var path = vm.FilePath ?? string.Empty;
            int pivot = GetCurrentViewportPivot();
            ThumbnailDecodeScheduler.Instance.UpdateSelectedIndex(pivot);
            ThumbnailDecodeScheduler.Instance.Enqueue(vm, path, index, pivot, DispatcherQueue);
        }

        private void BookmarksList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.Item is not MangaPageViewModel vm) return;
            if (args.InRecycleQueue)
            {
                vm.UnloadThumbnail();
                return;
            }

            string path = vm.FilePath ?? string.Empty;
            ThumbnailDecodeScheduler.Instance.EnqueueBookmark(vm, path, DispatcherQueue);
        }

        private void BookmarksList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is MangaPageViewModel vm && DataContext is MangaViewModel mvm)
                mvm.NavigateToBookmarkCommand.Execute(vm);
        }

        private void RemoveBookmark_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is MangaPageViewModel vm && DataContext is MangaViewModel mvm)
                mvm.RemoveBookmarkCommand.Execute(vm);
        }

        private void OnLeftOcrContainerSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (ViewModel != null)
            {
                ViewModel.UpdateLeftOcrContainerSize(e.NewSize.Width, e.NewSize.Height);
                _viewportRedecodeTimer?.Stop();
                _viewportRedecodeTimer?.Start();
            }
            RedrawLeft();
        }

        private void OnRightOcrContainerSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (ViewModel != null)
            {
                ViewModel.UpdateRightOcrContainerSize(e.NewSize.Width, e.NewSize.Height);
                _viewportRedecodeTimer?.Stop();
                _viewportRedecodeTimer?.Start();
            }
            RedrawRight();
        }

        private void OnSingleOcrContainerSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (ViewModel != null)
            {
                ViewModel.UpdateSingleOcrContainerSize(e.NewSize.Width, e.NewSize.Height);
                _viewportRedecodeTimer?.Stop();
                _viewportRedecodeTimer?.Start();
            }
            RedrawSingle();
        }

        private void ReaderSplitView_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // Debounce resize and avoid persisting control-driven auto-close.
            _suppressPersistDuringResize = true;

            try
            {
                if (ViewModel != null && this.XamlRoot != null)
                    ViewModel.UpdateRasterizationScale(this.XamlRoot.RasterizationScale);
            }
            catch { }

            // If preferred to be open, force it open immediately during resizing
            if (_preferredPaneOpen == true)
            {
                if (!ReaderSplitView.IsPaneOpen)
                    ReaderSplitView.IsPaneOpen = true;
                if (ViewModel != null && !ViewModel.IsPaneOpen)
                    ViewModel.IsPaneOpen = true; // will be ignored for persistence while suppress flag is set
            }

            _resizeDebounceTimer?.Stop();
            _resizeDebounceTimer?.Start();

            // trigger a viewport-fit re-decode after layout settles
            _viewportRedecodeTimer?.Stop();
            _viewportRedecodeTimer?.Start();
        }

        private void RebuildViewportThumbnailQueue(int radius = 48)
        {
            if (_isUnloaded || ThumbnailsList == null || ViewModel == null || ViewModel.Thumbnails.Count == 0) return;
            if (!TryGetRealizedIndexRange(out int minIndex, out int maxIndex)) return;

            int pivot = (minIndex + maxIndex) / 2;

            var seeds = new List<(MangaPageViewModel Vm, string Path, int Index)>();
            int count = ViewModel.Thumbnails.Count;

            void TryAdd(int i)
            {
                if (i < 0 || i >= count) return;
                var vm = ViewModel.Thumbnails[i];
                var path = vm.FilePath;
                if (string.IsNullOrEmpty(path)) return;
                if (vm.HasThumbnail) return;
                seeds.Add((vm, path!, i));
            }

            // 중심부터 나선형으로 추가
            TryAdd(pivot);
            for (int step = 1; step <= radius; step++)
            {
                TryAdd(pivot - step);
                TryAdd(pivot + step);
            }

            // 대기열 교체(거리 > 200 항목은 스케줄러에서 처리)
            ThumbnailDecodeScheduler.Instance.ReplacePendingWithViewportFirst(seeds, pivot, DispatcherQueue);
        }

        private void BookmarkPaneResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
        {
            if (BookmarksList?.Parent is Grid g)
            {
                double cur = g.ColumnDefinitions[1].Width.Value;
                double newLen = cur + e.HorizontalChange;
                newLen = Math.Clamp(newLen, BookmarkPaneMinWidth, BookmarkPaneMaxWidth);
                g.ColumnDefinitions[1].Width = new GridLength(newLen);
                try { SettingsProvider.Set("BookmarkPaneWidth", newLen); } catch { }
            }
        }

        private void OnLeftImageSizeChanged(object sender, SizeChangedEventArgs e) => RedrawLeft();
        private void OnRightImageSizeChanged(object sender, SizeChangedEventArgs e) => RedrawRight();
        private void OnSingleImageSizeChanged(object sender, SizeChangedEventArgs e) => RedrawSingle();

        private void RedrawAllOcr()
        {
            RedrawSingle();
            RedrawLeft();
            RedrawRight();
        }

        private void RedrawOcr(Grid? wrapper, Canvas? overlay, IEnumerable<BoundingBoxViewModel> boxes, BitmapImage? imageSource, bool isLeft)
        {
            if (overlay == null) return;
            if (wrapper == null || imageSource == null)
            {
                overlay.Children.Clear();
                return;
            }

            (int baseW, int baseH) = GetBestMatchingBaseSize(wrapper, boxes, imageSource);
            if (baseW <= 0 || baseH <= 0)
            {
                overlay.Children.Clear();
                return;
            }

            DrawBoxes(wrapper, overlay, boxes, baseW, baseH, isLeft);
        }

        private static (int W, int H) GetBestMatchingBaseSize(Grid wrapper, IEnumerable<BoundingBoxViewModel> boxes, BitmapImage imageSource)
        {
            int imgW = imageSource.PixelWidth;
            int imgH = imageSource.PixelHeight;
            if (imgW <= 0 || imgH <= 0) return (0, 0);

            var first = boxes.FirstOrDefault();
            if (first == null)
                return (imgW, imgH);

            double wrapperW = wrapper.ActualWidth;
            double wrapperH = wrapper.ActualHeight;
            if (wrapperW <= 0 || wrapperH <= 0)
                return (imgW, imgH);

            double wrapperAspect = wrapperW / wrapperH;

            double aspectA = imgW / (double)imgH;
            double aspectB = imgH / (double)imgW;

            double da = Math.Abs(wrapperAspect - aspectA);
            double db = Math.Abs(wrapperAspect - aspectB);

            if (db + 0.02 < da)
                return (imgH, imgW);

            return (imgW, imgH);
        }

        private void RedrawSingle() => RedrawOcr(SingleWrapper, SingleOverlay, ViewModel.LeftOcrBoxes, ViewModel.LeftImageSource, true);
        private void RedrawLeft() => RedrawOcr(LeftWrapper, LeftOverlay, ViewModel.LeftOcrBoxes, ViewModel.LeftImageSource, true);
        private void RedrawRight() => RedrawOcr(RightWrapper, RightOverlay, ViewModel.RightOcrBoxes, ViewModel.RightImageSource, false);

        private void EnsureOcrBrushes()
        {
            if (_ocrLeftStrokeBrush != null) return;

            var leftStroke = Colors.Yellow;
            var leftFill = leftStroke; leftFill.A = 0x40;
            var rightStroke = Colors.DeepSkyBlue;
            var rightFill = rightStroke; rightFill.A = 0x40;

            _ocrLeftStrokeBrush = new SolidColorBrush(leftStroke);
            _ocrLeftFillBrush = new SolidColorBrush(leftFill);
            _ocrRightStrokeBrush = new SolidColorBrush(rightStroke);
            _ocrRightFillBrush = new SolidColorBrush(rightFill);
        }

        private void DrawBoxes(Grid wrapper, Canvas overlay, IEnumerable<BoundingBoxViewModel> boxes, int imgPixelW, int imgPixelH, bool isLeft)
        {
            foreach (var child in overlay.Children)
            {
                if (child is Rectangle r) _rectPool.Enqueue(r);
            }
            overlay.Children.Clear();

            double wrapperW = wrapper.ActualWidth;
            double wrapperH = wrapper.ActualHeight;
            if (wrapperW <= 0 || wrapperH <= 0 || imgPixelW <= 0 || imgPixelH <= 0) return;

            EnsureOcrBrushes();
            var strokeBrush = isLeft ? _ocrLeftStrokeBrush! : _ocrRightStrokeBrush!;
            var fillBrush = isLeft ? _ocrLeftFillBrush! : _ocrRightFillBrush!;

            double scale = Math.Min(wrapperW / imgPixelW, wrapperH / imgPixelH);
            double displayW = imgPixelW * scale;
            double displayH = imgPixelH * scale;
            double offsetX = (wrapperW - displayW) / 2.0;
            double offsetY = (wrapperH - displayH) / 2.0;

            foreach (var b in boxes)
            {
                double w = b.OriginalW * scale;
                double h = b.OriginalH * scale;
                if (w <= 0 || h <= 0) continue;

                var rect = _rectPool.Count > 0 ? _rectPool.Dequeue() : CreatePooledRectangle();
                rect.Width = w;
                rect.Height = h;
                rect.Tag = b;
                rect.Stroke = strokeBrush;
                rect.Fill = fillBrush;

                double x = offsetX + b.OriginalX * scale;
                double y = offsetY + b.OriginalY * scale;
                Canvas.SetLeft(rect, x);
                Canvas.SetTop(rect, y);
                overlay.Children.Add(rect);
            }
        }

        private Rectangle CreatePooledRectangle()
        {
            var rect = new Rectangle { StrokeThickness = 1 };
            rect.Tapped += OnOcrRectTapped;
            rect.PointerEntered += (_, __) => rect.Opacity = 0.85;
            rect.PointerExited += (_, __) => rect.Opacity = 1.0;
            return rect;
        }

        private void OnOcrRectTapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is Rectangle r && r.Tag is BoundingBoxViewModel vm && !string.IsNullOrWhiteSpace(vm.Text))
            {
                try
                {
                    _clipboard.SetText(vm.Text);
                    r.StrokeThickness = 2;
                    _ = DispatcherQueue.TryEnqueue(async () =>
                    {
                        await System.Threading.Tasks.Task.Delay(300);
                        r.StrokeThickness = 1;
                    });
                }
                catch { }
            }
        }

        private void OnPageSlideRequested(object? sender, int delta)
        {
            if (PageTranslate == null || PageSlideHost == null) return;
            double width = PageSlideHost.ActualWidth;
            if (width <= 0) return;

            _pageSlideStoryboard?.Stop();

            bool rtl = ViewModel?.DirectionButtonText?.Contains("역방향") == true;
            int direction = delta > 0 ? 1 : -1;
            if (rtl) direction *= -1;

            double from = direction * width;
            PageTranslate.X = from;

            var anim = new DoubleAnimation
            {
                From = from,
                To = 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(220)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            _pageSlideStoryboard = new Storyboard();
            Storyboard.SetTarget(anim, PageTranslate);
            Storyboard.SetTargetProperty(anim, "X");
            _pageSlideStoryboard.Children.Add(anim);
            _pageSlideStoryboard.Begin();
        }

        private void PaneResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
        {
            double newLen = ReaderSplitView.OpenPaneLength - e.HorizontalChange;
            newLen = Math.Clamp(newLen, PaneMinWidth, PaneMaxWidth);
            ReaderSplitView.OpenPaneLength = newLen;
            try { SettingsProvider.Set("ReaderPaneWidth", newLen); } catch { }
        }

        private void PaneResizeThumb_DragCompleted(object sender, DragCompletedEventArgs e) { }
    }
}
