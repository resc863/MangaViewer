// Project: MangaViewer
// File: Pages/LibraryPage.xaml.cs
// Purpose: Code-behind for library page.

using MangaViewer.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace MangaViewer.Pages
{
    public sealed partial class LibraryPage : Page
    {
        public LibraryViewModel? ViewModel { get; private set; }

        private double _savedScrollPosition;

        public LibraryPage()
        {
            this.InitializeComponent();
            this.NavigationCacheMode = NavigationCacheMode.Enabled;
            this.Loaded += LibraryPage_Loaded;
        }

        private void LibraryPage_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateEmptyStateVisibility();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            
            if (e.Parameter is LibraryViewModel vm)
            {
                if (ViewModel != vm)
                {
                    // Unsubscribe from old ViewModel if different
                    if (ViewModel != null)
                    {
                        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
                        ViewModel.MangaFolders.CollectionChanged -= MangaFolders_CollectionChanged;
                    }

                    ViewModel = vm;
                    DataContext = ViewModel;
                    ViewModel.PropertyChanged += ViewModel_PropertyChanged;
                    ViewModel.MangaFolders.CollectionChanged += MangaFolders_CollectionChanged;
                }

                // Load library only if not already loaded
                await ViewModel.LoadLibraryIfNeededAsync();
                UpdateEmptyStateVisibility();

                // Restore scroll position
                if (_savedScrollPosition > 0)
                {
                    LibraryScrollViewer.ChangeView(null, _savedScrollPosition, null, disableAnimation: true);
                }
            }
        }

        protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
        {
            base.OnNavigatingFrom(e);

            // Save scroll position before navigating away
            _savedScrollPosition = LibraryScrollViewer.VerticalOffset;
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            
            // Don't unsubscribe events since page is cached
        }

        private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(LibraryViewModel.IsLoading))
            {
                UpdateEmptyStateVisibility();
            }
        }

        private void MangaFolders_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            UpdateEmptyStateVisibility();
        }

        private void UpdateEmptyStateVisibility()
        {
            if (ViewModel == null) return;
            
            bool isEmpty = !ViewModel.IsLoading && ViewModel.MangaFolders.Count == 0;
            bool hasItems = ViewModel.MangaFolders.Count > 0;
            
            EmptyState.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
            MangaGridView.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void GridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is MangaFolderViewModel folder)
            {
                // Navigate to reader and load the folder
                if (MainWindow.RootViewModel != null)
                {
                    await MainWindow.RootViewModel.LoadMangaFolderAsync(folder.FolderPath);
                    MainWindow.TryNavigate(typeof(MangaReaderPage), MainWindow.RootViewModel);
                }
            }
        }
    }
}
