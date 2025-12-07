// Project: MangaViewer
// File: ViewModels/LibraryViewModel.cs
// Purpose: ViewModel for library page showing manga folders.

using MangaViewer.Helpers;
using MangaViewer.Services;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;

namespace MangaViewer.ViewModels
{
    public class LibraryViewModel : BaseViewModel
    {
        private readonly LibraryService _libraryService;
        private bool _isLoading;
        private bool _isLoaded;

        public ObservableCollection<MangaFolderViewModel> MangaFolders { get; } = new();

        public bool IsLoading
        {
            get => _isLoading;
            set => SetProperty(ref _isLoading, value);
        }

        public bool IsLoaded
        {
            get => _isLoaded;
            private set => SetProperty(ref _isLoaded, value);
        }

        public ICommand RefreshCommand { get; }

        public LibraryViewModel(LibraryService libraryService)
        {
            _libraryService = libraryService;
            RefreshCommand = new RelayCommand(async _ => await RefreshLibraryAsync());
        }

        /// <summary>
        /// Loads the library only if it hasn't been loaded yet.
        /// </summary>
        public async Task LoadLibraryIfNeededAsync()
        {
            if (IsLoaded) return;
            await LoadLibraryAsync();
        }

        /// <summary>
        /// Forces a refresh of the library, clearing and reloading all folders.
        /// </summary>
        public async Task RefreshLibraryAsync()
        {
            IsLoaded = false;
            await LoadLibraryAsync();
        }

        private async Task LoadLibraryAsync()
        {
            IsLoading = true;
            MangaFolders.Clear();
            OnPropertyChanged(nameof(MangaFolders));

            var folders = await _libraryService.ScanLibraryAsync();

            foreach (var folder in folders)
            {
                var vm = new MangaFolderViewModel
                {
                    FolderPath = folder.FolderPath,
                    FolderName = folder.FolderName,
                    ThumbnailPath = folder.FirstImagePath
                };
                MangaFolders.Add(vm);
            }

            IsLoading = false;
            IsLoaded = true;
            OnPropertyChanged(nameof(MangaFolders));
        }
    }
}
