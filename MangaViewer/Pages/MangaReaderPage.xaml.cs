using MangaViewer.ViewModels;
using Microsoft.UI; // Colors
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Diagnostics;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Microsoft.UI.Xaml.Input; // Tapped
using Microsoft.UI.Xaml.Controls.Primitives; // DragDeltaEventArgs
using MangaViewer.Services.Thumbnails;
using System.Collections.Generic;
using Microsoft.UI.Xaml.Media.Imaging;
using MangaViewer.Services;
using Windows.Foundation;

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
        private SolidColorBrush? _ocrTranslatedFillBrush;
        private SolidColorBrush? _ocrTranslatedTextBrush;

        private readonly record struct OverlayPlacement(Rect Rect, double FontSize);

        public MangaReaderPage()
        {
            InitializeComponent();
            Loaded += MangaReaderPage_Loaded;
            Unloaded += MangaReaderPage_Unloaded;
            TranslationSettingsService.Instance.SettingsChanged += OnTranslationSettingsChanged;

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

            TranslationSettingsService.Instance.SettingsChanged -= OnTranslationSettingsChanged;

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

        private void OnTranslationSettingsChanged(object? sender, EventArgs e)
        {
            if (_isUnloaded) return;
            _ = DispatcherQueue.TryEnqueue(RedrawAllOcr);
        }

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
            {
                RebuildViewportThumbnailQueue(radius: 48);
                return;
            }

            if (e.PropertyName == nameof(MangaViewModel.LeftImageSource)
                || e.PropertyName == nameof(MangaViewModel.RightImageSource)
                || e.PropertyName == nameof(MangaViewModel.IsSinglePageMode)
                || e.PropertyName == nameof(MangaViewModel.IsTwoPageMode))
            {
                RedrawAllOcr();
            }
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

        // OCR 렌더링 진입점.
        // 단일/좌/우 캔버스를 모두 최신 OCR 상태로 다시 그린다.
        private void RedrawAllOcr()
        {
            RedrawSingle();
            RedrawLeft();
            RedrawRight();
        }

        // OCR 박스 좌표(원본 픽셀 기준)를 현재 wrapper 크기에 맞게 매핑한 뒤,
        // 일반 박스 또는 번역 오버레이를 그리도록 파이프라인을 시작한다.
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

        // OCR 결과의 좌표계(가로/세로)가 표시된 이미지와 다를 수 있으므로,
        // wrapper 종횡비와 비교해 가장 맞는 기준 크기(정방향/회전)를 선택한다.
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
            _ocrTranslatedFillBrush = new SolidColorBrush(ColorHelper.FromArgb(0xF0, 0xFF, 0xFF, 0xFF));
            _ocrTranslatedTextBrush = new SolidColorBrush(Colors.Black);
        }

        private void DrawBoxes(Grid wrapper, Canvas overlay, IEnumerable<BoundingBoxViewModel> boxes, int imgPixelW, int imgPixelH, bool isLeft)
        {
            // [OCR 렌더링 구조]
            // 1) 원본 OCR 박스를 현재 화면 좌표로 변환
            // 2) 번역 표시 모드면 그룹화/배치/글자 크기 계산 후 Border 오버레이 생성
            // 3) 일반 모드면 Rectangle 풀을 재사용해 원본 박스 렌더링
            var boxList = boxes as IList<BoundingBoxViewModel> ?? boxes.ToList();

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
            bool useTranslationOverlay = ViewModel?.IsTranslationActive == true
                && boxList.Any(x => !string.IsNullOrWhiteSpace(x.TranslatedText));
            bool isHybridTranslationOverlay = useTranslationOverlay
                && OcrService.Instance.Backend == OcrService.OcrBackend.Hybrid;

            double scale = Math.Min(wrapperW / imgPixelW, wrapperH / imgPixelH);
            double displayW = imgPixelW * scale;
            double displayH = imgPixelH * scale;
            double offsetX = (wrapperW - displayW) / 2.0;
            double offsetY = (wrapperH - displayH) / 2.0;
            var imageBounds = new Rect(offsetX, offsetY, displayW, displayH);

            var sourceRects = new List<Rect>(boxList.Count);
            foreach (var b in boxList)
            {
                double w = b.OriginalW * scale;
                double h = b.OriginalH * scale;
                if (w <= 0 || h <= 0)
                {
                    sourceRects.Add(new Rect());
                    continue;
                }

                double x = offsetX + b.OriginalX * scale;
                double y = offsetY + b.OriginalY * scale;
                sourceRects.Add(new Rect(x, y, w, h));
            }

            // Hybrid 백엔드는 박스 단위 번역의 가독성이 좋아 병합을 하지 않고,
            // 그 외 모드는 인접 박스를 병합해 번역 오버레이 수를 줄인다.
            var overlayGroups = BuildOverlayGroups(boxList, sourceRects, mergeAdjacent: !isHybridTranslationOverlay);

            if (useTranslationOverlay)
            {
                double preferredOverlayFontSize = TranslationSettingsService.Instance.OverlayFontSize;
                double preferredOverlayBoxScaleHorizontal = TranslationSettingsService.Instance.OverlayBoxScaleHorizontal;
                double preferredOverlayBoxScaleVertical = TranslationSettingsService.Instance.OverlayBoxScaleVertical;
                var groupRects = overlayGroups.Select(g => g.Rect).ToList();
                for (int i = 0; i < overlayGroups.Count; i++)
                {
                    var group = overlayGroups[i];
                    string overlayText = group.OverlayText;
                    // Hybrid: 라인 수가 아닌 박스 크기 + 텍스트 길이 기반으로 폰트 계산.
                    // Non-hybrid: 주변 밀도/영역을 고려해 오버레이 크기와 폰트를 계산.
                    var placement = isHybridTranslationOverlay
                        ? BuildHybridOverlayPlacement(group, overlayText, preferredOverlayFontSize, preferredOverlayBoxScaleHorizontal, preferredOverlayBoxScaleVertical, imageBounds)
                        : ComputeOverlayPlacement(i, overlayText, group.Rect, groupRects, imageBounds, preferredOverlayFontSize, preferredOverlayBoxScaleHorizontal, preferredOverlayBoxScaleVertical);
                    var constrainedRect = placement.Rect;

                    var border = new Border
                    {
                        Width = constrainedRect.Width,
                        Height = constrainedRect.Height,
                        Tag = group.TagBox,
                        Background = _ocrTranslatedFillBrush,
                        BorderBrush = strokeBrush,
                        BorderThickness = new Thickness(1),
                        Child = new ScrollViewer
                        {
                            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                            Content = new TextBlock
                            {
                                Text = overlayText,
                                TextWrapping = TextWrapping.Wrap,
                                Foreground = _ocrTranslatedTextBrush,
                                Margin = new Thickness(4, 2, 4, 2),
                                FontSize = placement.FontSize
                            }
                        }
                    };
                    border.Tapped += OnOcrOverlayTapped;
                    border.PointerEntered += (_, __) => border.Opacity = 0.85;
                    border.PointerExited += (_, __) => border.Opacity = 1.0;
                    Canvas.SetLeft(border, constrainedRect.X);
                    Canvas.SetTop(border, constrainedRect.Y);
                    overlay.Children.Add(border);
                }

                return;
            }

            for (int i = 0; i < boxList.Count; i++)
            {
                var b = boxList[i];
                var sourceRect = sourceRects[i];
                if (sourceRect.Width <= 0 || sourceRect.Height <= 0) continue;

                var rect = _rectPool.Count > 0 ? _rectPool.Dequeue() : CreatePooledRectangle();
                rect.Width = sourceRect.Width;
                rect.Height = sourceRect.Height;
                rect.Tag = b;
                rect.Stroke = strokeBrush;
                rect.Fill = fillBrush;
                Canvas.SetLeft(rect, sourceRect.X);
                Canvas.SetTop(rect, sourceRect.Y);
                overlay.Children.Add(rect);
            }
        }

        // Hybrid 전용 배치: 원본 박스를 사용자 스케일로 확장/축소하고
        // 텍스트 길이와 박스 면적을 기준으로 폰트를 산출한다.
        private static OverlayPlacement BuildHybridOverlayPlacement(
            (Rect Rect, string OverlayText, BoundingBoxViewModel TagBox) group,
            string overlayText,
            double preferredOverlayFontSize,
            double preferredOverlayBoxScaleHorizontal,
            double preferredOverlayBoxScaleVertical,
            Rect imageBounds)
        {
            var rect = ScaleRectAroundCenter(group.Rect, preferredOverlayBoxScaleHorizontal, preferredOverlayBoxScaleVertical, imageBounds);
            double fontSize = ComputeHybridOverlayFontSize(
                overlayText,
                preferredOverlayFontSize,
                rect.Width,
                rect.Height);

            return new OverlayPlacement(rect, fontSize);
        }

        // Project rule: Hybrid 폰트 계산은 줄 수(line count)보다
        // 번역 텍스트 길이 + 오버레이 박스 크기에 우선순위를 둔다.
        private static double ComputeHybridOverlayFontSize(
            string overlayText,
            double preferredOverlayFontSize,
            double overlayWidth,
            double overlayHeight)
        {
            double basePreferred = Math.Clamp(preferredOverlayFontSize, 8, 28);
            string normalized = string.IsNullOrWhiteSpace(overlayText)
                ? " "
                : overlayText.Replace("\r", string.Empty).Replace("\n", string.Empty).Trim();
            int textLength = Math.Max(1, normalized.Length);

            double width = Math.Max(32, overlayWidth);
            double height = Math.Max(24, overlayHeight);
            double area = width * height;

            double fontFromArea = Math.Sqrt(area / textLength) * 1.05;
            double targetCharsPerLine = Math.Clamp(Math.Sqrt(textLength) * 1.8, 6, 26);
            double fontFromWidth = (width - 10) / Math.Max(1, targetCharsPerLine * 0.58);
            double fitByHeight = Math.Max(8, overlayHeight * 0.72);

            double raw = Math.Min(fontFromArea, fontFromWidth);
            double preferredScale = basePreferred / 14.0;
            double scaled = raw * preferredScale;
            return Math.Clamp(scaled, 8, Math.Min(40, fitByHeight));
        }

        // 번역 오버레이 그룹 생성.
        // - mergeAdjacent=false: 박스 1개 = 오버레이 1개
        // - mergeAdjacent=true: 인접/겹침 박스를 연결요소로 병합해 오버레이 수를 줄임
        private static List<(Rect Rect, string OverlayText, BoundingBoxViewModel TagBox)> BuildOverlayGroups(
            IList<BoundingBoxViewModel> boxes,
            IReadOnlyList<Rect> sourceRects,
            bool mergeAdjacent)
        {
            if (!mergeAdjacent)
            {
                var singleGroups = new List<(Rect Rect, string OverlayText, BoundingBoxViewModel TagBox)>(boxes.Count);
                for (int i = 0; i < boxes.Count; i++)
                {
                    if (sourceRects[i].Width <= 0 || sourceRects[i].Height <= 0)
                        continue;

                    string overlayText = !string.IsNullOrWhiteSpace(boxes[i].TranslatedText)
                        ? boxes[i].TranslatedText
                        : boxes[i].Text;
                    singleGroups.Add((sourceRects[i], overlayText, boxes[i]));
                }

                return singleGroups;
            }

            var visited = new bool[boxes.Count];
            var groups = new List<(Rect Rect, string OverlayText, BoundingBoxViewModel TagBox)>();

            for (int i = 0; i < boxes.Count; i++)
            {
                if (visited[i]) continue;
                if (sourceRects[i].Width <= 0 || sourceRects[i].Height <= 0)
                {
                    visited[i] = true;
                    continue;
                }

                var stack = new Stack<int>();
                var memberIndexes = new List<int>();
                stack.Push(i);
                visited[i] = true;

                while (stack.Count > 0)
                {
                    int current = stack.Pop();
                    memberIndexes.Add(current);

                    for (int j = 0; j < boxes.Count; j++)
                    {
                        if (visited[j]) continue;
                        if (sourceRects[j].Width <= 0 || sourceRects[j].Height <= 0) continue;
                        if (!ShouldMergeOverlayRects(sourceRects[current], sourceRects[j])) continue;
                        visited[j] = true;
                        stack.Push(j);
                    }
                }

                memberIndexes.Sort();
                Rect mergedRect = sourceRects[memberIndexes[0]];
                foreach (int memberIndex in memberIndexes.Skip(1))
                    mergedRect = UnionRect(mergedRect, sourceRects[memberIndex]);

                string translated = memberIndexes
                    .Select(idx => boxes[idx].TranslatedText)
                    .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)) ?? string.Empty;
                string fallback = string.Join(Environment.NewLine, memberIndexes.Select(idx => boxes[idx].Text).Where(text => !string.IsNullOrWhiteSpace(text)));
                string overlayText = string.IsNullOrWhiteSpace(translated) ? fallback : translated;

                groups.Add((mergedRect, overlayText, boxes[memberIndexes[0]]));
            }

            return groups;
        }

        private static Rect UnionRect(Rect a, Rect b)
        {
            double x1 = Math.Min(a.X, b.X);
            double y1 = Math.Min(a.Y, b.Y);
            double x2 = Math.Max(a.X + a.Width, b.X + b.Width);
            double y2 = Math.Max(a.Y + a.Height, b.Y + b.Height);
            return new Rect(x1, y1, Math.Max(0, x2 - x1), Math.Max(0, y2 - y1));
        }

        private static Rect ConstrainPlacementNearSource(Rect candidate, Rect sourceRect, Rect imageBounds)
        {
            var zone = GetPlacementZoneBounds(sourceRect, imageBounds);

            double zoneX1 = zone.X;
            double zoneY1 = zone.Y;
            double zoneW = zone.Width;
            double zoneH = zone.Height;

            double width = Math.Min(candidate.Width, zoneW);
            double height = Math.Min(candidate.Height, zoneH);

            double x = Math.Clamp(candidate.X, zoneX1, zoneX1 + zoneW - width);
            double y = Math.Clamp(candidate.Y, zoneY1, zoneY1 + zoneH - height);

            return new Rect(x, y, width, height);
        }

        // 일반(Non-hybrid) 번역 오버레이 배치 계산.
        // 주변 박스 밀도와 배치 영역을 이용해 폭/높이를 구하고,
        // 텍스트 래핑을 고려해 폰트를 단계적으로 줄여가며 맞춘다.
        private static OverlayPlacement ComputeOverlayPlacement(
            int index,
            string text,
            Rect sourceRect,
            IReadOnlyList<Rect> allSourceRects,
            Rect imageBounds,
            double preferredFontSize,
            double preferredHorizontalScale,
            double preferredVerticalScale)
        {
            string normalizedText = string.IsNullOrWhiteSpace(text) ? " " : text.Trim();
            int textLength = Math.Max(1, normalizedText.Replace("\r", string.Empty).Replace("\n", string.Empty).Length);
            double density = EstimateLocalDensity(index, sourceRect, allSourceRects);
            var placementZone = GetPlacementZoneBounds(sourceRect, imageBounds);

            double minW = Math.Max(56, Math.Min(placementZone.Width, sourceRect.Width * (density > 0.8 ? 0.72 : 0.86)));
            double maxW = Math.Max(minW + 8, Math.Min(placementZone.Width, Math.Min(imageBounds.Width * (density > 0.8 ? 0.42 : 0.56), sourceRect.Width * 2.2 + 140)));
            double minH = Math.Max(26, Math.Min(placementZone.Height, sourceRect.Height * 0.7));
            double maxH = Math.Max(minH + 8, Math.Min(placementZone.Height, Math.Max(sourceRect.Height * 2.2, 260)));

            double fontSize = Math.Clamp(preferredFontSize, 8, 28);
            double desiredW = minW;
            double desiredH = minH;

            int explicitLineCount = Math.Max(1, normalizedText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries).Length);
            const double horizontalPadding = 12;
            const double verticalPadding = 10;
            const double charWidthFactor = 0.58;
            const double lineHeightFactor = 1.34;

            for (int attempt = 0; attempt < 7; attempt++)
            {
                double widthByText = (fontSize * charWidthFactor * Math.Clamp(textLength, 8, 28)) + horizontalPadding;
                desiredW = Math.Clamp(widthByText, minW, maxW);

                int charsPerLine = Math.Max(1, (int)Math.Floor((desiredW - horizontalPadding) / Math.Max(1, fontSize * charWidthFactor)));
                int wrappedLineCount = Math.Max(explicitLineCount, (int)Math.Ceiling(textLength / (double)charsPerLine));
                double heightByText = (wrappedLineCount * fontSize * lineHeightFactor) + verticalPadding;
                desiredH = Math.Clamp(heightByText, minH, maxH);

                bool fitsHeight = heightByText <= maxH + 0.5;
                if (fitsHeight || fontSize <= 8.2)
                    break;

                fontSize = Math.Max(8, fontSize - 0.7);
            }

            if (desiredH >= maxH - 0.5)
                fontSize = Math.Max(8, fontSize - 0.6);

            double horizontalScale = Math.Clamp(preferredHorizontalScale, 0.6, 2.2);
            double verticalScale = Math.Clamp(preferredVerticalScale, 0.6, 2.2);
            desiredW = Math.Min(imageBounds.Width, Math.Max(32, desiredW * horizontalScale));
            desiredH = Math.Min(imageBounds.Height, Math.Max(24, desiredH * verticalScale));

            var centeredRect = CreateCenteredRect(sourceRect, desiredW, desiredH);
            return new OverlayPlacement(FitInside(centeredRect, imageBounds), fontSize);
        }

        private static Rect CreateCenteredRect(Rect sourceRect, double width, double height)
        {
            double centerX = sourceRect.X + sourceRect.Width / 2.0;
            double centerY = sourceRect.Y + sourceRect.Height / 2.0;
            return new Rect(centerX - width / 2.0, centerY - height / 2.0, width, height);
        }

        private static Rect ScaleRectAroundCenter(Rect sourceRect, double horizontalScale, double verticalScale, Rect imageBounds)
        {
            double clampedHorizontalScale = Math.Clamp(horizontalScale, 0.6, 2.2);
            double clampedVerticalScale = Math.Clamp(verticalScale, 0.6, 2.2);
            double width = Math.Min(imageBounds.Width, Math.Max(32, sourceRect.Width * clampedHorizontalScale));
            double height = Math.Min(imageBounds.Height, Math.Max(24, sourceRect.Height * clampedVerticalScale));
            return FitInside(CreateCenteredRect(sourceRect, width, height), imageBounds);
        }

        private static Rect GetPlacementZoneBounds(Rect sourceRect, Rect imageBounds)
        {
            double marginX = Math.Clamp(sourceRect.Width * 0.35, 12, 80);
            double marginY = Math.Clamp(sourceRect.Height * 0.40, 12, 96);

            double zoneX1 = Math.Max(imageBounds.X, sourceRect.X - marginX);
            double zoneY1 = Math.Max(imageBounds.Y, sourceRect.Y - marginY);
            double zoneX2 = Math.Min(imageBounds.X + imageBounds.Width, sourceRect.X + sourceRect.Width + marginX);
            double zoneY2 = Math.Min(imageBounds.Y + imageBounds.Height, sourceRect.Y + sourceRect.Height + marginY);

            return new Rect(zoneX1, zoneY1, Math.Max(24, zoneX2 - zoneX1), Math.Max(20, zoneY2 - zoneY1));
        }

        private static double EstimateLocalDensity(int index, Rect sourceRect, IReadOnlyList<Rect> allSourceRects)
        {
            if (sourceRect.Width <= 0 || sourceRect.Height <= 0) return 0;

            double radius = Math.Max(sourceRect.Width, sourceRect.Height) * 1.8;
            double cx = sourceRect.X + sourceRect.Width / 2.0;
            double cy = sourceRect.Y + sourceRect.Height / 2.0;
            double density = 0;

            for (int i = 0; i < allSourceRects.Count; i++)
            {
                if (i == index) continue;
                var other = allSourceRects[i];
                if (other.Width <= 0 || other.Height <= 0) continue;

                double ocx = other.X + other.Width / 2.0;
                double ocy = other.Y + other.Height / 2.0;
                double dist = Math.Sqrt((ocx - cx) * (ocx - cx) + (ocy - cy) * (ocy - cy));
                if (dist > radius) continue;

                density += 1.0 - (dist / Math.Max(1, radius));
            }

            return Math.Clamp(density / 4.0, 0, 1.6);
        }

        private static Rect FitInside(Rect rect, Rect bounds)
        {
            double width = Math.Clamp(rect.Width, 32, bounds.Width);
            double height = Math.Clamp(rect.Height, 24, bounds.Height);

            double x = Math.Clamp(rect.X, bounds.X, bounds.X + bounds.Width - width);
            double y = Math.Clamp(rect.Y, bounds.Y, bounds.Y + bounds.Height - height);
            return new Rect(x, y, width, height);
        }

        private static double ScorePlacement(
            Rect candidate,
            Rect sourceRect,
            int sourceIndex,
            IReadOnlyList<Rect> allSourceRects,
            IReadOnlyList<Rect> placedRects,
            Rect bounds)
        {
            double score = 0;

            for (int i = 0; i < allSourceRects.Count; i++)
            {
                if (i == sourceIndex) continue;
                var other = allSourceRects[i];
                if (other.Width <= 0 || other.Height <= 0) continue;
                double overlap = IntersectionArea(candidate, other);
                if (overlap > 0)
                    score += overlap * 4.8;
            }

            foreach (var placed in placedRects)
            {
                double overlap = IntersectionArea(candidate, placed);
                if (overlap > 0)
                    score += overlap * 7.2;
            }

            double gapX = CenterDistanceX(candidate, sourceRect);
            double gapY = CenterDistanceY(candidate, sourceRect);
            score += (gapX + gapY) * 0.35;

            double marginLeft = candidate.X - bounds.X;
            double marginTop = candidate.Y - bounds.Y;
            double marginRight = (bounds.X + bounds.Width) - (candidate.X + candidate.Width);
            double marginBottom = (bounds.Y + bounds.Height) - (candidate.Y + candidate.Height);
            double edgePenalty = 0;

            if (marginLeft < 6) edgePenalty += 18 - (marginLeft * 2);
            if (marginTop < 6) edgePenalty += 18 - (marginTop * 2);
            if (marginRight < 6) edgePenalty += 18 - (marginRight * 2);
            if (marginBottom < 6) edgePenalty += 18 - (marginBottom * 2);

            score += Math.Max(0, edgePenalty);

            return score;
        }

        private static double CenterDistanceX(Rect a, Rect b)
            => Math.Abs((a.X + a.Width / 2.0) - (b.X + b.Width / 2.0));

        private static double CenterDistanceY(Rect a, Rect b)
            => Math.Abs((a.Y + a.Height / 2.0) - (b.Y + b.Height / 2.0));

        private static bool ShouldMergeOverlayRects(Rect a, Rect b)
        {
            if (a.Width <= 0 || a.Height <= 0 || b.Width <= 0 || b.Height <= 0)
                return false;

            if (IntersectionArea(a, b) > 0)
                return true;

            double horizontalGap = AxisGap(a.X, a.X + a.Width, b.X, b.X + b.Width);
            double verticalGap = AxisGap(a.Y, a.Y + a.Height, b.Y, b.Y + b.Height);
            double verticalOverlap = AxisOverlap(a.Y, a.Y + a.Height, b.Y, b.Y + b.Height);
            double horizontalOverlap = AxisOverlap(a.X, a.X + a.Width, b.X, b.X + b.Width);

            double minHeight = Math.Max(1, Math.Min(a.Height, b.Height));
            double minWidth = Math.Max(1, Math.Min(a.Width, b.Width));
            double verticalOverlapRatio = verticalOverlap / minHeight;
            double horizontalOverlapRatio = horizontalOverlap / minWidth;

            double horizontalGapThreshold = Math.Clamp(minWidth * 0.25, 2, 18);
            double verticalGapThreshold = Math.Clamp(minHeight * 0.25, 2, 18);

            bool sideBySideAdjacent = horizontalGap <= horizontalGapThreshold && verticalOverlapRatio >= 0.55;
            bool stackedAdjacent = verticalGap <= verticalGapThreshold && horizontalOverlapRatio >= 0.55;
            return sideBySideAdjacent || stackedAdjacent;
        }

        private static double AxisOverlap(double a1, double a2, double b1, double b2)
            => Math.Max(0, Math.Min(a2, b2) - Math.Max(a1, b1));

        private static double AxisGap(double a1, double a2, double b1, double b2)
            => Math.Max(0, Math.Max(a1, b1) - Math.Min(a2, b2));

        private static double IntersectionArea(Rect a, Rect b)
        {
            double x1 = Math.Max(a.X, b.X);
            double y1 = Math.Max(a.Y, b.Y);
            double x2 = Math.Min(a.X + a.Width, b.X + b.Width);
            double y2 = Math.Min(a.Y + a.Height, b.Y + b.Height);
            double w = x2 - x1;
            double h = y2 - y1;
            if (w <= 0 || h <= 0) return 0;
            return w * h;
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
                    Debug.WriteLine($"[OCR][Copy] Original text: {vm.Text}");
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

        private void OnOcrOverlayTapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is not Border border || border.Tag is not BoundingBoxViewModel vm) return;

            string textToCopy = !string.IsNullOrWhiteSpace(vm.TranslatedText) ? vm.TranslatedText : vm.Text;
            if (string.IsNullOrWhiteSpace(textToCopy)) return;

            try
            {
                _clipboard.SetText(textToCopy);
                Debug.WriteLine($"[OCR][Copy] Original text: {vm.Text}");
                border.BorderThickness = new Thickness(2);
                _ = DispatcherQueue.TryEnqueue(async () =>
                {
                    await System.Threading.Tasks.Task.Delay(300);
                    border.BorderThickness = new Thickness(1);
                });
            }
            catch { }
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
