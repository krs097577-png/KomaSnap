using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml;
using System.Xml.Linq;

sealed class ArchiveBook : ReaderBook
{
    static readonly Size TextPageSize = new Size(760, 1080);
    static readonly Brush Paper = Brushes.FloralWhite;
    static readonly Regex Spaces = new Regex(@"[\t\r\n ]+", RegexOptions.Compiled);
    static readonly Regex Entities = new Regex(@"&[A-Za-z][A-Za-z0-9]+;", RegexOptions.Compiled);
    static readonly HashSet<string> Blocks = new HashSet<string>
    {
        "address", "article", "blockquote", "div", "dl", "dt", "dd", "figure", "figcaption",
        "footer", "header", "h1", "h2", "h3", "h4", "h5", "h6", "li", "main", "p", "pre", "section", "tr"
    };

    sealed class ParagraphData
    {
        public readonly string Text;
        public readonly bool Heading;
        public ParagraphData(string text, bool heading) { Text = text; Heading = heading; }
    }

    sealed class Part
    {
        public string ImagePath;
        public Size Size;
        public List<ParagraphData> Text;
    }

    sealed class Page
    {
        public string ImagePath;
        public Size Size;
        public DocumentPaginator Paginator;
        public int PageNumber;
    }

    readonly Page[] pages;
    static readonly Lazy<Task<Dispatcher>> TextWorker = new Lazy<Task<Dispatcher>>(StartTextWorker);

    ArchiveBook(string path, Page[] pages, bool? rightToLeft)
        : base(path, pages.Select(page => page.Size).ToArray(), rightToLeft)
    {
        this.pages = pages;
    }

    public static async Task<ArchiveBook> LoadAsync(string path, bool epub, CancellationToken token,
        Action<int, int> onPagination = null)
    {
        var content = await Task.Run(() =>
        {
            using (var archive = OpenArchive(path))
            {
                bool? direction = null;
                var parts = epub ? ReadEpub(archive, out direction, token) : ReadComic(archive, token);
                if (epub && direction == null && parts.Any(part => part.Text != null)) direction = false;
                return Tuple.Create(parts, direction);
            }
        }, token);

        var pages = new List<Page>();
        int textParts = content.Item1.Count(part => part.Text != null);
        int formatted = 0;
        foreach (Part part in content.Item1)
        {
            token.ThrowIfCancellationRequested();
            if (part.ImagePath != null)
                pages.Add(new Page { ImagePath = part.ImagePath, Size = part.Size });
            else
            {
                var worker = await TextWorker.Value;
                if (formatted == 0 && onPagination != null) onPagination(0, textParts);
                var chapter = await worker.InvokeAsync(() => Paginate(part.Text, token), DispatcherPriority.Background, token).Task;
                pages.AddRange(chapter);
                formatted++;
                if (onPagination != null) onPagination(formatted, textParts);
            }
        }
        if (pages.Count == 0) throw new InvalidDataException(epub ? "这本 EPUB 没有可读取的文字或图片页面。" : "压缩包中没有支持的图片。");
        return new ArchiveBook(path, pages.ToArray(), content.Item2);
    }

    static ZipArchive OpenArchive(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        try { return new ZipArchive(stream, ZipArchiveMode.Read); }
        catch { stream.Dispose(); throw; }
    }

    static List<Part> ReadComic(ZipArchive archive, CancellationToken token)
    {
        var parts = new List<Part>();
        var order = Comparer<string>.Create(StrCmpLogicalW);
        var images = archive.Entries.Where(entry => IsImage(entry.Name) &&
            !entry.FullName.Split('/', '\\').Any(segment => segment.StartsWith(".", StringComparison.Ordinal) ||
            segment.Equals("__MACOSX", StringComparison.OrdinalIgnoreCase)));
        foreach (var entry in images.OrderBy(image => image.FullName, order).ThenBy(image => image.FullName, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            parts.Add(ReadImage(entry));
        }
        return parts;
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern int StrCmpLogicalW(string left, string right);

    static bool IsImage(string path)
    {
        switch (System.IO.Path.GetExtension(path).ToLowerInvariant())
        {
            case ".jpeg": case ".jpg": case ".png": case ".gif": case ".bmp": case ".tif": case ".tiff": return true;
            default: return false;
        }
    }

    static Part ReadImage(ZipArchiveEntry entry)
    {
        using (var bytes = ReadBytes(entry, 64 * 1024 * 1024))
        {
            var frame = BitmapDecoder.Create(bytes, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0 || (long)frame.PixelWidth * frame.PixelHeight > 120000000)
                throw new InvalidDataException("图片尺寸超出可读取范围：" + entry.FullName);
            return new Part { ImagePath = entry.FullName, Size = new Size(frame.PixelWidth, frame.PixelHeight) };
        }
    }

    static MemoryStream ReadBytes(ZipArchiveEntry entry, int limit)
    {
        if (entry.Length > limit) throw new InvalidDataException("压缩包中的单个文件过大：" + entry.FullName);
        var bytes = new MemoryStream((int)entry.Length);
        try
        {
            using (var stream = entry.Open())
            {
                var buffer = new byte[65536];
                int count;
                while ((count = stream.Read(buffer, 0, buffer.Length)) != 0)
                {
                    if (bytes.Length + count > limit) throw new InvalidDataException("压缩包中的单个文件过大：" + entry.FullName);
                    bytes.Write(buffer, 0, count);
                }
            }
            bytes.Position = 0;
            return bytes;
        }
        catch { bytes.Dispose(); throw; }
    }

    static XDocument ReadXml(ZipArchiveEntry entry, bool html = false)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = 12 * 1024 * 1024 };
        using (var bytes = ReadBytes(entry, 8 * 1024 * 1024))
        {
            try
            {
                using (var parser = XmlReader.Create(bytes, settings))
                    return XDocument.Load(parser, LoadOptions.PreserveWhitespace);
            }
            catch (XmlException)
            {
                if (!html) throw;
                bytes.Position = 0;
                using (var reader = new StreamReader(bytes, Encoding.UTF8, true))
                {
                    string normalized = Entities.Replace(reader.ReadToEnd(), match =>
                    {
                        string value = WebUtility.HtmlDecode(match.Value);
                        return value != match.Value ? string.Concat(value.Select(c => "&#" + (int)c + ";")) : match.Value;
                    });
                    using (var parser = XmlReader.Create(new StringReader(normalized), settings))
                        return XDocument.Load(parser, LoadOptions.PreserveWhitespace);
                }
            }
        }
    }

    static ZipArchiveEntry RequireEntry(ZipArchive archive, string path)
    {
        var entry = path == null ? null : archive.GetEntry(path);
        if (entry == null) throw new InvalidDataException("书中缺少文件：" + (path ?? "无法识别的路径"));
        return entry;
    }

    static string Resolve(string basePath, string reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        string path = Uri.UnescapeDataString(reference.Split('#', '?')[0]).Replace('\\', '/');
        if (path.Length == 0 || path.StartsWith("/", StringComparison.Ordinal) || path.IndexOf(':') >= 0) return null;
        int slash = basePath.LastIndexOf('/');
        var segments = (slash < 0 ? "" : basePath.Substring(0, slash + 1)) + path;
        var resolved = new List<string>();
        foreach (string segment in segments.Split('/'))
        {
            if (segment == "." || segment.Length == 0) continue;
            if (segment == "..")
            {
                if (resolved.Count == 0) return null;
                resolved.RemoveAt(resolved.Count - 1);
            }
            else resolved.Add(segment);
        }
        return string.Join("/", resolved);
    }

    static string Attr(XElement node, string name)
    {
        return (string)node.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == name);
    }

    static List<Part> ReadEpub(ZipArchive archive, out bool? rightToLeft, CancellationToken token)
    {
        var encryption = archive.GetEntry("META-INF/encryption.xml");
        if (encryption != null && ReadXml(encryption).Descendants().Any(node =>
            node.Name.LocalName == "EncryptionMethod" &&
            Attr(node, "Algorithm") != "http://www.idpf.org/2008/embedding" &&
            Attr(node, "Algorithm") != "http://ns.adobe.com/pdf/enc#RC"))
            throw new InvalidDataException("此 EPUB 含有加密内容，请使用可直接阅读的 EPUB 文件。");

        var container = ReadXml(RequireEntry(archive, "META-INF/container.xml"));
        var root = container.Descendants().FirstOrDefault(node => node.Name.LocalName == "rootfile");
        if (root == null) throw new InvalidDataException("找不到 EPUB 书籍清单。");
        string packagePath = Resolve("", Attr(root, "full-path"));
        var package = ReadXml(RequireEntry(archive, packagePath));
        var manifestNode = package.Root.Elements().FirstOrDefault(node => node.Name.LocalName == "manifest");
        var spine = package.Root.Elements().FirstOrDefault(node => node.Name.LocalName == "spine");
        if (manifestNode == null || spine == null) throw new InvalidDataException("EPUB 缺少文件清单或阅读顺序。");
        string direction = Attr(spine, "page-progression-direction");
        rightToLeft = direction == "rtl" ? (bool?)true : direction == "ltr" ? (bool?)false : null;
        var manifest = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var item in manifestNode.Elements().Where(node => node.Name.LocalName == "item"))
        {
            string id = Attr(item, "id");
            if (!string.IsNullOrEmpty(id)) manifest[id] = item;
        }
        var parts = new List<Part>();
        var imageInfo = new Dictionary<string, Part>(StringComparer.Ordinal);
        foreach (var reference in spine.Elements().Where(node => node.Name.LocalName == "itemref"))
        {
            token.ThrowIfCancellationRequested();
            if (Attr(reference, "linear") == "no") continue;
            XElement item;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string id = Attr(reference, "idref");
            while (id != null && seen.Add(id) && manifest.TryGetValue(id, out item))
            {
                string path = Resolve(packagePath, Attr(item, "href"));
                if (path == null) break;
                if (IsImage(path))
                {
                    parts.Add(GetImage(archive, imageInfo, path));
                    break;
                }
                string media = Attr(item, "media-type");
                if (media == "application/xhtml+xml" || media == "text/html" || media == "image/svg+xml" ||
                    path.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                {
                    var chapter = ReadXml(RequireEntry(archive, path), true);
                    var body = chapter.Descendants().FirstOrDefault(node => node.Name.LocalName == "body") ?? chapter.Root;
                    var reader = new Chapter(parts, archive, imageInfo, path);
                    reader.Visit(body, false, token, 0);
                    reader.FlushText();
                    break;
                }
                id = Attr(item, "fallback");
            }
        }
        return parts;
    }

    static Part GetImage(ZipArchive archive, Dictionary<string, Part> images, string path)
    {
        Part part;
        if (!images.TryGetValue(path, out part))
        {
            part = ReadImage(RequireEntry(archive, path));
            images.Add(path, part);
        }
        return part;
    }

    sealed class Chapter
    {
        readonly List<Part> parts;
        readonly ZipArchive archive;
        readonly Dictionary<string, Part> images;
        readonly string path;
        readonly StringBuilder line = new StringBuilder();
        List<ParagraphData> text = new List<ParagraphData>();

        public Chapter(List<Part> parts, ZipArchive archive, Dictionary<string, Part> images, string path)
        {
            this.parts = parts;
            this.archive = archive;
            this.images = images;
            this.path = path;
        }

        public void Visit(XNode node, bool heading, CancellationToken token, int depth)
        {
            token.ThrowIfCancellationRequested();
            if (depth > 128) throw new InvalidDataException("EPUB 的章节嵌套过深。");
            var value = node as XText;
            if (value != null) { line.Append(value.Value); return; }
            var element = node as XElement;
            if (element == null) return;
            string name = element.Name.LocalName.ToLowerInvariant();
            if (name == "head" || name == "script" || name == "style" || name == "nav" || name == "iframe") return;
            if (name == "br" || name == "hr") { FlushLine(heading); return; }
            if (name == "img" || name == "image" || name == "object")
            {
                string image = Resolve(path, Attr(element, "src") ?? Attr(element, "href") ?? Attr(element, "data"));
                if (image != null && IsImage(image) && archive.GetEntry(image) != null)
                {
                    FlushLine(heading);
                    FlushText();
                    parts.Add(GetImage(archive, images, image));
                    return;
                }
                string alternative = Attr(element, "alt");
                if (!string.IsNullOrWhiteSpace(alternative)) line.Append(alternative);
            }
            bool block = Blocks.Contains(name);
            if (block) FlushLine(heading);
            bool isHeading = heading || (name.Length == 2 && name[0] == 'h' && name[1] >= '1' && name[1] <= '6');
            if (name == "li") line.Append("• ");
            foreach (var child in element.Nodes()) Visit(child, isHeading, token, depth + 1);
            if (block) FlushLine(isHeading);
        }

        void FlushLine(bool heading)
        {
            string paragraph = Spaces.Replace(line.ToString(), " ").Trim();
            line.Clear();
            if (paragraph.Length > 0) text.Add(new ParagraphData(paragraph, heading));
        }

        public void FlushText()
        {
            FlushLine(false);
            if (text.Count == 0) return;
            parts.Add(new Part { Text = text });
            text = new List<ParagraphData>();
        }
    }

    static Task<Dispatcher> StartTextWorker()
    {
        // 排版对象和页面渲染必须使用同一个 STA 线程。
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            ready.SetResult(Dispatcher.CurrentDispatcher);
            Dispatcher.Run();
        });
        thread.Name = "KomaSnap EPUB 排版";
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task;
    }

    static Page[] Paginate(List<ParagraphData> text, CancellationToken token)
    {
        var doc = new FlowDocument
        {
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"),
            FontSize = 23, LineHeight = 38, PagePadding = new Thickness(58, 48, 58, 48),
            ColumnWidth = TextPageSize.Width, Foreground = Brushes.Black, TextAlignment = TextAlignment.Left
        };
        foreach (var item in text)
        {
            token.ThrowIfCancellationRequested();
            var paragraph = new Paragraph(new Run(item.Text)) { Margin = new Thickness(0, 0, 0, 14) };
            if (item.Heading)
            {
                paragraph.FontSize = 34;
                paragraph.LineHeight = 49;
                paragraph.FontWeight = FontWeights.SemiBold;
                paragraph.KeepWithNext = true;
            }
            doc.Blocks.Add(paragraph);
        }
        var paginator = (DynamicDocumentPaginator)((IDocumentPaginatorSource)doc).DocumentPaginator;
        paginator.IsBackgroundPaginationEnabled = false;
        paginator.PageSize = TextPageSize;
        paginator.ComputePageCount();
        token.ThrowIfCancellationRequested();
        var pages = new Page[paginator.PageCount];
        for (int page = 0; page < pages.Length; page++)
            pages[page] = new Page { Size = TextPageSize, Paginator = paginator, PageNumber = page };
        return pages;
    }

    protected override async Task<BitmapSource> RenderPageAsync(int index, int width, CancellationToken token)
    {
        Page page = pages[index];
        if (page.ImagePath != null)
            return await Task.Run<BitmapSource>(() =>
            {
                token.ThrowIfCancellationRequested();
                using (var archive = OpenArchive(Path))
                using (var stream = ReadBytes(RequireEntry(archive, page.ImagePath), 64 * 1024 * 1024))
                {
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.DecodePixelWidth = Math.Min(width, (int)page.Size.Width);
                    image.StreamSource = stream;
                    image.EndInit();
                    image.Freeze();
                    return image;
                }
            }, token);

        var worker = await TextWorker.Value;
        return await worker.InvokeAsync(() => RenderText(page, width, token), DispatcherPriority.Background, token).Task;
    }

    static BitmapSource RenderText(Page page, int width, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        DocumentPage source = page.Paginator.GetPage(page.PageNumber);
        int height = (int)Math.Ceiling(width * page.Size.Height / page.Size.Width);
        var drawing = new DrawingVisual();
        var target = new Rect(0, 0, width, height);
        using (DrawingContext context = drawing.RenderOpen())
        {
            context.DrawRectangle(Paper, null, target);
            var brush = new VisualBrush(source.Visual)
            {
                ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(page.Size), Stretch = Stretch.Fill
            };
            context.DrawRectangle(brush, null, target);
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        bitmap.Freeze();
        return bitmap;
    }
}

