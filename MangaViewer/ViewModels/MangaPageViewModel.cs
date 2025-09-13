using MangaViewer.Helpers;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Dispatching;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage; // added for FileAccessMode
using Windows.Storage.Streams;
using MangaViewer.Services;

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

        private BitmapImage? _thumbnailSource;
        public BitmapImage? ThumbnailSource
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

            // 메모리/파일 별도 경로 준비
            byte[]? bytes = null;
            IRandomAccessStream? ras = null;
            try
            {
                await s_decodeGate.WaitAsync();
                try
                {
                    if (_filePath.StartsWith("mem:", StringComparison.OrdinalIgnoreCase))
                    {
                        // In-memory image bytes
                        if (!ImageCacheService.Instance.TryGetMemoryImageBytes(_filePath, out bytes) || bytes == null)
                        {
                            bytes = null; // fall through -> handled below
                        }
                    }
                    else
                    {
                        // 전체 파일 스트림을 열어 디코더가 필요 데이터만 읽도록 한다 (부분 읽기 금지)
                        ras = await FileRandomAccessStream.OpenAsync(_filePath, FileAccessMode.Read);
                    }
                }
                finally
                {
                    s_decodeGate.Release();
                }
            }
            catch
            {
                _thumbnailLoading = false;
                ras?.Dispose();
                return; // 실패 무시
            }

            if ((bytes == null || bytes.Length == 0) && ras == null)
            {
                _thumbnailLoading = false;
                return;
            }

            // UI 스레드에서 BitmapImage 생성 및 Source 설정
            if (!dispatcher.TryEnqueue(() =>
            {
                try
                {
                    // 항목이 재활용되어 다른 파일로 바뀌었으면 중단
                    if (string.IsNullOrEmpty(_filePath) || localVersion != _version)
                    {
                        _thumbnailLoading = false; ras?.Dispose(); return;
                    }

                    var bmp = new BitmapImage();
                    bmp.DecodePixelWidth = 150;

                    if (ras != null)
                    {
                        bmp.SetSource(ras);
                        ras.Dispose();
                    }
                    else if (bytes != null)
                    {
                        using var ms = new MemoryStream(bytes, writable: false);
                        bmp.SetSource(ms.AsRandomAccessStream());
                    }

                    if (localVersion == _version)
                    {
                        ThumbnailSource = bmp;
                        ThumbnailCacheService.Instance.Add(_filePath, bmp);
                    }
                }
                catch { }
                finally { _thumbnailLoading = false; }
            }))
            {
                _thumbnailLoading = false; // 디스패처 큐 enqueue 실패
                ras?.Dispose();
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