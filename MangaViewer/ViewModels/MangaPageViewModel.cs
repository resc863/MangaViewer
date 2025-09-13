using MangaViewer.Helpers;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Dispatching;
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
        public string? FilePath
        {
            get => _filePath;
            set
            {
                if (_filePath != value)
                {
                    _filePath = value;
                    unchecked { _version++; }
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

        private static readonly SemaphoreSlim s_decodeGate = new(4); // 동시 디코딩 제한
        private bool _thumbnailLoading;
        public bool IsThumbnailLoading => _thumbnailLoading;
        public bool HasThumbnail => _thumbnailSource != null;

        public async Task EnsureThumbnailAsync(DispatcherQueue dispatcher)
        {
            if (HasThumbnail || IsThumbnailLoading) return;
            if (string.IsNullOrEmpty(_filePath)) return;
            int localVersion = _version;
            // 캐시 조회 우선
            var cached = ThumbnailCacheService.Instance.Get(_filePath);
            if (cached != null)
            {
                // 버전 체크 (중간에 바뀌지 않았는지)
                if (localVersion == _version) ThumbnailSource = cached;
                return;
            }
            _thumbnailLoading = true;

            var provider = ThumbnailProviderFactory.Get();
            try
            {
                await s_decodeGate.WaitAsync();
                if (_filePath.StartsWith("mem:", System.StringComparison.OrdinalIgnoreCase))
                {
                    if (!ImageCacheService.Instance.TryGetMemoryImageBytes(_filePath, out var bytes) || bytes == null)
                    {
                        _thumbnailLoading = false;
                        return;
                    }

                    var src = await provider.GetForBytesAsync(dispatcher, bytes, ThumbnailOptions.DecodePixelWidth, System.Threading.CancellationToken.None);
                    if (src != null && localVersion == _version)
                    {
                        ThumbnailSource = src;
                        ThumbnailCacheService.Instance.Add(_filePath, src);
                    }
                }
                else
                {
                    var src = await provider.GetForPathAsync(dispatcher, _filePath, ThumbnailOptions.DecodePixelWidth, System.Threading.CancellationToken.None);
                    if (src != null && localVersion == _version)
                    {
                        ThumbnailSource = src;
                        ThumbnailCacheService.Instance.Add(_filePath, src);
                    }
                }
            }
            catch { }
            finally
            {
                _thumbnailLoading = false;
                s_decodeGate.Release();
            }
        }

        public void UnloadThumbnail()
        {
            // 선택된 항목이 아니고 썸네일이 있다면 메모리 해제 유도 (참조 제거)
            if (_thumbnailSource != null)
            {
                ThumbnailSource = null;
            }
        }
    }
}