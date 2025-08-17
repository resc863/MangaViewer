using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;

namespace MangaViewer.Services
{
    /// <summary>
    /// 썸네일 전용 LRU 캐시 (DecodePixelWidth=150 가정). UI 스레드 사용 가정.
    /// </summary>
    public sealed class ThumbnailCacheService
    {
        private readonly int _capacity;
        private readonly Dictionary<string, LinkedListNode<CacheEntry>> _map = new(StringComparer.OrdinalIgnoreCase);
        private readonly LinkedList<CacheEntry> _lru = new();
        private static readonly Lazy<ThumbnailCacheService> _instance = new(() => new ThumbnailCacheService(300));
        public static ThumbnailCacheService Instance => _instance.Value;

        private record CacheEntry(string Path, BitmapImage Image);

        private ThumbnailCacheService(int capacity) => _capacity = Math.Max(50, capacity);

        /// <summary>
        /// 캐시에 존재하면 LRU 갱신 후 반환. 없으면 null.
        /// </summary>
        public BitmapImage? Get(string path)
        {
            if (_map.TryGetValue(path, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                return node.Value.Image;
            }
            return null;
        }

        /// <summary>
        /// 새 항목 삽입 (중복/공백 경로 무시)
        /// </summary>
        public void Add(string path, BitmapImage image)
        {
            if (string.IsNullOrWhiteSpace(path) || _map.ContainsKey(path)) return;
            var entry = new CacheEntry(path, image);
            var node = new LinkedListNode<CacheEntry>(entry);
            _lru.AddFirst(node);
            _map[path] = node;
            Trim();
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
