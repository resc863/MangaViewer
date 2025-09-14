using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace MangaViewer.Services.Thumbnails
{
    /// <summary>
    /// ���� C# ���� ����� ���ι��̴�. ���ڵ�� ��׶��忡�� �����ϰ�,
    /// UI �����忡���� ���� PNG�� SetSource �ϴ� ������ �۾��� �����մϴ�.
    ///
    /// [�ߴ��� ������ ����]
    /// - SoftwareBitmap �Ǵ� SoftwareBitmapSource�� ĳ�ÿ� �����ϰų�, ���� Image ��Ʈ�ѿ��� �����ϸ�
    ///   �����̳� ��Ȱ��/���� ��ũ�� �� ���ε� ����/����ε� Ÿ�ֿ̹� ������ ���� WinRT ���� ����Ƽ�� ����(Access Violation ��)�� �߻��� �� �ֽ��ϴ�.
    /// - ���� SoftwareBitmapSource.SetBitmapAsync�� UI ������ ������ ���ϰ�, ���� �ν��Ͻ��� ���� ������/���� ���ε��� ���� ũ���� ������ Ů�ϴ�.
    ///
    /// [����å]
    /// - ���ڵ� ��� SoftwareBitmap�� �ӽ÷θ� ���� ��� InMemory PNG�� ���ڵ��Ͽ� ���� ������ ��Ʈ������ ��ȯ�մϴ�.
    /// - UI �����忡���� BitmapImage.SetSourceAsync(stream)�� ȣ���Ͽ� ImageSource�� �����ϰ�, �� BitmapImage�� ĳ�ÿ� ����/�����մϴ�.
    /// - ��� WinRT ���ҽ�(stream, bitmap)�� ��� ���� Ȯ���� Dispose �Ͽ� ���� ������ �����մϴ�.
    /// - UI ������ �ݵ�� DispatcherQueue�� ���� �����մϴ�.
    /// </summary>
    public sealed class ManagedThumbnailProvider : IThumbnailProvider
    {
        public async Task<ImageSource?> GetForPathAsync(DispatcherQueue dispatcher, string path, int maxDecodeDim, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(path)) return null;

            IRandomAccessStream? ras = null;
            try
            {
                ras = await FileRandomAccessStream.OpenAsync(path, FileAccessMode.Read).AsTask(ct).ConfigureAwait(false);
            }
            catch
            {
                ras?.Dispose();
                return null;
            }

            try
            {
                var sb = await DecodeAsync(ras, maxDecodeDim, ct).ConfigureAwait(false);
                ras.Dispose();
                if (sb == null) return null;

                var png = await EncodeToPngAsync(sb, ct).ConfigureAwait(false);
                sb.Dispose();
                if (png == null) return null;

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
                ras?.Dispose();
                return null;
            }
        }

        public async Task<ImageSource?> GetForBytesAsync(DispatcherQueue dispatcher, byte[] data, int maxDecodeDim, CancellationToken ct)
        {
            if (data == null || data.Length == 0) return null;

            InMemoryRandomAccessStream? ras = null;
            DataWriter? writer = null;
            try
            {
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

                // ū �� ���� ��� ���ڵ� (�������� ����)
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

                // BGRA8 Premultiplied�� ��ȯ (XAML�� ȣȯ)
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
    }
}
