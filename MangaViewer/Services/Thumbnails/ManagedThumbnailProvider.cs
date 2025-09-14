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
    /// 순수 C# 구현 썸네일 프로바이더. 디코드는 백그라운드에서 수행하고,
    /// UI 스레드에서는 소형 PNG를 SetSource 하는 가벼운 작업만 수행합니다.
    ///
    /// [중대한 오류의 원인]
    /// - SoftwareBitmap 또는 SoftwareBitmapSource를 캐시에 공유하거나, 여러 Image 컨트롤에서 재사용하면
    ///   컨테이너 재활용/빠른 스크롤 중 바인딩 해제/재바인딩 타이밍에 수명이 꼬여 WinRT 내부 네이티브 예외(Access Violation 등)가 발생할 수 있습니다.
    /// - 또한 SoftwareBitmapSource.SetBitmapAsync는 UI 스레드 제약이 강하고, 같은 인스턴스를 교차 스레드/복수 바인딩에 쓰면 크래시 위험이 큽니다.
    ///
    /// [예방책]
    /// - 디코드 결과 SoftwareBitmap은 임시로만 쓰고 즉시 InMemory PNG로 인코딩하여 순수 데이터 스트림으로 변환합니다.
    /// - UI 스레드에서만 BitmapImage.SetSourceAsync(stream)을 호출하여 ImageSource를 생성하고, 이 BitmapImage만 캐시에 저장/공유합니다.
    /// - 모든 WinRT 리소스(stream, bitmap)는 사용 직후 확실히 Dispose 하여 수명 경합을 제거합니다.
    /// - UI 접근은 반드시 DispatcherQueue를 통해 실행합니다.
    /// </summary>
    public sealed class ManagedThumbnailProvider : IThumbnailProvider
    {
        // inflight coalescing only for disk-path source (mem: 바이트 기반은 키가 없어 단순화)
        private static readonly ConcurrentDictionary<string, Task<ImageSource?>> s_inflightPath = new();

        public async Task<ImageSource?> GetForPathAsync(DispatcherQueue dispatcher, string path, int maxDecodeDim, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(path)) return null;

            // 0) 캐시 재확인 (호출측에서도 확인하지만 중복 방지차 이중 확인)
            var cached = await RunOnUiAsync(dispatcher, () => ThumbnailCacheService.Instance.Get(path, maxDecodeDim)).ConfigureAwait(false);
            if (cached != null) return cached;

            string key = ThumbnailCacheService.MakeKey(path, maxDecodeDim);
            // 공유 태스크 생성 (개별 취소 토큰은 공유 태스크를 중단하지 않음)
            var task = s_inflightPath.GetOrAdd(key, _ => DecodeCreateAndCachePathAsync(dispatcher, path, maxDecodeDim));
            try
            {
                // 개별 호출 취소는 여기서만 반영
                using var reg = ct.Register(() => { /* no-op: just allow caller to stop awaiting */ });
                var result = await task.ConfigureAwait(false);
                return result;
            }
            finally
            {
                // 완료/실패 후 inflight 제거 (다음 요청을 새로 생성하도록)
                s_inflightPath.TryRemove(key, out _);
            }
        }

        private static async Task<ImageSource?> DecodeCreateAndCachePathAsync(DispatcherQueue dispatcher, string path, int maxDecodeDim)
        {
            IRandomAccessStream? ras = null;
            try
            {
                ras = await FileRandomAccessStream.OpenAsync(path, FileAccessMode.Read).AsTask().ConfigureAwait(false);
            }
            catch
            {
                ras?.Dispose();
                return null;
            }

            try
            {
                var sb = await DecodeAsync(ras, maxDecodeDim, CancellationToken.None).ConfigureAwait(false);
                ras.Dispose();
                if (sb == null) return null;

                var png = await EncodeToPngAsync(sb, CancellationToken.None).ConfigureAwait(false);
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

                // 큰 변 기준 축소 디코드 (업스케일 금지)
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

                // BGRA8 Premultiplied로 변환 (XAML과 호환)
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
