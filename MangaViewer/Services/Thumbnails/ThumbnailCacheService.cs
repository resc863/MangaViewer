using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;

namespace MangaViewer.Services.Thumbnails
{
    /// <summary>
    /// 썸네일 전용 LRU 캐시. Decode 폭을 키에 포함하여 설정 변경 시 충돌을 방지합니다.
    /// UI 스레드에서만 사용해야 합니다.
    /// </summary>
    public sealed class ThumbnailCacheService
    {
        private readonly int _capacity;
        private readonly Dictionary<string, LinkedListNode<CacheEntry>> _map = new(StringComparer.OrdinalIgnoreCase);
        private readonly LinkedList<CacheEntry> _lru = new();
        private static readonly Lazy<ThumbnailCacheService> _instance = new(() => new ThumbnailCacheService(300));
        public static ThumbnailCacheService Instance => _instance.Value;

        private record CacheEntry(string Key, ImageSource Image);

        private ThumbnailCacheService(int capacity) => _capacity = Math.Max(50, capacity);

        public static string MakeKey(string path, int decodeWidth) => $"{path}|w={decodeWidth}";

        /// <summary>
        /// 캐시에 존재하면 LRU 갱신 후 반환. 없으면 null.
        /// </summary>
        public ImageSource? Get(string path, int decodeWidth)
        {
            string key = MakeKey(path, decodeWidth);
            if (_map.TryGetValue(key, out var node))
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
        public void Add(string path, int decodeWidth, ImageSource image)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            string key = MakeKey(path, decodeWidth);
            if (_map.ContainsKey(key)) return;
            var entry = new CacheEntry(key, image);
            var node = new LinkedListNode<CacheEntry>(entry);
            _lru.AddFirst(node);
            _map[key] = node;
            Trim();
        }

        /// <summary>
        /// 전체 캐시를 비웁니다.
        /// </summary>
        public void Clear()
        {
            _map.Clear();
            _lru.Clear();
        }

        private void Trim()
        {
            while (_lru.Count > _capacity)
            {
                var last = _lru.Last; if (last == null) break;
                _lru.RemoveLast();
                _map.Remove(last.Value.Key);
            }
        }
    }
}
