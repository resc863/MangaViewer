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
    public class MangaPageViewModel : BaseViewModel
    {
        private string? _filePath;
        private int _version; // FilePath 변경 버전. 비동기 작업이 완료될 때 일치해야 반영.
        private CancellationTokenSource? _thumbCts;
        public string? FilePath
        {
            get => _filePath;
            set
            {
                if (_filePath != value)
                {
                    _filePath = value;
                    unchecked { _version++; }
                    CancelThumbnail();
                    OnPropertyChanged();
                    // 실제 썸네일 생성은 ListView.ContainerContentChanging 이벤트에서 지연 로딩
                    ThumbnailSource = null;
                }
            }
        }

        private ImageSource? _thumbnailSource;
        public ImageSource? ThumbnailSource
        {
            get => _thumbnailSource;
            private set // Setter is private, controlled by the FilePath property
            {
                if (_thumbnailSource != value)
                {
                    _thumbnailSource = value;
                    OnPropertyChanged();
                }
            }
        }

        // 동시 디코딩 수를 장치에 맞춰 설정 (디스크 캐시 없이 메모리/CPU 밸런스)
        private static readonly SemaphoreSlim s_decodeGate = new(Math.Clamp(Environment.ProcessorCount / 2, 4, 8));
        private bool _thumbnailLoading;
        public bool IsThumbnailLoading => _thumbnailLoading;
        public bool HasThumbnail => _thumbnailSource != null;

        public async Task EnsureThumbnailAsync(DispatcherQueue dispatcher)
        {
            if (HasThumbnail || IsThumbnailLoading) return;
            if (string.IsNullOrEmpty(_filePath)) return;
            int localVersion = _version;
            int decodeWidthHi = ThumbnailOptions.DecodePixelWidth;
            int decodeWidthLo = Math.Max(64, decodeWidthHi / 2);

            // 1) 캐시 우선 (고품질 → 저품질)
            var cachedHi = await RunOnUiAsync(dispatcher, () => ThumbnailCacheService.Instance.Get(_filePath!, decodeWidthHi)).ConfigureAwait(false);
            if (cachedHi != null)
            {
                if (localVersion == _version)
                    await RunOnUiAsync(dispatcher, () => { ThumbnailSource = cachedHi; }).ConfigureAwait(false);
                return;
            }
            var cachedLo = await RunOnUiAsync(dispatcher, () => ThumbnailCacheService.Instance.Get(_filePath!, decodeWidthLo)).ConfigureAwait(false);
            if (cachedLo != null && localVersion == _version)
            {
                await RunOnUiAsync(dispatcher, () => { ThumbnailSource = cachedLo; }).ConfigureAwait(false);
                // 나중에 고품질 업그레이드 시도 (아래에서 수행)
            }

            _thumbnailLoading = true;
            var provider = ThumbnailProviderFactory.Get();

            CancellationTokenSource cts = new();
            _thumbCts = cts; // 최신 요청 토큰 저장
            bool acquired = false;
            try
            {
                // 2) 저품질 빠른 디코드 (이미 저품질 캐시 없음 또는 표시가 필요한 경우)
                if (cachedLo == null)
                {
                    await s_decodeGate.WaitAsync(cts.Token).ConfigureAwait(false);
                    acquired = true;

                    ImageSource? loSrc;
                    if (_filePath!.StartsWith("mem:", System.StringComparison.OrdinalIgnoreCase))
                    {
                        if (!ImageCacheService.Instance.TryGetMemoryImageBytes(_filePath, out var bytes) || bytes == null)
                            return;
                        loSrc = await provider.GetForBytesAsync(dispatcher, bytes, decodeWidthLo, cts.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        loSrc = await provider.GetForPathAsync(dispatcher, _filePath!, decodeWidthLo, cts.Token).ConfigureAwait(false);
                    }

                    if (cts.IsCancellationRequested) return;
                    if (loSrc != null && localVersion == _version)
                    {
                        await RunOnUiAsync(dispatcher, () =>
                        {
                            // 먼저 저품질 표시
                            ThumbnailSource = ThumbnailSource ?? loSrc; // 이미 캐시로 세팅됐으면 유지
                            ThumbnailCacheService.Instance.Add(_filePath!, decodeWidthLo, loSrc);
                        }).ConfigureAwait(false);
                    }

                    s_decodeGate.Release();
                    acquired = false;
                }

                // 3) 잠깐 대기하여 스크롤 idle 유도 후 고품질 업그레이드
                try { await Task.Delay(140, cts.Token).ConfigureAwait(false); } catch { if (cts.IsCancellationRequested) return; }
                await s_decodeGate.WaitAsync(cts.Token).ConfigureAwait(false);
                acquired = true;

                // 재확인: 이미 고품질 캐시가 생겼는지
                var againCachedHi = await RunOnUiAsync(dispatcher, () => ThumbnailCacheService.Instance.Get(_filePath!, decodeWidthHi)).ConfigureAwait(false);
                if (againCachedHi != null)
                {
                    if (localVersion == _version)
                        await RunOnUiAsync(dispatcher, () => { ThumbnailSource = againCachedHi; }).ConfigureAwait(false);
                    return;
                }

                ImageSource? hiSrc;
                if (_filePath!.StartsWith("mem:", System.StringComparison.OrdinalIgnoreCase))
                {
                    if (!ImageCacheService.Instance.TryGetMemoryImageBytes(_filePath, out var bytes) || bytes == null)
                        return;
                    hiSrc = await provider.GetForBytesAsync(dispatcher, bytes, decodeWidthHi, cts.Token).ConfigureAwait(false);
                }
                else
                {
                    hiSrc = await provider.GetForPathAsync(dispatcher, _filePath!, decodeWidthHi, cts.Token).ConfigureAwait(false);
                }

                if (cts.IsCancellationRequested) return;
                if (hiSrc != null && localVersion == _version)
                {
                    await RunOnUiAsync(dispatcher, () =>
                    {
                        ThumbnailSource = hiSrc; // 업그레이드
                        ThumbnailCacheService.Instance.Add(_filePath!, decodeWidthHi, hiSrc);
                    }).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { /* ignore */ }
            finally
            {
                if (acquired) s_decodeGate.Release();
                _thumbnailLoading = false;
                if (_thumbCts == cts)
                {
                    _thumbCts.Dispose();
                    _thumbCts = null;
                }
            }
        }

        public void CancelThumbnail()
        {
            try { _thumbCts?.Cancel(); }
            catch { }
        }

        public void UnloadThumbnail()
        {
            // 스크롤로 화면에서 벗어남: 진행 중 디코드 취소 + 바인딩 해제
            CancelThumbnail();
            if (_thumbnailSource != null)
            {
                ThumbnailSource = null;
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

        private static Task RunOnUiAsync(DispatcherQueue dispatcher, System.Action action)
        {
            var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!dispatcher.TryEnqueue(() =>
            {
                try { action(); tcs.TrySetResult(null); }
                catch (System.Exception ex) { tcs.TrySetException(ex); }
            }))
            {
                tcs.TrySetResult(null);
            }
            return tcs.Task;
        }
    }
}