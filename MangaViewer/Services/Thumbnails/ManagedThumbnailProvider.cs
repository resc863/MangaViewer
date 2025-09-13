using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Streams;

namespace MangaViewer.Services.Thumbnails
{
    /// <summary>
    /// 현재 C# 경로와 동일한 방식으로 썸네일을 생성하는 기본 구현.
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
                ras = await FileRandomAccessStream.OpenAsync(path, FileAccessMode.Read);
                ct.ThrowIfCancellationRequested();
            }
            catch
            {
                ras?.Dispose();
                return null;
            }

            try
            {
                var tcs = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!dispatcher.TryEnqueue(() =>
                {
                    try
                    {
                        var bmp = new BitmapImage { DecodePixelWidth = maxDecodeDim > 0 ? maxDecodeDim : ThumbnailOptions.DecodePixelWidth };
                        bmp.SetSource(ras);
                        ras.Dispose();
                        tcs.TrySetResult(bmp);
                    }
                    catch
                    {
                        ras.Dispose();
                        tcs.TrySetResult(null);
                    }
                }))
                {
                    ras.Dispose();
                    return null;
                }
                return await tcs.Task.ConfigureAwait(false);
            }
            catch { ras?.Dispose(); return null; }
        }

        public Task<ImageSource?> GetForBytesAsync(DispatcherQueue dispatcher, byte[] data, int maxDecodeDim, CancellationToken ct)
        {
            if (data == null || data.Length == 0) return Task.FromResult<ImageSource?>(null);

            var tcs = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!dispatcher.TryEnqueue(() =>
            {
                try
                {
                    using var ms = new MemoryStream(data, writable: false);
                    var bmp = new BitmapImage { DecodePixelWidth = maxDecodeDim > 0 ? maxDecodeDim : ThumbnailOptions.DecodePixelWidth };
                    bmp.SetSource(ms.AsRandomAccessStream());
                    tcs.TrySetResult(bmp);
                }
                catch
                {
                    tcs.TrySetResult(null);
                }
            }))
            {
                tcs.TrySetResult(null);
            }

            return tcs.Task;
        }
    }
}
