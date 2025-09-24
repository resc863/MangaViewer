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
using System.Runtime.InteropServices.WindowsRuntime;

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
        /// ���� ��ηκ��� ������� �����մϴ�.
        /// 1) ��׶��忡�� ���ڵ� �� 2) PNG ���ڵ� �� 3) UI �����忡�� BitmapImage ���� �� ĳ��.
        /// </summary>
        public async Task<ImageSource?> GetForPathAsync(DispatcherQueue dispatcher, string path, int maxDecodeDim, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(path)) return null;

            // Double-check cache on UI thread to avoid duplicate work when scrolling quickly.
            var cached = await RunOnUiAsync(dispatcher, () => ThumbnailCacheService.Instance.Get(path, maxDecodeDim)).ConfigureAwait(false);
            if (cached != null) return cached;

            // PNG �����ڵ� ���� ���: BitmapImage.DecodePixelWidth ���
            var tcs = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!dispatcher.TryEnqueue(async () =>
            {
                try
                {
                    var img = new BitmapImage();
                    img.DecodePixelWidth = maxDecodeDim;
                    using var stream = await FileRandomAccessStream.OpenAsync(path, FileAccessMode.Read);
                    await img.SetSourceAsync(stream);
                    ThumbnailCacheService.Instance.Add(path, maxDecodeDim, img);
                    tcs.TrySetResult(img);
                }
                catch
                {
                    tcs.TrySetResult(null);
                }
            }))
            {
                tcs.TrySetResult(null);
            }
            return await tcs.Task.ConfigureAwait(false);
        }

        /// <summary>
        /// �޸� ����Ʈ �迭�κ��� ������� �����մϴ�.
        /// </summary>
        public async Task<ImageSource?> GetForBytesAsync(DispatcherQueue dispatcher, byte[] data, int maxDecodeDim, CancellationToken ct)
        {
            if (data == null || data.Length == 0) return null;

            InMemoryRandomAccessStream? ras = null;
            try
            {
                // DataWriter ����, ���� WriteAsync ���
                ras = new InMemoryRandomAccessStream();
                await ras.WriteAsync(data.AsBuffer());
                ras.Seek(0);

                var sb = await DecodeAsync(ras, maxDecodeDim, ct).ConfigureAwait(false);
                ras.Dispose();
                if (sb == null) return null;

                // Produce BitmapImage on UI thread
                var tcs = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!dispatcher.TryEnqueue(async () =>
                {
                    try
                    {
                        var img = new BitmapImage();
                        img.SetSource(ras);
                        tcs.TrySetResult(img);
                    }
                    catch
                    {
                        tcs.TrySetResult(null);
                    }
                }))
                {
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
    }
}
