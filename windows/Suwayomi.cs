using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

// 服务生命周期和 GraphQL 均限制在本机，第三方扩展由用户逐个确认安装。
static class SuwayomiService
{
    static Process process;
    static HttpClient http;
    static string baseUrl;
    static string dataFolder;
    static readonly SemaphoreSlim startup = new SemaphoreSlim(1, 1);
    static readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AttachConsole(uint id);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool FreeConsole();
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GenerateConsoleCtrlEvent(uint signal, uint group);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
    public static string BaseUrl { get { return baseUrl; } }
    public static bool IsRunning { get { try { return process != null && !process.HasExited; } catch { return false; } } }
    static string Quote(string value) { return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""; }
    public static async Task Start(string folder)
    {
        dataFolder = folder;
        await startup.WaitAsync();
        try { await StartCore(folder); } finally { startup.Release(); }
    }
    static async Task StartCore(string folder)
    {
        if (process != null && !process.HasExited && http != null) return;
        if (process != null) { process.Dispose(); process = null; }
        if (http != null) { http.Dispose(); http = null; }
        string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "suwayomi");
        string java = Path.Combine(root, "jre", "bin", "java.exe");
        string jar = Path.Combine(root, "bin", "Suwayomi-Server.jar");
        if (!File.Exists(java) || !File.Exists(jar)) throw new IOException("未找到随程序提供的 Suwayomi 服务组件。请保持 suwayomi 文件夹与程序位于同一目录。");
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        baseUrl = "http://127.0.0.1:" + port;
        string data = Path.Combine(folder, "Suwayomi"); Directory.CreateDirectory(data);
        string args = "-Dsuwayomi.tachidesk.config.server.rootDir=" + Quote(data) +
            " -Dsuwayomi.tachidesk.config.server.ip=127.0.0.1 -Dsuwayomi.tachidesk.config.server.port=" + port +
            " -Dsuwayomi.tachidesk.config.server.kcefEnabled=false -Dsuwayomi.tachidesk.config.server.webUIEnabled=false -Dsuwayomi.tachidesk.config.server.systemTrayEnabled=false" +
            " -Dsuwayomi.tachidesk.config.server.initialOpenInBrowserEnabled=false -jar " + Quote(jar);
        process = Process.Start(new ProcessStartInfo(java, args) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root });
        http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(60) };
        for (int i = 0; i < 90; i++)
        {
            if (process.HasExited) throw new IOException("Suwayomi 服务启动失败，请检查数据目录下的日志。\n" + data);
            try { await QueryRaw("{ __typename }"); return; } catch { }
            await Task.Delay(1000);
        }
        Stop(); throw new IOException("Suwayomi 服务启动超时。");
    }
    public static void Stop()
    {
        if (process != null) {
            try {
                if (!process.HasExited && AttachConsole((uint)process.Id)) {
                    SetConsoleCtrlHandler(IntPtr.Zero, true);
                    try { GenerateConsoleCtrlEvent(0, 0); process.WaitForExit(7000); }
                    finally { FreeConsole(); SetConsoleCtrlHandler(IntPtr.Zero, false); }
                }
                if (!process.HasExited) process.Kill();
            } catch { }
            process.Dispose(); process = null;
        }
        if (http != null) { http.Dispose(); http = null; }
    }
    public static async Task<Dictionary<string, object>> Query(string query)
    {
        if (process == null || process.HasExited) {
            if (string.IsNullOrEmpty(dataFolder)) throw new IOException("漫画服务尚未启动。");
            await Start(dataFolder);
        }
        return await QueryRaw(query);
    }
    static async Task<Dictionary<string, object>> QueryRaw(string query)
    {
        var body = json.Serialize(new Dictionary<string, object> { { "query", query } });
        using (var response = await http.PostAsync(baseUrl + "/api/graphql", new StringContent(body, Encoding.UTF8, "application/json")))
        {
            response.EnsureSuccessStatusCode();
            string text = await response.Content.ReadAsStringAsync();
            var doc = await Task.Run(() => (Dictionary<string, object>)new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 }.DeserializeObject(text));
            if (doc.ContainsKey("errors")) {
                var errors = Arr(doc["errors"]); string message = errors.Length > 0 && errors[0] is Dictionary<string, object> ? S(Obj(errors[0]), "message") : "未知错误";
                throw new IOException(FriendlyError(message));
            }
            return (Dictionary<string, object>)doc["data"];
        }
    }
    internal static string FriendlyError(string message)
    {
        message = message ?? "未知错误";
        if (message.IndexOf("Cloudflare bypass currently disabled", StringComparison.OrdinalIgnoreCase) >= 0)
            return "此漫画源触发了网站的访问验证，暂时无法获取图片。请尝试同一本漫画的其他来源，或切换网络后重试。详细信息保存在后台日志中。\n原始错误：Cloudflare bypass currently disabled";
        int newline = message.IndexOfAny(new[] { '\r', '\n' }); if (newline >= 0) message = message.Substring(0, newline);
        if (message.Length > 260) message = message.Substring(0, 260) + "…";
        return "服务返回错误：" + message;
    }
    public static string J(string text) { return json.Serialize(text ?? ""); }
    public static Dictionary<string, object> Obj(object value) { return (Dictionary<string, object>)value; }
    public static object[] Arr(object value) { return value as object[] ?? new object[0]; }
    public static string S(Dictionary<string, object> d, string key) { object v; return d.TryGetValue(key, out v) && v != null ? Convert.ToString(v) : ""; }
    public static async Task<byte[]> Page(string relative, CancellationToken token)
    {
        if (process == null || process.HasExited) await Start(dataFolder);
        if (string.IsNullOrEmpty(relative) || !relative.StartsWith("/api/v1/manga/", StringComparison.Ordinal) || relative.Contains("..")) throw new IOException("无效的页面地址。");
        using (var response = await http.GetAsync(baseUrl + relative, HttpCompletionOption.ResponseHeadersRead, token))
        {
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > 30 * 1024 * 1024) throw new IOException("页面文件过大。");
            return await ReadLimited(response, 30 * 1024 * 1024, token);
        }
    }
    public static async Task<byte[]> Thumbnail(string relative)
    {
        if (process == null || process.HasExited) await Start(dataFolder);
        if (!relative.StartsWith("/api/v1/manga/", StringComparison.Ordinal) || relative.Contains("..")) throw new IOException("无效的封面地址。");
        using (var response = await http.GetAsync(baseUrl + relative, HttpCompletionOption.ResponseHeadersRead)) {
            response.EnsureSuccessStatusCode(); return await ReadLimited(response, 10 * 1024 * 1024, CancellationToken.None);
        }
    }
    static async Task<byte[]> ReadLimited(HttpResponseMessage response, int limit, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > limit) throw new IOException("图片文件过大。");
        using (var input = await response.Content.ReadAsStreamAsync()) using (var output = new MemoryStream()) {
            var buffer = new byte[32768]; int n;
            while ((n = await input.ReadAsync(buffer, 0, buffer.Length, token)) != 0) {
                if (output.Length + n > limit) throw new IOException("图片文件过大。");
                output.Write(buffer, 0, n);
            }
            return output.ToArray();
        }
    }
}

sealed class SuwayomiBook : ReaderBook
{
    readonly string[] pages;
    readonly BitmapSource first;
    public readonly string Title;
    SuwayomiBook(int chapter, string title, string[] pages, BitmapSource first)
        : base("suwayomi://chapter/" + chapter, pages.Select(p => new Size(first.PixelWidth, first.PixelHeight)).ToArray())
    { this.pages = pages; this.first = first; Title = title; }
    public static async Task<SuwayomiBook> Open(int chapter, string title, string[] pages)
    {
        if (pages.Length == 0 || pages.Length > 2000) throw new IOException("章节没有页面，或页面过多。");
        var bytes = await SuwayomiService.Page(pages[0], CancellationToken.None);
        var first = await Task.Run(() => Decode(bytes));
        return new SuwayomiBook(chapter, title, pages, first);
    }
    static BitmapSource Decode(byte[] bytes)
    {
        using (var input = new MemoryStream(bytes))
        {
            var frame = BitmapDecoder.Create(input, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0 || (long)frame.PixelWidth * frame.PixelHeight > 64000000) throw new IOException("图片尺寸过大。");
            int width = frame.PixelWidth; input.Position = 0;
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = input; if (width > 3200) bitmap.DecodePixelWidth = 3200; bitmap.EndInit(); bitmap.Freeze();
            return bitmap;
        }
    }
    protected override async Task<BitmapSource> RenderPageAsync(int index, int width, CancellationToken token)
    { if (index == 0) return first; var bytes = await SuwayomiService.Page(pages[index], token); return await Task.Run(() => Decode(bytes), token); }
}

sealed class SuwayomiWindow
{
    readonly Window window;
    readonly ReaderState state;
    readonly Action<IEnumerable<string>> import;
    readonly ComboBox sources = new ComboBox { MinWidth = 210, Margin = new Thickness(4) };
    readonly TextBox search = new TextBox { MinWidth = 170, Margin = new Thickness(4) };
    readonly TextBox repo = new TextBox { Text = "https://github.com/keiyoushi/extensions/raw/repo/index.pb", MinWidth = 350, Margin = new Thickness(4) };
    readonly TextBox filter = new TextBox { MinWidth = 130, Margin = new Thickness(4) };
    readonly ListBox list = new ListBox { Margin = new Thickness(4), DisplayMemberPath = "Label" };
    readonly TextBlock status = new TextBlock { Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap };
    readonly TextBlock details = new TextBlock { Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap, MaxHeight = 110 };
    readonly Image cover = new Image { Width = 80, Height = 110, Margin = new Thickness(8), Stretch = System.Windows.Media.Stretch.Uniform };
    readonly List<Row> results = new List<Row>();
    Row manga; string mode = ""; bool busy;
    SuwayomiBook book;
    sealed class Row { public string Label { get; set; } public Dictionary<string, object> Data; public override string ToString() { return Label; } }
    SuwayomiWindow(Window owner, ReaderState state, Action<IEnumerable<string>> import)
    {
        this.state = state; this.import = import;
        window = new Window { Owner = owner, Title = "扩展漫画源 · KomaSnap", Width = 850, Height = 690, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var root = new DockPanel(); window.Content = root;
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        var bar = new WrapPanel(); top.Children.Add(bar); bar.Children.Add(sources); bar.Children.Add(search);
        Button(bar, "搜索", async () => await Search(false)); Button(bar, "多源搜索", async () => await Search(true));
        var manage = new WrapPanel(); top.Children.Add(manage); manage.Children.Add(repo);
        Button(manage, "添加仓库", AddRepo); Button(manage, "仓库列表", Stores); Button(manage, "刷新扩展", Refresh);
        var extbar = new WrapPanel(); top.Children.Add(extbar); extbar.Children.Add(filter);
        Button(extbar, "扩展列表（中文优先）", Extensions); Button(extbar, "安装／更新", Install); Button(extbar, "卸载／删除", Remove);
        var bottom = new StackPanel(); DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        var detailPanel = new DockPanel(); DockPanel.SetDock(cover, Dock.Left); detailPanel.Children.Add(cover); detailPanel.Children.Add(details); bottom.Children.Add(detailPanel); var actions = new WrapPanel(); bottom.Children.Add(actions);
        Button(actions, "打开选中项", Open); Button(actions, "返回搜索结果", ShowResults); Button(actions, "保存 CBZ 并加入书架", Download);
        bottom.Children.Add(status); root.Children.Add(list); list.MouseDoubleClick += async (s, e) => await Run(Open);
        list.SelectionChanged += async (s, e) => {
            cover.Source = null; var item = list.SelectedItem as Row;
            if (mode != "results" || item == null || string.IsNullOrEmpty(S(item.Data, "thumbnailUrl"))) return;
            try {
                var bytes = await SuwayomiService.Thumbnail(S(item.Data, "thumbnailUrl"));
                if (list.SelectedItem != item) return;
                using (var stream = new MemoryStream(bytes)) { var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.DecodePixelWidth = 160; image.StreamSource = stream; image.EndInit(); image.Freeze(); cover.Source = image; }
            } catch { }
        };
        window.Loaded += async (s, e) => await Run(async () => { status.Text = "正在启动本地服务…"; await SuwayomiService.Start(state.Folder); await Sources(); status.Text = "服务已启动。请添加仓库并手动安装扩展。"; });
    }
    void Button(Panel parent, string text, Func<Task> action)
    {
        var button = new Button { Content = text, Padding = new Thickness(9, 5, 9, 5), Margin = new Thickness(3) };
        button.Click += async (s, e) => await Run(action); parent.Children.Add(button);
    }
    async Task Run(Func<Task> action)
    {
        if (busy) return; busy = true;
        try { await action(); } catch (Exception e) { status.Text = "操作失败：" + e.Message; }
        finally { busy = false; }
    }
    public static SuwayomiBook Show(Window owner, ReaderState state, Action<IEnumerable<string>> import)
    { var dialog = new SuwayomiWindow(owner, state, import); dialog.window.ShowDialog(); return dialog.book; }
    static Dictionary<string, object> O(object x) { return SuwayomiService.Obj(x); }
    static string S(Dictionary<string, object> x, string k) { return SuwayomiService.S(x, k); }
    static string J(string x) { return SuwayomiService.J(x); }
    static object[] A(object x) { return SuwayomiService.Arr(x); }
    void SetRows(IEnumerable<Row> rows, string newMode) { mode = newMode; list.ItemsSource = rows.ToArray(); list.SelectedIndex = 0; }
    async Task Sources()
    {
        var data = await SuwayomiService.Query("{ sources { nodes { id name lang } } }");
        var nodes = A(O(data["sources"])["nodes"]).Select(O).OrderByDescending(x => S(x, "lang").StartsWith("zh")).ThenBy(x => S(x, "name"));
        sources.ItemsSource = nodes.Select(x => new Row { Data = x, Label = S(x, "name") + " [" + S(x, "lang") + "]" }).ToArray();
        sources.DisplayMemberPath = "Label"; sources.SelectedIndex = 0;
    }
    async Task AddRepo()
    {
        Uri u; if (!Uri.TryCreate(repo.Text.Trim(), UriKind.Absolute, out u) || u.Scheme != "https") throw new IOException("仓库地址必须是 HTTPS URL。");
        await SuwayomiService.Query("mutation { addExtensionStore(input:{indexUrl:" + J(u.AbsoluteUri) + "}) { extensionStore { name } } }");
        status.Text = "仓库已添加。请刷新扩展列表。";
    }
    async Task Stores()
    {
        var d = await SuwayomiService.Query("{ extensionStores { nodes { name indexUrl } } }");
        SetRows(A(O(d["extensionStores"])["nodes"]).Select(O).Select(x => new Row { Data = x, Label = S(x, "name") + " — " + S(x, "indexUrl") }), "stores"); status.Text = "选中仓库后可删除。";
    }
    async Task Refresh()
    {
        status.Text = "正在刷新扩展，可能需要一些时间…";
        await SuwayomiService.Query("mutation { fetchExtensions(input:{}) { extensions { pkgName } } }"); await Extensions();
    }
    async Task Extensions()
    {
        var d = await SuwayomiService.Query("{ extensions { nodes { name pkgName lang versionName isInstalled hasUpdate storeIndexUrl } } }");
        var items = A(O(d["extensions"])["nodes"]).Select(O).Where(x => (S(x, "name") + " " + S(x, "lang")).IndexOf(filter.Text.Trim(), StringComparison.OrdinalIgnoreCase) >= 0)
            .OrderByDescending(x => S(x, "lang").StartsWith("zh")).ThenBy(x => S(x, "name"));
        SetRows(items.Select(x => new Row { Data = x, Label = S(x, "name") + " [" + S(x, "lang") + "] " + S(x, "versionName") + (S(x, "isInstalled") == "True" ? " 已安装" : "") + (S(x, "hasUpdate") == "True" ? " 可更新" : "") }), "extensions");
        status.Text = "扩展是第三方代码。选中扩展后安装或更新；可用上方输入框按名称或语言筛选。";
    }
    Row Selected() { var row = list.SelectedItem as Row; if (row == null) throw new IOException("请先选择一项。"); return row; }
    async Task Install()
    {
        if (mode != "extensions") throw new IOException("请先打开扩展列表。"); var x = Selected().Data;
        string package = S(x, "pkgName"), store = S(x, "storeIndexUrl");
        if (string.IsNullOrWhiteSpace(package) || !store.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) throw new IOException("扩展来源不完整，拒绝安装。");
        bool update = S(x, "isInstalled") == "True";
        if (MessageBox.Show(window, "扩展会以当前用户权限运行，可能访问网络及本地数据。请仅安装信任的扩展。\n\n名称：" + S(x, "name") + "\n包名：" + package + "\n版本：" + S(x, "versionName") + "\n仓库：" + store + "\n\n安全检查仅核对来源和元数据，不能证明代码安全。确定继续？", "确认第三方扩展", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await SuwayomiService.Query("mutation { updateExtension(input:{id:" + J(package) + ",patch:{" + (update ? "update" : "install") + ":true}}) { extension { isInstalled } } }");
        await Sources(); await Extensions(); status.Text = "操作完成；请检查扩展状态。";
    }
    async Task Remove()
    {
        var x = Selected().Data;
        if (mode == "stores") { await SuwayomiService.Query("mutation { removeExtensionStore(input:{indexUrl:" + J(S(x, "indexUrl")) + "}) { extensionStore { name } } }"); await Stores(); }
        else if (mode == "extensions") { if (MessageBox.Show(window, "卸载 " + S(x, "name") + "？", "确认", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return; await SuwayomiService.Query("mutation { updateExtension(input:{id:" + J(S(x, "pkgName")) + ",patch:{uninstall:true}}) { extension { isInstalled } } }"); await Sources(); await Extensions(); }
        else throw new IOException("请先打开扩展或仓库列表。");
    }
    async Task Search(bool all)
    {
        var selected = sources.ItemsSource as Row[]; if (selected == null || selected.Length == 0) throw new IOException("没有已安装的漫画源。");
        var targets = all ? selected : new[] { sources.SelectedItem as Row }; results.Clear(); int failures = 0;
        foreach (var source in targets.Where(x => x != null))
        {
            try {
                status.Text = "正在搜索 " + source.Label + "…";
                var d = await SuwayomiService.Query("mutation { fetchSourceManga(input:{source:" + J(S(source.Data, "id")) + ",type:SEARCH,page:1,query:" + J(search.Text.Trim()) + "}) { mangas { id title thumbnailUrl } } }");
                results.AddRange(A(O(d["fetchSourceManga"])["mangas"]).Select(O).Select(x => new Row { Data = x, Label = S(x, "title") + " — " + source.Label }));
            } catch { failures++; }
        }
        await ShowResults(); status.Text = "找到 " + results.Count + " 条结果" + (failures > 0 ? "；" + failures + " 个源搜索失败。" : "。");
    }
    Task ShowResults() { SetRows(results, "results"); details.Text = ""; return Task.FromResult(0); }
    async Task Open()
    {
        var row = Selected();
        if (mode == "results")
        {
            manga = row; var d = await SuwayomiService.Query("mutation { fetchMangaAndChapters(input:{id:" + S(row.Data, "id") + ",fetchManga:true,fetchChapters:true}) { manga { title description } chapters { id name } } }");
            var payload = O(d["fetchMangaAndChapters"]); var m = O(payload["manga"]); details.Text = S(m, "title") + "\n" + S(m, "description");
            var chapters = A(payload["chapters"]).Select(O).Select(x => new Row { Data = x, Label = S(x, "name") }).ToArray();
            SetRows(chapters, "chapters"); status.Text = "共 " + chapters.Length + " 章。选择章节后打开。";
        }
        else if (mode == "chapters")
        {
            int chapter = int.Parse(S(row.Data, "id")); var pages = await Pages(chapter);
            book = await SuwayomiBook.Open(chapter, manga.Label + " · " + row.Label, pages); window.Close();
        }
        else throw new IOException("请选择搜索结果或章节。");
    }
    static async Task<string[]> Pages(int id)
    {
        var d = await SuwayomiService.Query("mutation { fetchChapterPages(input:{chapterId:" + id + "}) { pages } }");
        return A(O(d["fetchChapterPages"])["pages"]).Select(Convert.ToString).ToArray();
    }
    async Task Download()
    {
        if (mode != "chapters") throw new IOException("请先选择章节。"); var row = Selected();
        if (MessageBox.Show(window, "请确认您有权下载并保存该章节。继续将保存 CBZ 并加入书架。", "内容授权确认", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var pages = await Pages(int.Parse(S(row.Data, "id"))); if (pages.Length == 0 || pages.Length > 2000) throw new IOException("章节页数无效。");
        var folder = Path.Combine(state.Folder, "Downloads"); Directory.CreateDirectory(folder);
        string name = string.Concat((manga.Label + " - " + row.Label).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        if (name.Length > 100) name = name.Substring(0, 100);
        string path = Path.Combine(folder, name + "-" + S(row.Data, "id") + ".cbz");
        if (File.Exists(path)) throw new IOException("文件已存在：" + path);
        await SaveCbz(path, pages, (i, count) => status.Text = "正在保存第 " + i + " / " + count + " 页…");
        import(new[] { path }); status.Text = "已保存并加入书架：" + path;
    }
    internal static async Task SaveCbz(string path, string[] pages, Action<int, int> progress)
    {
        if (pages.Length == 0 || pages.Length > 2000) throw new IOException("章节页数无效。");
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            using (var file = new FileStream(temp, FileMode.CreateNew)) using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
                for (int i = 0; i < pages.Length; i++) {
                    progress(i + 1, pages.Length);
                    var bytes = await SuwayomiService.Page(pages[i], CancellationToken.None);
                    string ext = bytes.Length > 3 && bytes[0] == 0x89 && bytes[1] == 0x50 ? ".png" : bytes.Length > 2 && bytes[0] == 0xff && bytes[1] == 0xd8 ? ".jpg" : bytes.Length > 12 && Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF" && Encoding.ASCII.GetString(bytes, 8, 4) == "WEBP" ? ".webp" : null;
                    if (ext == null) throw new IOException("服务返回了不支持的图片格式。");
                    using (var output = archive.CreateEntry((i + 1).ToString("D4") + ext).Open()) await output.WriteAsync(bytes, 0, bytes.Length);
                }
            File.Move(temp, path);
        } finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

