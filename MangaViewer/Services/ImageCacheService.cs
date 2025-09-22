using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace MangaViewer.Services
{
    /// <summary>
    /// 원본 이미지 캐시 서비스.
    /// - 메모리 원본 바이트(mem: 키) LRU + 용량(개수/바이트) 기반 캐시
    /// - 디코드된 BitmapImage LRU 캐시(경로 단위)
    /// - UI 스레드에서 BitmapImage를 생성하기 위해 DispatcherQueue를 초기화해야 합니다.
    /// </summary>
    public sealed class ImageCacheService
    {
        private int _capacity;
        private readonly Dictionary<string, LinkedListNode<CacheEntry>> _map = new(StringComparer.OrdinalIgnoreCase);
        private readonly LinkedList<CacheEntry> _lru = new();
        private static readonly Lazy<ImageCacheService> _instance = new(() => new ImageCacheService(40));
        public static ImageCacheService Instance => _instance.Value;

        private record CacheEntry(string Path, BitmapImage Image);

        private readonly object _lruLock = new();
        private readonly ConcurrentDictionary<string, Task> _inflightPrefetch = new(StringComparer.OrdinalIgnoreCase);

        // UI dispatcher to create XAML objects safely
        private DispatcherQueue? _dispatcher;
        /// <summary>
        /// UI 스레드 디스패처를 초기화합니다(반드시 앱 시작 시 호출).
        /// </summary>
        public void InitializeUI(DispatcherQueue dispatcher) => _dispatcher = dispatcher;

        // In-memory byte cache for streaming galleries (mem:gid:####.ext)
        private static readonly Dictionary<string, byte[]> _memoryImages = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, LinkedListNode<string>> _memoryOrderMap = new(StringComparer.OrdinalIgnoreCase);
        private static readonly LinkedList<string> _memoryOrder = new();
        private static readonly object _memLock = new();
        private static long _memoryTotalBytes;

        private static int _maxMemoryImageCount = 2000;
        private static long _maxMemoryImageBytes = 2L * 1024 * 1024 * 1024;

        public int MaxMemoryImageCount => _maxMemoryImageCount;
        public long MaxMemoryImageBytes => _maxMemoryImageBytes;
        public int DecodedCacheCapacity => _capacity;

        /// <summary>
        /// 메모리 원본 캐시 상한(개수/바이트)을 설정합니다. 즉시 초과분을 축출합니다.
        /// </summary>
        public void SetMemoryLimits(int? maxCount, long? maxBytes)
        {
            lock (_memLock)
            {
                if (maxCount.HasValue && maxCount.Value > 0) _maxMemoryImageCount = maxCount.Value;
                if (maxBytes.HasValue && maxBytes.Value > 10_000_000) _maxMemoryImageBytes = maxBytes.Value;
                EvictMemory_NoLock();
            }
        }

        public (int imageCount, long totalBytes) GetMemoryUsage()
        {
            lock (_memLock) return (_memoryImages.Count, _memoryTotalBytes);
        }

        /// <summary>
        /// 갤러리별(mem:gid:####.ext) 이미지 개수 집계를 반환합니다.
        /// </summary>
        public Dictionary<string, int> GetPerGalleryCounts()
        {
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

        /// <summary>
        /// 특정 갤러리 ID(mem:gid:*)에 해당하는 모든 항목을 제거합니다.
        /// 디코드 캐시의 같은 키도 함께 제거합니다.
        /// </summary>
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
                    lock (_lruLock)
                    {
                        if (_map.TryGetValue(k, out var lruNode)) { _lru.Remove(lruNode); _map.Remove(k); }
                    }
                }
            }
        }

        private ImageCacheService(int capacity) => _capacity = Math.Max(4, capacity);

        /// <summary>
        /// 디코드된(BitmapImage) 캐시의 최대 보관 개수를 설정합니다.
        /// </summary>
        public void SetDecodedCacheCapacity(int capacity)
        {
            _capacity = Math.Max(4, capacity);
            lock (_lruLock) Trim_NoLock();
        }

        /// <summary>
        /// 메모리 원본 바이트(mem: 키)를 추가합니다(LRU 갱신 및 초과분 축출 포함).
        /// </summary>
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
                lock (_lruLock)
                {
                    if (_map.TryGetValue(k, out var node))
                    {
                        _lru.Remove(node);
                        _map.Remove(k);
                    }
                }
            }
        }

        /// <summary>
        /// 모든 메모리 원본을 비우고, 해당 mem: 항목에 대한 디코드 캐시도 제거합니다.
        /// </summary>
        public void ClearMemoryImages()
        {
            lock (_memLock)
            {
                _memoryImages.Clear();
                _memoryOrder.Clear();
                _memoryOrderMap.Clear();
                _memoryTotalBytes = 0;
            }
            lock (_lruLock)
            {
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

        /// <summary>
        /// 경로(파일 또는 mem:)에서 BitmapImage를 가져오거나 필요 시 생성 후 LRU에 저장합니다.
        /// </summary>
        public BitmapImage? Get(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            lock (_lruLock)
            {
                if (_map.TryGetValue(path, out var node))
                {
                    _lru.Remove(node);
                    _lru.AddFirst(node);
                    return node.Value.Image;
                }
            }

            BitmapImage? bmp = null;
            try
            {
                if (TryGetMemoryBytes(path, out var memBytes) && memBytes != null)
                {
                    bmp = CreateBitmapOnUi(() =>
                    {
                        var img = new BitmapImage();
                        using var ms = new MemoryStream(memBytes, writable: false);
                        img.SetSource(ms.AsRandomAccessStream());
                        return img;
                    });
                }
                else if (File.Exists(path))
                {
                    bmp = CreateBitmapOnUi(() => new BitmapImage(new Uri(path)));
                }
            }
            catch { return null; }

            if (bmp == null) return null;
            lock (_lruLock)
            {
                var entry = new CacheEntry(path, bmp);
                var newNode = new LinkedListNode<CacheEntry>(entry);
                _lru.AddFirst(newNode);
                _map[path] = newNode;
                Trim_NoLock();
            }
            return bmp;
        }

        /// <summary>
        /// 주어진 경로들을 비동기로 미리 로드해 디코드 캐시에 준비합니다(중복/동시성 제어).
        /// </summary>
        public void Prefetch(IEnumerable<string> paths)
        {
            foreach (var p in paths)
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                lock (_lruLock) { if (_map.ContainsKey(p)) continue; }
                _inflightPrefetch.GetOrAdd(p, key => Task.Run(() =>
                {
                    try { _ = Get(key); }
                    finally { _inflightPrefetch.TryRemove(key, out _); }
                }));
            }
        }

        private BitmapImage? CreateBitmapOnUi(Func<BitmapImage> factory)
        {
            // If we have a dispatcher and are on UI thread, create directly
            if (_dispatcher != null && _dispatcher.HasThreadAccess)
            {
                try { return factory(); } catch { return null; }
            }

            if (_dispatcher == null)
            {
                // No dispatcher known: best effort (may crash on non-UI thread)
                try { return factory(); } catch { return null; }
            }

            var tcs = new TaskCompletionSource<BitmapImage?>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_dispatcher.TryEnqueue(() =>
            {
                try { tcs.TrySetResult(factory()); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            }))
            {
                tcs.TrySetResult(null);
            }
            try { return tcs.Task.GetAwaiter().GetResult(); } catch { return null; }
        }

        private void Trim_NoLock()
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
