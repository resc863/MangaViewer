using MangaViewer.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace MangaViewer
{
    public sealed partial class MainWindow : Window
    {
        public MangaViewModel ViewModel { get; }

        public MainWindow()
        {
            this.InitializeComponent();
            this.Title = "Manga Viewer";
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            ViewModel = new MangaViewModel();
            RootGrid.DataContext = ViewModel;

            RootGrid.KeyDown += MainWindow_KeyDown;
        }

        private void MainWindow_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            switch (e.Key)
            {
                case VirtualKey.Left:
                    if (ViewModel.GoLeftCommand.CanExecute(null)) { ViewModel.GoLeftCommand.Execute(null); }
                    break;
                case VirtualKey.Right:
                    if (ViewModel.GoRightCommand.CanExecute(null)) { ViewModel.GoRightCommand.Execute(null); }
                    break;
            }
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (ViewModel.OpenFolderCommand.CanExecute(windowHandle)) { ViewModel.OpenFolderCommand.Execute(windowHandle); }
        }

        private void Image_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            double leftScaleX = 0, leftScaleY = 0;
            double rightScaleX = 0, rightScaleY = 0;

            if (ViewModel.IsSinglePageMode)
            {
                if (SingleImage.ActualWidth > 0 && ViewModel.LeftImageOriginalWidth > 0)
                {
                    leftScaleX = SingleImage.ActualWidth / ViewModel.LeftImageOriginalWidth;
                }
                if (SingleImage.ActualHeight > 0 && ViewModel.LeftImageOriginalHeight > 0)
                {
                    leftScaleY = SingleImage.ActualHeight / ViewModel.LeftImageOriginalHeight;
                }
            }
            else if (ViewModel.IsTwoPageMode)
            {
                if (LeftImage.ActualWidth > 0 && ViewModel.LeftImageOriginalWidth > 0)
                {
                    leftScaleX = LeftImage.ActualWidth / ViewModel.LeftImageOriginalWidth;
                }
                if (LeftImage.ActualHeight > 0 && ViewModel.LeftImageOriginalHeight > 0)
                {
                    leftScaleY = LeftImage.ActualHeight / ViewModel.LeftImageOriginalHeight;
                }

                if (RightImage.ActualWidth > 0 && ViewModel.RightImageOriginalWidth > 0)
                {
                    rightScaleX = RightImage.ActualWidth / ViewModel.RightImageOriginalWidth;
                }
                if (RightImage.ActualHeight > 0 && ViewModel.RightImageOriginalHeight > 0)
                {
                    rightScaleY = RightImage.ActualHeight / ViewModel.RightImageOriginalHeight;
                }
            }

            ViewModel.UpdateOcrScales(leftScaleX, leftScaleY, rightScaleX, rightScaleY);
        }
    }
}
