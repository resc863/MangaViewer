using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace MangaViewer.Services
{
    public sealed class ImageCacheService
    {
        private int _capacity;
        private readonly Dictionary<string, LinkedListNode<CacheEntry>> _map = new(StringComparer.OrdinalIgnoreCase);
        private readonly LinkedList<CacheEntry> _lru = new();
        private static readonly Lazy<ImageCacheService> _instance = new(() => new ImageCacheService(40));
        public static ImageCacheService Instance => _instance.Value;

        private record CacheEntry(string Path, BitmapImage Image);

        // In-memory raw image bytes (mem: keys) with LRU + size limit
        private static readonly Dictionary<string, byte[]> _memoryImages = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, LinkedListNode<string>> _memoryOrderMap = new(StringComparer.OrdinalIgnoreCase);
        private static readonly LinkedList<string> _memoryOrder = new(); // head = most recent
        private static readonly object _memLock = new();
        private static long _memoryTotalBytes;

        // Limits (configurable)
        private static int _maxMemoryImageCount = 2000;                // default max images
        private static long _maxMemoryImageBytes = 2L * 1024 * 1024 * 1024; // default 2GB

        public int MaxMemoryImageCount => _maxMemoryImageCount;
        public long MaxMemoryImageBytes => _maxMemoryImageBytes;

        public void SetMemoryLimits(int? maxCount, long? maxBytes)
        {
            lock (_memLock)
            {
                if (maxCount.HasValue && maxCount.Value > 0) _maxMemoryImageCount = maxCount.Value;
                if (maxBytes.HasValue && maxBytes.Value > 10_000_000) _maxMemoryImageBytes = maxBytes.Value; // enforce sane minimum
                EvictMemory_NoLock();
            }
        }

        public (int imageCount, long totalBytes) GetMemoryUsage()
        {
            lock (_memLock) return (_memoryImages.Count, _memoryTotalBytes);
        }

        public Dictionary<string, int> GetPerGalleryCounts()
        {
            // mem:gid:####.ext -> group by gid
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
                    if (_map.TryGetValue(k, out var lruNode)) { _lru.Remove(lruNode); _map.Remove(k); }
                }
            }
        }

        private ImageCacheService(int capacity) => _capacity = Math.Max(4, capacity);

        public void SetDecodedCacheCapacity(int capacity)
        {
            _capacity = Math.Max(4, capacity);
            Trim();
        }

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
            while ((_memoryImages.Count > _maxMemoryImageCount) || (_memoryTotalBytes > _maxMemoryImageBytes))
            {
                var last = _memoryOrder.Last; if (last == null) break;
                string k = last.Value;
                if (_memoryImages.TryGetValue(k, out var bytes)) _memoryTotalBytes -= bytes.LongLength;
                _memoryImages.Remove(k);
                _memoryOrder.RemoveLast();
                _memoryOrderMap.Remove(k);
                if (_map.TryGetValue(k, out var node))
                {
                    _lru.Remove(node);
                    _map.Remove(k);
                }
            }
        }

        public void ClearMemoryImages()
        {
            lock (_memLock)
            {
                _memoryImages.Clear();
                _memoryOrder.Clear();
                _memoryOrderMap.Clear();
                _memoryTotalBytes = 0;
            }
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

        public BitmapImage? Get(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            if (_map.TryGetValue(path, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                return node.Value.Image;
            }

            BitmapImage? bmp = null;
            try
            {
                if (TryGetMemoryBytes(path, out var memBytes) && memBytes != null)
                {
                    using var ms = new MemoryStream(memBytes, writable: false);
                    bmp = new BitmapImage();
                    bmp.SetSource(ms.AsRandomAccessStream());
                }
                else if (File.Exists(path))
                {
                    bmp = new BitmapImage(new Uri(path));
                }
            }
            catch { return null; }

            if (bmp == null) return null;
            var entry = new CacheEntry(path, bmp);
            var newNode = new LinkedListNode<CacheEntry>(entry);
            _lru.AddFirst(newNode);
            _map[path] = newNode;
            Trim();
            return bmp;
        }

        public void Prefetch(IEnumerable<string> paths)
        {
            foreach (var p in paths)
            {
                if (_map.ContainsKey(p)) continue;
                _ = Get(p);
            }
        }

        private void Trim()
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
