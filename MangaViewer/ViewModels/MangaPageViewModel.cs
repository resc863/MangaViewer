using MangaViewer.Helpers;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Dispatching;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage.Streams;
using MangaViewer.Services;

namespace MangaViewer.ViewModels
{
    /// <summary>
    /// 단일 이미지(페이지) 단위 ViewModel. 썸네일 지연 생성/해제 관리.
    /// </summary>
    public class MangaPageViewModel : BaseViewModel
    {
        private string? _filePath;
        private int _version; // FilePath 변경 버전 (비동기 작업 레이스 방지)

        public string? FilePath
        {
            get => _filePath;
            set
            {
                if (_filePath == value) return;
                _filePath = value;
                unchecked { _version++; }
                OnPropertyChanged();
                // 실제 썸네일 생성은 ListView.ContainerContentChanging 시점에서 EnsureThumbnailAsync 호출
                ThumbnailSource = null;
            }
        }

        private BitmapImage? _thumbnailSource;
        public BitmapImage? ThumbnailSource
        {
            get => _thumbnailSource;
            private set
            {
                if (_thumbnailSource == value) return;
                _thumbnailSource = value;
                OnPropertyChanged();
            }
        }

        private static readonly SemaphoreSlim s_decodeGate = new(4); // 동시 디코딩 제한
        private bool _thumbnailLoading;
        public bool IsThumbnailLoading => _thumbnailLoading;
        public bool HasThumbnail => _thumbnailSource != null;

        /// <summary>
        /// 썸네일을 보장. 이미 있거나 로딩 중이면 바로 반환.
        /// 디코딩은 제한된 동시성으로 수행 후 UI 스레드에서 BitmapImage 생성.
        /// </summary>
        public async Task EnsureThumbnailAsync(DispatcherQueue dispatcher)
        {
            if (HasThumbnail || IsThumbnailLoading || string.IsNullOrEmpty(_filePath)) return;
            int localVersion = _version;

            // 캐시 우선 조회
            var cached = ThumbnailCacheService.Instance.Get(_filePath);
            if (cached != null)
            {
                if (localVersion == _version) ThumbnailSource = cached; // 버전 일치 시만 적용
                return;
            }

            _thumbnailLoading = true;
            byte[]? bytes = null;
            try
            {
                await s_decodeGate.WaitAsync();
                try
                {
                    using var fs = File.OpenRead(_filePath);
                    using var ms = new MemoryStream();
                    await fs.CopyToAsync(ms);
                    bytes = ms.ToArray();
                }
                finally { s_decodeGate.Release(); }
            }
            catch { _thumbnailLoading = false; return; }

            if (bytes == null || bytes.Length == 0) { _thumbnailLoading = false; return; }

            // UI 스레드에서 BitmapImage 생성 & 적용
            if (!dispatcher.TryEnqueue(() => ApplyThumbnail(bytes, localVersion)))
            {
                _thumbnailLoading = false; // Dispatcher enqueue 실패
            }
        }

        private void ApplyThumbnail(byte[] bytes, int localVersion)
        {
            try
            {
                if (string.IsNullOrEmpty(_filePath) || localVersion != _version) { _thumbnailLoading = false; return; }
                using var ms = new MemoryStream(bytes);
                var bmp = new BitmapImage { DecodePixelWidth = 150 };
                bmp.SetSource(ms.AsRandomAccessStream());
                if (localVersion == _version)
                {
                    ThumbnailSource = bmp;
                    ThumbnailCacheService.Instance.Add(_filePath, bmp);
                }
            }
            catch { }
            finally { _thumbnailLoading = false; }
        }

        /// <summary>
        /// 선택 해제된 항목의 썸네일 참조 제거 (GC 유도).
        /// </summary>
        public void UnloadThumbnail()
        {
            if (_thumbnailSource != null) ThumbnailSource = null;
        }
    }
}