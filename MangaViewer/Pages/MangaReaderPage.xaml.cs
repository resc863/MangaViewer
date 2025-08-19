using MangaViewer.ViewModels;
using Microsoft.UI; // Colors
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using System;

namespace MangaViewer.Pages
{
    public sealed partial class MangaReaderPage : Page
    {
        public MangaViewModel ViewModel { get; private set; } = null!;
        private Storyboard? _pageSlideStoryboard;

        public MangaReaderPage()
        {
            InitializeComponent();
            Loaded += MangaReaderPage_Loaded;
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
        }

        protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is MangaViewModel vm)
            {
                DataContext = vm;
                HookVm(vm);
            }
        }

        private void HookVm(MangaViewModel vm)
        {
            if (ViewModel == vm) return;
            ViewModel = vm;
            ViewModel.PageViewChanged += (_, __) => RedrawAllOcr();
            ViewModel.OcrCompleted += (_, __) => RedrawAllOcr();
            ViewModel.PageSlideRequested += OnPageSlideRequested;
            RedrawAllOcr();
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
                Services.ThumbnailDecodeScheduler.Instance.Enqueue(vm, path, index, ViewModel.SelectedThumbnailIndex, DispatcherQueue);
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

        private void RedrawSingle()
        {
            if (SingleWrapperGrid == null || SingleOverlayCanvas == null || ViewModel.LeftImageSource == null) { SingleOverlayCanvas?.Children?.Clear(); return; }
            DrawBoxes(SingleWrapperGrid, SingleOverlayCanvas, ViewModel.LeftOcrBoxes, ViewModel.LeftImageSource.PixelWidth, ViewModel.LeftImageSource.PixelHeight, true);
        }
        private void RedrawLeft()
        {
            if (LeftWrapperGrid == null || LeftOverlayCanvas == null || ViewModel.LeftImageSource == null || !ViewModel.IsTwoPageMode) { LeftOverlayCanvas?.Children?.Clear(); return; }
            DrawBoxes(LeftWrapperGrid, LeftOverlayCanvas, ViewModel.LeftOcrBoxes, ViewModel.LeftImageSource.PixelWidth, ViewModel.LeftImageSource.PixelHeight, true);
        }
        private void RedrawRight()
        {
            if (RightWrapperGrid == null || RightOverlayCanvas == null || ViewModel.RightImageSource == null) { RightOverlayCanvas?.Children?.Clear(); return; }
            DrawBoxes(RightWrapperGrid, RightOverlayCanvas, ViewModel.RightOcrBoxes, ViewModel.RightImageSource.PixelWidth, ViewModel.RightImageSource.PixelHeight, false);
        }

        private static void DrawBoxes(Grid wrapper, Canvas overlay, System.Collections.Generic.IEnumerable<BoundingBoxViewModel> boxes, int imgPixelW, int imgPixelH, bool isLeft)
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
                    Fill = fillBrush
                };
                Canvas.SetLeft(rect, x);
                Canvas.SetTop(rect, y);
                overlay.Children.Add(rect);
            }
        }

        private void OnPageSlideRequested(object? sender, int delta)
        {
            if (PageTranslateTransform == null || PageSlideHostGrid == null) return;
            double width = PageSlideHostGrid.ActualWidth;
            if (width <= 0) return;

            _pageSlideStoryboard?.Stop();

            bool rtl = ViewModel?.DirectionButtonText?.Contains("¿ª¹æÇâ") == true;
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
    }
}
