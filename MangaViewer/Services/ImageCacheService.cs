using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.IO;

namespace MangaViewer.Services
{
    /// <summary>
    /// 간단한 LRU 메모리 캐시 (BitmapImage). UI 스레드 사용 가정.
    /// </summary>
    public sealed class ImageCacheService
    {
        private readonly int _capacity;
        private readonly Dictionary<string, LinkedListNode<CacheEntry>> _map = new(StringComparer.OrdinalIgnoreCase);
        private readonly LinkedList<CacheEntry> _lru = new();
        private static readonly Lazy<ImageCacheService> _instance = new(() => new ImageCacheService(40));
        public static ImageCacheService Instance => _instance.Value;

        private ImageCacheService(int capacity) => _capacity = Math.Max(4, capacity);
        private record CacheEntry(string Path, BitmapImage Image);

        /// <summary>
        /// 경로 이미지 가져오기. 캐시에 없으면 로드 후 삽입.
        /// </summary>
        public BitmapImage? Get(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            if (_map.TryGetValue(path, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                return node.Value.Image;
            }
            var bmp = new BitmapImage(new Uri(path));
            var entry = new CacheEntry(path, bmp);
            var newNode = new LinkedListNode<CacheEntry>(entry);
            _lru.AddFirst(newNode);
            _map[path] = newNode;
            Trim();
            return bmp;
        }

        /// <summary>
        /// 단순 선행 로드. 이미 있으면 건너뜀.
        /// </summary>
        public void Prefetch(IEnumerable<string> paths)
        {
            foreach (var p in paths)
            {
                if (_map.ContainsKey(p)) continue;
                Get(p);
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
