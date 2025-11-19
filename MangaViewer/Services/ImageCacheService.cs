using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using Windows.Storage.Streams;

namespace MangaViewer.Services
{
    /// <summary>
    /// ImageCacheService
    /// Purpose: Manage two tiers of image caching:
    ///  1) Memory Original Bytes ("mem:" keys) with LRU + capacity (count/bytes) constraints.
    ///  2) Decoded BitmapImage objects keyed by file or mem path (LRU of fixed capacity).
    /// Design Notes:
    ///  - UI-thread creation requirement: BitmapImage must be constructed on UI thread; service accepts a DispatcherQueue.
    ///  - mem: images originate from streaming downloads (EhentaiService) and stored as raw bytes for later decoding.
    ///  - Prefetch feature reads ahead image file headers (or minimal bytes) to warm file system cache without decoding.
    /// Thread Safety:
    ///  - Separate locks: _memLock for mem byte cache; _lruLock for decoded cache. Avoids contention across tiers.
    ///  - Public APIs that combine both tiers acquire locks in consistent order (memLock then lruLock) to prevent deadlocks.
    /// Failure Handling: IO and decoding exceptions swallowed; methods return null or perform no-op on failure.
    /// Extension Ideas:
    ///  - Disk-backed eviction persistence for mem: images.
    ///  - Adaptive prefetch radius based on scroll velocity.
    ///  - Image format metadata cache for dimension queries without decoding.
    /// </summary>
    public sealed class ImageCacheService
    {
        private int _capacity;
        private readonly Dictionary<string, LinkedListNode<CacheEntry>> _map = new(StringComparer.OrdinalIgnoreCase);
        private readonly LinkedList<CacheEntry> _lru = new();
        private static readonly Lazy<ImageCacheService> _instance = new(() => new ImageCacheService(40));
        public static ImageCacheService Instance => _instance.Value;

        private record CacheEntry(string Path, BitmapImage Image);

        private readonly object _lruLock = new();
        private readonly ConcurrentDictionary<string, Task> _inflightPrefetch = new(StringComparer.OrdinalIgnoreCase);

        // UI dispatcher to create XAML objects safely
        private DispatcherQueue? _dispatcher;
        /// <summary>
        /// Initialize UI DispatcherQueue (must be called early on UI thread).
        /// </summary>
        public void InitializeUI(DispatcherQueue dispatcher) => _dispatcher = dispatcher;

        // In-memory byte cache for streaming galleries (mem:gid:####.ext)
        private static readonly Dictionary<string, byte[]> _memoryImages = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, LinkedListNode<string>> _memoryOrderMap = new(StringComparer.OrdinalIgnoreCase);
        private static readonly LinkedList<string> _memoryOrder = new();
        private static readonly object _memLock = new();
        private static long _memoryTotalBytes;

        private static int _maxMemoryImageCount = 2000;
        private static long _maxMemoryImageBytes = 2L * 1024 * 1024 * 1024; // 2GB default

        public int MaxMemoryImageCount => _maxMemoryImageCount;
        public long MaxMemoryImageBytes => _maxMemoryImageBytes;
        public int DecodedCacheCapacity => _capacity;

        /// <summary>
        /// Set memory byte cache limits; evicts excess immediately.
        /// </summary>
        public void SetMemoryLimits(int? maxCount, long? maxBytes)
        {
            lock (_memLock)
            {
                if (maxCount.HasValue && maxCount.Value > 0) _maxMemoryImageCount = maxCount.Value;
                if (maxBytes.HasValue && maxBytes.Value > 10_000_000) _maxMemoryImageBytes = maxBytes.Value;
                EvictMemory_NoLock();
            }
        }

        /// <summary>Get current memory usage snapshot.</summary>
        public (int imageCount, long totalBytes) GetMemoryUsage()
        {
            lock (_memLock) return (_memoryImages.Count, _memoryTotalBytes);
        }

        /// <summary>
        /// Build per-gallery mem: image counts (galleryId -> image count).
        /// </summary>
        public Dictionary<string, int> GetPerGalleryCounts()
        {
            var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            lock (_memLock)
            {
                foreach (var key in _memoryImages.Keys)
                {
                    if (!key.StartsWith("mem:", StringComparison.OrdinalIgnoreCase)) continue;
                    var parts = key.Split(':');
                    if (parts.Length < 3) continue;
                    string gid = parts[1];
                    dict.TryGetValue(gid, out int c);
                    dict[gid] = c + 1;
                }
            }
            return dict;
        }

        /// <summary>
        /// Remove all mem: images for a specific gallery and related decoded entries.
        /// </summary>
        public void ClearGalleryMemory(string galleryId)
        {
            if (string.IsNullOrWhiteSpace(galleryId)) return;
            lock (_memLock)
            {
                var remove = _memoryImages.Keys.Where(k => k.StartsWith("mem:" + galleryId + ":", StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var k in remove)
                {
                    if (_memoryImages.TryGetValue(k, out var bytes)) _memoryTotalBytes -= bytes.LongLength;
                    _memoryImages.Remove(k);
                    if (_memoryOrderMap.TryGetValue(k, out var node)) { _memoryOrder.Remove(node); _memoryOrderMap.Remove(k); }
                    lock (_lruLock)
                    {
                        if (_map.TryGetValue(k, out var lruNode)) { _lru.Remove(lruNode); _map.Remove(k); }
                    }
                }
            }
        }

        private ImageCacheService(int capacity) => _capacity = Math.Max(4, capacity);

        /// <summary>
        /// Set decoded BitmapImage cache capacity (evicts if reduced).
        /// </summary>
        public void SetDecodedCacheCapacity(int capacity)
        {
            _capacity = Math.Max(4, capacity);
            lock (_lruLock) Trim_NoLock();
        }

        /// <summary>
        /// Add or replace mem: image bytes; updates LRU ordering and evicts if limits exceeded.
        /// </summary>
        public void AddMemoryImage(string key, byte[] data)
        {
            if (string.IsNullOrWhiteSpace(key) || data == null || data.Length == 0) return;
            if (!key.StartsWith("mem:", StringComparison.OrdinalIgnoreCase)) return;
            lock (_memLock)
            {
                if (_memoryImages.TryGetValue(key, out var old))
                {
                    _memoryTotalBytes -= old.LongLength;
                    _memoryImages[key] = data;
                    _memoryTotalBytes += data.LongLength;
                    if (_memoryOrderMap.TryGetValue(key, out var node))
                    {
                        _memoryOrder.Remove(node);
                        _memoryOrder.AddFirst(node);
                    }
                }
                else
                {
                    var node = new LinkedListNode<string>(key);
                    _memoryOrder.AddFirst(node);
                    _memoryOrderMap[key] = node;
                    _memoryImages[key] = data;
                    _memoryTotalBytes += data.LongLength;
                }
                EvictMemory_NoLock();
            }
        }

        private void EvictMemory_NoLock()
        {
            var keysToRemove = new List<string>();
            
            while ((_memoryImages.Count > _maxMemoryImageCount) || (_memoryTotalBytes > _maxMemoryImageBytes))
            {
                var last = _memoryOrder.Last; if (last == null) break;
                string k = last.Value;
                if (_memoryImages.TryGetValue(k, out var bytes)) _memoryTotalBytes -= bytes.LongLength;
                _memoryImages.Remove(k);
                _memoryOrder.RemoveLast();
                _memoryOrderMap.Remove(k);
                keysToRemove.Add(k);
            }
            
            // Remove from LRU cache outside of nested lock
            if (keysToRemove.Count > 0)
            {
                lock (_lruLock)
                {
                    foreach (var k in keysToRemove)
                    {
                        if (_map.TryGetValue(k, out var node))
                        {
                            _lru.Remove(node);
                            _map.Remove(k);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Clear all mem: images and associated decoded entries.
        /// </summary>
        public void ClearMemoryImages()
        {
            lock (_memLock)
            {
                _memoryImages.Clear();
                _memoryOrder.Clear();
                _memoryOrderMap.Clear();
                _memoryTotalBytes = 0;
            }
            lock (_lruLock)
            {
                var toRemove = new List<string>();
                foreach (var kv in _map)
                {
                    if (kv.Key.StartsWith("mem:", StringComparison.OrdinalIgnoreCase))
                        toRemove.Add(kv.Key);
                }
                foreach (var k in toRemove)
                {
                    if (_map.TryGetValue(k, out var node))
                    {
                        _lru.Remove(node);
                        _map.Remove(k);
                    }
                }
            }
        }

        /// <summary>
        /// Try get mem: raw bytes and update LRU ordering.
        /// </summary>
        public bool TryGetMemoryImageBytes(string key, out byte[]? data)
        {
            data = null;
            if (string.IsNullOrWhiteSpace(key) || !key.StartsWith("mem:", StringComparison.OrdinalIgnoreCase)) return false;
            lock (_memLock)
            {
                if (_memoryImages.TryGetValue(key, out var bytes))
                {
                    if (_memoryOrderMap.TryGetValue(key, out var node))
                    {
                        _memoryOrder.Remove(node);
                        _memoryOrder.AddFirst(node);
                    }
                    data = bytes; return true;
                }
            }
            return false;
        }

        private static bool TryGetMemoryBytes(string path, out byte[]? bytes)
        {
            bytes = null;
            if (!path.StartsWith("mem:", StringComparison.OrdinalIgnoreCase)) return false;
            lock (_memLock)
            {
                if (_memoryImages.TryGetValue(path, out var d)) { bytes = d; return true; }
            }
            return false;
        }

        /// <summary>
        /// Get decoded BitmapImage from cache or create new one (UI thread) and add to LRU.
        /// </summary>
        public BitmapImage? Get(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            lock (_lruLock)
            {
                if (_map.TryGetValue(path, out var node))
                {
                    _lru.Remove(node);
                    _lru.AddFirst(node);
                    return node.Value.Image;
                }
            }

            BitmapImage? bmp = null;
            try
            {
                if (TryGetMemoryBytes(path, out var memBytes) && memBytes != null)
                {
                    bmp = CreateBitmapOnUi(() =>
                    {
                        var img = new BitmapImage();
                        using (var ras = new InMemoryRandomAccessStream())
                        {
                            ras.AsStreamForWrite().Write(memBytes, 0, memBytes.Length);
                            ras.Seek(0);
                            img.SetSource(ras);
                        }
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
            lock (_lruLock)
            {
                var entry = new CacheEntry(path, bmp);
                var newNode = new LinkedListNode<CacheEntry>(entry);
                _lru.AddFirst(newNode);
                _map[path] = newNode;
                Trim_NoLock();
            }
            return bmp;
        }

        // Prefetch concurrency gate
        private readonly SemaphoreSlim _prefetchGate = new(2); // limit 2 concurrent prefetches
        private readonly ConcurrentQueue<string> _prefetchQueue = new();
        private int _isPrefetching = 0; // 0 = false, 1 = true (thread-safe)

        /// <summary>
        /// Prefetch given paths: reads minimal bytes (not full decode) to warm OS caches; avoids decoding overhead.
        /// </summary>
        public void Prefetch(IEnumerable<string> paths)
        {
            foreach (var p in paths)
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                lock (_lruLock) { if (_map.ContainsKey(p)) continue; }
                _prefetchQueue.Enqueue(p);
            }
            
            // Start worker if not already running
            if (Interlocked.CompareExchange(ref _isPrefetching, 1, 0) == 0)
            {
                _ = Task.Run(() => PrefetchWorker());
            }
        }

        private async Task PrefetchWorker()
        {
            while (_prefetchQueue.TryDequeue(out var path))
            {
                await _prefetchGate.WaitAsync();
                try
                {
                    if (path.StartsWith("mem:", StringComparison.OrdinalIgnoreCase))
                    {
                        TryGetMemoryImageBytes(path, out _); // already cached in memory
                    }
                    else if (File.Exists(path))
                    {
                        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        var buffer = new byte[Math.Min(4096, fs.Length)];
                        await fs.ReadAsync(buffer, 0, buffer.Length);
                    }
                }
                catch { }
                finally
                {
                    _prefetchGate.Release();
                    await Task.Delay(16); // throttle
                }
            }
            
            Interlocked.Exchange(ref _isPrefetching, 0);
        }

        private BitmapImage? CreateBitmapOnUi(Func<BitmapImage> factory)
        {
            if (_dispatcher != null && _dispatcher.HasThreadAccess)
            {
                try { return factory(); } catch { return null; }
            }

            if (_dispatcher == null)
            {
                try { return factory(); } catch { return null; }
            }

            var tcs = new TaskCompletionSource<BitmapImage?>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_dispatcher.TryEnqueue(() =>
            {
                try { tcs.TrySetResult(factory()); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            }))
            {
                tcs.TrySetResult(null);
            }
            try { return tcs.Task.GetAwaiter().GetResult(); } catch { return null; }
        }

        private void Trim_NoLock()
        {
            while (_lru.Count > _capacity)
            {
                var last = _lru.Last; if (last == null) break;
                _lru.RemoveLast();
                _map.Remove(last.Value.Path);
            }
        }
    }
}
