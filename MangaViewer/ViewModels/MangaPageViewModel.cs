using MangaViewer.Helpers;
using Microsoft.UI.Xaml.Media.Imaging;
using System;

namespace MangaViewer.ViewModels
{
    public class MangaPageViewModel : BaseViewModel
    {
        private string _filePath;
        public string FilePath
        {
            get => _filePath;
            set
            {
                if (_filePath != value)
                {
                    _filePath = value;
                    OnPropertyChanged();

                    if (!string.IsNullOrEmpty(_filePath))
                    {
                        // Lazy load thumbnail when file path is set
                        ThumbnailSource = new BitmapImage(new Uri(_filePath))
                        {
                            DecodePixelWidth = 150 // Thumbnail width
                        };
                    }
                    else
                    {
                        ThumbnailSource = null;
                    }
                }
            }
        }

        private BitmapImage _thumbnailSource;
        public BitmapImage ThumbnailSource
        {
            get => _thumbnailSource;
            private set // Setter is private, controlled by the FilePath property
            {
                if (_thumbnailSource != value)
                {
                    _thumbnailSource = value;
                    OnPropertyChanged();
                }
            }
        }
    }
}