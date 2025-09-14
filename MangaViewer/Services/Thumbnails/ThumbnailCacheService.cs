using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;

namespace MangaViewer.Services.Thumbnails
{
    /// <summary>
    /// ����� ���� LRU ĳ��. Decode ���� Ű�� �����Ͽ� ���� ���� �� �浹�� �����մϴ�.
    /// UI �����忡���� ����ؾ� �մϴ�.
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
        /// ĳ�ÿ� �����ϸ� LRU ���� �� ��ȯ. ������ null.
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
        /// �� �׸� ���� (�ߺ�/���� ��� ����)
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
        /// ��ü ĳ�ø� ���ϴ�.
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
