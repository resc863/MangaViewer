using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;

namespace MangaViewer.Services.Thumbnails
{
    /// <summary>
    /// 썸네일 ImageSource LRU 캐시.
    /// - 키는 "{path}|w={decodeWidth}" 형식입니다.
    /// - UI 스레드에서만 접근하도록 설계되었습니다(생성/바인딩 안전).
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
        /// 캐시 키 생성: 경로와 디코드 너비를 조합.
        /// </summary>
        public static string MakeKey(string path, int decodeWidth) => $"{path}|w={decodeWidth}";

        // 바이트 단위 캐시 상한
        private readonly long _maxBytes = 128 * 1024 * 1024; // 128MB
        private long _currentBytes = 0;
        private static long EstimateImageBytes(ImageSource image, int decodeWidth)
        {
            // BitmapImage는 PixelWidth/PixelHeight를 알 수 있으나, 안전하게 decodeWidth^2*4로 추정
            return decodeWidth * decodeWidth * 4;
        }
        /// <summary>
        /// 키가 존재하면 LRU 갱신 후 이미지 반환, 없으면 null.
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
        /// 새 항목을 추가합니다(중복/용량 초과 자동 처리).
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
        /// 저해상도/고해상도 썸네일 제거 지원 (고해상도 업그레이드 시 메모리 절감)
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
        /// 전체 캐시를 비웁니다.
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
                // decodeWidth 추출
                int decodeWidth = 256;
                var parts = last.Value.Key.Split("|w=");
                if (parts.Length == 2 && int.TryParse(parts[1], out int w)) decodeWidth = w;
                _currentBytes -= EstimateImageBytes(last.Value.Image, decodeWidth);
            }
        }
    }
}
