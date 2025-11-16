using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Streams;
using System.Runtime.InteropServices.WindowsRuntime;

namespace MangaViewer.Services.Thumbnails
{
    /// <summary>
    /// ManagedThumbnailProvider
    /// Strategy:
    ///  - Performs file open + decode entirely on UI thread using BitmapImage (WinUI limitation for SetSourceAsync).
    ///  - Caches result in ThumbnailCacheService keyed by path + decode width.
    ///  - For memory bytes, wraps them in InMemoryRandomAccessStream before SetSourceAsync.
    /// Reasons for design:
    ///  - Avoid lifetime issues of SoftwareBitmap / SoftwareBitmapSource across rapid virtualization.
    ///  - BitmapImage.SetSourceAsync is stable and inexpensive for small thumbnail decode pixel widths.
    /// Cancellation: If dispatcher enqueue fails or token canceled before operations complete returns null.
    /// </summary>
    public sealed class ManagedThumbnailProvider : IThumbnailProvider
    {
        /// <summary>
        /// Create thumbnail from file path. Attempts cache first (UI thread) then decodes.
        /// </summary>
        public async Task<ImageSource?> GetForPathAsync(DispatcherQueue dispatcher, string path, int maxDecodeDim, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(path)) return null;

            // Double-check cache on UI thread to avoid duplicate work when scrolling quickly.
            var cached = await RunOnUiAsync(dispatcher, () => ThumbnailCacheService.Instance.Get(path, maxDecodeDim)).ConfigureAwait(false);
            if (cached != null) return cached;

            var tcs = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!dispatcher.TryEnqueue(async () =>
            {
                try
                {
                    using var stream = await FileRandomAccessStream.OpenAsync(path, FileAccessMode.Read);
                    var img = new BitmapImage { DecodePixelWidth = maxDecodeDim };
                    await img.SetSourceAsync(stream);
                    ThumbnailCacheService.Instance.Add(path, maxDecodeDim, img);
                    tcs.TrySetResult(img);
                }
                catch { tcs.TrySetResult(null); }
            })) tcs.TrySetResult(null);
            return await tcs.Task.ConfigureAwait(false);
        }

        /// <summary>
        /// Create thumbnail from in-memory bytes (stream gallery).
        /// </summary>
        public async Task<ImageSource?> GetForBytesAsync(DispatcherQueue dispatcher, byte[] data, int maxDecodeDim, CancellationToken ct)
        {
            if (data == null || data.Length == 0) return null;

            var tcs = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!dispatcher.TryEnqueue(async () =>
            {
                try
                {
                    using var ras = new InMemoryRandomAccessStream();
                    await ras.WriteAsync(data.AsBuffer()); ras.Seek(0);
                    var img = new BitmapImage { DecodePixelWidth = maxDecodeDim };
                    await img.SetSourceAsync(ras);
                    tcs.TrySetResult(img);
                }
                catch { tcs.TrySetResult(null); }
            })) return null;
            return await tcs.Task.ConfigureAwait(false);
        }

        private static Task<T?> RunOnUiAsync<T>(DispatcherQueue dispatcher, System.Func<T?> func)
        {
            var tcs = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!dispatcher.TryEnqueue(() => { try { tcs.TrySetResult(func()); } catch (System.Exception ex) { tcs.TrySetException(ex); } })) tcs.TrySetResult(default);
            return tcs.Task;
        }
    }
}
