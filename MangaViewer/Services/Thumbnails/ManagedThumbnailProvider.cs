using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace MangaViewer.Services.Thumbnails
{
    /// <summary>
    /// Managed thumbnail provider that decodes images off the UI thread and creates a UI-friendly
    /// BitmapImage on the UI thread using a small in-memory PNG stream.
    ///
    /// Rationale:
    /// - SoftwareBitmap/SoftwareBitmapSource are fragile to share across bindings/threads.
    /// - Encoding to PNG and calling BitmapImage.SetSourceAsync on the UI thread is robust and cheap.
    /// - All WinRT resources are disposed ASAP to avoid lifetime races during fast scrolling.
    /// </summary>
    public sealed class ManagedThumbnailProvider : IThumbnailProvider
    {
        // Coalesce concurrent requests per (path,width) to avoid redundant decode work.
        private static readonly ConcurrentDictionary<string, Task<ImageSource?>> s_inflightPath = new();

        /// <summary>
        /// 파일 경로로부터 썸네일을 생성합니다.
        /// 1) 백그라운드에서 디코드 → 2) PNG 인코드 → 3) UI 스레드에서 BitmapImage 생성 및 캐시.
        /// </summary>
        public async Task<ImageSource?> GetForPathAsync(DispatcherQueue dispatcher, string path, int maxDecodeDim, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(path)) return null;

            // Double-check cache on UI thread to avoid duplicate work when scrolling quickly.
            var cached = await RunOnUiAsync(dispatcher, () => ThumbnailCacheService.Instance.Get(path, maxDecodeDim)).ConfigureAwait(false);
            if (cached != null) return cached;

            string key = ThumbnailCacheService.MakeKey(path, maxDecodeDim);
            var task = s_inflightPath.GetOrAdd(key, _ => DecodeCreateAndCachePathAsync(dispatcher, path, maxDecodeDim));
            try
            {
                using var _ = ct.Register(() => { });
                return await task.ConfigureAwait(false);
            }
            finally
            {
                s_inflightPath.TryRemove(key, out _);
            }
        }

        /// <summary>
        /// 메모리 바이트 배열로부터 썸네일을 생성합니다.
        /// </summary>
        public async Task<ImageSource?> GetForBytesAsync(DispatcherQueue dispatcher, byte[] data, int maxDecodeDim, CancellationToken ct)
        {
            if (data == null || data.Length == 0) return null;

            InMemoryRandomAccessStream? ras = null;
            DataWriter? writer = null;
            try
            {
                // Materialize provided bytes into an IRandomAccessStream for decoder
                ras = new InMemoryRandomAccessStream();
                writer = new DataWriter(ras);
                writer.WriteBytes(data);
                await writer.StoreAsync().AsTask(ct).ConfigureAwait(false);
                writer.DetachStream();
                writer.Dispose(); writer = null;
                ras.Seek(0);

                var sb = await DecodeAsync(ras, maxDecodeDim, ct).ConfigureAwait(false);
                ras.Dispose();
                if (sb == null) return null;

                var png = await EncodeToPngAsync(sb, ct).ConfigureAwait(false);
                sb.Dispose();
                if (png == null) return null;

                // Produce BitmapImage on UI thread
                var tcs = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!dispatcher.TryEnqueue(async () =>
                {
                    try
                    {
                        var img = new BitmapImage();
                        await img.SetSourceAsync(png);
                        png.Dispose();
                        tcs.TrySetResult(img);
                    }
                    catch
                    {
                        try { png.Dispose(); } catch { }
                        tcs.TrySetResult(null);
                    }
                }))
                {
                    try { png.Dispose(); } catch { }
                    return null;
                }
                return await tcs.Task.ConfigureAwait(false);
            }
            catch
            {
                writer?.Dispose();
                ras?.Dispose();
                return null;
            }
        }

        // Decode downscaled BGRA8 SoftwareBitmap honoring EXIF orientation and sRGB color management.
        private static async Task<SoftwareBitmap?> DecodeAsync(IRandomAccessStream ras, int requestedMaxDim, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var decoder = await BitmapDecoder.CreateAsync(ras).AsTask(ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();

                uint srcW = decoder.PixelWidth;
                uint srcH = decoder.PixelHeight;
                if (srcW == 0 || srcH == 0) return null;

                int target = requestedMaxDim > 0 ? requestedMaxDim : ThumbnailOptions.DecodePixelWidth;
                target = Math.Clamp(target, 32, 1024);

                // Scale by the longest side; never upscale
                double scale = (double)target / Math.Max(srcW, srcH);
                if (scale > 1.0) scale = 1.0;
                uint scaledW = (uint)Math.Max(1, Math.Round(srcW * scale));
                uint scaledH = (uint)Math.Max(1, Math.Round(srcH * scale));

                var transform = new BitmapTransform
                {
                    ScaledWidth = scaledW,
                    ScaledHeight = scaledH,
                    InterpolationMode = BitmapInterpolationMode.Fant
                };

                var sb = await decoder.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    transform,
                    ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.ColorManageToSRgb
                ).AsTask(ct).ConfigureAwait(false);

                return sb;
            }
            catch
            {
                return null;
            }
        }

        // Encode SoftwareBitmap to an in-memory PNG stream.
        private static async Task<InMemoryRandomAccessStream?> EncodeToPngAsync(SoftwareBitmap sb, CancellationToken ct)
        {
            try
            {
                var stream = new InMemoryRandomAccessStream();
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream).AsTask(ct).ConfigureAwait(false);
                encoder.SetSoftwareBitmap(sb);
                encoder.IsThumbnailGenerated = false;
                await encoder.FlushAsync().AsTask(ct).ConfigureAwait(false);
                stream.Seek(0);
                return stream;
            }
            catch
            {
                return null;
            }
        }

        private static Task<T?> RunOnUiAsync<T>(DispatcherQueue dispatcher, System.Func<T?> func)
        {
            var tcs = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!dispatcher.TryEnqueue(() =>
            {
                try { tcs.TrySetResult(func()); }
                catch (System.Exception ex) { tcs.TrySetException(ex); }
            }))
            {
                tcs.TrySetResult(default);
            }
            return tcs.Task;
        }

        private static async Task<ImageSource?> DecodeCreateAndCachePathAsync(DispatcherQueue dispatcher, string path, int maxDecodeDim)
        {
            IRandomAccessStream? ras = null;
            try
            {
                // Open file stream (background thread)
                ras = await FileRandomAccessStream.OpenAsync(path, FileAccessMode.Read).AsTask().ConfigureAwait(false);
            }
            catch
            {
                ras?.Dispose();
                return null;
            }

            try
            {
                // Decode downscaled SoftwareBitmap
                var sb = await DecodeAsync(ras, maxDecodeDim, CancellationToken.None).ConfigureAwait(false);
                ras.Dispose();
                if (sb == null) return null;

                // Encode to PNG (keeps only raw bytes to be consumed on UI thread safely)
                var png = await EncodeToPngAsync(sb, CancellationToken.None).ConfigureAwait(false);
                sb.Dispose();
                if (png == null) return null;

                // Create BitmapImage on UI thread and cache it
                var tcs = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!dispatcher.TryEnqueue(async () =>
                {
                    try
                    {
                        var img = new BitmapImage();
                        await img.SetSourceAsync(png);
                        png.Dispose();
                        ThumbnailCacheService.Instance.Add(path, maxDecodeDim, img);
                        tcs.TrySetResult(img);
                    }
                    catch
                    {
                        try { png.Dispose(); } catch { }
                        tcs.TrySetResult(null);
                    }
                }))
                {
                    try { png.Dispose(); } catch { }
                    return null;
                }
                return await tcs.Task.ConfigureAwait(false);
            }
            catch
            {
                ras?.Dispose();
                return null;
            }
        }
    }
}
