namespace MangaViewer.Services.Thumbnails
{
    public static class ThumbnailOptions
    {
        /// <summary>
        /// Decode pixel width for thumbnails.
        /// </summary>
        public static int DecodePixelWidth
        {
            get => ThumbnailSettingsService.Instance.DecodeWidth;
            set => ThumbnailSettingsService.Instance.DecodeWidth = value;
        }

        /// <summary>
        /// Delay between low-quality and high-quality decode during progressive loading.
        /// Allows scroll operations to complete before triggering expensive high-quality decode.
        /// Default: 140ms
        /// </summary>
        public static int ProgressiveDecodeDelay { get; set; } = 140;
    }
}
