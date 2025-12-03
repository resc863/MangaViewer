// Project: MangaViewer
// File: ViewModels/LibraryViewModel.cs
// Purpose: ViewModel for library page showing manga folders.

using MangaViewer.Helpers;
using MangaViewer.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows.Input;

namespace MangaViewer.ViewModels
{
    public class LibraryViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private readonly LibraryService _libraryService;
        private bool _isLoading;

        public ObservableCollection<MangaFolderViewModel> MangaFolders { get; } = new();

        public bool IsLoading
        {
            get => _isLoading;
            set { _isLoading = value; OnPropertyChanged(nameof(IsLoading)); }
        }

        public ICommand RefreshCommand { get; }

        public LibraryViewModel(LibraryService libraryService)
        {
            _libraryService = libraryService;
            RefreshCommand = new RelayCommand(async _ => await LoadLibraryAsync());
            _ = LoadLibraryAsync();
        }

        public async Task LoadLibraryAsync()
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
            OnPropertyChanged(nameof(MangaFolders));
        }

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
