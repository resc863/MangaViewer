using MangaViewer.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using Windows.System;

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

            ViewModel = new MangaViewModel();
            RootGrid.DataContext = ViewModel;
            RootGrid.KeyDown += OnRootGridKeyDown;

            // Initial navigation
            ContentFrame.Navigate(typeof(Pages.MangaReaderPage), ViewModel);
            LeftNav.SelectedItem = LeftNav.MenuItems[0];
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

        private void LeftNav_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
        {
            if (args.IsSettingsInvoked)
            {
                if (ContentFrame.Content?.GetType() != typeof(Pages.SettingsPage))
                    ContentFrame.Navigate(typeof(Pages.SettingsPage), ViewModel, new EntranceNavigationTransitionInfo());
                return;
            }
            if (args.InvokedItemContainer is not NavigationViewItem item) return;
            string tag = item.Tag as string ?? string.Empty;
            if (tag == "Reader")
            {
                if (ContentFrame.Content?.GetType() != typeof(Pages.MangaReaderPage))
                    ContentFrame.Navigate(typeof(Pages.MangaReaderPage), ViewModel, new EntranceNavigationTransitionInfo());
            }
        }
    }
}
