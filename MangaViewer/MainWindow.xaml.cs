using MangaViewer.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using System;
using Microsoft.UI.Xaml.Media;
using System.Diagnostics;
using Microsoft.UI.Composition.SystemBackdrops; // For DesktopAcrylicBackdrop / MicaBackdrop
using Windows.UI; // For Color

namespace MangaViewer
{
    public sealed partial class MainWindow : Window
    {
        public MangaViewModel ViewModel { get; }

        public MainWindow()
        {
            InitializeComponent();
            Title = "Manga Viewer";
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            TrySetAcrylic();

            ViewModel = new MangaViewModel();
            ViewModel.OcrCompleted += (_,__) => RebuildAllOverlays();
            ViewModel.PageViewChanged += (_,__) => { ClearAllOverlays(); };
            RootGrid.DataContext = ViewModel;
            RootGrid.KeyDown += OnRootGridKeyDown;
        }

        private void TrySetAcrylic()
        {
            try
            {
                // Try Desktop Acrylic first; fallback to Mica
                try { SystemBackdrop = new DesktopAcrylicBackdrop(); }
                catch { SystemBackdrop = new MicaBackdrop(); }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Backdrop] Failed: " + ex.Message);
            }
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

        private void OnLeftOcrContainerSizeChanged(object sender, SizeChangedEventArgs e)
        {
            ViewModel.UpdateLeftOcrContainerSize(e.NewSize.Width, e.NewSize.Height);
            RebuildOverlayForSide(true);
        }
        private void OnRightOcrContainerSizeChanged(object sender, SizeChangedEventArgs e)
        {
            ViewModel.UpdateRightOcrContainerSize(e.NewSize.Width, e.NewSize.Height);
            RebuildOverlayForSide(false);
        }
        private void OnSingleOcrContainerSizeChanged(object sender, SizeChangedEventArgs e)
        {
            ViewModel.UpdateLeftOcrContainerSize(e.NewSize.Width, e.NewSize.Height);
            RebuildOverlayForSingle();
        }

        private void ClearAllOverlays()
        {
            LeftOverlay.Children.Clear();
            RightOverlay.Children.Clear();
            SingleOverlay.Children.Clear();
        }
        private void RebuildAllOverlays()
        {
            if (ViewModel.IsSinglePageMode)
            {
                RebuildOverlayForSingle();
            }
            else if (ViewModel.IsTwoPageMode)
            {
                RebuildOverlayForSide(true);
                RebuildOverlayForSide(false);
            }
        }

        private void RebuildOverlayForSingle() => BuildBoxes(SingleWrapper, SingleImage, SingleOverlay, ViewModel.LeftOcrBoxes, sideTag:"S");
        private void RebuildOverlayForSide(bool isLeft)
        {
            if (ViewModel.IsSinglePageMode) return;
            if (isLeft) BuildBoxes(LeftWrapper, LeftImage, LeftOverlay, ViewModel.LeftOcrBoxes, sideTag:"L");
            else BuildBoxes(RightWrapper, RightImage, RightOverlay, ViewModel.RightOcrBoxes, sideTag:"R");
        }

        private void BuildBoxes(FrameworkElement wrapper, Image image, Canvas overlay, System.Collections.Generic.IReadOnlyList<BoundingBoxViewModel> boxes, string sideTag)
        {
            overlay.Children.Clear();
            if (boxes.Count == 0 || image.Source == null) return;
            double wrapperW = wrapper.ActualWidth;
            double wrapperH = wrapper.ActualHeight;
            if (wrapperW <= 0 || wrapperH <= 0) return;

            double imgDispW = image.ActualWidth;
            double imgDispH = image.ActualHeight;
            if (imgDispW <= 0 || imgDispH <= 0)
            {
                DispatcherQueue.TryEnqueue(() => BuildBoxes(wrapper, image, overlay, boxes, sideTag));
                return;
            }

            var first = boxes[0];
            double imgPixelW = first.ImagePixelWidth;
            double imgPixelH = first.ImagePixelHeight;
            if (imgPixelW <= 0 || imgPixelH <= 0) return;

            double scaleX = imgDispW / imgPixelW;
            double scaleY = imgDispH / imgPixelH;
            double offsetX = (wrapperW - imgDispW) / 2.0;
            double offsetY = (wrapperH - imgDispH) / 2.0;

            foreach (var b in boxes)
            {
                double x = offsetX + b.OriginalX * scaleX;
                double y = offsetY + b.OriginalY * scaleY;
                double w = b.OriginalW * scaleX;
                double h = b.OriginalH * scaleY;
                var border = new Border
                {
                    Width = w,
                    Height = h,
                    Background = new SolidColorBrush(Color.FromArgb(60, 255, 255, 0)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(255, 255, 255, 0)),
                    BorderThickness = new Thickness(1)
                };
                Canvas.SetLeft(border, x);
                Canvas.SetTop(border, y);
                overlay.Children.Add(border);
                ToolTipService.SetToolTip(border, b.Text);

                border.PointerReleased += (s,e)=>
                {
                    try
                    {
                        ViewModel.SelectedOcrBox = b;
                        var dp = new DataPackage();
                        dp.SetText(b.Text);
                        Clipboard.SetContent(dp);
                        Debug.WriteLine("[OCR-Copy] " + b.Text);
                    }
                    catch { }
                };
            }
            Debug.WriteLine($"[OverlayDraw]{sideTag} wrapper={wrapperW}x{wrapperH} imgDisp={imgDispW:F1}x{imgDispH:F1} imgPix={imgPixelW}x{imgPixelH} scale=({scaleX:F4},{scaleY:F4}) off=({offsetX:F1},{offsetY:F1}) boxes={boxes.Count}");
        }

        private void OnOcrBoxClick(object sender, RoutedEventArgs e) { }
        private void OnLeftImageOpened(object sender, RoutedEventArgs e) { }
        private void OnRightImageOpened(object sender, RoutedEventArgs e) { }
        private void OnSingleImageOpened(object sender, RoutedEventArgs e) { }
        private void OnLeftImageSizeChanged(object sender, SizeChangedEventArgs e) { }
        private void OnRightImageSizeChanged(object sender, SizeChangedEventArgs e) { }
        private void OnSingleImageSizeChanged(object sender, SizeChangedEventArgs e) { }

        private async void OpenSettings_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SettingsWindow();
            await dlg.ShowFor(RootGrid);
        }
    }
}
