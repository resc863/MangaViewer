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

            _lruLock.EnterWriteLock();
            try
            {
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

            return bmp;
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
                            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
                            int bufferSize = (int)Math.Min(4096, fs.Length);
                            var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
                            try
                            {
                                await fs.ReadAsync(buffer.AsMemory(0, bufferSize), ct).ConfigureAwait(false);
                            }
                            finally
                            {
                                ArrayPool<byte>.Shared.Return(buffer);
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
