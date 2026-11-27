using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

sealed partial class OnlineRule
{
    const string Keyword = "{{keyword}}";
    const string ChapterUrl = "{{chapterUrl}}";
    const string BaseUrl = "{{baseUrl}}";
    static readonly Regex HeaderName = new Regex(@"^[!#$%&'*+.^_`|~0-9A-Za-z-]+$",
        RegexOptions.CultureInvariant, MatchTimeout);
    static readonly HashSet<string> TransportHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Content-Length", "Transfer-Encoding", "Connection", "Proxy-Connection", "Proxy-Authorization",
        "Keep-Alive", "TE", "Trailer", "Upgrade", "Expect"
    };
    static readonly HashSet<string> PublicImageHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "User-Agent", "Referer", "Accept", "Accept-Language"
    };

    public sealed class ImageRule
    {
        public string mode { get; set; }
        public string selector { get; set; }
        public string pattern { get; set; }
        internal Regex Pattern;
    }

    void ValidateHeaders()
    {
        if (headers == null) return;
        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in headers)
        {
            string key = header.Key, value = header.Value;
            if (string.IsNullOrWhiteSpace(key) || key.Length > 80 || !HeaderName.IsMatch(key) ||
                TransportHeaders.Contains(key) || value == null || value.Length > 4096 ||
                value.Any(character => char.IsControl(character) && character != '\t') || normalized.ContainsKey(key))
                throw new FormatException("图源含有无效或重复的 HTTP 请求头。");
            if (key.Equals("Referer", StringComparison.OrdinalIgnoreCase))
            {
                Uri address = OnlineManga.Https(ExpandHeader(value, null));
                if (address == null || !IsAllowed(address, requestHosts))
                    throw new FormatException("Referer 需要使用图源或 requestHosts 内声明的 HTTPS 域名。");
            }
            normalized.Add(key, value);
        }
        headers = normalized;
    }

    string ExpandHeader(string value, Uri page)
    {
        return value.Replace(ChapterUrl, (page ?? Referer).AbsoluteUri).Replace(BaseUrl, Site.AbsoluteUri);
    }

    internal OnlineManga.RequestOptions RequestHeaders(Uri address, Uri page, bool image = false,
        string postBody = null, string contentType = null)
    {
        if (headers == null && postBody == null && (!image || !IsStandard || page == null))
            return null;
        bool trusted = IsAllowed(address, requestHosts) || image && IsAllowed(address, imageHosts);
        var applied = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (image && IsStandard && page != null)
            applied["Referer"] = page.AbsoluteUri;
        if (headers != null)
        {
            foreach (var header in headers)
            {
                if (header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) continue;
                if (trusted || image && PublicImageHeaders.Contains(header.Key))
                    applied[header.Key] = ExpandHeader(header.Value, page);
            }
        }
        return new OnlineManga.RequestOptions(applied, postBody, contentType);
    }

    void ValidateStandard()
    {
        if (search == null || string.IsNullOrWhiteSpace(search.url) || search.url.Length > 3000)
            throw new FormatException("标准图源需要 search.url。");
        string method = string.IsNullOrWhiteSpace(search.method) ? "GET" : search.method.Trim().ToUpperInvariant();
        if (method != "GET" && method != "POST")
            throw new FormatException("搜索方法需使用 GET 或 POST。");
        search.method = method;
        if (method == "POST")
        {
            string headerType;
            string type = search.contentType;
            if (string.IsNullOrWhiteSpace(type) && headers != null && headers.TryGetValue("Content-Type", out headerType))
                type = headerType;
            if (string.IsNullOrWhiteSpace(type))
                type = search.body != null && !(search.body is string) ? "application/json" :
                    "application/x-www-form-urlencoded";
            MediaTypeHeaderValue parsed;
            if (!MediaTypeHeaderValue.TryParse(type, out parsed))
                throw new FormatException("搜索请求的 Content-Type 无效。");
            search.contentType = parsed.ToString();
        }
        SearchAddress("测试");
        ValidateHtmlStage(search, "搜索");
        ValidateHtmlStage(chapters, "章节");
        if (images == null || string.IsNullOrWhiteSpace(images.mode))
            throw new FormatException("标准图源需要 images.mode。");
        images.mode = images.mode.Trim().ToLowerInvariant();
        if (images.mode == "css")
        {
            if (string.IsNullOrWhiteSpace(images.selector)) images.selector = "img::data-src,data-original,src";
            OnlineHtml.ValidateField(images.selector, false);
        }
        else if (images.mode == "regex")
        {
            images.Pattern = OnlineHtml.MakePattern(images.pattern, "图片");
            if (!images.Pattern.GetGroupNumbers().Contains(1))
                throw new FormatException("图片正则需要第 1 个捕获组来保存图片地址。");
        }
        else throw new FormatException("images.mode 需使用 css 或 regex。");
    }

    static void ValidateHtmlStage(Stage stage, string label)
    {
        if (stage == null || string.IsNullOrWhiteSpace(stage.listSelector) ||
            string.IsNullOrWhiteSpace(stage.titleSelector) || string.IsNullOrWhiteSpace(stage.urlSelector))
            throw new FormatException(label + "规则需要 listSelector、titleSelector 和 urlSelector。");
        stage.HtmlList = new OnlineHtml.ListRule(stage.listSelector);
        foreach (string field in new[] { stage.titleSelector, stage.urlSelector, stage.coverSelector,
            stage.authorSelector, stage.statusSelector, stage.latestSelector })
            if (!string.IsNullOrWhiteSpace(field)) OnlineHtml.ValidateField(field, stage.HtmlList.IsRegex);
    }

    Uri SearchAddress(string query)
    {
        var address = OnlineManga.Resolve(Site, search.url.Replace(Keyword, Uri.EscapeDataString(query)));
        if (address == null || !IsAllowed(address, requestHosts))
            throw new FormatException("搜索地址需要使用图源或 requestHosts 内声明的 HTTPS 域名。");
        return address;
    }

    string SearchBody(string query)
    {
        if (search.method != "POST") return null;
        string formValue = Uri.EscapeDataString(query);
        if (search.body == null) return search.url.Contains(Keyword) ? "" : "keyword=" + formValue;
        var text = search.body as string;
        if (text != null)
        {
            if (!search.contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
                return text.Replace(Keyword, formValue);
            string escaped = Serializer(RuleLimit).Serialize(query);
            return text.Replace(Keyword, escaped.Substring(1, escaped.Length - 2));
        }
        return Serializer(RuleLimit).Serialize(ExpandBody(search.body, query));
    }

    static object ExpandBody(object value, string query)
    {
        if (value is string) return ((string)value).Replace(Keyword, query);
        var dictionary = value as IDictionary<string, object>;
        if (dictionary != null) return dictionary.ToDictionary(pair => pair.Key, pair => ExpandBody(pair.Value, query));
        var list = value as IList;
        return list == null ? value : list.Cast<object>().Select(item => ExpandBody(item, query)).ToArray();
    }

    async Task<OnlineManga.Entry[]> ReadHtmlEntries(OnlineManga.Source source, string query, OnlineManga.Entry entry,
        CancellationToken token)
    {
        var stage = entry == null ? search : chapters;
        var address = entry == null ? SearchAddress(query) : entry.PageUrl;
        if (!IsAllowed(address, requestHosts))
            throw new FormatException("章节目录地址使用了未声明的域名。");
        var options = RequestHeaders(address, entry == null ? null : address,
            postBody: entry == null ? SearchBody(query) : null, contentType: entry == null ? stage.contentType : null);
        string html = await OnlineManga.GetText(address, Referer, ResponseLimit, token, options).ConfigureAwait(false);
        var seen = new HashSet<Uri>();
        var items = new List<OnlineManga.Entry>();
        foreach (var record in stage.HtmlList.Read(html))
        {
            token.ThrowIfCancellationRequested();
            string title = OnlineManga.Label(record.Read(stage.titleSelector, null));
            Uri link = OnlineManga.Resolve(Site, record.Read(stage.urlSelector, "href"));
            if (title.Length == 0 || link == null || !seen.Add(link)) continue;
            var cover = StandardImage(Site, record.Read(stage.coverSelector, OnlineHtml.ImageAttributes));
            items.Add(new OnlineManga.Entry(source, title, link, false, cover,
                record.Read(stage.authorSelector, null), record.Read(stage.statusSelector, null),
                record.Read(stage.latestSelector, null)));
            if (items.Count == (entry == null ? 100 : ChapterLimit)) break;
        }
        if (entry != null && stage.reverse) items.Reverse();
        return items.ToArray();
    }

    async Task<OnlineManga.Page[]> ReadHtmlPages(OnlineManga.Entry entry, CancellationToken token)
    {
        var address = entry.PageUrl;
        if (!IsAllowed(address, requestHosts))
            throw new FormatException("章节页地址使用了未声明的域名。");
        string html = await OnlineManga.GetText(address, Referer, ResponseLimit, token, RequestHeaders(address, address))
            .ConfigureAwait(false);
        var result = new List<OnlineManga.Page>();
        if (images.Pattern != null)
        {
            foreach (Match match in images.Pattern.Matches(html))
            {
                token.ThrowIfCancellationRequested();
                var link = StandardImage(address, OnlineHtml.DecodeLiteralUrl(match.Groups[1].Value));
                if (link != null) result.Add(new OnlineManga.Page(link, 0, 0));
                if (result.Count == PageLimit) break;
            }
        }
        else
        {
            foreach (var image in OnlineHtml.ReadImages(html, images.selector))
            {
                token.ThrowIfCancellationRequested();
                var link = StandardImage(address, image.Url);
                if (link != null) result.Add(new OnlineManga.Page(link, image.Width, image.Height));
                if (result.Count == PageLimit) break;
            }
        }
        return result.ToArray();
    }

    Uri StandardImage(Uri origin, string value)
    {
        var address = OnlineManga.Resolve(origin, value);
        return address != null && (imageHosts == null || imageHosts.Length == 0 ||
            address.IdnHost == origin.IdnHost || IsAllowed(address, imageHosts)) ? address : null;
    }
}

