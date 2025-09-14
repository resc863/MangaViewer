using System;

namespace MangaViewer.Services.Thumbnails
{
    public static class ThumbnailProviderFactory
    {
        private static IThumbnailProvider? _instance;

        public static IThumbnailProvider Get()
        {
            if (_instance != null) return _instance;
            _instance = new ManagedThumbnailProvider();
            return _instance;
        }

        public static void ResetForTesting(IThumbnailProvider? provider = null)
        {
            _instance = provider;
        }
    }
}
