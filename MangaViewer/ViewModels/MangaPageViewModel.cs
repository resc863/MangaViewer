using MangaViewer.Helpers;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Dispatching;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage.Streams;
using MangaViewer.Services;

namespace MangaViewer.ViewModels
{
    public class MangaPageViewModel : BaseViewModel
    {
        private string? _filePath;
        private int _version; // FilePath 변경 버전.
        private bool _isPlaceholder = true;
        public bool IsPlaceholder
        {
            get => _isPlaceholder;
            private set { if (_isPlaceholder != value) { _isPlaceholder = value; OnPropertyChanged(); } }
        }
        public string? FilePath
        {
            get => _filePath;
            set
            {
                if (_filePath == value) return;
                _filePath = value;
                unchecked { _version++; }
                OnPropertyChanged();
                if (!string.IsNullOrEmpty(_filePath)) IsPlaceholder = false; else IsPlaceholder = true;
                ThumbnailSource = null; // 지연 로드
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

        private static readonly SemaphoreSlim s_decodeGate = new(4);
        private bool _thumbnailLoading;
        public bool IsThumbnailLoading => _thumbnailLoading;
        public bool HasThumbnail => _thumbnailSource != null;

        public async Task EnsureThumbnailAsync(DispatcherQueue dispatcher)
        {
            if (HasThumbnail || _thumbnailLoading || string.IsNullOrEmpty(_filePath)) return;
            int localVersion = _version;

            // mem: immediate load via cache bytes
            if (_filePath.StartsWith("mem:", StringComparison.OrdinalIgnoreCase))
            {
                if (ImageCacheService.Instance.TryGetMemoryImageBytes(_filePath, out var bytes) && bytes != null)
                {
                    try
                    {
                        using var ms = new MemoryStream(bytes, writable: false);
                        var bmp = new BitmapImage { DecodePixelWidth = 150 };
                        bmp.SetSource(ms.AsRandomAccessStream());
                        if (localVersion == _version) ThumbnailSource = bmp;
                    }
                    catch { }
                }
                return;
            }

            // 캐시
            var cached = ThumbnailCacheService.Instance.Get(_filePath);
            if (cached != null)
            {
                if (localVersion == _version) ThumbnailSource = cached;
                return;
            }

            _thumbnailLoading = true;
            byte[]? buffer = null;
            try
            {
                await s_decodeGate.WaitAsync();
                try
                {
                    if (File.Exists(_filePath))
                    {
                        using var fs = File.OpenRead(_filePath);
                        const int MaxReadBytes = 5 * 1024 * 1024; // 5MB
                        long len = fs.Length;
                        if (len <= MaxReadBytes)
                        {
                            buffer = new byte[len];
                            int readTotal = 0;
                            while (readTotal < len)
                            {
                                int r = fs.Read(buffer, readTotal, (int)(len - readTotal));
                                if (r <= 0) break;
                                readTotal += r;
                            }
                        }
                        else
                        {
                            buffer = new byte[MaxReadBytes];
                            int readTotal = 0;
                            while (readTotal < MaxReadBytes)
                            {
                                int r = fs.Read(buffer, readTotal, MaxReadBytes - readTotal);
                                if (r <= 0) break;
                                readTotal += r;
                            }
                            if (readTotal < MaxReadBytes) Array.Resize(ref buffer, readTotal);
                        }
                    }
                }
                finally { s_decodeGate.Release(); }
            }
            catch
            {
                _thumbnailLoading = false;
                return;
            }

            if (buffer == null || buffer.Length == 0 || localVersion != _version)
            {
                _thumbnailLoading = false;
                return;
            }

            if (!dispatcher.TryEnqueue(() =>
            {
                try
                {
                    if (localVersion != _version || string.IsNullOrEmpty(_filePath)) { _thumbnailLoading = false; return; }
                    using var ms = new MemoryStream(buffer);
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
            }))
            {
                _thumbnailLoading = false; // enqueue 실패
            }
        }

        public void UnloadThumbnail()
        {
            if (_thumbnailSource != null)
                ThumbnailSource = null;
        }
    }
}