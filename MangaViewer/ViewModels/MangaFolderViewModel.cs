// Project: MangaViewer
// File: ViewModels/MangaFolderViewModel.cs
// Purpose: ViewModel for individual manga folder in library grid.

using MangaViewer.Helpers;
using System.ComponentModel;
using System.Windows.Input;

namespace MangaViewer.ViewModels
{
    public class MangaFolderViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private string _folderPath = string.Empty;
        private string _folderName = string.Empty;
        private string? _thumbnailPath;

        public string FolderPath
        {
            get => _folderPath;
            set { _folderPath = value; OnPropertyChanged(nameof(FolderPath)); }
        }

        public string FolderName
        {
            get => _folderName;
            set { _folderName = value; OnPropertyChanged(nameof(FolderName)); }
        }

        public string? ThumbnailPath
        {
            get => _thumbnailPath;
            set { _thumbnailPath = value; OnPropertyChanged(nameof(ThumbnailPath)); }
        }

        public ICommand? OpenCommand { get; set; }

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
