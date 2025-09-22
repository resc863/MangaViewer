// Project: MangaViewer
// File: ViewModels/SearchViewModel.cs
// Purpose: Defines search-related view models and HTML parsers for gallery results. Handles
//          HTTP fetching, parsing list/detail pages, incremental loading, and thumbnail binding.

using HtmlAgilityPack;
using MangaViewer.Helpers;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace MangaViewer.ViewModels;

#region Parser Abstractions
/// <summary>
/// 갤러리 상세 페이지 HTML에서 타이틀/태그를 파싱하는 파서 인터페이스.
/// </summary>
public interface IGalleryDetailParser { void Parse(GalleryItemViewModel item, string html); }

/// <summary>
/// E-Hentai 갤러리 상세 페이지용 파서 구현.
/// - <h1> 타이틀과 태그 테이블(#taglist)을 분석하여 ViewModel에 반영합니다.
/// </summary>
public sealed class EhentaiGalleryDetailParser : IGalleryDetailParser
{
    /// <summary>
    /// 상세 페이지 HTML을 파싱하여 항목의 타이틀과 태그를 채웁니다.
    /// </summary>
    /// <param name="item">대상 뷰모델</param>
    /// <param name="html">상세 페이지 HTML 문자열</param>
    public void Parse(GalleryItemViewModel item, string html)
    {
        try
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(html);
            var h1Node = doc.DocumentNode.SelectSingleNode("//h1");
            if (h1Node != null)
            {
                var rawTitle = HtmlEntity.DeEntitize(h1Node.InnerText.Trim());
                string cleaned = GalleryItemViewModel.CleanTitle(rawTitle);
                if (!string.IsNullOrEmpty(cleaned) && (item.Title == null || cleaned.Length < item.Title.Length))
                    item.Title = cleaned;
            }
            var tagRows = doc.DocumentNode.SelectNodes("//div[@id='taglist']//tr");
            if (tagRows == null) return;
            foreach (var tr in tagRows)
            {
                try
                {
                    var catNode = tr.SelectSingleNode(".//td[contains(@class,'tc')][1]");
                    string rawCat = catNode?.InnerText ?? string.Empty;
                    string category = rawCat.Replace("\n", "").Replace("\r", "").Replace("\t", "").Trim(':', ' ').ToLowerInvariant();
                    if (string.IsNullOrEmpty(category)) category = string.Empty;
                    var tagLinks = tr.SelectNodes(".//td[last()]//a");
                    if (tagLinks == null) continue;
                    foreach (var a in tagLinks)
                    {
                        string tag = HtmlEntity.DeEntitize(a.InnerText.Trim());
                        if (string.IsNullOrEmpty(tag)) continue;
                        item.ApplyTag(category, tag);
                    }
                }
                catch { }
            }
        }
        catch (Exception ex) { Debug.WriteLine("[EhentaiParser] " + ex.Message); }
        finally { item.RebuildDerived(); }
    }
}
#endregion

/// <summary>
/// 검색 결과의 단일 갤러리 항목을 표현하는 ViewModel.
/// - 태그 카테고리화, 썸네일 바인딩, 상세 로딩 상태 등을 포함합니다.
/// </summary>
public class GalleryItemViewModel : BaseViewModel
{
    // Precompiled regex for title cleaning
    private static readonly Regex _cleanTitleRegex = new(@"^(?:[\[(][^\])]++[\])]\s*)+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public string? GalleryId { get; set; }
    public string? GalleryUrl { get; set; }
    private string? _title; public string? Title { get => _title; set => SetProperty(ref _title, value); }
    private string? _artist; public string? Artist { get => _artist; set => SetProperty(ref _artist, value); }
    private string? _group; public string? Group { get => _group; set => SetProperty(ref _group, value); }

    public ObservableCollection<string> Artists { get; } = new();
    public ObservableCollection<string> Groups { get; } = new();
    public ObservableCollection<string> Parodies { get; } = new();
    public ObservableCollection<string> Languages { get; } = new();
    public ObservableCollection<string> Tags { get; } = new();
    public ObservableCollection<string> MaleTags { get; } = new();
    public ObservableCollection<string> FemaleTags { get; } = new();

    private readonly Dictionary<string, ObservableCollection<string>> _categoryMap = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, ObservableCollection<string>> CategoryMap => _categoryMap;

    public string? ThumbnailUrl { get; set; }
    private BitmapImage? _thumbnail; public BitmapImage? Thumbnail { get => _thumbnail; set => SetProperty(ref _thumbnail, value); }

    private string _tagsJoined = string.Empty; public string TagsJoined { get => _tagsJoined; private set => SetProperty(ref _tagsJoined, value); }

    /// <summary>
    /// 검색 가능한 태그(artist/group/parody/language/male/female 접두사는 제외).
    /// </summary>
    public IEnumerable<string> SearchableTags => Tags.Where(t => !(t.StartsWith("artist:") || t.StartsWith("group:") || t.StartsWith("parody:") || t.StartsWith("language:") || t.StartsWith("male:") || t.StartsWith("female:")));

    private bool _detailsLoaded; public bool DetailsLoaded { get => _detailsLoaded; private set => SetProperty(ref _detailsLoaded, value); }
    private bool _isDetailsLoading; public bool IsDetailsLoading { get => _isDetailsLoading; private set => SetProperty(ref _isDetailsLoading, value); }

    public RelayCommand OpenCommand { get; }
    public event EventHandler<GalleryItemViewModel>? OpenRequested;

    public GalleryItemViewModel()
    {
        OpenCommand = new RelayCommand(_ => OpenRequested?.Invoke(this, this));
        Tags.CollectionChanged += OnTagsChanged;
        MaleTags.CollectionChanged += OnTagsChanged;
        FemaleTags.CollectionChanged += OnTagsChanged;
        _categoryMap["artist"] = Artists;
        _categoryMap["group"] = Groups;
        _categoryMap["parody"] = Parodies;
        _categoryMap["language"] = Languages;
        _categoryMap["male"] = MaleTags;
        _categoryMap["female"] = FemaleTags;
    }
    private void OnTagsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildDerived();

    /// <summary>
    /// 상세 페이지를 필요 시 로드하여 파서로 반영합니다(1회).
    /// </summary>
    /// <param name="htmlFetcher">URL을 받아 HTML을 반환하는 비동기 함수</param>
    /// <param name="parser">상세 파서</param>
    internal async Task EnsureDetailsAsync(Func<string, Task<string>> htmlFetcher, IGalleryDetailParser parser)
    {
        if (DetailsLoaded || IsDetailsLoading || string.IsNullOrEmpty(GalleryUrl)) return;
        IsDetailsLoading = true;
        try
        {
            string html = await htmlFetcher(GalleryUrl);
            parser.Parse(this, html);
            DetailsLoaded = true;
        }
        catch (Exception ex) { Debug.WriteLine("[Details] fail: " + ex.Message); }
        finally { IsDetailsLoading = false; }
    }

    /// <summary>
    /// 태그를 카테고리에 따라 분류하여 내부 컬렉션에 추가하고, <see cref="Tags"/> 집합에도 접두사와 함께 추가합니다.
    /// </summary>
    /// <param name="category">태그 카테고리(artist/group/parody/language/male/female/기타)</param>
    /// <param name="tag">태그 텍스트</param>
    public void ApplyTag(string category, string tag)
    {
        switch (category)
        {
            case "artist": if (!Artists.Contains(tag)) Artists.Add(tag); if (Artist == null) Artist = tag; break;
            case "group": if (!Groups.Contains(tag)) Groups.Add(tag); if (Group == null) Group = tag; break;
            case "parody": if (!Parodies.Contains(tag)) Parodies.Add(tag); break;
            case "language": if (!Languages.Contains(tag)) Languages.Add(tag); break;
            case "male": if (!MaleTags.Contains(tag)) MaleTags.Add(tag); break;
            case "female": if (!FemaleTags.Contains(tag)) FemaleTags.Add(tag); break;
            default:
                if (!_categoryMap.TryGetValue(category, out var col))
                {
                    col = new ObservableCollection<string>();
                    col.CollectionChanged += OnTagsChanged;
                    _categoryMap[category] = col;
                }
                if (!col.Contains(tag)) col.Add(tag); break;
        }
        string full = string.IsNullOrEmpty(category) ? tag : ($"{category}:{tag}");
        if (!Tags.Contains(full)) Tags.Add(full);
    }

    /// <summary>
    /// 파생 속성(결합 문자열, 검색 태그 변경 알림)을 갱신합니다.
    /// </summary>
    public void RebuildDerived() { TagsJoined = string.Join(", ", Tags); OnPropertyChanged(nameof(SearchableTags)); }

    /// <summary>
    /// 흔한 접두 대괄호/소괄호 블록([Artist], (Group) 등)을 제거하여 타이틀을 정리합니다.
    /// </summary>
    /// <param name="title">원본 타이틀</param>
    /// <returns>정리된 타이틀</returns>
    public static string CleanTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return title;
        return _cleanTitleRegex.Replace(title, string.Empty).Trim();
    }

    /// <summary>
    /// 카테고리별 태그 섹션 모델을 생성합니다(뷰에서 목록 섹션 렌더링용).
    /// </summary>
    public record Section(string Key, string Header, IReadOnlyList<string> Items, string? SearchPrefix, int Order);

    /// <summary>
    /// 현재 태그 상태를 바탕으로 정렬된 섹션들을 생성합니다.
    /// </summary>
    /// <returns>섹션 열거</returns>
    public IEnumerable<Section> GetSections()
    {
        string HeaderFor(string key) => key switch { "artist" => "작가", "group" => "그룹", "parody" => "패러디", "language" => "언어", "male" => "Male 태그", "female" => "Female 태그", _ => key + " 태그" };
        string? PrefixFor(string key) => key switch { "artist" => "artist:", "group" => "group:", "parody" => "parody:", "male" => "male:", "female" => "female:", _ => null };
        int OrderFor(string key) => key switch { "artist" => 0, "group" => 1, "parody" => 2, "language" => 3, "female" => 4, "male" => 5, "misc" => 9, _ => 10 };
        foreach (var kv in _categoryMap)
        {
            if (kv.Value.Count == 0) continue;
            yield return new Section(kv.Key, HeaderFor(kv.Key), kv.Value.ToList(), PrefixFor(kv.Key), OrderFor(kv.Key));
        }
        var misc = SearchableTags.ToList();
        if (misc.Count > 0) yield return new Section("misc", "기타 태그", misc, null, 9);
    }
}

/// <summary>
/// E-Hentai 검색 뷰모델. 검색 요청/페이징/썸네일 로드 및 상세 파싱을 담당합니다.
/// </summary>
public class SearchViewModel : BaseViewModel
{
    private static readonly HttpClient _http = CreateClient();
    private readonly IGalleryDetailParser _detailParser = new EhentaiGalleryDetailParser();
    public ObservableCollection<GalleryItemViewModel> Results { get; } = new();

    private int _currentPage = 0; // 1-based after first page load
    private string? _nextPageUrl; // href from id=dnext (already absolute)
    private bool _isLoadingMore; private bool _hasMore; private string _lastQuery = string.Empty;
    public bool IsLoadingMore { get => _isLoadingMore; private set => SetProperty(ref _isLoadingMore, value); }
    public bool HasMore { get => _hasMore; private set => SetProperty(ref _hasMore, value); }

    // Precompiled regex (gallery id) reused across parsers
    private static readonly Regex _galleryIdRegex = new(@"/(\d+)/(?:[0-9a-f]+)/?", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// HTTP 클라이언트 생성(압축/쿠키/헤더 설정 포함).
    /// </summary>
    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = true,
            CookieContainer = new CookieContainer()
        };
        var h = new HttpClient(handler, disposeHandler: true);
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        h.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        h.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9,ko;q=0.8");
        h.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true };
        return h;
    }

    private static string BuildFirstPageUrl(string query) => $"https://e-hentai.org/?f_search={Uri.EscapeDataString(query)}";

    /// <summary>
    /// 새 검색을 시작합니다(결과 초기화 후 첫 페이지 로드, 필요 시 두 번째 페이지 프리페치).
    /// </summary>
    /// <param name="query">검색 쿼리</param>
    public async Task SearchAsync(string query)
    {
        Results.Clear();
        _lastQuery = query;
        _currentPage = 0;
        _nextPageUrl = null;
        HasMore = false;
        if (string.IsNullOrWhiteSpace(query)) return;
        await LoadMoreInternalAsync(reset: true); // first page
        if (HasMore) await LoadMoreInternalAsync(reset: false); // optional prefetch second page
    }

    /// <summary>
    /// 다음 페이지를 비동기로 로드합니다(무한 스크롤 등에서 호출).
    /// </summary>
    public Task LoadMoreAsync()
    {
        if (!HasMore || IsLoadingMore || string.IsNullOrWhiteSpace(_lastQuery)) return Task.CompletedTask;
        return LoadMoreInternalAsync(reset: false);
    }

    /// <summary>
    /// 내부 페이징 로더. reset=true이면 첫 페이지부터 다시 시작합니다.
    /// </summary>
    private async Task LoadMoreInternalAsync(bool reset)
    {
        try
        {
            IsLoadingMore = true;
            string? url = reset ? BuildFirstPageUrl(_lastQuery) : _nextPageUrl;
            if (string.IsNullOrEmpty(url)) return;
            int targetPage = reset ? 1 : _currentPage + 1;
            Debug.WriteLine($"[Search] Page {targetPage} URL={url}");
            string html;
            try { html = await _http.GetStringAsync(url); }
            catch (Exception ex) { Debug.WriteLine("[Search] HTTP fail: " + ex.Message); return; }
            int before = Results.Count;
            ParseSimple(html); if (Results.Count == before) ParseLoose(html); // fallback parser
            await LoadThumbnailsAsync(startIndex: before);
            _currentPage = targetPage;
            _nextPageUrl = ExtractNextPageUrl(html);
            HasMore = !string.IsNullOrEmpty(_nextPageUrl);
        }
        finally { IsLoadingMore = false; }
    }

    /// <summary>
    /// 다음 페이지의 URL을 HTML에서 추출합니다.
    /// </summary>
    /// <param name="html">검색 결과 페이지 HTML</param>
    /// <returns>다음 페이지 URL 또는 null</returns>
    private string? ExtractNextPageUrl(string html)
    {
        try
        {
            var doc = new HtmlDocument(); doc.LoadHtml(html);
            var nextA = doc.DocumentNode.SelectSingleNode("//a[@id='dnext']")
                       ?? doc.DocumentNode.SelectSingleNode("//a[normalize-space(text())='Next >' or normalize-space(text())='>' or normalize-space(text())='Next']");
            if (nextA != null)
            {
                string href = WebUtility.HtmlDecode(nextA.GetAttributeValue("href", string.Empty));
                if (!string.IsNullOrEmpty(href)) return href;
            }
        }
        catch (Exception ex) { Debug.WriteLine("[Search] next parse fail: " + ex.Message); }
        return null;
    }

    /// <summary>
    /// 갤러리 상세(태그/타이틀)를 필요 시 로드합니다.
    /// </summary>
    public Task LoadItemDetailsAsync(GalleryItemViewModel item) => item.EnsureDetailsAsync(url => _http.GetStringAsync(url), _detailParser);

    /// <summary>
    /// 표준 테이블 뷰(itg) 기반 빠른 파서.
    /// </summary>
    private void ParseSimple(string html)
    {
        try
        {
            var doc = new HtmlDocument(); doc.LoadHtml(html);
            var rows = doc.DocumentNode.SelectNodes("//table[contains(@class,'itg')]/tr"); if (rows == null) return;
            foreach (var tr in rows)
            {
                if (tr.SelectSingleNode(".//th") != null) continue; // header row
                try
                {
                    var link = tr.SelectSingleNode(".//td[3]//a"); if (link == null) continue;
                    string galleryUrl = link.GetAttributeValue("href", string.Empty); if (!galleryUrl.Contains("/g/")) continue;
                    string id = ExtractId(galleryUrl); if (string.IsNullOrEmpty(id)) continue;
                    string rawTitle = HtmlEntity.DeEntitize(link.InnerText.Trim()); string title = GalleryItemViewModel.CleanTitle(rawTitle);
                    string thumb = ExtractThumbFromNode(tr);
                    AddResult(id, galleryUrl, title, thumb);
                }
                catch (Exception ex) { Debug.WriteLine("[ParseSimpleRow] " + ex.Message); }
            }
        }
        catch (Exception ex) { Debug.WriteLine("[ParseSimple] " + ex.Message); }
    }

    /// <summary>
    /// HTML 구조가 변했을 때 대비한 느슨한 파서(모든 링크에서 /g/ 포함을 탐색).
    /// </summary>
    private void ParseLoose(string html)
    {
        try
        {
            var doc = new HtmlDocument(); doc.LoadHtml(html);
            var anchors = doc.DocumentNode.SelectNodes("//a[contains(@href,'/g/')]"); if (anchors == null) return;
            foreach (var a in anchors)
            {
                try
                {
                    string galleryUrl = a.GetAttributeValue("href", string.Empty); if (!galleryUrl.Contains("/g/")) continue;
                    string id = ExtractId(galleryUrl); if (string.IsNullOrEmpty(id) || Exists(id)) continue;
                    string rawTitle = HtmlEntity.DeEntitize(a.InnerText.Trim()); string title = GalleryItemViewModel.CleanTitle(rawTitle);
                    var img = a.SelectSingleNode(".//img");
                    string thumb = img?.GetAttributeValue("data-src", null) ?? img?.GetAttributeValue("src", string.Empty) ?? string.Empty;
                    AddResult(id, galleryUrl, title, thumb);
                }
                catch (Exception ex) { Debug.WriteLine("[ParseLooseNode] " + ex.Message); }
            }
        }
        catch (Exception ex) { Debug.WriteLine("[ParseLoose] " + ex.Message); }
    }

    /// <summary>
    /// 결과 행 노드에서 썸네일 이미지 URL을 추출합니다.
    /// </summary>
    private static string ExtractThumbFromNode(HtmlNode node)
    {
        var thumbImg = node.SelectSingleNode(".//td[2]//img") ?? node.SelectSingleNode(".//img");
        return thumbImg?.GetAttributeValue("data-src", null) ?? thumbImg?.GetAttributeValue("src", string.Empty) ?? string.Empty;
    }

    /// <summary>
    /// 갤러리 URL에서 ID를 정규식으로 추출합니다.
    /// </summary>
    private static string ExtractId(string galleryUrl)
    {
        var m = _galleryIdRegex.Match(galleryUrl);
        return m.Success ? m.Groups[1].Value : string.Empty;
    }

    private bool Exists(string id) => id.Length > 0 && Results.Any(r => r.GalleryId == id);

    /// <summary>
    /// 결과 목록에 새 항목을 추가합니다(중복 방지 포함).
    /// </summary>
    private void AddResult(string id, string url, string title, string thumb)
    {
        if (Exists(id)) return;
        var item = new GalleryItemViewModel
        {
            GalleryId = id,
            GalleryUrl = url,
            Title = title,
            ThumbnailUrl = thumb
        };
        item.OpenRequested += OnOpenRequested;
        Results.Add(item);
    }

    /// <summary>
    /// 썸네일 이미지 소스를 점진적으로 로드합니다(느린 네트워크 대비, UI 부하 완화).
    /// </summary>
    private async Task LoadThumbnailsAsync(int startIndex = 0)
    {
        for (int i = startIndex; i < Results.Count; i++)
        {
            var item = Results[i];
            if (string.IsNullOrEmpty(item.ThumbnailUrl) || item.Thumbnail != null) continue;
            try
            {
                item.Thumbnail = new BitmapImage { DecodePixelWidth = 180, UriSource = new Uri(item.ThumbnailUrl) };
            }
            catch (Exception ex) { Debug.WriteLine("[Thumb] " + ex.Message); }
            await Task.Delay(15);
        }
    }

    /// <summary>
    /// 타일 클릭 등으로 갤러리 열기가 요청되었을 때 발생하는 이벤트를 전파합니다.
    /// </summary>
    public event EventHandler<GalleryItemViewModel>? GalleryOpenRequested;
    private void OnOpenRequested(object? sender, GalleryItemViewModel e) => GalleryOpenRequested?.Invoke(this, e);
}
