using System;

namespace MangaViewer.Services.Thumbnails
{
    /// <summary>
    /// Factory for thumbnail provider abstraction. Centralizes instance creation so we can swap
    /// out implementation (e.g., native decoder) without touching call sites. A single pre-created
    /// instance is used to avoid Lazy overhead and improve AOT friendliness.
    /// Testing: ResetForTesting currently a no-op; extend to allow injecting mock provider.
    /// </summary>
    public static class ThumbnailProviderFactory
    {
        // Pre-create single provider instance (no lazy path JIT) for AOT friendliness.
        public static readonly IThumbnailProvider Instance = new ManagedThumbnailProvider();
        public static IThumbnailProvider Get() => Instance;
        public static void ResetForTesting(IThumbnailProvider? provider = null) { /* testing shim no-op in AOT optimized mode */ }
    }
}
