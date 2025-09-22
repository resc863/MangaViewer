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
using Microsoft.UI.Xaml.Media; // for VisualTreeHelper if needed
using Windows.System.Threading;
using Windows.ApplicationModel.DataTransfer; // Clipboard
using Microsoft.UI.Input; // Pointer identifiers
using Microsoft.UI.Xaml.Input; // Tapped
using Microsoft.UI.Xaml.Input; // Tapped, PointerRoutedEventArgs
using Windows.Storage; // LocalSettings
using Microsoft.UI.Xaml.Controls.Primitives; // DragDeltaEventArgs
using Services = MangaViewer.Services; // alias root services if needed
using MangaViewer.Services.Thumbnails; // moved scheduler

namespace MangaViewer.Pages
{
    /// <summary>
    /// 리더 페이지 코드비하인드.
    /// - 썸네일 컨테이너(realized) 감지 시 디코드 스케줄러 큐잉
    /// - 현재 뷰포트 기준 우선순위 갱신 및 근접 프리페치
    /// - OCR 결과 박스 캔버스 렌더링/클립보드 복사/하이라이트 효과
    /// - 우측 패널 폭/열림 상태 저장 및 리사이즈 디바운스 처리
    /// </summary>
    public sealed partial class MangaReaderPage : Page
    {
        public MangaViewModel ViewModel { get; private set; } = null!;
        private Storyboard? _pageSlideStoryboard;
        private ThreadPoolTimer? _thumbRefreshTimer;
        private bool _isUnloaded;
        private ScrollViewer? _thumbScrollViewer;
        private bool? _preferredPaneOpen; // persisted preferred state
        private DispatcherTimer? _resizeDebounceTimer; // debounce resize
        private bool _suppressPersistDuringResize; // suppress persisting IsPaneOpen while resizing
        private const double PaneMinWidth = 120;
        private const double PaneMaxWidth = 420;

        public MangaReaderPage()
        {
            InitializeComponent();
            Loaded += MangaReaderPage_Loaded;
            Unloaded += MangaReaderPage_Unloaded;

            // Initialize resize debounce timer
            _resizeDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _resizeDebounceTimer.Tick += (_, __) =>
            {
                _resizeDebounceTimer!.Stop();
                _suppressPersistDuringResize = false;
                // Reapply preferred state after resize settles
                if (_preferredPaneOpen.HasValue && ViewModel != null)
                {
                    if (ViewModel.IsPaneOpen != _preferredPaneOpen.Value)
                    {
                        ViewModel.IsPaneOpen = _preferredPaneOpen.Value;
                    }
                }
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
            {
                HookVm(vm);
            }
            HookThumbScrollViewer();

            // Load saved pane width
            try
            {
                var settings = ApplicationData.Current.LocalSettings;
                if (settings.Values.TryGetValue("ReaderPaneWidth", out var v) && v is not null)
                {
                    var d = Convert.ToDouble(v);
                    ReaderSplitView.OpenPaneLength = Math.Clamp(d, PaneMinWidth, PaneMaxWidth);
                }
            }
            catch { }
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
        }

        protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            Cleanup();
        }

        private void MangaReaderPage_Unloaded(object sender, RoutedEventArgs e)
        {
            Cleanup();
        }

        /// <summary>
        /// 이벤트 핸들러/타이머 등을 해제하고 스크롤 뷰어 구독을 정리합니다.
        /// </summary>
        private void Cleanup()
        {
            if (_isUnloaded) return;
            _isUnloaded = true;
            _thumbRefreshTimer?.Cancel();
            _thumbRefreshTimer = null;
            if (_thumbScrollViewer != null)
            {
                _thumbScrollViewer.ViewChanged -= OnThumbsViewChanged;
                _thumbScrollViewer = null;
            }
            if (_resizeDebounceTimer != null)
            {
                _resizeDebounceTimer.Stop();
                _resizeDebounceTimer = null;
            }
            if (ViewModel != null)
            {
                ViewModel.PageViewChanged -= (_, __) => RedrawAllOcr();
                ViewModel.OcrCompleted -= (_, __) => RedrawAllOcr();
                ViewModel.PageSlideRequested -= OnPageSlideRequested;
                if (ViewModel.Thumbnails is INotifyCollectionChanged incc)
                    incc.CollectionChanged -= OnThumbsChanged;
                foreach (var p in ViewModel.Thumbnails)
                    p.PropertyChanged -= OnPagePropertyChanged;
                ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            }
        }

        private void HookVm(MangaViewModel vm)
        {
            if (ViewModel == vm) return;
            ViewModel = vm;
            ViewModel.PageViewChanged += (_, __) => RedrawAllOcr();
            ViewModel.OcrCompleted += (_, __) => RedrawAllOcr();
            ViewModel.PageSlideRequested += OnPageSlideRequested;
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;

            // Load preferred pane state from settings and apply once
            try
            {
                var settings = ApplicationData.Current.LocalSettings;
                if (settings.Values.TryGetValue("ReaderPaneOpenPreferred", out var v) && v is bool b)
                {
                    _preferredPaneOpen = b;
                    if (ViewModel.IsPaneOpen != b)
                        ViewModel.IsPaneOpen = b;
                }
                else
                {
                    _preferredPaneOpen = ViewModel.IsPaneOpen;
                }
            }
            catch { _preferredPaneOpen = ViewModel.IsPaneOpen; }

            if (ViewModel.Thumbnails is INotifyCollectionChanged incc)
            {
                incc.CollectionChanged -= OnThumbsChanged;
                incc.CollectionChanged += OnThumbsChanged;
            }
            foreach (var page in ViewModel.Thumbnails)
                SubscribePage(page);

            StartThumbnailAutoRefresh();
            RedrawAllOcr();
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is not MangaViewModel vm) return;
            if (e.PropertyName == nameof(MangaViewModel.IsPaneOpen))
            {
                if (_suppressPersistDuringResize) return; // ignore auto close/open during resize

                // Update preferred state and persist
                _preferredPaneOpen = vm.IsPaneOpen;
                try
                {
                    ApplicationData.Current.LocalSettings.Values["ReaderPaneOpenPreferred"] = _preferredPaneOpen;
                }
                catch { }
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

        /// <summary>
        /// 주기적으로 뷰포트에 가까운 항목을 갱신하고 디코딩을 킥합니다.
        /// </summary>
        private void StartThumbnailAutoRefresh()
        {
            _thumbRefreshTimer?.Cancel();
            _thumbRefreshTimer = ThreadPoolTimer.CreatePeriodicTimer(_ =>
            {
                if (_isUnloaded) return;
                try { DispatcherQueue.TryEnqueue(() => { UpdatePriorityByViewport(); KickVisibleThumbnailDecode(); }); } catch { }
            }, TimeSpan.FromMilliseconds(700));
        }

        private void OnThumbsViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
        {
            if (_isUnloaded) return;
            UpdatePriorityByViewport();
            KickVisibleThumbnailDecode();
        }

        /// <summary>
        /// 현재 가시 영역의 항목 인덱스를 추정하고, 우선순위 및 프리페치를 갱신합니다.
        /// </summary>
        private void UpdatePriorityByViewport()
        {
            if (ThumbnailsList == null || ViewModel == null) return;
            var panel = ThumbnailsList.ItemsPanelRoot as Panel;
            if (panel == null || panel.Children.Count == 0) return;

            int minIndex = int.MaxValue;
            int maxIndex = -1;
            for (int i = 0; i < panel.Children.Count; i++)
            {
                if (panel.Children[i] is ListViewItem lvi)
                {
                    int idx = ThumbnailsList.IndexFromContainer(lvi);
                    if (idx >= 0)
                    {
                        if (idx < minIndex) minIndex = idx;
                        if (idx > maxIndex) maxIndex = idx;
                    }
                }
            }
            if (minIndex == int.MaxValue || maxIndex < 0) return;
            int pivot = (minIndex + maxIndex) / 2;

            // 우선순위 재정렬(대기 큐) 및 근접 항목 프리페치
            ThumbnailDecodeScheduler.Instance.UpdateSelectedIndex(pivot);

            int radius = 24; // 프리페치 반경
            int start = Math.Max(0, pivot - radius);
            int end = Math.Min(ViewModel.Thumbnails.Count - 1, pivot + radius);
            for (int i = start; i <= end; i++)
            {
                var vm = ViewModel.Thumbnails[i];
                if (vm.FilePath != null && !vm.HasThumbnail && !vm.IsThumbnailLoading)
                {
                    ThumbnailDecodeScheduler.Instance.Enqueue(vm, vm.FilePath, i, pivot, DispatcherQueue);
                }
            }
        }

        /// <summary>
        /// 화면에 실재로 그려진 컨테이너들만 대상으로 즉시 디코드를 큐잉합니다.
        /// </summary>
        private void KickVisibleThumbnailDecode()
        {
            if (_isUnloaded) return;
            if (ThumbnailsList == null || ViewModel == null || ViewModel.Thumbnails.Count == 0) return;
            if (!DispatcherQueue.HasThreadAccess) return; // safety
            var panel = ThumbnailsList.ItemsPanelRoot as Panel;
            if (panel == null || panel.Children.Count == 0) return;

            int minIndex = int.MaxValue;
            int maxIndex = -1;

            for (int i = 0; i < panel.Children.Count; i++)
            {
                if (panel.Children[i] is ListViewItem lvi)
                {
                    int index = ThumbnailsList.IndexFromContainer(lvi);
                    if (index < 0 || index >= ViewModel.Thumbnails.Count) continue;
                    if (index < minIndex) minIndex = index;
                    if (index > maxIndex) maxIndex = index;
                    var vm = ViewModel.Thumbnails[index];
                    if (vm.FilePath != null && !vm.HasThumbnail && !vm.IsThumbnailLoading)
                    {
                        ThumbnailDecodeScheduler.Instance.Enqueue(vm, vm.FilePath, index, ViewModel.SelectedThumbnailIndex, DispatcherQueue);
                    }
                }
            }

            if (minIndex != int.MaxValue && maxIndex >= 0)
            {
                int pivot = (minIndex + maxIndex) / 2;
                ThumbnailDecodeScheduler.Instance.UpdateSelectedIndex(pivot);
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
            // After changes attempt refresh of visible ones quickly
            KickVisibleThumbnailDecode();
        }

        private void SubscribePage(MangaPageViewModel page)
        {
            page.PropertyChanged -= OnPagePropertyChanged;
            page.PropertyChanged += OnPagePropertyChanged;
        }

        private void OnPagePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is not MangaPageViewModel vm) return;
            if (e.PropertyName == nameof(MangaPageViewModel.FilePath))
            {
                if (!string.IsNullOrEmpty(vm.FilePath))
                {
                    int index = ViewModel.Thumbnails.IndexOf(vm);
                    if (index >= 0)
                    {
                        ThumbnailDecodeScheduler.Instance.Enqueue(vm, vm.FilePath, index, ViewModel.SelectedThumbnailIndex, DispatcherQueue);
                        // If container not yet realized, timer will catch it later; if realized ensure immediate attempt
                        KickVisibleThumbnailDecode();
                    }
                }
            }
        }

        private void ThumbnailsList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.Item is not MangaPageViewModel vm) return;
            int index = sender.IndexFromContainer(args.ItemContainer);
            if (args.InRecycleQueue)
            {
                if (index != ViewModel.SelectedThumbnailIndex) vm.UnloadThumbnail();
            }
            else
            {
                var path = vm.FilePath ?? string.Empty;
                ThumbnailDecodeScheduler.Instance.Enqueue(vm, path, index, ViewModel.SelectedThumbnailIndex, DispatcherQueue);
            }
        }

        private void OnLeftOcrContainerSizeChanged(object sender, SizeChangedEventArgs e) => RedrawLeft();
        private void OnRightOcrContainerSizeChanged(object sender, SizeChangedEventArgs e) => RedrawRight();
        private void OnSingleOcrContainerSizeChanged(object sender, SizeChangedEventArgs e) => RedrawSingle();
        private void OnLeftImageSizeChanged(object sender, SizeChangedEventArgs e) => RedrawLeft();
        private void OnRightImageSizeChanged(object sender, SizeChangedEventArgs e) => RedrawRight();
        private void OnSingleImageSizeChanged(object sender, SizeChangedEventArgs e) => RedrawSingle();

        private void RedrawAllOcr()
        {
            RedrawSingle();
            RedrawLeft();
            RedrawRight();
        }

        /// <summary>단일 페이지 모드의 OCR 박스 렌더링.</summary>
        private void RedrawSingle()
        {
            if (SingleWrapperGrid == null || SingleOverlayCanvas == null || ViewModel.LeftImageSource == null) { SingleOverlayCanvas?.Children?.Clear(); return; }
            DrawBoxes(SingleWrapperGrid, SingleOverlayCanvas, ViewModel.LeftOcrBoxes, ViewModel.LeftImageSource.PixelWidth, ViewModel.LeftImageSource.PixelHeight, true);
        }
        /// <summary>좌측 페이지 OCR 박스 렌더링(양면 모드).</summary>
        private void RedrawLeft()
        {
            if (LeftWrapperGrid == null || LeftOverlayCanvas == null || ViewModel.LeftImageSource == null || !ViewModel.IsTwoPageMode) { LeftOverlayCanvas?.Children?.Clear(); return; }
            DrawBoxes(LeftWrapperGrid, LeftOverlayCanvas, ViewModel.LeftOcrBoxes, ViewModel.LeftImageSource.PixelWidth, ViewModel.LeftImageSource.PixelHeight, true);
        }
        /// <summary>우측 페이지 OCR 박스 렌더링.</summary>
        private void RedrawRight()
        {
            if (RightWrapperGrid == null || RightOverlayCanvas == null || ViewModel.RightImageSource == null) { RightOverlayCanvas?.Children?.Clear(); return; }
            DrawBoxes(RightWrapperGrid, RightOverlayCanvas, ViewModel.RightOcrBoxes, ViewModel.RightImageSource.PixelWidth, ViewModel.RightImageSource.PixelHeight, false);
        }

        /// <summary>
        /// 원본 픽셀 좌표 기반 박스들을 현재 컨테이너에 맞춰 스케일링해 그립니다.
        /// </summary>
        private void DrawBoxes(Grid wrapper, Canvas overlay, System.Collections.Generic.IEnumerable<BoundingBoxViewModel> boxes, int imgPixelW, int imgPixelH, bool isLeft)
        {
            overlay.Children.Clear();
            double wrapperW = wrapper.ActualWidth;
            double wrapperH = wrapper.ActualHeight;
            if (wrapperW <= 0 || wrapperH <= 0 || imgPixelW <= 0 || imgPixelH <= 0) return;
            double scale = Math.Min(wrapperW / imgPixelW, wrapperH / imgPixelH);
            double displayW = imgPixelW * scale;
            double displayH = imgPixelH * scale;
            double offsetX = (wrapperW - displayW) / 2.0;
            double offsetY = (wrapperH - displayH) / 2.0;

            var strokeBrush = new SolidColorBrush(isLeft ? Colors.Yellow : Colors.DeepSkyBlue);
            byte a = 0x40;
            var fillColor = isLeft ? Colors.Yellow : Colors.DeepSkyBlue;
            fillColor.A = a;
            var fillBrush = new SolidColorBrush(fillColor);

            foreach (var b in boxes)
            {
                double x = offsetX + b.OriginalX * scale;
                double y = offsetY + b.OriginalY * scale;
                double w = b.OriginalW * scale;
                double h = b.OriginalH * scale;
                if (w <= 0 || h <= 0) continue;
                var rect = new Rectangle
                {
                    Width = w,
                    Height = h,
                    StrokeThickness = 1,
                    Stroke = strokeBrush,
                    Fill = fillBrush,
                    Tag = b
                };
                rect.Tapped += OnOcrRectTapped;
                rect.PointerEntered += (_, __) => rect.Opacity = 0.85;
                rect.PointerExited += (_, __) => rect.Opacity = 1.0;
                Canvas.SetLeft(rect, x);
                Canvas.SetTop(rect, y);
                overlay.Children.Add(rect);
            }
        }

        private void OnOcrRectTapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is Rectangle r && r.Tag is BoundingBoxViewModel vm && !string.IsNullOrWhiteSpace(vm.Text))
            {
                try
                {
                    var dp = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
                    dp.SetText(vm.Text);
                    Clipboard.SetContent(dp);
                    Clipboard.Flush();
                    // optional: brief visual feedback
                    r.StrokeThickness = 2;
                    var _ = DispatcherQueue.TryEnqueue(async () =>
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
            if (PageTranslateTransform == null || PageSlideHostGrid == null) return;
            double width = PageSlideHostGrid.ActualWidth;
            if (width <= 0) return;

            _pageSlideStoryboard?.Stop();

            bool rtl = ViewModel?.DirectionButtonText?.Contains("역방향") == true;
            int direction = delta > 0 ? 1 : -1;
            if (rtl) direction *= -1;

            double from = direction * width;
            double to = 0;
            PageTranslateTransform.X = from;

            var anim = new DoubleAnimation
            {
                From = from,
                To = to,
                Duration = new Duration(TimeSpan.FromMilliseconds(220)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            _pageSlideStoryboard = new Storyboard();
            Storyboard.SetTarget(anim, PageTranslateTransform);
            Storyboard.SetTargetProperty(anim, "X");
            _pageSlideStoryboard.Children.Add(anim);
            _pageSlideStoryboard.Begin();
        }

        private void PaneResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
        {
            // Right-side overlay pane: dragging left (HorizontalChange < 0) expands pane width; dragging right shrinks it.
            double newLen = ReaderSplitView.OpenPaneLength - e.HorizontalChange;
            newLen = Math.Clamp(newLen, PaneMinWidth, PaneMaxWidth);
            ReaderSplitView.OpenPaneLength = newLen;
            try { ApplicationData.Current.LocalSettings.Values["ReaderPaneWidth"] = newLen; } catch { }
        }

        private void PaneResizeThumb_PointerEntered(object sender, PointerRoutedEventArgs e) { }
        private void PaneResizeThumb_PointerMoved(object sender, PointerRoutedEventArgs e) { }
        private void PaneResizeThumb_PointerExited(object sender, PointerRoutedEventArgs e) { }
        private void PaneResizeThumb_DragCompleted(object sender, DragCompletedEventArgs e) { }

        /// <summary>
        /// SplitView 크기 변경 시 패널 자동 열림/상태 저장을 디바운스 처리합니다.
        /// </summary>
        private void ReaderSplitView_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // Debounce resize and avoid persisting control-driven auto-close.
            _suppressPersistDuringResize = true;

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
        }
    }
}
