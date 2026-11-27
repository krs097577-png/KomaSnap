using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Xml.Linq;

sealed class OnlineManga
{
    public const string DefaultCatalog =
        "https://raw.githubusercontent.com/Mallyd11/mangayomi-anime-extensions/main/index.json";
    const int HtmlLimit = 4 * 1024 * 1024;
    const int CatalogLimit = 8 * 1024 * 1024;
    const string ExtensionPrefix = "eu.kanade.tachiyomi.extension.";
    static readonly Uri KeiyoushiCatalog = new Uri("https://raw.githubusercontent.com/keiyoushi/extensions/repo/index.json");
    static readonly Uri YuzonoCatalog = new Uri("https://raw.githubusercontent.com/yuzono/manga-repo/repo/index.json");
    static readonly HttpClient Http = CreateClient();
    static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);
    static readonly Regex Anchors = Pattern(@"<a\b(?<attrs>[^>]*)>(?<body>.*?)</a\s*>");
    static readonly Regex Images = Pattern(@"<img\b(?<attrs>[^>]*)>");
    static readonly Regex Tags = Pattern(@"<[^>]+>");
    static readonly Regex Spaces = Pattern(@"\s+");
    static readonly Regex MangaPath = Pattern(@"^/manga/\d+/[^/]+/?$");
    static readonly Regex PillChapterPath = Pattern(@"^/chapters/\d+-\d+/[^/]+/?$");
    static readonly Regex WeebSeries = Pattern(@"^/series/(?<id>[a-z0-9]{26})(?:/[^/]+)?/?$");
    static readonly Regex WeebChapter = Pattern(@"^/chapters/[a-z0-9]{26}/?$");
    static readonly Regex WeebChapterId = Pattern(@"^[a-z0-9]{26}$");
    static readonly Regex WeebChapterRow = Pattern(
        @"<span\b[^>]*class=[""'][^""']*\bgap-2\b[^""']*[""'][^>]*>\s*<span\b[^>]*>(?<label>.*?)</span>.*?<input\b(?<attrs>[^>]*)>");
    static readonly Regex PillPages = Pattern(@"<chapter-page\b[^>]*>(?<body>.*?)</chapter-page\s*>");
    static readonly Regex WelomaSeries = Pattern(@"^/m/[a-z0-9]+/?$");
    static readonly Regex WelomaChapter = Pattern(@"^/c/[a-z0-9]+/?$");
    static readonly Regex WelomaImages = Pattern(@"<\w+\b(?<attrs>[^>]*\bdata-img\s*=[^>]*)>");
    static readonly Regex GodaSeries = Pattern(@"^/manga/[^/]+/?$");
    static readonly Regex GodaChapterConfig = Pattern(@"<div\b(?<attrs>[^>]*\bid=[""']chapterDrawerConfig[""'][^>]*)>");
    static readonly Regex NumericId = Pattern(@"^\d{1,12}$");
    static readonly Regex OnlineSlug = Pattern(@"^[a-z0-9][a-z0-9_.-]*$");
    static readonly Regex WeebCards = Pattern(@"(?=<article\b[^>]*class=[""'][^""']*\bbg-base-300\b)");
    static readonly Regex PillCards = Pattern(@"(?=<a\b[^>]*class=[""']relative block[""'])");
    static readonly Regex ImageStyle = Pattern(@"background(?:-image)?\s*:\s*url\(['""]?(?<value>[^'""\)]+)");
    static readonly Regex WeebAuthor = Pattern(@"<strong\b[^>]*>\s*Author\(s\):\s*</strong>\s*<span\b[^>]*>(?<value>.*?)</span>");
    static readonly Regex WeebStatus = Pattern(@"<strong\b[^>]*>\s*Status:\s*</strong>\s*<span\b[^>]*>(?<value>.*?)</span>");
    static readonly Regex PillStatus = Pattern(@">\s*(?<value>publishing|finished|on hiatus|discontinued|not yet published)\s*<");
    static readonly Regex ChapterLabel = Pattern(@"\bchapter\s+\d+(?:\.\d+)?\b");
    static readonly Regex LastGodaChapter = Pattern(@"<div\b(?<attrs>[^>]*\bid=[""']chaplistlast[""'][^>]*)>");
    static readonly Regex CopyTitleMeta = Pattern(
        @"<title\b[^>]*>[^<]*?[-－]\s*(?<latest>[^<\r\n-]+?)\s*[-－]\s*(?<status>已完[结結]|連載中|连载中|連載|连载)\s*[-－]");

    static HttpClient CreateClient()
    {
        // 由 Windows 选择站点支持的 TLS 版本，兼容仅提供 TLS 1.3 的源。
        AppContext.SetSwitch("Switch.System.Net.DontEnableSystemDefaultTlsVersions", false);
        AppContext.SetSwitch("Switch.System.Net.DontEnableSchUseStrongCrypto", false);
        ServicePointManager.SecurityProtocol = (SecurityProtocolType)0;
        return new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            UseCookies = false
        });
    }

    public sealed class Source
    {
        public readonly int Id;
        public readonly string Name;
        public readonly Uri Site;
        public readonly OnlineRule Rule;
        public Uri Referer { get { return Rule == null ? Site : Rule.Referer; } }

        public Source(int id, string name, string site, OnlineRule rule = null)
        {
            Id = id;
            Name = name;
            Site = new Uri(site);
            Rule = rule;
        }

        public override string ToString() { return Name; }
    }

    public sealed class Entry : INotifyPropertyChanged
    {
        public readonly Source Source;
        public string Title { get; private set; }
        public string SourceName { get { return Source.Name; } }
        public readonly Uri Url;
        public readonly Uri PageUrl;
        public readonly bool BrowserOnly;
        public readonly string RemoteId;
        public Uri Cover { get; private set; }
        public string Author { get; private set; }
        public string Status { get; private set; }
        public string Latest { get; private set; }
        public string Details { get; private set; }
        internal bool DetailsLoaded;
        public event PropertyChangedEventHandler PropertyChanged;

        public Entry(Source source, string title, Uri url, bool browserOnly = false, Uri cover = null,
            string author = null, string status = null, string latest = null, string remoteId = null, Uri pageUrl = null)
        {
            Source = source;
            Title = title;
            Url = url;
            PageUrl = pageUrl ?? url;
            BrowserOnly = browserOnly;
            Cover = cover;
            RemoteId = remoteId;
            UpdateDetails(author, status, latest);
        }

        internal void UpdateDetails(string author, string status, string latest)
        {
            if (!string.IsNullOrWhiteSpace(author)) Author = author;
            if (!string.IsNullOrWhiteSpace(status)) Status = DisplayStatus(status);
            if (!string.IsNullOrWhiteSpace(latest)) Latest = latest;
            string detail = string.Join("   ·   ", new[] { string.IsNullOrWhiteSpace(Latest) ? null : "最新 " + Latest,
                Author, Status }.Where(value => !string.IsNullOrWhiteSpace(value)));
            if (Details == detail) return;
            Details = detail;
            if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs("Details"));
        }
    }

    public sealed class Catalog
    {
        public readonly Uri Url;
        public readonly Source[] Sources;
        public readonly int UnsupportedCount;
        public readonly int CatalogCount;
        public readonly bool IsExtensionCatalog;
        public readonly bool MigratedFromYuzono;

        public Catalog(Uri url, Source[] sources, int unsupportedCount, int catalogCount = 1,
            bool isExtensionCatalog = false, bool migratedFromYuzono = false)
        {
            Url = url;
            Sources = sources;
            UnsupportedCount = unsupportedCount;
            CatalogCount = catalogCount;
            IsExtensionCatalog = isExtensionCatalog;
            MigratedFromYuzono = migratedFromYuzono;
        }
    }

    static readonly Source Mangapill = new Source(960321322, "Mangapill", "https://mangapill.com/");
    static readonly Source WeebCentral = new Source(693275080, "Weeb Central", "https://weebcentral.com/");
    static readonly Source Weloma = new Source(1890238687, "WeLoMa", "https://weloma.art/");
    static readonly Source[] Supported = { Mangapill, Weloma, WeebCentral };
    static readonly Source MangaDex = OnlineRule.ReadEmbedded("MangaDexRule.json", -4);
    static readonly Dictionary<string, Source> ExtensionAdapters = new Dictionary<string, Source>(StringComparer.OrdinalIgnoreCase)
    {
        { ExtensionPrefix + "all.mangadex", MangaDex },
        { ExtensionPrefix + "en.mangapill", Mangapill },
        { ExtensionPrefix + "en.weebcentral", WeebCentral },
        { ExtensionPrefix + "ja.rawlh", Weloma }
    };
    static readonly Source[] SavedAdapters = Supported.Concat(new[] { MangaDex }).ToArray();
    static readonly Source Goda = new Source(-1, "G 站漫画", "https://godamh.com/");
    static readonly Source Copy = new Source(-2, "拷贝漫画", "https://www.2026copy.com/");
    static readonly Source MyComic = new Source(-3, "MYCOMIC", "https://mycomic.com/");
    static readonly Source[] Direct = { Goda, Copy, MyComic };
    static readonly Uri GodaApi = new Uri("https://v2.apikk.top/");

    public static IEnumerable<Source> SearchSources(Catalog catalog)
    {
        return Direct.Concat(catalog.Sources);
    }

    public static Entry BrowserSearch(Source source, string query)
    {
        string escaped = Uri.EscapeDataString(query);
        string path;
        if (source == Goda) path = "/s/" + escaped;
        else if (source == Copy) path = "/search?q=" + escaped + "&q_type=";
        else if (source == MyComic) path = "/cn";
        else return null;
        return new Entry(source, source == MyComic ? "MYCOMIC 站内搜索" : "在网页搜索「" + query + "」",
            new Uri(source.Site, path), true);
    }

    internal static bool CanLoadDetails(Entry entry)
    {
        return !entry.DetailsLoaded && (entry.Source == Goda && GodaSeries.IsMatch(entry.Url.AbsolutePath) ||
            entry.Source == Copy && entry.Url.AbsolutePath.StartsWith("/comic/", StringComparison.Ordinal));
    }

    internal static async Task LoadDetails(Entry entry, CancellationToken token)
    {
        string html = await GetText(entry.Url, entry.Source.Referer, HtmlLimit, token);
        token.ThrowIfCancellationRequested();
        if (entry.Source == Goda)
        {
            var authors = Anchors.Matches(html).Cast<Match>().Where(anchor =>
                Attribute(anchor.Groups["attrs"].Value, "href").StartsWith("/manga-author/", StringComparison.Ordinal))
                .Select(anchor => Label(anchor.Groups["body"].Value).Trim(' ', ',', '，'))
                .Where(author => author.Length > 0).Distinct();
            entry.UpdateDetails(string.Join("、", authors), null,
                Attribute(LastGodaChapter.Match(html).Groups["attrs"].Value, "data-title"));
        }
        else
        {
            var details = CopyTitleMeta.Match(html);
            entry.UpdateDetails(null, Label(details.Groups["status"].Value), Label(details.Groups["latest"].Value));
        }
        entry.DetailsLoaded = true;
    }

    public sealed class Page
    {
        public readonly Uri Url;
        public readonly int Width, Height;

        public Page(Uri url, int width, int height)
        {
            Url = url;
            bool valid = width > 0 && height > 0 && width <= 16000 && height <= 16000 &&
                width * (long)height <= 64000000;
            Width = valid ? width : 0;
            Height = valid ? height : 0;
        }
    }

    static Catalog Default()
    {
        return new Catalog(new Uri(DefaultCatalog), Supported, 1);
    }

    static Catalog[] ReadCatalogs(string folder)
    {
        try
        {
            var saved = XElement.Load(Path.Combine(folder, "online-manga.xml"));
            var elements = saved.Name == "catalogs" ? saved.Elements("catalog") : new[] { saved }.AsEnumerable();
            var catalogs = elements.Select(item =>
            {
                Uri url = Https((string)item.Attribute("url"));
                var ids = item.Elements("source").Select(node => (int?)node.Attribute("id")).ToArray();
                var sources = SavedAdapters.Where(source => ids.Contains(source.Id)).ToArray();
                return url == null || sources.Length == 0 ? null :
                    new Catalog(url, sources, Math.Max(0, (int?)item.Attribute("unsupported") ?? 0),
                        isExtensionCatalog: (bool?)item.Attribute("extensionCatalog") == true);
            }).Where(item => item != null).ToArray();
            if (catalogs.Length > 0) return catalogs;
        }
        catch (Exception error) { if (!ReaderState.IsStorageError(error)) throw; }
        return new[] { Default() };
    }

    static Catalog Combined(Catalog[] catalogs)
    {
        var latest = catalogs.Last();
        return new Catalog(latest.Url, catalogs.SelectMany(catalog => catalog.Sources).Distinct().ToArray(),
            latest.UnsupportedCount, catalogs.Length, latest.IsExtensionCatalog, latest.MigratedFromYuzono);
    }

    public static Catalog LoadCatalog(string folder) { return Combined(ReadCatalogs(folder)); }

    public static Catalog SaveCatalog(string folder, Catalog catalog)
    {
        var catalogs = ReadCatalogs(folder).Where(previous => previous.Url != catalog.Url)
            .Concat(new[] { catalog }).ToArray();
        WriteCatalogs(folder, catalogs);
        return Combined(catalogs);
    }

    public static Catalog ResetCatalog(string folder)
    {
        var defaults = Default();
        WriteCatalogs(folder, new[] { defaults });
        return defaults;
    }

    static void WriteCatalogs(string folder, Catalog[] catalogs)
    {
        var saved = new XElement("catalogs", catalogs.Select(catalog => new XElement("catalog",
            new XAttribute("url", catalog.Url),
            new XAttribute("unsupported", catalog.UnsupportedCount),
            new XAttribute("extensionCatalog", catalog.IsExtensionCatalog),
            catalog.Sources.Select(source => new XElement("source", new XAttribute("id", source.Id))))));
        ReaderState.WriteAtomic(Path.Combine(folder, "online-manga.xml"), saved.Save);
    }

    public static async Task<Catalog> FetchCatalog(string address, CancellationToken token)
    {
        Uri url = Https(address);
        if (url == null) throw new FormatException("请输入 HTTPS 漫画源索引地址。");
        if (SameHost(url, MyComic.Site))
            throw new FormatException("MYCOMIC 已有原站入口，站点验证通过后可在浏览器中搜索。");
        if (Direct.Any(source => SameHost(url, source.Site)))
            throw new FormatException("此站点已内置。可在来源下拉框里选择站点，再输入书名搜索。");
        if (url.AbsolutePath.EndsWith("/anime_index.json", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("这是动画索引。请使用同仓库的 index.json，或点击「恢复默认」。");
        url = CurrentCatalogUrl(url);
        string json = await GetText(url, null, CatalogLimit, token).ConfigureAwait(false);
        object document = ReadCatalogJson(json);
        var entries = ExtensionEntries(document);
        bool migrated = url == YuzonoCatalog && entries != null && entries.Length == 1 &&
            (string)Field(entries[0], "packageName") == ExtensionPrefix + "all.keiyoushi";
        if (migrated)
        {
            url = KeiyoushiCatalog;
            json = await GetText(url, null, CatalogLimit, token).ConfigureAwait(false);
            document = ReadCatalogJson(json);
        }
        return ParseCatalog(url, document, migrated);
    }

    internal static Catalog ParseCatalog(Uri url, string json)
    {
        return ParseCatalog(url, ReadCatalogJson(json), false);
    }

    static Uri CurrentCatalogUrl(Uri url)
    {
        foreach (var published in new[] { KeiyoushiCatalog, YuzonoCatalog })
        {
            string root = published.AbsolutePath.Substring(0, published.AbsolutePath.LastIndexOf('/'));
            string github = root.Substring(0, root.LastIndexOf('/')) + "/raw/repo";
            bool known = url.IdnHost == published.IdnHost &&
                root.Equals(url.AbsolutePath.Substring(0, url.AbsolutePath.LastIndexOf('/')), StringComparison.OrdinalIgnoreCase);
            bool officialLink = url.IdnHost == "github.com" &&
                github.Equals(url.AbsolutePath.Substring(0, url.AbsolutePath.LastIndexOf('/')), StringComparison.OrdinalIgnoreCase);
            string file = url.Segments.Last();
            if ((known || officialLink) && (file == "index.min.json" || file == "index.pb" || file == "index.json"))
                return published;
        }
        return url;
    }

    static object ReadCatalogJson(string json)
    {
        return new JavaScriptSerializer { MaxJsonLength = CatalogLimit, RecursionLimit = 24 }.DeserializeObject(json);
    }

    static object[] ExtensionEntries(object document)
    {
        var current = Field(Field(document, "extensionList"), "extensions") as object[];
        if (current != null) return current;
        var legacy = document as object[];
        return legacy != null && legacy.Any(item => Field(item, "pkg") is string) ? legacy : null;
    }

    static Catalog ParseCatalog(Uri url, object document, bool migrated)
    {
        var extensions = ExtensionEntries(document);
        if (extensions != null) return ParseExtensions(url, extensions, migrated);
        var items = document as object[];
        if (items == null) throw new FormatException("索引格式有误，需要 Mangayomi 漫画源列表。");
        var mapped = new HashSet<int>();
        int manga = 0;
        foreach (var item in items.OfType<Dictionary<string, object>>())
        {
            object flag, id, site;
            if (!item.TryGetValue("isManga", out flag) || !true.Equals(flag)) continue;
            manga++;
            if (!item.TryGetValue("id", out id) || !(id is int) ||
                !item.TryGetValue("baseUrl", out site)) continue;
            Uri origin = Https(site as string);
            var supported = Supported.FirstOrDefault(source => source.Id == (int)id && SameHost(origin, source.Site));
            if (supported != null) mapped.Add(supported.Id);
        }
        if (manga == 0) throw new FormatException("索引没有漫画源。请检查是否用了动画索引。");
        if (mapped.Count == 0) throw new FormatException("此索引暂时没有 KomaSnap 已适配的漫画源。");
        return new Catalog(url, Supported.Where(source => mapped.Contains(source.Id)).ToArray(), manga - mapped.Count);
    }

    static Catalog ParseExtensions(Uri url, object[] entries, bool migrated)
    {
        var matched = new HashSet<Source>();
        int mapped = 0;
        int actual = 0;
        foreach (object entry in entries)
        {
            string package = (Field(entry, "packageName") ?? Field(entry, "pkg")) as string;
            if (string.IsNullOrEmpty(package) || package == ExtensionPrefix + "all.keiyoushi" ||
                package == ExtensionPrefix + "all.mihon") continue;
            actual++;
            Source adapter;
            if (!ExtensionAdapters.TryGetValue(package, out adapter)) continue;
            var declared = Field(entry, "sources") as object[];
            if (declared == null || !declared.Any(source =>
            {
                var home = Https((Field(source, "homeUrl") ?? Field(source, "baseUrl")) as string);
                return SameHost(home, adapter.Site) || adapter == Weloma && home != null && home.IdnHost == "weloma.net";
            })) continue;
            matched.Add(adapter);
            mapped++;
        }
        if (actual == 0) throw new FormatException("这个索引只包含升级或迁移提示，没有可导入的图源。");
        if (matched.Count == 0) throw new FormatException("这个扩展索引暂时没有 KomaSnap 已适配的图源。");
        return new Catalog(url, SavedAdapters.Where(matched.Contains).ToArray(), actual - mapped,
            isExtensionCatalog: true, migratedFromYuzono: migrated);
    }

    public static async Task<Entry[]> Search(Source source, string query, CancellationToken token)
    {
        if (source.Rule != null) return await source.Rule.ReadEntries(source, query, null, token).ConfigureAwait(false);
        if (source == MyComic) throw new IOException("原站要求浏览器验证，可使用原站入口。");
        string text = Uri.EscapeDataString(query);
        if (source == Copy)
        {
            var uri = new Uri(source.Site, "/api/kb/web/searchcl/comics?offset=0&platform=2&limit=12&q=" +
                text + "&q_type=");
            var response = await GetJson(uri, source.Site, token).ConfigureAwait(false);
            var list = Field(Field(response, "results"), "list") as object[];
            if (!200.Equals(Field(response, "code")) || list == null)
                throw new IOException("站点未返回有效的搜索结果。");
            return list.Select(row =>
            {
                string slug = Field(row, "path_word") as string;
                string title = Field(row, "name") as string;
                var authors = Field(row, "author") as object[] ?? new object[0];
                return string.IsNullOrWhiteSpace(title) || slug == null || !OnlineSlug.IsMatch(slug) ?
                    null : new Entry(source, title.Trim(), new Uri(source.Site, "/comic/" + slug), true,
                        Resolve(source.Site, Field(row, "cover") as string),
                        string.Join("、", authors.Select(author => Field(author, "name") as string)
                            .Where(name => !string.IsNullOrWhiteSpace(name))), Field(row, "status") as string,
                        Field(row, "last_chapter") as string);
            }).Where(entry => entry != null).GroupBy(entry => entry.Url).Select(group => group.First()).ToArray();
        }
        if (source == Goda)
        {
            string page = await GetText(BrowserSearch(source, query).Url, source.Site, HtmlLimit, token)
                .ConfigureAwait(false);
            return ReadLinks(source, page, GodaSeries, false, false, true).Take(32).ToArray();
        }
        string path = source == Mangapill ? "/search?q=" + text : source == Weloma ?
            "/manga-list.html?name=" + text + "&page=1" :
            "/search/data?limit=32&offset=0&author=&text=" + text +
            "&sort=Best%20Match&order=Ascending&official=Any&display_mode=Full%20Display";
        string html = await GetText(new Uri(source.Site, path), source.Site, HtmlLimit, token).ConfigureAwait(false);
        var cards = source == WeebCentral ? WeebCards.Split(html) :
            source == Mangapill ? PillCards.Split(html) : new[] { html };
        return cards.SelectMany(card => ReadLinks(source, card,
                source == Mangapill ? MangaPath : source == Weloma ? WelomaSeries : WeebSeries,
                source == WeebCentral, source == Weloma, true))
            .GroupBy(item => item.Url).Select(group => group.First()).Take(100).ToArray();
    }

    public static async Task<Entry[]> Chapters(Entry manga, CancellationToken token)
    {
        var source = manga.Source;
        if (source.Rule != null) return await source.Rule.ReadEntries(source, null, manga, token).ConfigureAwait(false);
        Uri request = manga.Url;
        if (source == WeebCentral)
            request = new Uri(source.Site, "/series/" + WeebSeries.Match(manga.Url.AbsolutePath).Groups["id"].Value +
                "/full-chapter-list");
        string html = await GetText(request, source.Site, HtmlLimit, token).ConfigureAwait(false);
        if (source == Goda)
        {
            var config = GodaChapterConfig.Match(html);
            string id = Attribute(config.Groups["attrs"].Value, "data-mid");
            if (!NumericId.IsMatch(id)) throw new IOException("站点未提供章节列表。可用浏览器打开这部漫画。");
            var endpoint = new Uri(GodaApi, "/api/v2/manga/get?mid=" + id + "&mode=all");
            var result = await GetJson(endpoint, source.Site, token).ConfigureAwait(false);
            var chapters = Field(Field(result, "data"), "chapters") as object[];
            if (!true.Equals(Field(result, "status")) || chapters == null)
                throw new IOException("站点未提供章节列表。可用浏览器打开这部漫画。");
            return chapters.Select(row =>
            {
                object details = Field(row, "attributes");
                string title = Field(details, "title") as string;
                string slug = Field(details, "slug") as string;
                return string.IsNullOrWhiteSpace(title) || slug == null || !OnlineSlug.IsMatch(slug) ?
                    null : new Entry(source, title.Trim(),
                        new Uri(source.Site, manga.Url.AbsolutePath.TrimEnd('/') + "/" + slug), true);
            }).Where(entry => entry != null).GroupBy(entry => entry.Url).Select(group => group.First())
                .Reverse().ToArray();
        }
        var links = ReadLinks(source, html, source == Mangapill ? PillChapterPath :
            source == Weloma ? WelomaChapter : WeebChapter, false, source == Weloma).ToArray();
        if (links.Length != 0 || source != WeebCentral) return links;

        return WeebChapterRow.Matches(html).Cast<Match>().Select(row =>
        {
            string id = Attribute(row.Groups["attrs"].Value, "value");
            return WeebChapterId.IsMatch(id) ?
                new Entry(source, Label(row.Groups["label"].Value), new Uri(source.Site, "/chapters/" + id)) : null;
        }).Where(chapter => chapter != null && chapter.Title.Length != 0)
            .GroupBy(chapter => chapter.Url).Select(group => group.First()).ToArray();
    }

    public static async Task<Page[]> PageImages(Entry chapter, CancellationToken token)
    {
        var source = chapter.Source;
        if (source.Rule != null) return await source.Rule.ReadPages(chapter, token).ConfigureAwait(false);
        Uri request = source != WeebCentral ? chapter.Url : new Uri(source.Site,
            chapter.Url.AbsolutePath.TrimEnd('/') + "/images?current_page=1&reading_style=long_strip");
        string html = await GetText(request, source.Site, HtmlLimit, token).ConfigureAwait(false);
        return ReadImages(source, html);
    }

    internal static Page[] ReadImages(Source source, string html)
    {
        if (source == Weloma)
        {
            var decoded = new List<Page>();
            foreach (Match image in WelomaImages.Matches(html))
            {
                string attributes = image.Groups["attrs"].Value;
                if (!Attribute(attributes, "class").Split(' ').Contains("chapter-img")) continue;
                try
                {
                    string src = Encoding.UTF8.GetString(Convert.FromBase64String(Attribute(attributes, "data-img")));
                    Uri url = Resolve(source.Site, src);
                    if (url != null) decoded.Add(new Page(url, 0, 0));
                }
                catch (FormatException) { }
            }
            return decoded.ToArray();
        }

        var areas = source == Mangapill
            ? PillPages.Matches(html).Cast<Match>().Select(page => page.Groups["body"].Value)
            : new[] { html }.AsEnumerable();
        var pages = new List<Page>();
        foreach (string area in areas)
        {
            foreach (Match image in Images.Matches(area))
            {
                string attributes = image.Groups["attrs"].Value;
                string src = Attribute(attributes, "data-src");
                if (src.Length == 0) src = Attribute(attributes, "src");
                Uri url = Resolve(source.Site, src);
                if (url != null && (source == Mangapill || !SameHost(url, source.Site)))
                {
                    int width, height;
                    int.TryParse(Attribute(attributes, "width"), out width);
                    int.TryParse(Attribute(attributes, "height"), out height);
                    if (width <= 0 || height <= 0 || width > 16000 || height > 16000 ||
                        width * (long)height > 64000000) width = height = 0;
                    pages.Add(new Page(url, width, height));
                }
                if (source == Mangapill) break;
            }
        }
        return pages.ToArray();
    }

    static IEnumerable<Entry> ReadLinks(Source source, string html, Regex route, bool useSlug, bool preferTitle,
        bool collectMetadata = false)
    {
        var found = new Dictionary<Uri, int>();
        var results = new List<Entry>();
        var placeholders = new HashSet<Uri>();
        var covers = new Dictionary<Uri, Uri>();
        string author = collectMetadata && source == WeebCentral ? Label(WeebAuthor.Match(html).Groups["value"].Value) : null;
        string status = !collectMetadata ? null : source == WeebCentral ? Label(WeebStatus.Match(html).Groups["value"].Value) :
            source == Mangapill ? PillStatus.Match(html).Groups["value"].Value : null;
        string latest = collectMetadata && source == WeebCentral ? ChapterLabel.Match(Label(html)).Value : null;
        foreach (Match anchor in Anchors.Matches(html))
        {
            string attrs = anchor.Groups["attrs"].Value;
            string body = anchor.Groups["body"].Value;
            Uri url = Resolve(source.Site, Attribute(attrs, "href"));
            if (!SameHost(url, source.Site) || !route.IsMatch(url.AbsolutePath)) continue;
            string title = Label(body);
            Uri cover = null;
            if (collectMetadata)
            {
                string image = Images.Match(body).Groups["attrs"].Value;
                string src = Attribute(image, "data-src");
                if (src.Length == 0) src = Attribute(image, "src");
                if (src.Length == 0) src = ImageStyle.Match(body).Groups["value"].Value;
                cover = Resolve(source.Site, src);
                if (cover != null) covers[url] = cover;
                else covers.TryGetValue(url, out cover);
                if (source == WeebCentral)
                {
                    string alt = Attribute(image, "alt");
                    if (alt.EndsWith(" cover", StringComparison.OrdinalIgnoreCase))
                        title = alt.Substring(0, alt.Length - 6);
                }
            }
            if (preferTitle)
            {
                string attribute = Attribute(attrs, "title");
                if (attribute.Length == 0) continue;
                title = attribute;
            }
            if (useSlug && (title.Length == 0 || title.Equals("More Info", StringComparison.OrdinalIgnoreCase) ||
                title.Equals("Details", StringComparison.OrdinalIgnoreCase)))
            {
                title = Attribute(attrs, "title");
                if (title.Length == 0)
                    title = Uri.UnescapeDataString(url.AbsolutePath.TrimEnd('/').Split('/').Last()).Replace('-', ' ');
                if (WeebChapterId.IsMatch(title)) continue;
                if (!found.ContainsKey(url)) placeholders.Add(url);
            }
            if (title.Length == 0) continue;
            int existing;
            if (found.TryGetValue(url, out existing))
            {
                if (Label(body) == title && placeholders.Remove(url))
                    results[existing] = new Entry(source, title, url, false, cover, author, status, latest);
                continue;
            }
            found.Add(url, results.Count);
            results.Add(new Entry(source, title, url, false, cover, author, status, latest));
        }
        return results;
    }

    static string DisplayStatus(string status)
    {
        switch ((status ?? "").Trim().ToLowerInvariant())
        {
            case "ongoing": case "publishing": return "连载中";
            case "complete": case "completed": case "finished": case "已完結": return "已完结";
            case "hiatus": case "on hiatus": return "休刊";
            case "cancelled": case "canceled": case "discontinued": return "已停止";
            default: return status;
        }
    }

    internal static string Label(string html)
    {
        return Spaces.Replace(WebUtility.HtmlDecode(Tags.Replace(html, " ")), " ").Trim();
    }

    static string Attribute(string attributes, string name)
    {
        string pattern = @"(?:^|\s)" + name + @"\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))";
        Match match = Regex.Match(attributes, pattern, RegexOptions.IgnoreCase, MatchTimeout);
        for (int i = 1; i < match.Groups.Count; i++)
            if (match.Groups[i].Success) return WebUtility.HtmlDecode(match.Groups[i].Value).Trim();
        return "";
    }

    static Regex Pattern(string pattern)
    {
        return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline, MatchTimeout);
    }

    internal static Uri Https(string value)
    {
        Uri url;
        return Uri.TryCreate(value, UriKind.Absolute, out url) && url.Scheme == Uri.UriSchemeHttps &&
            url.IsDefaultPort && !url.IsLoopback && url.HostNameType == UriHostNameType.Dns &&
            url.IdnHost.Contains(".") && url.UserInfo.Length == 0 ? url : null;
    }

    internal static Uri Resolve(Uri origin, string value)
    {
        Uri url;
        return !string.IsNullOrEmpty(value) && Uri.TryCreate(origin, value, out url) ?
            Https(url.AbsoluteUri) : null;
    }

    static bool SameHost(Uri url, Uri site)
    {
        return url != null && string.Equals(url.IdnHost, site.IdnHost, StringComparison.OrdinalIgnoreCase);
    }

    internal sealed class RequestOptions
    {
        public readonly IDictionary<string, string> Headers;
        public readonly string Body, ContentType;
        public bool IsPost { get { return Body != null; } }

        public RequestOptions(IDictionary<string, string> headers, string body = null, string contentType = null)
        {
            Headers = headers;
            Body = body;
            ContentType = contentType;
        }
    }

    internal static async Task<string> GetText(Uri url, Uri site, int maxBytes, CancellationToken token,
        RequestOptions options = null)
    {
        using (var bytes = await Get(url, site, maxBytes, false, token, options).ConfigureAwait(false))
        using (var reader = new StreamReader(bytes, Encoding.UTF8, true))
            return reader.ReadToEnd();
    }

    static async Task<object> GetJson(Uri url, Uri site, CancellationToken token)
    {
        string json = await GetText(url, site, HtmlLimit, token).ConfigureAwait(false);
        return new JavaScriptSerializer { MaxJsonLength = HtmlLimit, RecursionLimit = 24 }.DeserializeObject(json);
    }

    internal static object Field(object row, string name)
    {
        var fields = row as Dictionary<string, object>;
        object value;
        return fields != null && fields.TryGetValue(name, out value) ? value : null;
    }

    internal static async Task<MemoryStream> Get(Uri url, Uri site, int maxBytes, bool image, CancellationToken token,
        RequestOptions options = null)
    {
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(14));
            try
            {
                bool post = options != null && options.IsPost;
                for (int redirects = 0; redirects <= 3; redirects++)
                {
                    using (var request = new HttpRequestMessage(post ? HttpMethod.Post : HttpMethod.Get, url))
                    {
                        request.Headers.UserAgent.ParseAdd("KomaSnap/1.14");
                        if (site != null) request.Headers.Referrer = site;
                        if (image) request.Headers.Accept.ParseAdd("image/jpeg, image/png, image/gif, image/webp;q=0.8");
                        if (options != null && options.Headers != null)
                        {
                            foreach (var header in options.Headers)
                            {
                                if (header.Key.Equals("Referer", StringComparison.OrdinalIgnoreCase))
                                {
                                    request.Headers.Referrer = Https(header.Value);
                                    continue;
                                }
                                request.Headers.Remove(header.Key);
                                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                            }
                        }
                        if (post)
                        {
                            request.Content = new StringContent(options.Body, Encoding.UTF8);
                            request.Content.Headers.ContentType =
                                System.Net.Http.Headers.MediaTypeHeaderValue.Parse(options.ContentType);
                        }
                        using (var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                            timeout.Token).ConfigureAwait(false))
                        {
                            int status = (int)response.StatusCode;
                            if (status >= 300 && status < 400 && response.Headers.Location != null)
                            {
                                Uri next = Resolve(url, response.Headers.Location.OriginalString);
                                if (!SameHost(next, url)) throw new IOException("网站将请求重定向到了其他域名。");
                                if (status == 301 || status == 302 || status == 303) post = false;
                                url = next;
                                continue;
                            }
                            IEnumerable<string> mitigated;
                            if (response.Headers.TryGetValues("cf-mitigated", out mitigated) &&
                                mitigated.Any(value => value.Equals("challenge", StringComparison.OrdinalIgnoreCase)))
                                throw new IOException("站点要求浏览器验证。");
                            if (!response.IsSuccessStatusCode)
                                throw new IOException("网站返回 HTTP " + status + "。");
                            if (response.Content.Headers.ContentLength > maxBytes) throw new IOException("响应过大。");
                            var media = response.Content.Headers.ContentType;
                            if (image && media != null && !media.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                                && media.MediaType != "application/octet-stream")
                                throw new IOException("来源返回的内容不是图片。");

                            using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                            {
                                var data = new MemoryStream();
                                try
                                {
                                    var buffer = new byte[16384];
                                    int count;
                                    while ((count = await stream.ReadAsync(buffer, 0, buffer.Length, timeout.Token)
                                        .ConfigureAwait(false)) != 0)
                                    {
                                        if (data.Length + count > maxBytes) throw new IOException("响应过大。");
                                        data.Write(buffer, 0, count);
                                    }
                                    data.Position = 0;
                                    return data;
                                }
                                catch { data.Dispose(); throw; }
                            }
                        }
                    }
                }
                throw new IOException("网站重定向次数过多。");
            }
            catch (OperationCanceledException)
            {
                token.ThrowIfCancellationRequested();
                throw new IOException("连接网站超时。");
            }
        }
    }
}

