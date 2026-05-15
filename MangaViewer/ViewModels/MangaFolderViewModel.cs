// Project: MangaViewer
// File: ViewModels/MangaFolderViewModel.cs
// Purpose: ViewModel for individual manga folder in library grid.

using MangaViewer.Helpers;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.IO;

namespace MangaViewer.ViewModels
{
    public partial class MangaFolderViewModel : BaseViewModel
    {
        private string _folderPath = string.Empty;
        private string _folderName = string.Empty;
        private string? _thumbnailPath;
        private ImageSource? _thumbnailSource;

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
            set
            {
                if (SetProperty(ref _thumbnailPath, value))
                    ThumbnailSource = CreateThumbnailSource(value);
            }
        }

        public ImageSource? ThumbnailSource
        {
            get => _thumbnailSource;
            private set => SetProperty(ref _thumbnailSource, value);
        }

        private static ImageSource? CreateThumbnailSource(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return null;

            try
            {
                return new BitmapImage
                {
                    DecodePixelWidth = 220,
                    UriSource = new Uri(path)
                };
            }
            catch
            {
                return null;
            }
        }
    }
}
