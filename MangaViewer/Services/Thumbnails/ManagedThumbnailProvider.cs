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
        /// 메모리 바이트 배열로부터 썸네일을 생성합니다.
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
