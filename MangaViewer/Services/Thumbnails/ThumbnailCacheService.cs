using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Threading;

namespace MangaViewer.Services.Thumbnails
{
    /// <summary>
    /// ThumbnailCacheService
    /// Purpose: Thread-safe LRU cache for decoded ImageSource thumbnails keyed by path + decode width.
    /// Characteristics:
    ///  - Capacity limited both by entry count (_capacity) and approximate memory footprint (128MB soft limit).
    ///  - Image byte size estimation uses (decodeWidth^2 * 4) as rough RGBA approximation.
    ///  - Thread-safe implementation using ReaderWriterLockSlim for optimal read performance.
    ///  - Removal API allows discarding outdated lower-quality thumbnail when high-quality replacement arrives.
    /// </summary>
    public sealed class ThumbnailCacheService : IDisposable
    {
        private readonly int _capacity;
        private readonly Dictionary<string, LinkedListNode<CacheEntry>> _map = new(StringComparer.OrdinalIgnoreCase);
        private readonly LinkedList<CacheEntry> _lru = new();
        private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
        private static readonly Lazy<ThumbnailCacheService> _instance = new(() => new ThumbnailCacheService(300));
        public static ThumbnailCacheService Instance => _instance.Value;

        private record CacheEntry(string Key, ImageSource Image, int DecodeWidth);

        private ThumbnailCacheService(int capacity) => _capacity = Math.Max(50, capacity);

        /// <summary>
        /// Make cache key combining path and decode width.
        /// </summary>
        public static string MakeKey(string path, int decodeWidth) => $"{path}|w={decodeWidth}";

        private readonly long _maxBytes = 128 * 1024 * 1024; // 128MB soft limit
        private long _currentBytes = 0;
        private static long EstimateImageBytes(int decodeWidth) => (long)decodeWidth * decodeWidth * 4;

        /// <summary>
        /// Get thumbnail from cache if exists and update LRU ordering, returns null if not found.
        /// Thread-safe read operation.
        /// </summary>
        public ImageSource? Get(string path, int decodeWidth)
        {
            string key = MakeKey(path, decodeWidth);
            
            _lock.EnterUpgradeableReadLock();
            try
            {
                if (_map.TryGetValue(key, out var node))
                {
                    _lock.EnterWriteLock();
                    try
                    {
                        _lru.Remove(node);
                        _lru.AddFirst(node);
                    }
                    finally
                    {
                        _lock.ExitWriteLock();
                    }
                    return node.Value.Image;
                }
            }
            finally
            {
                _lock.ExitUpgradeableReadLock();
            }
            
            return null;
        }

        /// <summary>
        /// Add new thumbnail to cache (deduplication/overflow auto handled).
        /// Thread-safe write operation.
        /// </summary>
        public void Add(string path, int decodeWidth, ImageSource image)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            string key = MakeKey(path, decodeWidth);
            
            _lock.EnterWriteLock();
            try
            {
                if (_map.ContainsKey(key)) return;
                
                var entry = new CacheEntry(key, image, decodeWidth);
                var node = new LinkedListNode<CacheEntry>(entry);
                _lru.AddFirst(node);
                _map[key] = node;
                _currentBytes += EstimateImageBytes(decodeWidth);
                Trim_NoLock();
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Remove specific resolution variant (e.g., when upgrading low-res to high-res)
        /// Thread-safe write operation.
        /// </summary>
        public void Remove(string path, int decodeWidth)
        {
            string key = MakeKey(path, decodeWidth);
            
            _lock.EnterWriteLock();
            try
            {
                if (_map.TryGetValue(key, out var node))
                {
                    _lru.Remove(node);
                    _map.Remove(key);
                    _currentBytes -= EstimateImageBytes(node.Value.DecodeWidth);
                }
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Clear entire cache.
        /// Thread-safe write operation.
        /// </summary>
        public void Clear()
        {
            _lock.EnterWriteLock();
            try
            {
                _map.Clear();
                _lru.Clear();
                _currentBytes = 0;
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Get current cache statistics.
        /// Thread-safe read operation.
        /// </summary>
        public (int count, long bytes, int capacity) GetStats()
        {
            _lock.EnterReadLock();
            try
            {
                return (_lru.Count, _currentBytes, _capacity);
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        private void Trim_NoLock()
        {
            while (_lru.Count > _capacity || _currentBytes > _maxBytes)
            {
                var last = _lru.Last; 
                if (last == null) break;
                
                _lru.RemoveLast();
                _map.Remove(last.Value.Key);
                _currentBytes -= EstimateImageBytes(last.Value.DecodeWidth);
            }
        }

        public void Dispose()
        {
            _lock?.Dispose();
        }
    }
}
