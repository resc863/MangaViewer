namespace MangaViewer.Services.Thumbnails
{
    public static class ThumbnailOptions
    {
        public static int DecodePixelWidth
        {
            get => ThumbnailSettingsService.Instance.DecodeWidth;
            set => ThumbnailSettingsService.Instance.DecodeWidth = value;
        }
    }
}
