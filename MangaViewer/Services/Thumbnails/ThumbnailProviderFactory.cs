using System;

namespace MangaViewer.Services.Thumbnails
{
    public static class ThumbnailProviderFactory
    {
        // Pre-create single provider instance (no lazy path JIT) for AOT friendliness.
        public static readonly IThumbnailProvider Instance = new ManagedThumbnailProvider();
        public static IThumbnailProvider Get() => Instance;
        public static void ResetForTesting(IThumbnailProvider? provider = null) { /* testing shim no-op in AOT optimized mode */ }
    }
}
