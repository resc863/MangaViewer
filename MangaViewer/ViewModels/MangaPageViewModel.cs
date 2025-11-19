using MangaViewer.Helpers;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Dispatching;
using System;
using System.Threading;
using System.Threading.Tasks;
using MangaViewer.Services;
using MangaViewer.Services.Thumbnails;

namespace MangaViewer.ViewModels
{
    /// <summary>
    /// Represents a single manga image (page) in thumbnail lists and bookmark panels.
    /// Responsibilities:
    ///  - Holds FilePath and manages versioning so stale async thumbnail results are discarded.
    ///  - Provides asynchronous thumbnail loading with progressive quality (low -> high) and caching.
    ///  - Supports cancellation & unloading when list items recycle (improves memory + scroll perf).
    /// Threading Model:
    ///  - Public async APIs perform background work and marshal UI updates via DispatcherQueue.
    ///  - Version field (_version) increments whenever FilePath changes; async continuations compare
    ///    captured version with current to ignore outdated results.
    /// Caching Strategy:
    ///  - Reads from ThumbnailCacheService (size-limited LRU of ImageSource) before decoding.
    ///  - Stores both low and high resolution variants; replaces low with high when ready.
    ///  - For memory-backed images (mem: keys) it fetches raw bytes from ImageCacheService.
    /// </summary>
    public class MangaPageViewModel : BaseViewModel
    {
        private string? _filePath;
        private int _version; // Incremented when FilePath changes; used to discard stale async results.
        private CancellationTokenSource? _thumbCts;
        public string? FilePath
        {
            get => _filePath;
            set
            {
                if (_filePath != value)
                {
                    _filePath = value;
                    unchecked { _version++; } // allow overflow benignly
                    CancelThumbnail();
                    OnPropertyChanged();
                    // Thumbnail will be produced lazily by container realization event.
                    ThumbnailSource = null;
                }
            }
        }

        private ImageSource? _thumbnailSource;
        public ImageSource? ThumbnailSource
        {
            get => _thumbnailSource;
            private set // Only set internally after decoding/lookup
            {
                if (_thumbnailSource != value)
                {
                    _thumbnailSource = value;
                    OnPropertyChanged();
                }
            }
        }

        private bool _thumbnailLoading;
        public bool IsThumbnailLoading => _thumbnailLoading;
        public bool HasThumbnail => _thumbnailSource != null;

        /// <summary>
        /// Ensure thumbnail is loaded; performs progressive decode (cache -> low -> delay -> high).
        /// Returns immediately if already loading or present.
        /// </summary>
        public async Task EnsureThumbnailAsync(DispatcherQueue dispatcher)
        {
            if (HasThumbnail || IsThumbnailLoading) return;
            if (string.IsNullOrEmpty(_filePath)) return;
            int localVersion = _version;
            int decodeWidthHi = ThumbnailOptions.DecodePixelWidth;
            int decodeWidthLo = Math.Max(64, decodeWidthHi / 2);

            _thumbnailLoading = true;
            var provider = ThumbnailProviderFactory.Get();
            CancellationTokenSource cts = new();
            _thumbCts = cts; // capture latest CTS
            try
            {
                // 1) High-quality cache check
                var cachedHi = ThumbnailCacheService.Instance.Get(_filePath!, decodeWidthHi);
                if (cachedHi != null)
                {
                    if (localVersion == _version)
                        await DispatcherHelper.RunOnUiAsync(dispatcher, () => { ThumbnailSource = cachedHi; });
                    return;
                }
                // 2) Low-quality cache check (potential quick placeholder)
                var cachedLo = ThumbnailCacheService.Instance.Get(_filePath!, decodeWidthLo);
                if (cachedLo != null && localVersion == _version)
                {
                    await DispatcherHelper.RunOnUiAsync(dispatcher, () => { ThumbnailSource = cachedLo; });
                    // Continue to attempt high-quality upgrade.
                }

                // 3) Decode low-quality if absent
                if (cachedLo == null)
                {
                    ImageSource? loSrc = await GetThumbnailSourceAsync(dispatcher, _filePath!, decodeWidthLo, cts.Token).ConfigureAwait(false);

                    if (cts.IsCancellationRequested) return;
                    if (loSrc != null && localVersion == _version)
                    {
                        await DispatcherHelper.RunOnUiAsync(dispatcher, () =>
                        {
                            ThumbnailSource = ThumbnailSource ?? loSrc;
                            ThumbnailCacheService.Instance.Add(_filePath!, decodeWidthLo, loSrc);
                        });
                    }
                }

                // 4) Adaptive delay (scroll idle heuristic)
                try { await Task.Delay(ThumbnailOptions.ProgressiveDecodeDelay, cts.Token); } 
                catch { if (cts.IsCancellationRequested) return; }

                // 5) Re-check high-quality cache before decoding
                var againCachedHi = ThumbnailCacheService.Instance.Get(_filePath!, decodeWidthHi);
                if (againCachedHi != null)
                {
                    if (localVersion == _version)
                        await DispatcherHelper.RunOnUiAsync(dispatcher, () => { ThumbnailSource = againCachedHi; });
                    return;
                }

                // 6) High-quality decode
                ImageSource? hiSrc = await GetThumbnailSourceAsync(dispatcher, _filePath!, decodeWidthHi, cts.Token).ConfigureAwait(false);

                if (cts.IsCancellationRequested) return;
                if (hiSrc != null && localVersion == _version)
                {
                    await DispatcherHelper.RunOnUiAsync(dispatcher, () =>
                    {
                        ThumbnailSource = hiSrc;
                        ThumbnailCacheService.Instance.Add(_filePath!, decodeWidthHi, hiSrc);
                        ThumbnailCacheService.Instance.Remove(_filePath!, decodeWidthLo);
                    });
                }
            }
            catch (OperationCanceledException) { /* expected on recycle */ }
            finally
            {
                _thumbnailLoading = false;
                if (_thumbCts == cts)
                {
                    _thumbCts.Dispose();
                    _thumbCts = null;
                }
            }
        }

        /// <summary>Cancel any in-flight thumbnail decode.</summary>
        public void CancelThumbnail()
        {
            try { _thumbCts?.Cancel(); }
            catch { }
        }

        /// <summary>Release thumbnail reference (e.g., recycled list item) to lower memory usage.</summary>
        public void UnloadThumbnail()
        {
            CancelThumbnail();
            if (_thumbnailSource != null)
            {
                ThumbnailSource = null;
            }
        }

        /// <summary>
        /// Helper for creating thumbnail ImageSource from bytes or path. Delegates to provider.
        /// Memory images (mem:) resolved via ImageCacheService byte store.
        /// </summary>
        private async Task<ImageSource?> GetThumbnailSourceAsync(DispatcherQueue dispatcher, string filePath, int decodeWidth, CancellationToken token)
        {
            if (filePath.StartsWith("mem:", StringComparison.OrdinalIgnoreCase))
            {
                if (!ImageCacheService.Instance.TryGetMemoryImageBytes(filePath, out var bytes) || bytes == null)
                    return null;
                return await ThumbnailProviderFactory.Get().GetForBytesAsync(dispatcher, bytes, decodeWidth, token).ConfigureAwait(false);
            }
            else
            {
                return await ThumbnailProviderFactory.Get().GetForPathAsync(dispatcher, filePath, decodeWidth, token).ConfigureAwait(false);
            }
        }
    }
}