using MangaViewer.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Diagnostics;
using Microsoft.UI.Composition.SystemBackdrops; // For DesktopAcrylicBackdrop / MicaBackdrop
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI; // Colors

namespace MangaViewer
{
    public sealed partial class MainWindow : Window
    {
        public MangaViewModel ViewModel { get; }
        private SettingsWindow? _settingsDialog;

        public MainWindow()
        {
            InitializeComponent();
            Title = "Manga Viewer";
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
            TrySetAcrylic();

            ViewModel = new MangaViewModel();
            RootGrid.DataContext = ViewModel;
            RootGrid.KeyDown += OnRootGridKeyDown;

            ViewModel.PageViewChanged += (_, __) => RedrawAllOcr();
            ViewModel.OcrCompleted += (_, __) => RedrawAllOcr();
        }

        private void TrySetAcrylic()
        {
            try
            {
                try { SystemBackdrop = new DesktopAcrylicBackdrop(); }
                catch { SystemBackdrop = new MicaBackdrop(); }
            }
            catch (Exception ex) { Debug.WriteLine("[Backdrop] Failed: " + ex.Message); }
        }

        private void OnRootGridKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key is VirtualKey.Left)
            {
                if (ViewModel.GoLeftCommand.CanExecute(null)) ViewModel.GoLeftCommand.Execute(null);
            }
            else if (e.Key is VirtualKey.Right)
            {
                if (ViewModel.GoRightCommand.CanExecute(null)) ViewModel.GoRightCommand.Execute(null);
            }
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (ViewModel.OpenFolderCommand.CanExecute(hwnd))
                ViewModel.OpenFolderCommand.Execute(hwnd);
        }

        private async void LeftNav_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
        {
            if (args.IsSettingsInvoked)
            {
                _settingsDialog ??= new SettingsWindow();
                await _settingsDialog.ShowFor(RootGrid);
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
            if (SingleWrapper == null || SingleOverlay == null || ViewModel.LeftImageSource == null) { SingleOverlay?.Children?.Clear(); return; }
            DrawBoxes(SingleWrapper, SingleOverlay, ViewModel.LeftOcrBoxes, ViewModel.LeftImageSource.PixelWidth, ViewModel.LeftImageSource.PixelHeight, true);
        }
        private void RedrawLeft()
        {
            if (LeftWrapper == null || LeftOverlay == null || ViewModel.LeftImageSource == null || !ViewModel.IsTwoPageMode) { LeftOverlay?.Children?.Clear(); return; }
            DrawBoxes(LeftWrapper, LeftOverlay, ViewModel.LeftOcrBoxes, ViewModel.LeftImageSource.PixelWidth, ViewModel.LeftImageSource.PixelHeight, true);
        }
        private void RedrawRight()
        {
            if (RightWrapper == null || RightOverlay == null || ViewModel.RightImageSource == null) { RightOverlay?.Children?.Clear(); return; }
            DrawBoxes(RightWrapper, RightOverlay, ViewModel.RightOcrBoxes, ViewModel.RightImageSource.PixelWidth, ViewModel.RightImageSource.PixelHeight, false);
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
            // Semi-transparent fill (manual alpha blend)
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
    }
}
