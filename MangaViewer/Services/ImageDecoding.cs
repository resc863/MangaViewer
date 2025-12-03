using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.Storage; // for FileAccessMode

namespace MangaViewer.Services
{
    /// <summary>
    /// IImageDecoder abstraction for producing SoftwareBitmap suitable for OCR.
    /// </summary>
    public interface IImageDecoder
    {
        Task<SoftwareBitmap?> DecodeForOcrAsync(string path, CancellationToken token);
    }

    /// <summary>
    /// WinRtImageDecoder
    /// Strategy:
    ///  - Open file or memory image bytes as IRandomAccessStream.
    ///  - Use BitmapDecoder with scaling transform if image exceeds maximum dimension (4000px).
    ///  - Attempt original pixel format then fall back to BGRA8 then final generic decode.
    ///  - Convert to Gray8 when possible to reduce OCR workload.
    /// Diagnostics: Records success & latency using DiagnosticsService under key 'ImageDecode'.
    /// Modernization: Uses FileRandomAccessStream.OpenAsync for file access (Windows App SDK 1.8+ compatible).
    /// </summary>
    internal sealed class WinRtImageDecoder : IImageDecoder
    {
        public async Task<SoftwareBitmap?> DecodeForOcrAsync(string path, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool success = false;
            try
            {
                IRandomAccessStream? ras = null;
                try
                {
                    if (path.StartsWith("mem:", StringComparison.OrdinalIgnoreCase))
                    {
                        // Memory-based image from cache
                        if (ImageCacheService.Instance.TryGetMemoryImageBytes(path, out var bytes) && bytes != null)
                        {
                            var ms = new MemoryStream(bytes, writable: false);
                            ras = ms.AsRandomAccessStream();
                        }
                    }
                    else
                    {
                        // File-based image: use FileRandomAccessStream (works with Windows App SDK 1.8+)
                        ras = await FileRandomAccessStream.OpenAsync(path, FileAccessMode.Read).AsTask(token);
                    }
                }
                catch { ras = null; }
                if (ras == null) return null;

                var decoder = await BitmapDecoder.CreateAsync(ras);
                token.ThrowIfCancellationRequested();

                var transform = new BitmapTransform();
                uint width = decoder.PixelWidth;
                uint height = decoder.PixelHeight;
                const uint MaxDim = 4000;
                if (width > MaxDim || height > MaxDim)
                {
                    double scale = width > height ? (double)MaxDim / width : (double)MaxDim / height;
                    transform.ScaledWidth = (uint)Math.Max(1, Math.Round(width * scale));
                    transform.ScaledHeight = (uint)Math.Max(1, Math.Round(height * scale));
                }

                SoftwareBitmap bitmap;
                try
                {
                    bitmap = await decoder.GetSoftwareBitmapAsync(decoder.BitmapPixelFormat, BitmapAlphaMode.Ignore, transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
                }
                catch
                {
                    try
                    {
                        bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
                    }
                    catch
                    {
                        bitmap = await decoder.GetSoftwareBitmapAsync();
                    }
                }

                if (bitmap.BitmapPixelFormat != BitmapPixelFormat.Gray8 && bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
                {
                    try { bitmap = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Gray8); } catch { }
                }
                success = true;
                return bitmap;
            }
            finally
            {
                DiagnosticsService.Instance.Record("ImageDecode", success, sw.ElapsedTicks);
            }
        }
    }
}
