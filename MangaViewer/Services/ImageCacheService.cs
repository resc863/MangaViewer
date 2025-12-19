using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Windows.Storage.Streams;
using MangaViewer.Helpers;
using System.Runtime.InteropServices.WindowsRuntime; // Added for AsBuffer()

namespace MangaViewer.Services
{
    /// <summary>
    /// ImageCacheService - 디코딩된 BitmapImage LRU 캐시 + 프리페치
    /// 메모리 바이트 캐시는 MemoryImageCache로 분리됨
    /// </summary>
    public sealed class ImageCacheService : IDisposable
    {
        private int _capacity;
        private readonly Dictionary<string, LinkedListNode<CacheEntry>> _map = new(StringComparer.OrdinalIgnoreCase);
        private readonly LinkedList<CacheEntry> _lru = new();
        private readonly ReaderWriterLockSlim _lruLock = new(LockRecursionPolicy.NoRecursion);
        private static readonly Lazy<ImageCacheService> _instance = new(() => new ImageCacheService(40));
        public static ImageCacheService Instance => _instance.Value;

        private record CacheEntry(string Path, BitmapImage Image);

        private DispatcherQueue? _dispatcher;
        private readonly MemoryImageCache _memoryCache = MemoryImageCache.Instance;

        // Prefetch infrastructure
        private readonly Channel<string> _prefetchChannel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        private readonly SemaphoreSlim _prefetchGate = new(2, 2);
        private int _isPrefetching;
        private CancellationTokenSource? _prefetchCts;

        public int DecodedCacheCapacity => _capacity;
        public int MaxMemoryImageCount => _memoryCache.MaxCount;
        public long MaxMemoryImageBytes => _memoryCache.MaxBytes;

        private ImageCacheService(int capacity) => _capacity = Math.Max(4, capacity);

        public void InitializeUI(DispatcherQueue dispatcher) => _dispatcher = dispatcher;

        #region Memory Cache Delegation
        public void SetMemoryLimits(int? maxCount, long? maxBytes) => _memoryCache.SetLimits(maxCount, maxBytes);
        public (int imageCount, long totalBytes) GetMemoryUsage() => _memoryCache.GetUsage();
        public Dictionary<string, int> GetPerGalleryCounts() => _memoryCache.GetPerGalleryCounts();

        public void AddMemoryImage(string key, byte[] data) => _memoryCache.Add(key, data);

        public bool TryGetMemoryImageBytes(string key, out byte[]? data) => _memoryCache.TryGet(key, out data);

        public void ClearGalleryMemory(string galleryId)
        {
            var removed = _memoryCache.ClearGallery(galleryId);
            RemoveFromDecodedCache(removed);
        }

        public void ClearMemoryImages()
        {
            var removed = _memoryCache.Clear();
            RemoveFromDecodedCache(removed);
        }

        private void RemoveFromDecodedCache(List<string> keys)
        {
            if (keys.Count == 0) return;

            _lruLock.EnterWriteLock();
            try
            {
                foreach (var k in keys)
                {
                    if (_map.TryGetValue(k, out var node))
                    {
                        _lru.Remove(node);
                        _map.Remove(k);
                    }
                }
            }
            finally
            {
                _lruLock.ExitWriteLock();
            }
        }
        #endregion

        #region Decoded Cache
        public void SetDecodedCacheCapacity(int capacity)
        {
            _capacity = Math.Max(4, capacity);

            _lruLock.EnterWriteLock();
            try
            {
                Trim();
            }
            finally
            {
                _lruLock.ExitWriteLock();
            }
        }

        public BitmapImage? Get(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            _lruLock.EnterUpgradeableReadLock();
            try
            {
                if (_map.TryGetValue(path, out var node))
                {
                    _lruLock.EnterWriteLock();
                    try
                    {
                        _lru.Remove(node);
                        _lru.AddFirst(node);
                    }
                    finally
                    {
                        _lruLock.ExitWriteLock();
                    }
                    return node.Value.Image;
                }
            }
            finally
            {
                _lruLock.ExitUpgradeableReadLock();
            }

            // Cache miss - create new image
            BitmapImage? bmp = null;
            try
            {
                if (_memoryCache.TryGet(path, out var memBytes) && memBytes != null)
                {
                    bmp = CreateBitmapOnUi(() =>
                    {
                        var img = new BitmapImage();
                        using var ras = new InMemoryRandomAccessStream();
                        ras.AsStreamForWrite().Write(memBytes, 0, memBytes.Length);
                        ras.Seek(0);
                        img.SetSource(ras);
                        return img;
                    });
                }
                else if (File.Exists(path))
                {
                    bmp = CreateBitmapOnUi(() => new BitmapImage(new Uri(path)));
                }
            }
            catch { return null; }

            if (bmp == null) return null;

            AddToCache(path, bmp);

            return bmp;
        }

        public async Task<BitmapImage?> GetAsync(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            // 1. Check LRU (Fast path)
            _lruLock.EnterUpgradeableReadLock();
            try
            {
                if (_map.TryGetValue(path, out var node))
                {
                    _lruLock.EnterWriteLock();
                    try
                    {
                        _lru.Remove(node);
                        _lru.AddFirst(node);
                    }
                    finally
                    {
                        _lruLock.ExitWriteLock();
                    }
                    return node.Value.Image;
                }
            }
            finally
            {
                _lruLock.ExitUpgradeableReadLock();
            }

            // 2. Load bytes (Async/Background)
            byte[]? memBytes = null;
            if (_memoryCache.TryGet(path, out var b))
            {
                memBytes = b;
            }
            else if (File.Exists(path))
            {
                try
                {
                    memBytes = await File.ReadAllBytesAsync(path);
                    // Optionally populate memory cache to speed up subsequent accesses
                    _memoryCache.Add(path, memBytes);
                }
                catch { return null; }
            }

            if (memBytes == null) return null;

            // 3. Create BitmapImage on UI Thread
            return await CreateBitmapOnUiAsync(memBytes, path);
        }

        private void AddToCache(string path, BitmapImage bmp)
        {
            _lruLock.EnterWriteLock();
            try
            {
                if (_map.ContainsKey(path)) return;
                var entry = new CacheEntry(path, bmp);
                var newNode = new LinkedListNode<CacheEntry>(entry);
                _lru.AddFirst(newNode);
                _map[path] = newNode;
                Trim();
            }
            finally
            {
                _lruLock.ExitWriteLock();
            }
        }
        #endregion

        #region Prefetch
        public void Prefetch(IEnumerable<string> paths)
        {
            _lruLock.EnterReadLock();
            try
            {
                foreach (var p in paths)
                {
                    if (string.IsNullOrWhiteSpace(p)) continue;
                    if (_map.ContainsKey(p)) continue;
                    _prefetchChannel.Writer.TryWrite(p);
                }
            }
            finally
            {
                _lruLock.ExitReadLock();
            }

            if (Interlocked.CompareExchange(ref _isPrefetching, 1, 0) == 0)
            {
                _prefetchCts?.Cancel();
                _prefetchCts?.Dispose();
                _prefetchCts = new CancellationTokenSource();
                _ = Task.Run(() => PrefetchWorkerAsync(_prefetchCts.Token));
            }
        }

        private async Task PrefetchWorkerAsync(CancellationToken ct)
        {
            try
            {
                await foreach (var path in _prefetchChannel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    await _prefetchGate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        if (path.StartsWith("mem:", StringComparison.OrdinalIgnoreCase))
                        {
                            _memoryCache.TryGet(path, out _);
                        }
                        else if (File.Exists(path))
                        {
                            // Load full file into memory cache
                            if (!_memoryCache.Contains(path))
                            {
                                var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
                                _memoryCache.Add(path, bytes);
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        _prefetchGate.Release();
                        break;
                    }
                    catch { }
                    finally
                    {
                        _prefetchGate.Release();
                    }

                    try
                    {
                        await Task.Delay(16, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _isPrefetching, 0);
            }
        }
        #endregion

        /// <summary>
        /// Display (viewport-fit) cache is separate from the general decoded-cache.
        /// Keyed by: path + decodeLongSide
        /// </summary>
        private readonly Dictionary<string, LinkedListNode<DisplayCacheEntry>> _displayMap = new(StringComparer.OrdinalIgnoreCase);
        private readonly LinkedList<DisplayCacheEntry> _displayLru = new();
        private int _displayCapacity = 24;

        private record DisplayCacheEntry(string Key, string Path, int LongSide, BitmapImage Image);

        private static string MakeDisplayKey(string path, int longSide) => $"{path}|dls={longSide}";

        private BitmapImage? TryGetDisplayCached(string path, int longSide)
        {
            string key = MakeDisplayKey(path, longSide);
            _lruLock.EnterUpgradeableReadLock();
            try
            {
                if (_displayMap.TryGetValue(key, out var node))
                {
                    _lruLock.EnterWriteLock();
                    try
                    {
                        _displayLru.Remove(node);
                        _displayLru.AddFirst(node);
                    }
                    finally { _lruLock.ExitWriteLock(); }
                    return node.Value.Image;
                }
            }
            finally { _lruLock.ExitUpgradeableReadLock(); }
            return null;
        }

        private void AddDisplayCache(string path, int longSide, BitmapImage bmp)
        {
            string key = MakeDisplayKey(path, longSide);
            _lruLock.EnterWriteLock();
            try
            {
                if (_displayMap.ContainsKey(key)) return;
                var entry = new DisplayCacheEntry(key, path, longSide, bmp);
                var node = new LinkedListNode<DisplayCacheEntry>(entry);
                _displayLru.AddFirst(node);
                _displayMap[key] = node;
                TrimDisplay_NoLock();
            }
            finally { _lruLock.ExitWriteLock(); }
        }

        private void TrimDisplay_NoLock()
        {
            while (_displayLru.Count > _displayCapacity)
            {
                var last = _displayLru.Last;
                if (last == null) break;
                _displayLru.RemoveLast();
                _displayMap.Remove(last.Value.Key);
            }
        }

        /// <summary>
        /// Get a BitmapImage decoded to fit the given viewport size (in DIPs) WITHOUT breaking aspect ratio.
        /// Uses DecodePixelWidth only (long side target) so WinUI keeps original aspect ratio.
        /// </summary>
        public async Task<BitmapImage?> GetForViewportAsync(string path, double viewportWidthDip, double viewportHeightDip, double rasterizationScale)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            if (viewportWidthDip <= 0 || viewportHeightDip <= 0) return await GetAsync(path).ConfigureAwait(false);

            rasterizationScale = Math.Max(0.1, rasterizationScale);
            int targetW = (int)Math.Clamp(Math.Ceiling(viewportWidthDip * rasterizationScale), 1, 16384);
            int targetH = (int)Math.Clamp(Math.Ceiling(viewportHeightDip * rasterizationScale), 1, 16384);
            int longSide = Math.Max(targetW, targetH);

            // 1) Display cache fast path
            var cached = TryGetDisplayCached(path, longSide);
            if (cached != null) return cached;

            // 2) Load bytes
            byte[]? memBytes = null;
            if (_memoryCache.TryGet(path, out var b))
            {
                memBytes = b;
            }
            else if (File.Exists(path))
            {
                try
                {
                    memBytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
                    _memoryCache.Add(path, memBytes);
                }
                catch { return null; }
            }

            if (memBytes == null) return null;

            // 3) Create BitmapImage on UI thread with DecodePixelWidth only
            return await CreateViewportBitmapOnUiAsync(memBytes, path, longSide).ConfigureAwait(false);
        }

        private async Task<BitmapImage?> CreateViewportBitmapOnUiAsync(byte[] data, string path, int decodeLongSide)
        {
            if (_dispatcher == null) return null;

            var tcs = new TaskCompletionSource<BitmapImage?>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool enqueued = _dispatcher.TryEnqueue(async () =>
            {
                try
                {
                    var cached = TryGetDisplayCached(path, decodeLongSide);
                    if (cached != null)
                    {
                        tcs.TrySetResult(cached);
                        return;
                    }

                    var img = new BitmapImage
                    {
                        DecodePixelWidth = decodeLongSide
                    };

                    using var ras = new InMemoryRandomAccessStream();
                    await ras.WriteAsync(data.AsBuffer());
                    ras.Seek(0);
                    await img.SetSourceAsync(ras);

                    AddDisplayCache(path, decodeLongSide, img);
                    tcs.TrySetResult(img);
                }
                catch
                {
                    tcs.TrySetResult(null);
                }
            });

            if (!enqueued) return null;
            return await tcs.Task.ConfigureAwait(false);
        }

        private BitmapImage? CreateBitmapOnUi(Func<BitmapImage> factory)
        {
            if (_dispatcher != null && _dispatcher.HasThreadAccess)
            {
                try { return factory(); }
                catch { return null; }
            }

            if (_dispatcher == null)
            {
                try { return factory(); }
                catch { return null; }
            }

            try
            {
                return DispatcherHelper.RunOnUiAsync(_dispatcher, factory)
                    .ConfigureAwait(false)
                    .GetAwaiter()
                    .GetResult();
            }
            catch { return null; }
        }

        private async Task<BitmapImage?> CreateBitmapOnUiAsync(byte[] data, string path)
        {
            if (_dispatcher == null) return null;

            var tcs = new TaskCompletionSource<BitmapImage?>(TaskCreationOptions.RunContinuationsAsynchronously);

            bool enqueued = _dispatcher.TryEnqueue(async () =>
            {
                try
                {
                    var img = new BitmapImage();
                    using var ras = new InMemoryRandomAccessStream();
                    await ras.WriteAsync(data.AsBuffer());
                    ras.Seek(0);
                    await img.SetSourceAsync(ras);

                    AddToCache(path, img);
                    tcs.TrySetResult(img);
                }
                catch
                {
                    tcs.TrySetResult(null);
                }
            });

            if (!enqueued) return null;
            return await tcs.Task.ConfigureAwait(false);
        }

        private void Trim()
        {
            while (_lru.Count > _capacity)
            {
                var last = _lru.Last;
                if (last == null) break;
                _lru.RemoveLast();
                _map.Remove(last.Value.Path);
            }
        }

        public void Dispose()
        {
            _prefetchCts?.Cancel();
            _prefetchCts?.Dispose();
            _prefetchGate?.Dispose();
            _prefetchChannel.Writer.Complete();
            _lruLock?.Dispose();
        }
    }
}
