namespace MangaViewer.Services.Thumbnails
{
    public static class ThumbnailOptions
    {
        public static bool UseNativeProvider
        {
            get => MangaViewer.Services.ThumbnailSettingsService.Instance.UseNative;
            set => MangaViewer.Services.ThumbnailSettingsService.Instance.UseNative = value;
        }

        public static int DecodePixelWidth
        {
            get => MangaViewer.Services.ThumbnailSettingsService.Instance.DecodeWidth;
            set => MangaViewer.Services.ThumbnailSettingsService.Instance.DecodeWidth = value;
        }
    }
}
