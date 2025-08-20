using MangaViewer.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using System;
using Windows.System;

namespace MangaViewer
{
    public sealed partial class MainWindow : Window
    {
        public MangaViewModel ViewModel { get; }
        public static Frame? RootFrame { get; private set; }
        public static MangaViewModel? RootViewModel { get; private set; }

        public MainWindow()
        {
            this.InitializeComponent();
            Title = "Manga Viewer";
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            ViewModel = new MangaViewModel();
            RootViewModel = ViewModel;
            RootGrid.DataContext = ViewModel;
            RootGrid.KeyDown += OnRootGridKeyDown;

            ContentFrame.CacheSize = 10; // keep more pages cached
            ContentFrame.Navigated += OnFrameNavigated;
            RootFrame = ContentFrame;

            // Initial navigation
            ContentFrame.Navigate(typeof(Pages.MangaReaderPage), ViewModel);
            LeftNav.SelectedItem = LeftNav.MenuItems[0];
            UpdateBackButton();
        }

        private void OnFrameNavigated(object sender, NavigationEventArgs e)
        {
            UpdateBackButton();
            var current = e.SourcePageType;
            if (current == typeof(Pages.MangaReaderPage))
                LeftNav.SelectedItem = LeftNav.MenuItems[0];
            else if (current == typeof(Pages.SearchPage))
            {
                foreach (var mi in LeftNav.MenuItems)
                    if (mi is NavigationViewItem nvi && (string?)nvi.Tag == "Search") { LeftNav.SelectedItem = nvi; break; }
            }
        }

        private void UpdateBackButton()
        {
            LeftNav.IsBackEnabled = ContentFrame.CanGoBack;
        }

        public static bool TryNavigate(Type pageType, object? parameter = null)
        {
            if (RootFrame == null) return false;
            if (RootFrame.Content?.GetType() == pageType) return true;
            return RootFrame.Navigate(pageType, parameter, new EntranceNavigationTransitionInfo());
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
            else if (tag == "Search")
            {
                if (ContentFrame.Content?.GetType() != typeof(Pages.SearchPage))
                    ContentFrame.Navigate(typeof(Pages.SearchPage), ViewModel, new EntranceNavigationTransitionInfo());
            }
            UpdateBackButton();
        }

        private void LeftNav_BackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
        {
            if (ContentFrame.CanGoBack)
            {
                ContentFrame.GoBack();
            }
            UpdateBackButton();
        }
    }
}
