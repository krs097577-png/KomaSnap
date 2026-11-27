using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using Fizzler.Systems.HtmlAgilityPack;
using HtmlAgilityPack;

static class OnlineHtml
{
    internal const string ImageAttributes = "data-src,data-original,src";
    static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(750);
    static readonly Regex EscapedUrl = new Regex(@"\\(?:u[0-9a-fA-F]{4}|x[0-9a-fA-F]{2}|/|\\)",
        RegexOptions.CultureInvariant, Timeout);

    internal static void RegisterEmbeddedLibraries()
    {
        AppDomain.CurrentDomain.AssemblyResolve += LoadEmbeddedLibrary;
    }

    static Assembly LoadEmbeddedLibrary(object sender, ResolveEventArgs args)
    {
        string name = new AssemblyName(args.Name).Name;
        if (name != "HtmlAgilityPack" && name != "Fizzler" && name != "Fizzler.Systems.HtmlAgilityPack")
            return null;
        var loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(item => item.GetName().Name == name);
        if (loaded != null) return loaded;
        using (var library = Assembly.GetExecutingAssembly().GetManifestResourceStream("OnlineParser." + name))
        using (var bytes = new MemoryStream())
        {
            if (library == null) return null;
            library.CopyTo(bytes);
            return Assembly.Load(bytes.ToArray());
        }
    }

    static HtmlNode Document(string text)
    {
        var document = new HtmlDocument();
        document.OptionMaxNestedChildNodes = 512;
        try { document.LoadHtml(text); }
        catch (Exception error)
        {
            if (!(error is ArgumentException || error is InvalidOperationException)) throw;
            throw new FormatException("无法解析图源返回的 HTML。", error);
        }
        return document.DocumentNode;
    }

    internal static Regex MakePattern(string pattern, string label)
    {
        if (string.IsNullOrWhiteSpace(pattern)) throw new FormatException(label + "规则需要正则表达式。");
        try { return new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.Singleline, Timeout); }
        catch (ArgumentException error) { throw new FormatException(label + "正则无效。", error); }
    }

    static void ValidateCss(string selector)
    {
        if (string.IsNullOrWhiteSpace(selector)) return;
        try { Document("<div></div>").QuerySelectorAll(selector).Take(1).ToArray(); }
        catch (Exception error)
        {
            if (!(error is ArgumentException || error is InvalidOperationException ||
                error is FormatException || error is NotSupportedException)) throw;
            throw new FormatException("CSS 选择器无效或不受支持。" + selector, error);
        }
    }

    internal static void ValidateField(string selector, bool regexList)
    {
        var field = new FieldRule(selector, null);
        if (field.Group != null && !regexList) throw new FormatException("group: 提取规则需要 regex: 列表。");
        ValidateCss(field.Css);
    }

    internal sealed class ListRule
    {
        readonly string selector;
        readonly Regex pattern;
        internal bool IsRegex { get { return pattern != null; } }

        internal ListRule(string rule)
        {
            rule = rule.Trim();
            if (rule.StartsWith("regex:", StringComparison.OrdinalIgnoreCase))
                pattern = MakePattern(rule.Substring(6), "列表");
            else
            {
                selector = rule;
                ValidateCss(selector);
            }
        }

        internal IEnumerable<Record> Read(string text)
        {
            if (pattern != null)
                return pattern.Matches(text).Cast<Match>().Select(match => new Record(match));
            return Document(text).QuerySelectorAll(selector).OrderBy(node => node.StreamPosition)
                .Select(node => new Record(node));
        }
    }

    internal sealed class Record
    {
        HtmlNode node;
        readonly Match match;
        internal Record(HtmlNode node) { this.node = node; }
        internal Record(Match match) { this.match = match; }

        internal string Read(string expression, string fallbackAttributes)
        {
            if (string.IsNullOrWhiteSpace(expression)) return "";
            var field = new FieldRule(expression, fallbackAttributes);
            if (field.Group != null)
                return match == null ? "" : WebUtility.HtmlDecode(match.Groups[field.Group].Value).Trim();
            if (node == null) node = Document(match.Value);
            return field.Read(field.Css == null ? node : node.QuerySelector(field.Css), fallbackAttributes != null);
        }
    }

    sealed class FieldRule
    {
        public readonly string Css, Group, Attributes;

        public FieldRule(string expression, string fallbackAttributes)
        {
            string field = expression.Trim();
            if (field.StartsWith("group:", StringComparison.OrdinalIgnoreCase))
            {
                Group = field.Substring(6).Trim();
                if (Group.Length == 0) throw new FormatException("group: 后需要捕获组编号或名称。");
                return;
            }
            if (field.Equals("text", StringComparison.OrdinalIgnoreCase)) return;
            if (field.StartsWith("attr:", StringComparison.OrdinalIgnoreCase))
            {
                Attributes = field.Substring(5).Trim();
                if (Attributes.Length == 0) throw new FormatException("attr: 后需要属性名。");
                return;
            }
            int split = field.LastIndexOf("::", StringComparison.Ordinal);
            if (split < 0)
            {
                Css = field;
                Attributes = fallbackAttributes;
                return;
            }
            Css = field.Substring(0, split).Trim();
            if (Css.Length == 0) Css = null;
            string value = field.Substring(split + 2).Trim();
            if (value.StartsWith("attr:", StringComparison.OrdinalIgnoreCase)) value = value.Substring(5).Trim();
            if (value.Length == 0) throw new FormatException("选择器的 :: 后需要 text 或属性名。");
            if (!value.Equals("text", StringComparison.OrdinalIgnoreCase)) Attributes = value;
        }

        public string Read(HtmlNode node, bool url)
        {
            if (node == null) return "";
            if (Attributes == null) return Clean(node.InnerText);
            foreach (string attribute in Attributes.Split(','))
            {
                var value = Clean(node.GetAttributeValue(attribute.Trim(), ""));
                if (value.Length != 0 && (!url || IsUrlValue(value))) return value;
            }
            return "";
        }
    }

    internal sealed class Image
    {
        public readonly string Url;
        public readonly int Width, Height;
        public Image(string url, int width, int height) { Url = url; Width = width; Height = height; }
    }

    internal static IEnumerable<Image> ReadImages(string html, string selector)
    {
        var field = new FieldRule(selector, ImageAttributes);
        if (field.Group != null) throw new FormatException("CSS 图片选择器需要元素和属性。");
        var root = Document(html);
        var nodes = field.Css == null ? root.QuerySelectorAll("img") : root.QuerySelectorAll(field.Css);
        foreach (var node in nodes.OrderBy(item => item.StreamPosition))
        {
            string url = field.Read(node, true);
            if (!IsUrlValue(url)) continue;
            int width, height;
            int.TryParse(node.GetAttributeValue("width", ""), out width);
            int.TryParse(node.GetAttributeValue("height", ""), out height);
            yield return new Image(url, width, height);
        }
    }

    static string Clean(string value) { return WebUtility.HtmlDecode(value).Trim(); }

    static bool IsUrlValue(string value)
    {
        return value.Length > 0 && value[0] != '#' &&
            !value.StartsWith("data:", StringComparison.OrdinalIgnoreCase) &&
            !value.StartsWith("blob:", StringComparison.OrdinalIgnoreCase) &&
            !value.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase);
    }

    internal static string DecodeLiteralUrl(string value)
    {
        value = Clean(value);
        if (!IsUrlValue(value)) return "";
        return EscapedUrl.Replace(value, match =>
        {
            if (match.Length == 2) return match.Value.Substring(1);
            int number = int.Parse(match.Value.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return ((char)number).ToString();
        });
    }
}

