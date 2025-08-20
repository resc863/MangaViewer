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
using System.IO; // Path 사용

namespace MangaViewer.Services;

public class EhentaiService
{
    private static readonly HttpClient _http = new(new HttpClientHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All
    });

    private static readonly ConcurrentDictionary<string, List<string>> _galleryCache = new(StringComparer.OrdinalIgnoreCase);
    public static List<string>? TryGetCachedGallery(string url) => _galleryCache.TryGetValue(url, out var list) ? list : null;

    #region Page URL Fetch
    public async Task<(List<string> pageUrls, string? title)> GetAllPageUrlsAsync(string galleryUrl, CancellationToken token)
    {
        string html = await _http.GetStringAsync(galleryUrl, token);
        return await ParseGalleryAllPagesAsync(galleryUrl, html, token);
    }
    #endregion

    #region Ordered Streaming (memory only)
    public record GalleryBatch(IReadOnlyList<string> Files, int Completed, int Total);

    public async IAsyncEnumerable<GalleryBatch> DownloadPagesStreamingOrderedAsync(string galleryUrl, List<string> pages, int batchSize, Action<string>? progress, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        if (_galleryCache.TryGetValue(galleryUrl, out var cached) && cached.Count == pages.Count)
        {
            yield return new GalleryBatch(cached, cached.Count, cached.Count);
            yield break;
        }
        int total = pages.Count;
        if (total == 0) yield break;

        int maxParallel = Math.Clamp(Environment.ProcessorCount, 2, 6);
        var results = new string?[total]; // 인덱스별 key 저장
        int completed = 0;
        int nextIndexToSchedule = 0;
        var running = new List<Task>();
        object lockObj = new();

        async Task Fetch(int idx)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                string pageUrl = pages[idx];
                string pageHtml = await _http.GetStringAsync(pageUrl, token);
                string? imgUrl = ExtractImageUrl(pageHtml);
                if (imgUrl == null) return;
                using var req = new HttpRequestMessage(HttpMethod.Get, imgUrl) { Headers = { Referrer = new Uri(pageUrl) } };
                var resp = await _http.SendAsync(req, token);
                if (!resp.IsSuccessStatusCode) return;
                byte[] data = await resp.Content.ReadAsByteArrayAsync(token);
                string ext = Path.GetExtension(new Uri(imgUrl).AbsolutePath);
                if (string.IsNullOrEmpty(ext) || ext.Length > 5) ext = ".jpg";
                string key = $"mem:{(idx + 1).ToString("D4")}{ext}";
                ImageCacheService.Instance.AddMemoryImage(key, data);
                lock (lockObj)
                {
                    results[idx] = key;
                    completed++;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Debug.WriteLine("[EH] fetch error: " + ex.Message); }
        }

        while (completed < total && !token.IsCancellationRequested)
        {
            while (running.Count < maxParallel && nextIndexToSchedule < total)
            {
                int i = nextIndexToSchedule++;
                var t = Fetch(i);
                running.Add(t);
            }

            if (running.Count == 0) break;
            var finished = await Task.WhenAny(running);
            running.Remove(finished);

            int currentCompleted;
            lock (lockObj) currentCompleted = completed;
            if (currentCompleted == total || currentCompleted % batchSize == 0 || currentCompleted <= Math.Min(4, total))
            {
                List<string> ordered;
                lock (lockObj)
                {
                    ordered = results
                        .Select((k, i) => (k, i))
                        .Where(t => t.k != null)
                        .OrderBy(t => t.i)
                        .Select(t => t.k!)
                        .ToList();
                }
                if (ordered.Count > 0)
                {
                    progress?.Invoke($"진행 {currentCompleted}/{total}");
                    yield return new GalleryBatch(ordered, currentCompleted, total);
                }
            }
        }

        if (!token.IsCancellationRequested)
        {
            List<string> final;
            lock (lockObj)
            {
                final = results
                    .Select((k, i) => (k, i))
                    .Where(t => t.k != null)
                    .OrderBy(t => t.i)
                    .Select(t => t.k!)
                    .ToList();
            }
            if (final.Count == total)
                _galleryCache[galleryUrl] = final;
        }
    }
    #endregion

    #region Parsing Internals
    private async Task<(List<string> pageUrls, string? title)> ParseGalleryAllPagesAsync(string galleryUrl, string firstHtml, CancellationToken token)
    {
        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var (firstPages, title) = ParseGalleryPage(firstHtml);
        foreach (var p in firstPages) if (seen.Add(p)) ordered.Add(p);

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

        const int MaxIndexPages = 25; // 안전 제한
        int count = 0;
        foreach (var idxUrl in extraIndexUrls)
        {
            if (count++ >= MaxIndexPages) break;
            try
            {
                string html = await _http.GetStringAsync(idxUrl, token);
                var (pPages, _) = ParseGalleryPage(html);
                foreach (var p in pPages) if (seen.Add(p)) ordered.Add(p);
                await Task.Delay(40, token); // 서버 부담 감소 (짧은 지연)
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
            // 썸네일 그리드 / 리스트 한 번에 추출
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
                foreach (Match m in matches) if (IsImagePageLink(m.Value)) list.Add(m.Value);
            }
            catch { }
        }
        list = list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Debug.WriteLine($"[EH] ParseGalleryPage -> {list.Count} page links");
        return (list, title);
    }

    private static bool IsImagePageLink(string href) => !string.IsNullOrEmpty(href) && href.Contains("/s/") && Regex.IsMatch(href, @"/s/[0-9a-f]{5,}/\d+-\d+");

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

    public async Task<int> GetEstimatedPageCountAsync(string galleryUrl, CancellationToken token)
    {
        try
        {
            var (pages, _) = await GetAllPageUrlsAsync(galleryUrl, token);
            return pages.Count;
        }
        catch { return 0; }
    }

    public static void ClearInMemoryCacheForNewGallery() => ImageCacheService.Instance.ClearMemoryImages();
}
