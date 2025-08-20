using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace MangaViewer.Services
{
    public sealed class ImageCacheService
    {
        private readonly int _capacity;
        private readonly Dictionary<string, LinkedListNode<CacheEntry>> _map = new(StringComparer.OrdinalIgnoreCase);
        private readonly LinkedList<CacheEntry> _lru = new();
        private static readonly Lazy<ImageCacheService> _instance = new(() => new ImageCacheService(40));
        public static ImageCacheService Instance => _instance.Value;

        private record CacheEntry(string Path, BitmapImage Image);
        private static readonly Dictionary<string, byte[]> _memoryImages = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object _memLock = new();

        private ImageCacheService(int capacity) => _capacity = Math.Max(4, capacity);

        public void AddMemoryImage(string key, byte[] data)
        {
            if (string.IsNullOrWhiteSpace(key) || data == null || data.Length == 0) return;
            lock (_memLock)
            {
                _memoryImages[key] = data; // overwrite ok
            }
        }

        public void ClearMemoryImages()
        {
            lock (_memLock)
            {
                _memoryImages.Clear();
            }
            // remove cached BitmapImages for mem: entries
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
                if (_memoryImages.TryGetValue(key, out var bytes)) { data = bytes; return true; }
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
