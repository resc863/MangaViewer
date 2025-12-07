// Project: MangaViewer
// File: ViewModels/MangaFolderViewModel.cs
// Purpose: ViewModel for individual manga folder in library grid.

using MangaViewer.Helpers;

namespace MangaViewer.ViewModels
{
    public class MangaFolderViewModel : BaseViewModel
    {
        private string _folderPath = string.Empty;
        private string _folderName = string.Empty;
        private string? _thumbnailPath;

        public string FolderPath
        {
            get => _folderPath;
            set => SetProperty(ref _folderPath, value);
        }

        public string FolderName
        {
            get => _folderName;
            set => SetProperty(ref _folderName, value);
        }

        public string? ThumbnailPath
        {
            get => _thumbnailPath;
            set => SetProperty(ref _thumbnailPath, value);
        }
    }
}
