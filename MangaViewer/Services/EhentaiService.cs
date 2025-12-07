using HtmlAgilityPack;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MangaViewer.Services;

/// <summary>
/// EhentaiService
/// Purpose: Stream images from an E-Hentai gallery into in-memory byte cache while yielding ordered batches.
/// Core Features:
///  - Fetch gallery page URLs (multi-page navigation parsing) and then each image page to extract image URLs.
///  - Adaptive concurrent downloads with global semaphore (_globalFetchGate) + per-session parallel limit.
///  - Memory image caching: Each downloaded image stored via ImageCacheService under a mem: key (mem:gid:####.ext).
///  - Supports partial session restoration (resume after cancellation) using _partialGalleryCache.
///  - Provides streaming consumer API (IAsyncEnumerable<GalleryBatch>) delivering delta batches in order.
/// Session Lifecycle:
///  - Create or restore session; spawn background runner if first request.
///  - Runner schedules fetch tasks until complete or canceled; upon completion exports ordered list to _galleryCache.
///  - CancelDownload snapshots partial progress (if not completed) for later resumption.
/// Robustness:
///  - Swallows parse/network errors per item; marks session Faulted only when image fetch errors occur.
///  - Resets session on mismatch if caller provides different page URL list.
/// Thread Safety: Uses ConcurrentDictionary and lock blocks only where necessary. Individual image keys stored in
/// a ConcurrentDictionary<int,string> for stable ordering by index.
/// Performance Improvements:
///  - Adaptive concurrency based on processor count
///  - Optimized semaphore usage
/// Extension Ideas:
///  - Persist partial cache across application restarts (disk-backed resume).
///  - Adaptive prefetch radius based on scroll velocity.
///  - Support writing files to disk optionally instead of memory-only.
/// </summary>
public class EhentaiService
{
    private static readonly HttpClient _http = new(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All });

    // Completed galleries -> ordered in-memory keys (mem:gid:####.ext)
    private static readonly ConcurrentDictionary<string, List<string>> _galleryCache = new(StringComparer.OrdinalIgnoreCase);

    // Partial per-file cache (index -> key) for interrupted sessions (in-memory only)
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<int, string>> _partialGalleryCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Try get fully completed gallery list (ordered mem: keys) from cache.
    /// </summary>
    public static List<string>? TryGetCachedGallery(string url) => _galleryCache.TryGetValue(url, out var list) ? list : null;

    /// <summary>
    /// Try get partial progress dictionary (index->mem key) for resumed session.
    /// </summary>
    public static bool TryGetPartialGallery(string url, out IReadOnlyDictionary<int, string> dict)
    {
        if (_partialGalleryCache.TryGetValue(url, out var d)) { dict = d; return true; }
        dict = new Dictionary<int, string>();
        return false;
    }

    private sealed class GallerySession
    {
        public string GalleryUrl = string.Empty;
        public string GalleryId = string.Empty;
        public string[] PageUrls = Array.Empty<string>();
        public int Total;
        public ConcurrentDictionary<int, string> Keys = new();
        public Task? Runner;
        public CancellationTokenSource Cts = new();
        public DateTime StartTimeUtc = DateTime.UtcNow;
        public volatile bool Completed;
        public volatile bool Faulted;
        public int LastYieldCount;
        public bool RestoredPartial;
    }
    
    private static readonly ConcurrentDictionary<string, GallerySession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan CompletedSessionRetain = TimeSpan.FromMinutes(10);
    
    // Adaptive concurrency based on processor count
    private static readonly int _globalConcurrency = Math.Clamp(Environment.ProcessorCount * 2, 4, 8);
    private static readonly SemaphoreSlim _globalFetchGate = new(_globalConcurrency, _globalConcurrency);

    public static bool VerboseDebug { get; set; } = true;
    private static int _inFlightFetches;
    private static int _sessionCounter;

    private static string GetGalleryId(string galleryUrl)
    {
        try
        {
            using var sha1 = SHA1.Create();
            var hash = sha1.ComputeHash(Encoding.UTF8.GetBytes(galleryUrl));
            return Convert.ToHexString(hash.AsSpan(0, 5));
        }
        catch { return "UNK"; }
    }

    #region Public Session Utilities
    /// <summary>
    /// Cancel a running download; snapshot partial progress for later resume.
    /// </summary>
    public static void CancelDownload(string galleryUrl)
    {
        if (_sessions.TryRemove(galleryUrl, out var s))
        {
            try
            {
                if (!s.Completed && !s.Faulted && s.Keys.Count > 0 && !_galleryCache.ContainsKey(galleryUrl))
                {
                    var partial = new ConcurrentDictionary<int, string>(s.Keys);
                    _partialGalleryCache[galleryUrl] = partial;
                    if (VerboseDebug) Debug.WriteLine($"[EH][PARTIAL-SNAPSHOT] {galleryUrl} count={partial.Count}");
                }
                s.Cts.Cancel();
            }
            catch { }
            if (VerboseDebug) Debug.WriteLine($"[EH][CANCEL] {galleryUrl}");
        }
    }

    /// <summary>
    /// Cancel all sessions except those in keepUrls list.
    /// </summary>
    public static void CancelAllExcept(IEnumerable<string> keepUrls)
    {
        var keep = new HashSet<string>(keepUrls ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var kv in _sessions.ToArray()) 
            if (!keep.Contains(kv.Key)) 
                CancelDownload(kv.Key);
    }

    /// <summary>
    /// Cleanup completed sessions after retention window to free memory.
    /// </summary>
    public static void CleanupSessions()
    {
        var now = DateTime.UtcNow;
        foreach (var kv in _sessions.ToArray())
        {
            var s = kv.Value;
            if (s.Completed && (now - s.StartTimeUtc) > CompletedSessionRetain) 
                CancelDownload(kv.Key);
        }
    }
    #endregion

    #region Page URL Fetch
    /// <summary>
    /// Fetch all image page URLs for a gallery (first page plus pagination).
    /// </summary>
    public async Task<(List<string> pageUrls, string? title)> GetAllPageUrlsAsync(string galleryUrl, CancellationToken token)
    {
        string html = await _http.GetStringAsync(galleryUrl, token).ConfigureAwait(false);
        return await ParseGalleryAllPagesAsync(galleryUrl, html, token).ConfigureAwait(false);
    }
    #endregion

    #region Streaming Download (memory only)
    /// <summary>
    /// Represents a batch increment from streaming enumeration.
    /// </summary>
    public record GalleryBatch(IReadOnlyList<string> Files, int Completed, int Total);

    /// <summary>
    /// Current ordered progress list (completed, in-progress or partial snapshot). Returns null if unknown.
    /// </summary>
    public static IReadOnlyList<string>? TryGetInProgressOrdered(string galleryUrl)
    {
        if (_galleryCache.TryGetValue(galleryUrl, out var done)) return done;
        if (_sessions.TryGetValue(galleryUrl, out var s)) return s.Keys.OrderBy(k => k.Key).Select(k => k.Value).ToList();
        if (_partialGalleryCache.TryGetValue(galleryUrl, out var partial)) return partial.OrderBy(k => k.Key).Select(k => k.Value).ToList();
        return null;
    }

    /// <summary>
    /// Stream gallery download yielding ordered delta batches. Handles resume and caching.
    /// </summary>
    public async IAsyncEnumerable<GalleryBatch> DownloadPagesStreamingOrderedAsync(
        string galleryUrl,
        List<string> pages,
        int batchSize,
        Action<string>? progress,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        int total = pages.Count;
        if (total == 0) yield break;

        // Fast path: fully cached
        if (_galleryCache.TryGetValue(galleryUrl, out var finished) && finished.Count == total)
        {
            if (VerboseDebug) Debug.WriteLine($"[EH][CACHE-HIT] {galleryUrl} total={total}");
            yield return new GalleryBatch(finished, finished.Count, finished.Count);
            yield break;
        }

        // Session mismatch detection
        if (_sessions.TryGetValue(galleryUrl, out var existing))
        {
            bool mismatch = existing.Total != total;
            if (!mismatch)
            {
                for (int i = 0; i < Math.Min(5, total) && !mismatch; i++) 
                    mismatch |= !string.Equals(existing.PageUrls[i], pages[i], StringComparison.OrdinalIgnoreCase);
                    
                for (int i = 0; i < Math.Min(5, total) && !mismatch; i++)
                {
                    int idxCheck = total - 1 - i;
                    mismatch |= !string.Equals(existing.PageUrls[idxCheck], pages[idxCheck], StringComparison.OrdinalIgnoreCase);
                }
            }
            if (mismatch)
            {
                if (VerboseDebug) Debug.WriteLine($"[EH][SESSION-RESET] {galleryUrl}");
                CancelDownload(galleryUrl);
            }
        }

        var session = _sessions.GetOrAdd(galleryUrl, url =>
        {
            var gs = new GallerySession
            {
                GalleryUrl = url,
                GalleryId = GetGalleryId(url),
                PageUrls = pages.ToArray(),
                Total = pages.Count,
                StartTimeUtc = DateTime.UtcNow
            };
            
            // Restore partial snapshot
            if (_partialGalleryCache.TryGetValue(url, out var part) && part.Count > 0)
            {
                foreach (var kv in part) gs.Keys.TryAdd(kv.Key, kv.Value);
                gs.LastYieldCount = gs.Keys.Count;
                gs.RestoredPartial = true;
                if (VerboseDebug) Debug.WriteLine($"[EH][PARTIAL-RESTORE] {url} restored={gs.Keys.Count}");
            }
            return gs;
        });

        // Spawn runner
        if (session.Runner == null)
        {
            lock (session)
            {
                if (session.Runner == null)
                {
                    int sid = Interlocked.Increment(ref _sessionCounter);
                    if (VerboseDebug) Debug.WriteLine($"[EH][SESSION-START] id={sid} url={galleryUrl} pages={session.Total} restored={session.RestoredPartial}");
                    session.Runner = Task.Run(() => RunDownloadAsync(session, sid), session.Cts.Token);
                }
            }
        }

        // Initial batch (restored partial or empty placeholder)
        if (session.Keys.Count > 0)
        {
            var orderedInit = session.Keys.OrderBy(k => k.Key).Select(k => k.Value).ToList();
            yield return new GalleryBatch(orderedInit, orderedInit.Count, session.Total);
        }
        else
        {
            yield return new GalleryBatch(Array.Empty<string>(), 0, session.Total);
        }

        while (!token.IsCancellationRequested)
        {
            if (session.Completed)
            {
                if (_galleryCache.TryGetValue(galleryUrl, out var finalList))
                {
                    if (VerboseDebug) Debug.WriteLine($"[EH][SESSION-FINISH] url={galleryUrl} completed={finalList.Count}");
                    yield return new GalleryBatch(finalList, finalList.Count, finalList.Count);
                    yield break;
                }
                if (session.Faulted)
                {
                    var partialFault = session.Keys.OrderBy(k => k.Key).Select(k => k.Value).ToList();
                    if (partialFault.Count > session.LastYieldCount)
                    {
                        var deltaF = partialFault.Skip(session.LastYieldCount).ToList();
                        session.LastYieldCount = partialFault.Count;
                        yield return new GalleryBatch(deltaF, partialFault.Count, session.Total);
                    }
                    yield break;
                }
            }

            int current = session.Keys.Count;
            if (current > session.LastYieldCount)
            {
                int newCount = current - session.LastYieldCount;
                bool needYield = current == session.Total || newCount >= batchSize || current <= Math.Min(4, session.Total);
                if (needYield)
                {
                    var ordered = session.Keys.OrderBy(k => k.Key).Select(k => k.Value).ToList();
                    var delta = ordered.Skip(session.LastYieldCount).ToList();
                    session.LastYieldCount = ordered.Count;
                    if (delta.Count > 0)
                    {
                        progress?.Invoke($"다운로드 {ordered.Count}/{session.Total}");
                        if (VerboseDebug) Debug.WriteLine($"[EH][YIELD] url={galleryUrl} delta={delta.Count} total={ordered.Count}/{session.Total} inflight={Volatile.Read(ref _inFlightFetches)}");
                        yield return new GalleryBatch(delta, ordered.Count, session.Total);
                    }
                }
            }

            if (session.Completed && session.Keys.Count == session.Total)
            {
                if (!_galleryCache.ContainsKey(galleryUrl))
                {
                    var final = session.Keys.OrderBy(k => k.Key).Select(k => k.Value).ToList();
                    if (final.Count == session.Total) _galleryCache[session.GalleryUrl] = final;
                }
                _partialGalleryCache.TryRemove(galleryUrl, out _);
                yield break;
            }

            try 
            { 
                await Task.Delay(120, token).ConfigureAwait(false); 
            } 
            catch (OperationCanceledException) 
            { 
                CancelDownload(galleryUrl); 
                yield break; 
            }
        }
    }

    private static async Task RunDownloadAsync(GallerySession session, int sessionId)
    {
        var token = session.Cts.Token;
        
        // Adaptive per-session concurrency
        int sessionParallel = Math.Clamp(Environment.ProcessorCount / 2, 2, 4);
        var running = new List<Task>();
        int nextIndex = 0;

        async Task Fetch(int idx)
        {
            if (session.Keys.ContainsKey(idx)) return;
            DateTime start = DateTime.UtcNow;
            try
            {
                token.ThrowIfCancellationRequested();
                await _globalFetchGate.WaitAsync(token).ConfigureAwait(false);
                var waited = (DateTime.UtcNow - start).TotalMilliseconds;
                Interlocked.Increment(ref _inFlightFetches);
                if (VerboseDebug) Debug.WriteLine($"[EH][FETCH-START] S{sessionId} idx={idx + 1} waitMs={waited:F1} inflight={_inFlightFetches}");
                try
                {
                    string pageUrl = session.PageUrls[idx];
                    string pageHtml = await _http.GetStringAsync(pageUrl, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    string? imgUrl = ExtractImageUrl(pageHtml);
                    if (imgUrl == null) return;
                    
                    using var req = new HttpRequestMessage(HttpMethod.Get, imgUrl) { Headers = { Referrer = new Uri(pageUrl) } };
                    var resp = await _http.SendAsync(req, token).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode) return;
                    
                    byte[] data = await resp.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    
                    string ext = Path.GetExtension(new Uri(imgUrl).AbsolutePath);
                    if (string.IsNullOrEmpty(ext) || ext.Length > 5) ext = ".jpg";
                    string key = $"mem:{session.GalleryId}:{(idx + 1).ToString("D4")}{ext}";
                    ImageCacheService.Instance.AddMemoryImage(key, data);
                    session.Keys.TryAdd(idx, key);
                }
                finally
                {
                    var dur = (DateTime.UtcNow - start).TotalMilliseconds;
                    Interlocked.Decrement(ref _inFlightFetches);
                    if (VerboseDebug) Debug.WriteLine($"[EH][FETCH-END] S{sessionId} idx={idx + 1} durMs={dur:F1} inflight={_inFlightFetches}");
                    _globalFetchGate.Release();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                session.Faulted = true; 
                Debug.WriteLine($"[EH][FETCH-ERR] S{sessionId} idx={idx + 1} {ex.Message}");
            }
        }

        try
        {
            if (session.Keys.Count > 0)
            {
                int maxExisting = session.Keys.Keys.DefaultIfEmpty(-1).Max();
                nextIndex = Math.Max(nextIndex, maxExisting + 1);
            }

            while (!token.IsCancellationRequested && session.Keys.Count < session.Total)
            {
                while (!token.IsCancellationRequested && running.Count < sessionParallel && nextIndex < session.Total)
                {
                    int i = nextIndex++;
                    var t = Fetch(i);
                    running.Add(t);
                }
                if (running.Count == 0) break;
                
                var finished = await Task.WhenAny(running).ConfigureAwait(false);
                running.Remove(finished);
            }
        }
        finally
        {
            session.Completed = true;
            if (session.Keys.Count == session.Total && !session.Faulted)
            {
                var final = session.Keys.OrderBy(k => k.Key).Select(k => k.Value).ToList();
                _galleryCache[session.GalleryUrl] = final;
            }
            if (VerboseDebug)
            {
                Debug.WriteLine($"[EH][SESSION-END] S{sessionId} url={session.GalleryUrl} ok={session.Keys.Count}/{session.Total} faulted={session.Faulted}");
            }
        }
    }
    #endregion

    #region Parsing Internals
    private async Task<(List<string> pageUrls, string? title)> ParseGalleryAllPagesAsync(string galleryUrl, string firstHtml, CancellationToken token)
    {
        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var (firstPages, title) = ParseGalleryPage(firstHtml);
        foreach (var p in firstPages) 
            if (seen.Add(p)) 
                ordered.Add(p);

        var doc = new HtmlDocument();
        doc.LoadHtml(firstHtml);
        var navLinks = doc.DocumentNode.SelectNodes("//table[contains(@class,'ptt')]//a|//div[contains(@class,'ptt')]//a|//table[contains(@class,'gtb')]//a");
        var extraIndexUrls = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        if (navLinks != null)
        {
            foreach (var a in navLinks)
            {
                string href = a.GetAttributeValue("href", string.Empty);
                if (href.Contains("?p=")) extraIndexUrls.Add(href);
            }
        }

        const int MaxIndexPages = 25;
        int count = 0;
        foreach (var idxUrl in extraIndexUrls)
        {
            if (count++ >= MaxIndexPages) break;
            try
            {
                string html = await _http.GetStringAsync(idxUrl, token).ConfigureAwait(false);
                var (pPages, _) = ParseGalleryPage(html);
                foreach (var p in pPages) 
                    if (seen.Add(p)) 
                        ordered.Add(p);
                await Task.Delay(40, token).ConfigureAwait(false);
            }
            catch { }
        }

        ordered = ordered
            .Select(u => (u, num: ExtractPageNumber(u)))
            .OrderBy(t => t.num)
            .ThenBy(t => t.u, StringComparer.OrdinalIgnoreCase)
            .Select(t => t.u)
            .ToList();
        return (ordered, title);
    }

    private static int ExtractPageNumber(string url)
    {
        var m = Regex.Match(url, @"/(\d+)-(\d+)$");
        return m.Success && int.TryParse(m.Groups[2].Value, out int n) ? n : int.MaxValue;
    }

    private static (List<string> pageUrls, string? title) ParseGalleryPage(string html)
    {
        if (string.IsNullOrEmpty(html)) return (new List<string>(), null);
        var doc = new HtmlDocument();
        try { doc.LoadHtml(html); } catch { return (new List<string>(), null); }
        string? title = doc.DocumentNode.SelectSingleNode("//h1")?.InnerText?.Trim();
        var list = new List<string>();
        try
        {
            var thumbLinks = doc.DocumentNode.SelectNodes("//div[(contains(@class,'gdtm') or contains(@class,'gdtl'))]//a");
            if (thumbLinks != null)
            {
                foreach (var a in thumbLinks)
                {
                    string href = a.GetAttributeValue("href", string.Empty);
                    if (IsImagePageLink(href)) list.Add(href);
                }
            }
        }
        catch { }
        if (list.Count == 0)
        {
            try
            {
                var matches = Regex.Matches(html, @"https?://[^""]+/s/[0-9a-f]{10,}/\d+-\d+");
                foreach (Match m in matches) 
                    if (IsImagePageLink(m.Value)) 
                        list.Add(m.Value);
            }
            catch { }
        }
        list = list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (VerboseDebug) Debug.WriteLine($"[EH][PARSE-PAGE] extracted={list.Count}");
        return (list, title);
    }

    private static bool IsImagePageLink(string href) => 
        !string.IsNullOrEmpty(href) && href.Contains("/s/") && Regex.IsMatch(href, @"/s/[0-9a-f]{5,}/\d+-\d+");

    private static string? ExtractImageUrl(string html)
    {
        if (string.IsNullOrEmpty(html)) return null;
        try
        {
            var fast = Regex.Match(html, "<img[^>]*id=['\\\"]img['\\\"][^>]*src=['\\\"]([^'\\\"]+)['\\\"][^>]*>");
            if (fast.Success) return fast.Groups[1].Value;
        }
        catch { }
        var doc = new HtmlDocument();
        try { doc.LoadHtml(html); } catch { return null; }
        return doc.DocumentNode.SelectSingleNode("//img[@id='img']")?.GetAttributeValue("src", null);
    }
    #endregion

    /// <summary>
    /// Estimate page count by enumerating all URLs (no streaming).
    /// </summary>
    public async Task<int> GetEstimatedPageCountAsync(string galleryUrl, CancellationToken token)
    {
        try 
        { 
            var (pages, _) = await GetAllPageUrlsAsync(galleryUrl, token).ConfigureAwait(false); 
            return pages.Count; 
        } 
        catch { return 0; }
    }
}
