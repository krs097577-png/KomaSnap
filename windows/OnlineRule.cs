using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

sealed partial class OnlineRule
{
    const int RuleLimit = 256 * 1024;
    const int ResponseLimit = 4 * 1024 * 1024;
    const int ChapterLimit = 10000;
    const int PageLimit = 2000;
    static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(750);
    static readonly Regex Slot = new Regex(@"\{([^{}]+)\}", RegexOptions.None, MatchTimeout);

    public string id { get; set; }
    public string name { get; set; }
    public string baseUrl { get; set; }
    public string referer { get; set; }
    public Dictionary<string, string> headers { get; set; }
    public string[] requestHosts { get; set; }
    public string[] imageHosts { get; set; }
    public bool oldestFirst { get; set; }
    public Stage search { get; set; }
    public Stage chapters { get; set; }
    public Stage pages { get; set; }
    public ImageRule images { get; set; }
    internal Uri Site, Referer;
    internal bool IsStandard { get { return images != null || search != null && search.listSelector != null; } }

    static OnlineRule() { OnlineHtml.RegisterEmbeddedLibraries(); }

    public sealed class Stage
    {
        public string request { get; set; }
        public string rows { get; set; }
        public string pattern { get; set; }
        public int pageSize { get; set; }
        public int maxPages { get; set; }
        public Dictionary<string, object> fields { get; set; }
        public string url { get; set; }
        public string method { get; set; }
        public string listSelector { get; set; }
        public string titleSelector { get; set; }
        public string urlSelector { get; set; }
        public string coverSelector { get; set; }
        public string authorSelector { get; set; }
        public string statusSelector { get; set; }
        public string latestSelector { get; set; }
        public bool reverse { get; set; }
        public object body { get; set; }
        public string contentType { get; set; }
        internal Regex Records;
        internal OnlineHtml.ListRule HtmlList;
    }

    public static OnlineRule[] ReadSaved(string folder)
    {
        string path = Path.Combine(folder, "online-source-rules.json");
        try { return File.Exists(path) ? ReadRules(path) : new OnlineRule[0]; }
        catch (Exception error)
        {
            if (!(ReaderState.IsStorageError(error) || error is ArgumentException || error is InvalidOperationException))
                throw;
            return new OnlineRule[0];
        }
    }

    public static OnlineRule[] Import(string folder, string path)
    {
        var incoming = ReadRules(path);
        var merged = incoming.Concat(ReadSaved(folder))
            .GroupBy(rule => rule.id, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToArray();
        if (merged.Length > 24) throw new FormatException("最多可保存 24 个 JSON 图源。");
        string json = Serializer(RuleLimit).Serialize(merged);
        ReaderState.WriteAtomic(Path.Combine(folder, "online-source-rules.json"), temp => File.WriteAllText(temp, json));
        return merged;
    }

    static OnlineRule[] ReadRules(string path)
    {
        if (new FileInfo(path).Length > RuleLimit) throw new FormatException("图源规则文件不能超过 256 KB。");
        string json = File.ReadAllText(path).TrimStart();
        var serializer = Serializer(RuleLimit);
        var rules = json.StartsWith("[", StringComparison.Ordinal) ?
            serializer.Deserialize<OnlineRule[]>(json) : new[] { serializer.Deserialize<OnlineRule>(json) };
        if (rules == null || rules.Length == 0 || rules.Length > 24)
            throw new FormatException("规则文件需要包含 1 到 24 个 KomaSnap 图源。");
        foreach (var rule in rules)
        {
            if (rule == null) throw new FormatException("规则文件包含空的图源。");
            rule.Validate();
        }
        if (rules.Select(rule => rule.id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != rules.Length)
            throw new FormatException("同一个规则文件里的图源 ID 必须唯一。");
        return rules;
    }

    void Validate()
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 64 || (Site = OnlineManga.Https(baseUrl)) == null)
            throw new FormatException("请填写有效的 name 和 HTTPS baseUrl。");
        if (string.IsNullOrWhiteSpace(id))
            id = "rule-" + ReaderState.FileKey(Site.AbsoluteUri + "|" + name.Trim()).Substring(0, 24);
        if (!Regex.IsMatch(id, @"^[a-zA-Z0-9][a-zA-Z0-9_-]{0,63}$"))
            throw new FormatException("图源 ID 只能包含字母、数字、下划线和连字符。");
        Referer = string.IsNullOrEmpty(referer) ? Site : OnlineManga.Https(referer);
        if (Referer == null || Referer.IdnHost != Site.IdnHost)
            throw new FormatException("Referer 需使用图源自身的 HTTPS 域名。");
        ValidateHosts(requestHosts, false);
        ValidateHosts(imageHosts, true);
        ValidateHeaders();
        if (IsStandard)
        {
            ValidateStandard();
            return;
        }
        CheckStage(search, "查询", "url", "title");
        CheckStage(chapters, "目录", "url", "title");
        CheckStage(pages, "图片", "url");
    }

    static void ValidateHosts(string[] hosts, bool wildcards)
    {
        if (hosts == null) return;
        if (hosts.Length > 16) throw new FormatException("图源允许的域名过多。");
        foreach (string host in hosts)
        {
            string name = wildcards && host != null && host.StartsWith("*.", StringComparison.Ordinal) ?
                host.Substring(2) : host;
            var uri = OnlineManga.Https("https://" + name + "/");
            if (uri == null || !string.Equals(uri.IdnHost, name, StringComparison.OrdinalIgnoreCase))
                throw new FormatException("域名清单只填写主机名。");
        }
    }

    static void CheckStage(Stage stage, string label, params string[] keys)
    {
        if (stage == null || string.IsNullOrWhiteSpace(stage.request) || stage.request.Length > 3000 ||
            stage.fields == null || keys.Any(key => !stage.fields.ContainsKey(key)) ||
            string.IsNullOrEmpty(stage.rows) == string.IsNullOrEmpty(stage.pattern) ||
            stage.maxPages < 0 || stage.maxPages > 100 ||
            stage.maxPages > 1 && (stage.pageSize < 1 || stage.pageSize > 500 ||
                !stage.request.Contains("{offset}") && !stage.request.Contains("{page}")))
            throw new FormatException(label + "规则缺少请求、提取方式或必要的字段。");
        foreach (var field in stage.fields.ToArray())
        {
            if (field.Value is string) continue;
            var list = field.Value as System.Collections.IList;
            var values = list == null ? null : list.Cast<object>().ToArray();
            if (values == null || values.Length == 0 || values.Any(value => !(value is string)))
                throw new FormatException(label + "字段需要字符串或字符串列表。");
            stage.fields[field.Key] = values;
        }
        if (!string.IsNullOrEmpty(stage.pattern))
            stage.Records = new Regex(stage.pattern, RegexOptions.Singleline | RegexOptions.IgnoreCase, MatchTimeout);
    }

    public OnlineManga.Source AsSource() { return new OnlineManga.Source(0, name, baseUrl, this); }

    internal static OnlineManga.Source ReadEmbedded(string resource, int sourceId)
    {
        using (var file = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
        using (var text = new StreamReader(file))
        {
            var rule = Serializer(RuleLimit).Deserialize<OnlineRule>(text.ReadToEnd());
            rule.Validate();
            return new OnlineManga.Source(sourceId, rule.name, rule.baseUrl, rule);
        }
    }

    public async Task<OnlineManga.Entry[]> ReadEntries(OnlineManga.Source source, string query, OnlineManga.Entry entry,
        CancellationToken token)
    {
        if (IsStandard) return await ReadHtmlEntries(source, query, entry, token).ConfigureAwait(false);
        var stage = entry == null ? search : chapters;
        var result = new List<OnlineManga.Entry>();
        var seen = new HashSet<Uri>();
        int limit = entry == null ? 100 : ChapterLimit;
        for (int index = 0; index < Math.Max(1, stage.maxPages) && result.Count < limit; index++)
        {
            var records = await Fetch(stage, query, entry, index, token).ConfigureAwait(false);
            int countBefore = result.Count;
            foreach (var record in records)
            {
                Uri address = OnlineManga.Resolve(record.Address, record.Get("url"));
                var title = OnlineManga.Label(record.Get("title"));
                if (title.Length == 0 || address == null || !seen.Add(address)) continue;
                Uri external = OnlineManga.Https(record.Get("external"));
                result.Add(new OnlineManga.Entry(source, title, external ?? address, external != null,
                    ImageAddress(record, "cover"), record.Get("author"), record.Get("status"),
                    record.Get("latest"), record.Get("id"), address));
                if (result.Count == limit) break;
            }
            if (records.Length < stage.pageSize || countBefore == result.Count) break;
        }
        if (entry != null && oldestFirst) result.Reverse();
        return result.ToArray();
    }

    public async Task<OnlineManga.Page[]> ReadPages(OnlineManga.Entry entry, CancellationToken token)
    {
        if (IsStandard) return await ReadHtmlPages(entry, token).ConfigureAwait(false);
        var result = new List<OnlineManga.Page>();
        for (int index = 0; index < Math.Max(1, pages.maxPages) && result.Count < PageLimit; index++)
        {
            var records = await Fetch(pages, null, entry, index, token).ConfigureAwait(false);
            foreach (var record in records)
            {
                Uri url = ImageAddress(record, "url");
                if (url == null) continue;
                int width, height;
                int.TryParse(record.Get("width"), out width);
                int.TryParse(record.Get("height"), out height);
                result.Add(new OnlineManga.Page(url, width, height));
                if (result.Count == PageLimit) break;
            }
            if (records.Length < pages.pageSize) break;
        }
        return result.ToArray();
    }

    Uri ImageAddress(Record record, string name)
    {
        var address = OnlineManga.Resolve(record.Address, record.Get(name));
        return address != null && (address.IdnHost == record.Address.IdnHost ||
            IsAllowed(address, imageHosts)) ? address : null;
    }

    bool IsAllowed(Uri url, string[] hosts)
    {
        return url.IdnHost == Site.IdnHost || hosts != null && hosts.Any(host =>
            host.StartsWith("*.", StringComparison.Ordinal) ?
                url.IdnHost.EndsWith(host.Substring(1), StringComparison.OrdinalIgnoreCase) :
                string.Equals(host, url.IdnHost, StringComparison.OrdinalIgnoreCase));
    }

    async Task<Record[]> Fetch(Stage stage, string query, OnlineManga.Entry entry, int index,
        CancellationToken token)
    {
        string request = Slot.Replace(stage.request, match =>
        {
            switch (match.Groups[1].Value)
            {
                case "query": return Uri.EscapeDataString(query ?? "");
                case "id": return Uri.EscapeDataString(entry == null ? "" : entry.RemoteId ?? "");
                case "entry": return entry == null ? "" : entry.PageUrl.AbsoluteUri;
                case "page": return (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                case "offset": return (index * stage.pageSize).ToString(System.Globalization.CultureInfo.InvariantCulture);
                default: throw new FormatException("规则包含未知的请求参数。");
            }
        });
        var address = OnlineManga.Resolve(Site, request);
        if (address == null || !IsAllowed(address, requestHosts))
            throw new FormatException("规则请求使用了未声明的 HTTPS 域名。");
        string body = await OnlineManga.GetText(address, Referer, ResponseLimit, token,
            RequestHeaders(address, entry == null ? null : entry.PageUrl)).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (stage.Records != null)
            return stage.Records.Matches(body).Cast<Match>().Take(ChapterLimit)
                .Select(match => new Record(stage, address, match, null)).ToArray();
        object root = Serializer(ResponseLimit).DeserializeObject(body);
        var rows = stage.rows == "$" ? root as object[] : SelectPath(root, stage.rows) as object[];
        if (rows == null) throw new FormatException("返回数据缺少规则指定的 JSON 列表。");
        return rows.Take(ChapterLimit).Select(row => new Record(stage, address, row, root)).ToArray();
    }

    static JavaScriptSerializer Serializer(int limit)
    {
        return new JavaScriptSerializer { MaxJsonLength = limit, RecursionLimit = 32 };
    }

    static object SelectPath(object current, string path)
    {
        foreach (string part in path.Split('.'))
        {
            int bracket = part.IndexOf('[');
            string field = bracket < 0 ? part : part.Substring(0, bracket);
            if (field.Length > 0) current = OnlineManga.Field(current, field);
            if (current == null) return null;
            if (bracket < 0) continue;
            var array = current as object[];
            if (array == null || !part.EndsWith("]", StringComparison.Ordinal)) return null;
            string selector = part.Substring(bracket + 1, part.Length - bracket - 2);
            int equals = selector.IndexOf('=');
            int position;
            if (equals > 0)
                current = array.FirstOrDefault(row => Equals(OnlineManga.Field(row, selector.Substring(0, equals)),
                    selector.Substring(equals + 1)));
            else
                current = int.TryParse(selector, out position) && position >= 0 && position < array.Length ?
                    array[position] : null;
        }
        return current;
    }

    sealed class Record
    {
        readonly Stage stage;
        readonly object row, root;
        public readonly Uri Address;

        public Record(Stage stage, Uri address, object row, object root)
        {
            this.stage = stage;
            Address = address;
            this.row = row;
            this.root = root;
        }

        public string Get(string name)
        {
            object mapping;
            if (!stage.fields.TryGetValue(name, out mapping)) return "";
            var options = mapping as object[] ?? new[] { mapping };
            foreach (string option in options)
            {
                bool missing = false;
                string value = Slot.Replace(option, match =>
                {
                    string path = match.Groups[1].Value;
                    var html = row as Match;
                    object raw = html != null ? WebUtility.HtmlDecode(html.Groups[path].Value) :
                        path == "value" ? row :
                        path.StartsWith("root.", StringComparison.Ordinal) ? SelectPath(root, path.Substring(5)) :
                        SelectPath(row, path);
                    string part = raw == null ? "" : Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture);
                    if (string.IsNullOrWhiteSpace(part)) missing = true;
                    return part;
                });
                if (!missing && !string.IsNullOrWhiteSpace(value)) return value.Trim();
            }
            return "";
        }
    }
}

