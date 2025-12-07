using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace MangaViewer.Services
{
    /// <summary>
    /// MemoryImageCache - 스트리밍 갤러리용 메모리 바이트 캐시
    /// LRU + 용량(개수/바이트) 제한으로 관리
    /// </summary>
    public sealed class MemoryImageCache
    {
        private static readonly Lazy<MemoryImageCache> _instance = new(() => new MemoryImageCache());
        public static MemoryImageCache Instance => _instance.Value;

        private readonly ConcurrentDictionary<string, byte[]> _images = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, LinkedListNode<string>> _orderMap = new(StringComparer.OrdinalIgnoreCase);
        private readonly LinkedList<string> _order = new();
        private readonly object _lock = new();
        private long _totalBytes;

        private int _maxCount = 2000;
        private long _maxBytes = 2L * 1024 * 1024 * 1024; // 2GB

        public int MaxCount => _maxCount;
        public long MaxBytes => _maxBytes;
        public int Count => _images.Count;
        public long TotalBytes => Interlocked.Read(ref _totalBytes);

        private MemoryImageCache() { }

        /// <summary>
        /// 메모리 캐시 제한 설정
        /// </summary>
        public void SetLimits(int? maxCount, long? maxBytes)
        {
            lock (_lock)
            {
                if (maxCount.HasValue && maxCount.Value > 0) _maxCount = maxCount.Value;
                if (maxBytes.HasValue && maxBytes.Value > 10_000_000) _maxBytes = maxBytes.Value;
                Evict();
            }
        }

        /// <summary>
        /// 현재 메모리 사용량 조회
        /// </summary>
        public (int imageCount, long totalBytes) GetUsage()
        {
            lock (_lock)
            {
                return (_images.Count, Interlocked.Read(ref _totalBytes));
            }
        }

        /// <summary>
        /// 갤러리별 이미지 수 조회
        /// </summary>
        public Dictionary<string, int> GetPerGalleryCounts()
        {
            var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            lock (_lock)
            {
                foreach (var key in _images.Keys)
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
        /// 메모리 이미지 추가 또는 갱신
        /// </summary>
        public void Add(string key, byte[] data)
        {
            if (string.IsNullOrWhiteSpace(key) || data == null || data.Length == 0) return;
            if (!key.StartsWith("mem:", StringComparison.OrdinalIgnoreCase)) return;

            lock (_lock)
            {
                if (_images.TryGetValue(key, out var old))
                {
                    Interlocked.Add(ref _totalBytes, -old.LongLength);
                    _images[key] = data;
                    Interlocked.Add(ref _totalBytes, data.LongLength);

                    if (_orderMap.TryGetValue(key, out var node))
                    {
                        _order.Remove(node);
                        _order.AddFirst(node);
                    }
                }
                else
                {
                    _images[key] = data;
                    Interlocked.Add(ref _totalBytes, data.LongLength);

                    var node = new LinkedListNode<string>(key);
                    _order.AddFirst(node);
                    _orderMap[key] = node;
                }

                Evict();
            }
        }

        /// <summary>
        /// 메모리 이미지 조회 (LRU 갱신)
        /// </summary>
        public bool TryGet(string key, out byte[]? data)
        {
            data = null;
            if (string.IsNullOrWhiteSpace(key) || !key.StartsWith("mem:", StringComparison.OrdinalIgnoreCase))
                return false;

            if (_images.TryGetValue(key, out var bytes))
            {
                lock (_lock)
                {
                    if (_orderMap.TryGetValue(key, out var node))
                    {
                        _order.Remove(node);
                        _order.AddFirst(node);
                    }
                }
                data = bytes;
                return true;
            }

            return false;
        }

        /// <summary>
        /// 특정 갤러리의 모든 메모리 이미지 제거
        /// </summary>
        public List<string> ClearGallery(string galleryId)
        {
            var removed = new List<string>();
            if (string.IsNullOrWhiteSpace(galleryId)) return removed;

            lock (_lock)
            {
                foreach (var key in _images.Keys.ToArray())
                {
                    if (key.StartsWith("mem:" + galleryId + ":", StringComparison.OrdinalIgnoreCase))
                    {
                        if (_images.TryRemove(key, out var bytes))
                        {
                            Interlocked.Add(ref _totalBytes, -bytes.LongLength);
                        }

                        if (_orderMap.TryRemove(key, out var node))
                        {
                            _order.Remove(node);
                        }

                        removed.Add(key);
                    }
                }
            }

            return removed;
        }

        /// <summary>
        /// 모든 메모리 이미지 제거
        /// </summary>
        public List<string> Clear()
        {
            List<string> allKeys;

            lock (_lock)
            {
                allKeys = _images.Keys.ToList();
                _images.Clear();
                _order.Clear();
                _orderMap.Clear();
                Interlocked.Exchange(ref _totalBytes, 0);
            }

            return allKeys;
        }

        /// <summary>
        /// 키 존재 여부 확인
        /// </summary>
        public bool Contains(string key) => _images.ContainsKey(key);

        private void Evict()
        {
            while ((_images.Count > _maxCount) || (Interlocked.Read(ref _totalBytes) > _maxBytes))
            {
                var last = _order.Last;
                if (last == null) break;

                string k = last.Value;
                if (_images.TryRemove(k, out var bytes))
                {
                    Interlocked.Add(ref _totalBytes, -bytes.LongLength);
                }

                _order.RemoveLast();
                _orderMap.TryRemove(k, out _);
            }
        }
    }
}
