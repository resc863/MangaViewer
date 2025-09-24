using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;

namespace MangaViewer.Services.Thumbnails
{
    /// <summary>
    /// ����� ImageSource LRU ĳ��.
    /// - Ű�� "{path}|w={decodeWidth}" �����Դϴ�.
    /// - UI �����忡���� �����ϵ��� ����Ǿ����ϴ�(����/���ε� ����).
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

        /// <summary>
        /// ĳ�� Ű ����: ��ο� ���ڵ� �ʺ� ����.
        /// </summary>
        public static string MakeKey(string path, int decodeWidth) => $"{path}|w={decodeWidth}";

        // ����Ʈ ���� ĳ�� ����
        private readonly long _maxBytes = 128 * 1024 * 1024; // 128MB
        private long _currentBytes = 0;
        private static long EstimateImageBytes(ImageSource image, int decodeWidth)
        {
            // BitmapImage�� PixelWidth/PixelHeight�� �� �� ������, �����ϰ� decodeWidth^2*4�� ����
            return decodeWidth * decodeWidth * 4;
        }
        /// <summary>
        /// Ű�� �����ϸ� LRU ���� �� �̹��� ��ȯ, ������ null.
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
        /// �� �׸��� �߰��մϴ�(�ߺ�/�뷮 �ʰ� �ڵ� ó��).
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
            _currentBytes += EstimateImageBytes(image, decodeWidth);
            Trim();
        }

        /// <summary>
        /// ���ػ�/���ػ� ����� ���� ���� (���ػ� ���׷��̵� �� �޸� ����)
        /// </summary>
        public void Remove(string path, int decodeWidth)
        {
            string key = MakeKey(path, decodeWidth);
            if (_map.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _map.Remove(key);
                _currentBytes -= EstimateImageBytes(node.Value.Image, decodeWidth);
            }
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
            while (_lru.Count > _capacity || _currentBytes > _maxBytes)
            {
                var last = _lru.Last; if (last == null) break;
                _lru.RemoveLast();
                _map.Remove(last.Value.Key);
                // decodeWidth ����
                int decodeWidth = 256;
                var parts = last.Value.Key.Split("|w=");
                if (parts.Length == 2 && int.TryParse(parts[1], out int w)) decodeWidth = w;
                _currentBytes -= EstimateImageBytes(last.Value.Image, decodeWidth);
            }
        }
    }
}
