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

        public LibraryPage()
        {
            this.InitializeComponent();
            this.Loaded += LibraryPage_Loaded;
        }

        private void LibraryPage_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateEmptyStateVisibility();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            
            if (e.Parameter is LibraryViewModel vm)
            {
                ViewModel = vm;
                DataContext = ViewModel;
                ViewModel.PropertyChanged += ViewModel_PropertyChanged;
                ViewModel.MangaFolders.CollectionChanged += MangaFolders_CollectionChanged;
                UpdateEmptyStateVisibility();
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            
            if (ViewModel != null)
            {
                ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
                ViewModel.MangaFolders.CollectionChanged -= MangaFolders_CollectionChanged;
            }
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
