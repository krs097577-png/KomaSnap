using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;

// 主窗口中的在线工作区：阅读器和本地书架继续使用既有实现。
sealed class OnlineWorkspace
{
    readonly Window owner;
    readonly ReaderState state;
    readonly Action<IEnumerable<string>> import;
    readonly Action<ReaderBook> read;
    readonly DockPanel root = new DockPanel { Margin = new Thickness(28, 20, 28, 12) };
    readonly StackPanel toolbar = new StackPanel();
    readonly TextBlock status = new TextBlock { Margin = new Thickness(0, 8, 0, 5), TextWrapping = TextWrapping.Wrap };
    readonly ScrollViewer scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    readonly StackPanel content = new StackPanel();
    readonly List<Job> jobs = new List<Job>();
    readonly SemaphoreSlim jobSlots = new SemaphoreSlim(2);
    List<Dictionary<string, object>> sources = new List<Dictionary<string, object>>();
    List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();
    Dictionary<string, object> manga;
    TextBox searchBox;
    CheckBox allLanguages;
    string section = "";
    int navigation;
    bool ready;
    bool checking;
    public UIElement View { get { return root; } }
    sealed class Job { public string Title, Path, Error; public int Chapter, Current, Total; public bool Running, Done; }
    static Dictionary<string, object> O(object x) { return SuwayomiService.Obj(x); }
    static object[] A(object x) { return SuwayomiService.Arr(x); }
    static string S(Dictionary<string, object> x, string k) { return SuwayomiService.S(x, k); }
    static string J(string x) { return SuwayomiService.J(x); }
    public OnlineWorkspace(Window owner, ReaderState state, Action<IEnumerable<string>> import, Action<ReaderBook> read)
    {
        this.owner = owner; this.state = state; this.import = import; this.read = read;
        LoadJobs();
        status.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        DockPanel.SetDock(toolbar, Dock.Top); root.Children.Add(toolbar);
        DockPanel.SetDock(status, Dock.Bottom); root.Children.Add(status);
        scroll.Content = content; root.Children.Add(scroll);
        var monitor = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        monitor.Tick += async (s, e) => {
            if (!ready || checking || SuwayomiService.IsRunning) return;
            checking = true; status.Text = "连接中断，正在重新连接…";
            try { await SuwayomiService.Start(state.Folder); status.Text = "连接已恢复。"; }
            catch (Exception error) { status.Text = "连接暂不可用，将自动重试：" + error.Message; }
            finally { checking = false; }
        };
        owner.Closed += (s, e) => monitor.Stop(); monitor.Start();
    }
    public async void Navigate(string page)
    {
        section = page; int id = ++navigation; toolbar.Children.Clear(); content.Children.Clear();
        try {
            if (page != "downloads") {
                status.Text = "正在连接漫画服务…"; await SuwayomiService.Start(state.Folder); ready = true;
                if (id != navigation) return;
            }
            if (page == "search") await SearchPage();
            else if (page == "sources") await SourcesPage();
            else if (page == "settings") await SettingsPage();
            else if (page == "downloads") DownloadsPage();
        } catch (Exception e) { if (id == navigation) status.Text = "暂时无法连接：" + e.Message + "。再次打开此页面可重试。"; }
    }
    TextBlock Text(string text, int size, bool bold = false)
    {
        var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8), FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary"); return t;
    }
    Button Btn(string label, Func<Task> click)
    {
        var button = new Button { Content = label, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(13, 7, 13, 7) };
        button.Click += async (s, e) => { button.IsEnabled = false; try { await click(); } catch (Exception ex) { status.Text = "操作未完成：" + ex.Message; } finally { button.IsEnabled = true; } };
        return button;
    }
    async Task RefreshSources()
    {
        var d = await SuwayomiService.Query("{ sources { nodes { id name lang } } }");
        sources = A(O(d["sources"])["nodes"]).Select(O).Where(x => S(x, "lang") != "localsourcelang").ToList();
    }
    async Task SearchPage()
    {
        toolbar.Children.Add(Text("发现漫画", 25, true));
        var line = new WrapPanel(); toolbar.Children.Add(line);
        searchBox = new TextBox { Width = 310, Height = 35, Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 0, 8, 8), ToolTip = "输入漫画名称" };
        line.Children.Add(searchBox); line.Children.Add(Btn("搜索已安装漫画源", Search));
        allLanguages = new CheckBox { Content = "包含其他语言", Margin = new Thickness(0, 0, 12, 8), VerticalAlignment = VerticalAlignment.Center }; line.Children.Add(allLanguages);
        line.Children.Add(Btn("我的在线书架", OnlineShelf));
        searchBox.KeyDown += async (s, e) => { if (e.Key == Key.Enter) { e.Handled = true; try { await Search(); } catch (Exception error) { status.Text = error.Message; } } };
        await RefreshSources();
        status.Text = sources.Count == 0 ? "还没有安装漫画源。请打开“漫画源”选择信任的来源。" : "已连接 " + sources.Count + " 个漫画源。输入名称即可同时搜索。";
        content.Children.Add(Text("搜索漫画，或打开你的在线书架。", 15));
    }
    async Task Search()
    {
        string query = searchBox.Text.Trim(); if (query.Length == 0) throw new IOException("请输入漫画名称。");
        await RefreshSources(); if (sources.Count == 0) throw new IOException("请先在“漫画源”中安装来源。");
        var targets = allLanguages.IsChecked == true ? sources : sources.Where(x => S(x, "lang").StartsWith("zh", StringComparison.OrdinalIgnoreCase) || S(x, "lang") == "all").ToList();
        if (targets.Count == 0) throw new IOException("没有中文漫画源，可勾选“包含其他语言”。");
        int id = navigation; content.Children.Clear(); status.Text = "正在搜索 " + targets.Count + " 个来源…";
        var gate = new SemaphoreSlim(4); int failures = 0;
        var calls = targets.Select(async source => {
            await gate.WaitAsync();
            try {
                var d = await SuwayomiService.Query("mutation { fetchSourceManga(input:{source:" + J(S(source, "id")) + ",type:SEARCH,page:1,query:" + J(query) + "}) { mangas { id title thumbnailUrl } } }");
                var found = A(O(d["fetchSourceManga"])["mangas"]).Select(O).ToArray();
                foreach (var item in found) item["displaySource"] = S(source, "name") + " · " + S(source, "lang");
                return found;
            } catch { Interlocked.Increment(ref failures); return new Dictionary<string, object>[0]; }
            finally { gate.Release(); }
        }).ToArray();
        var all = await Task.WhenAll(calls); if (id != navigation) return;
        results = all.SelectMany(x => x).ToList(); ShowCards(results);
        status.Text = "找到 " + results.Count + " 本漫画" + (failures > 0 ? "，" + failures + " 个来源暂不可用。" : "。");
    }
    void ShowCards(IEnumerable<Dictionary<string, object>> rows)
    {
        content.Children.Clear(); var cards = new WrapPanel(); content.Children.Add(cards);
        foreach (var item in rows) {
            var image = new Image { Height = 180, Width = 128, Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Center };
            var block = new StackPanel { Width = 150, Margin = new Thickness(0, 0, 0, 3) };
            var frame = new Border { Width = 128, Height = 180, CornerRadius = new CornerRadius(7), ClipToBounds = true, Child = image };
            frame.SetResourceReference(Border.BackgroundProperty, "Elevated"); block.Children.Add(frame);
            var title = Text(S(item, "title"), 13, true); title.MaxHeight = 42; title.TextTrimming = TextTrimming.CharacterEllipsis; title.Margin = new Thickness(4, 7, 4, 2); block.Children.Add(title);
            var source = Text(S(item, "displaySource"), 11); source.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary"); source.Margin = new Thickness(4, 0, 4, 3); block.Children.Add(source);
            var button = new Button { Content = block, Margin = new Thickness(0, 0, 14, 14), Padding = new Thickness(6), VerticalContentAlignment = VerticalAlignment.Top };
            button.Click += async (s, e) => { try { await Detail(item); } catch (Exception error) { status.Text = "无法打开：" + error.Message; } };
            cards.Children.Add(button); LoadCover(image, S(item, "thumbnailUrl"));
        }
        if (!cards.Children.Cast<UIElement>().Any()) content.Children.Add(Text("没有找到结果。", 15));
    }
    async void LoadCover(Image image, string url)
    {
        if (string.IsNullOrEmpty(url)) return;
        try { var bytes = await SuwayomiService.Thumbnail(url); var bitmap = await Task.Run(() => {
            using (var stream = new MemoryStream(bytes)) { var b = new BitmapImage(); b.BeginInit(); b.CacheOption = BitmapCacheOption.OnLoad; b.DecodePixelWidth = 180; b.StreamSource = stream; b.EndInit(); b.Freeze(); return b; }
        }); image.Source = bitmap; } catch { }
    }
    async Task OnlineShelf()
    {
        var d = await SuwayomiService.Query("{ mangas(condition:{inLibrary:true}) { nodes { id title thumbnailUrl } } }");
        var rows = A(O(d["mangas"])["nodes"]).Select(O).ToArray();
        foreach (var x in rows) x["displaySource"] = "在线书架";
        ShowCards(rows); status.Text = "在线书架 · " + rows.Length + " 本";
    }
    async Task Detail(Dictionary<string, object> item)
    {
        int id = navigation; status.Text = "正在加载漫画详情…";
        var d = await SuwayomiService.Query("mutation { fetchMangaAndChapters(input:{id:" + S(item, "id") + ",fetchManga:true,fetchChapters:true}) { manga { id title description thumbnailUrl inLibrary } chapters { id name } } }");
        if (id != navigation) return; var payload = O(d["fetchMangaAndChapters"]); manga = O(payload["manga"]);
        var chapters = A(payload["chapters"]).Select(O).ToArray(); content.Children.Clear();
        var back = Btn("← 返回结果", () => { ShowCards(results); status.Text = "搜索结果"; return Task.FromResult(0); }); content.Children.Add(back);
        var heading = new DockPanel { Margin = new Thickness(0, 8, 0, 18) };
        var cover = new Image { Width = 150, Height = 210, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 20, 0), VerticalAlignment = VerticalAlignment.Top };
        DockPanel.SetDock(cover, Dock.Left); heading.Children.Add(cover); LoadCover(cover, S(manga, "thumbnailUrl"));
        var info = new StackPanel(); info.Children.Add(Text(S(manga, "title"), 23, true));
        if (!string.IsNullOrEmpty(S(item, "displaySource"))) info.Children.Add(Text("来源：" + S(item, "displaySource"), 12));
        var desc = Text(S(manga, "description"), 13); desc.MaxHeight = 135; desc.TextTrimming = TextTrimming.CharacterEllipsis; info.Children.Add(desc);
        var actions = new WrapPanel(); info.Children.Add(actions);
        if (chapters.Length > 0) actions.Children.Add(Btn("开始阅读", async () => await ReadChapter(chapters[chapters.Length - 1])));
        actions.Children.Add(Btn("寻找其他来源", async () => { searchBox.Text = S(manga, "title"); await Search(); }));
        actions.Children.Add(Btn("加入在线书架", async () => { await SuwayomiService.Query("mutation { updateManga(input:{id:" + S(manga, "id") + ",patch:{inLibrary:true}}) { manga { id inLibrary } } }"); status.Text = "已加入在线书架。"; }));
        heading.Children.Add(info); content.Children.Add(heading); content.Children.Add(Text("章节 · " + chapters.Length, 18, true));
        var selected = new HashSet<int>(); var selectors = new List<CheckBox>();
        var selectionBar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        var selectedCount = Text("已选 0 章", 13); selectedCount.VerticalAlignment = VerticalAlignment.Center;
        var downloadSelected = Btn("下载已选章节", () => {
            var chosen = chapters.Where(x => selected.Contains(int.Parse(S(x, "id")))).ToArray();
            if (chosen.Length == 0) throw new IOException("请先勾选需要下载的章节。");
            if (MessageBox.Show(owner, "即将下载选中的 " + chosen.Length + " 章。请确认您有权下载并保存这些章节。", "批量下载确认", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                EnqueueChapters(S(manga, "title"), chosen);
            return Task.FromResult(0);
        });
        selectionBar.Children.Add(Btn("全选", () => { foreach (var box in selectors) box.IsChecked = true; return Task.FromResult(0); }));
        selectionBar.Children.Add(Btn("清空选择", () => { foreach (var box in selectors) box.IsChecked = false; return Task.FromResult(0); }));
        selectionBar.Children.Add(downloadSelected); selectionBar.Children.Add(selectedCount); content.Children.Add(selectionBar);
        foreach (var chapter in chapters) {
            var line = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            int chapterId = int.Parse(S(chapter, "id"));
            var check = new CheckBox { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 12, 0), ToolTip = "选择 " + S(chapter, "name") };
            check.Checked += (s, e) => { selected.Add(chapterId); selectedCount.Text = "已选 " + selected.Count + " 章"; };
            check.Unchecked += (s, e) => { selected.Remove(chapterId); selectedCount.Text = "已选 " + selected.Count + " 章"; };
            selectors.Add(check); DockPanel.SetDock(check, Dock.Left); line.Children.Add(check);
            var download = Btn("下载", () => QueueDownload(chapter)); DockPanel.SetDock(download, Dock.Right); line.Children.Add(download);
            var open = Btn(S(chapter, "name"), async () => await ReadChapter(chapter)); open.HorizontalContentAlignment = HorizontalAlignment.Left; line.Children.Add(open); content.Children.Add(line);
        }
        status.Text = chapters.Length == 0 ? "该来源暂未提供章节。" : "选择章节开始阅读，或下载有权保存的内容。";
    }
    static async Task<string[]> Pages(int chapter)
    {
        var d = await SuwayomiService.Query("mutation { fetchChapterPages(input:{chapterId:" + chapter + "}) { pages } }");
        return A(O(d["fetchChapterPages"])["pages"]).Select(Convert.ToString).ToArray();
    }
    async Task ReadChapter(Dictionary<string, object> chapter)
    {
        status.Text = "正在打开章节…"; int id = int.Parse(S(chapter, "id"));
        var book = await SuwayomiBook.Open(id, S(manga, "title") + " · " + S(chapter, "name"), await Pages(id)); read(book);
    }
    Task QueueDownload(Dictionary<string, object> chapter)
    {
        if (MessageBox.Show(owner, "请确认您有权下载并保存此章节。", "下载确认", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return Task.FromResult(0);
        EnqueueChapters(S(manga, "title"), new[] { chapter }); return Task.FromResult(0);
    }
    internal void EnqueueChapters(string mangaTitle, IEnumerable<Dictionary<string, object>> chapters)
    {
        string folder = Path.Combine(state.Folder, "Downloads"); Directory.CreateDirectory(folder);
        int skipped = 0; var added = new List<Job>();
        var known = new HashSet<string>(jobs.Select(x => x.Path), StringComparer.OrdinalIgnoreCase);
        foreach (var chapter in chapters) {
            int id = int.Parse(S(chapter, "id")); string title = mangaTitle + " - " + S(chapter, "name");
            string safe = string.Concat(title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)); if (safe.Length > 100) safe = safe.Substring(0, 100);
            string path = Path.Combine(folder, safe + "-" + id + ".cbz");
            if (File.Exists(path) || !known.Add(path)) { skipped++; continue; }
            var job = new Job { Chapter = id, Title = title, Path = path }; jobs.Add(job); added.Add(job);
        }
        SaveJobs(); foreach (var job in added) RunJobCore(job, false);
        status.Text = "已添加 " + added.Count + " 章下载任务" + (skipped > 0 ? "，跳过 " + skipped + " 章已下载或已在队列中的章节。" : "。") + "可在“下载管理”查看进度。";
    }
    string JobFile { get { return Path.Combine(state.Folder, "online-downloads.xml"); } }
    void LoadJobs()
    {
        try {
            if (!File.Exists(JobFile)) return;
            string folder = Path.GetFullPath(Path.Combine(state.Folder, "Downloads")) + Path.DirectorySeparatorChar;
            foreach (var node in XElement.Load(JobFile).Elements("job")) {
                int chapter, current, total; string path = (string)node.Attribute("path");
                if (!int.TryParse((string)node.Attribute("chapter"), out chapter) || chapter < 1 || string.IsNullOrEmpty(path)) continue;
                path = Path.GetFullPath(path); if (!path.StartsWith(folder, StringComparison.OrdinalIgnoreCase) || !path.EndsWith(".cbz", StringComparison.OrdinalIgnoreCase)) continue;
                int.TryParse((string)node.Attribute("current"), out current); int.TryParse((string)node.Attribute("total"), out total);
                bool done = (string)node.Attribute("done") == "true" && File.Exists(path);
                jobs.Add(new Job { Chapter = chapter, Title = (string)node.Attribute("title") ?? "章节", Path = path, Current = Math.Max(0, current), Total = Math.Max(0, total), Done = done, Error = done ? null : "任务中断，请重试。" });
            }
        } catch { }
    }
    void SaveJobs()
    {
        try {
            Directory.CreateDirectory(state.Folder);
            var xml = new XElement("downloads", jobs.Select(x => new XElement("job", new XAttribute("chapter", x.Chapter), new XAttribute("title", x.Title ?? ""), new XAttribute("path", x.Path), new XAttribute("current", x.Current), new XAttribute("total", x.Total), new XAttribute("done", x.Done ? "true" : "false"))));
            string temp = JobFile + ".tmp"; xml.Save(temp);
            if (File.Exists(JobFile)) File.Replace(temp, JobFile, null); else File.Move(temp, JobFile);
        } catch { }
    }
    void RunJob(Job job)
    {
        RunJobCore(job, true);
    }
    async void RunJobCore(Job job, bool saveBefore)
    {
        if (job.Running || job.Done) return; job.Running = true; job.Error = null; if (saveBefore) SaveJobs();
        try {
            await jobSlots.WaitAsync();
            try {
                var pages = await Pages(job.Chapter); job.Total = pages.Length;
                await Task.Run(async () => await SuwayomiWindow.SaveCbz(job.Path, pages, (i, n) => {
                    job.Current = i; job.Total = n; owner.Dispatcher.BeginInvoke(new Action(() => { if (section == "downloads") DownloadsPage(); }));
                }));
                job.Done = true; import(new[] { job.Path });
            } finally { jobSlots.Release(); }
        } catch (Exception e) { job.Error = e.Message; }
        finally { job.Running = false; SaveJobs(); if (section == "downloads") DownloadsPage(); }
    }
    void DownloadsPage()
    {
        toolbar.Children.Clear(); toolbar.Children.Add(Text("下载管理", 25, true)); content.Children.Clear();
        if (jobs.Count == 0) content.Children.Add(Text("还没有下载任务。可在漫画详情中下载章节。", 15));
        foreach (var job in jobs.ToArray()) {
            var box = new StackPanel { Margin = new Thickness(0, 0, 0, 14) }; box.Children.Add(Text(job.Title, 15, true));
            string stateText = job.Done ? "已完成并加入本地书架" : job.Error != null ? "失败：" + job.Error : job.Total > 0 ? "正在下载 " + job.Current + " / " + job.Total : "等待中…";
            box.Children.Add(Text(stateText, 12));
            if (job.Total > 0) box.Children.Add(new ProgressBar { Minimum = 0, Maximum = job.Total, Value = job.Current, Height = 6, Margin = new Thickness(0, 0, 0, 5) });
            if (job.Error != null && !job.Running) box.Children.Add(Btn("重试", () => { RunJob(job); DownloadsPage(); return Task.FromResult(0); }));
            content.Children.Add(box);
        }
        status.Text = "中断或失败的任务可以重试。";
    }
    async Task EnsureDefaultStore()
    {
        string marker = Path.Combine(state.Folder, "online-store-initialized");
        if (File.Exists(marker)) return;
        var stores = await SuwayomiService.Query("{ extensionStores { nodes { indexUrl } } }");
        if (A(O(stores["extensionStores"])["nodes"]).Length == 0) {
            status.Text = "正在准备漫画源列表…";
            await SuwayomiService.Query("mutation { addExtensionStore(input:{indexUrl:\"https://github.com/keiyoushi/extensions/raw/repo/index.pb\"}) { extensionStore { name } } }");
            await SuwayomiService.Query("mutation { fetchExtensions(input:{}) { extensions { pkgName } } }");
        }
        File.WriteAllText(marker, "1");
    }
    async Task SourcesPage()
    {
        toolbar.Children.Add(Text("漫画源", 25, true));
        var bar = new WrapPanel(); toolbar.Children.Add(bar);
        var input = new TextBox { Width = 220, Height = 34, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(7, 4, 7, 4), ToolTip = "搜索漫画源名称" };
        var chinese = new CheckBox { Content = "中文优先", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 8) };
        var installed = new CheckBox { Content = "只看已安装", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 8) };
        bar.Children.Add(input); bar.Children.Add(chinese); bar.Children.Add(installed);
        await EnsureDefaultStore();
        var data = await SuwayomiService.Query("{ extensions { nodes { name pkgName lang versionName isInstalled hasUpdate storeIndexUrl } } }");
        var extensions = A(O(data["extensions"])["nodes"]).Select(O).ToArray();
        Action show = () => {
            content.Children.Clear();
            var matches = extensions.Where(x => (S(x, "name") + " " + S(x, "lang")).IndexOf(input.Text.Trim(), StringComparison.OrdinalIgnoreCase) >= 0)
                .Where(x => installed.IsChecked != true || S(x, "isInstalled") == "True")
                .Where(x => chinese.IsChecked != true || S(x, "lang").StartsWith("zh", StringComparison.OrdinalIgnoreCase) || S(x, "lang") == "all")
                .OrderByDescending(x => S(x, "isInstalled") == "True").ThenBy(x => S(x, "name")).ToArray();
            content.Children.Add(Text("已安装的来源排在前面 · 共 " + matches.Length + " 项" + (matches.Length > 150 ? "（显示前 150 项，请使用搜索缩小范围）" : ""), 13));
            foreach (var ext in matches.Take(150)) {
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
                bool present = S(ext, "isInstalled") == "True"; bool update = S(ext, "hasUpdate") == "True";
                var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right }; DockPanel.SetDock(buttons, Dock.Right); row.Children.Add(buttons);
                if (!present || update) buttons.Children.Add(Btn(present ? "更新" : "安装", async () => { await InstallExtension(ext, present); Navigate("sources"); }));
                if (present) {
                    buttons.Children.Add(Btn("检测", async () => await CheckSource(ext)));
                    buttons.Children.Add(Btn("卸载", async () => {
                        if (MessageBox.Show(owner, "确定卸载“" + S(ext, "name") + "”？", "卸载漫画源", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                        await SuwayomiService.Query("mutation { updateExtension(input:{id:" + J(S(ext, "pkgName")) + ",patch:{uninstall:true}}) { extension { isInstalled } } }"); Navigate("sources");
                    }));
                }
                var label = new StackPanel(); label.Children.Add(Text(S(ext, "name"), 15, true));
                label.Children.Add(Text(S(ext, "lang") + " · " + S(ext, "versionName") + " · " + (present ? (update ? "已安装，有更新" : "已安装") : "未安装"), 12)); row.Children.Add(label); content.Children.Add(row);
            }
        };
        input.TextChanged += (s, e) => show(); chinese.Click += (s, e) => show(); installed.Click += (s, e) => show(); show();
        status.Text = "安装扩展前请确认来源可信；可用性会受网站和网络影响。";
    }
    async Task InstallExtension(Dictionary<string, object> ext, bool update)
    {
        string package = S(ext, "pkgName"), store = S(ext, "storeIndexUrl");
        if (string.IsNullOrWhiteSpace(package) || !store.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) throw new IOException("来源信息不完整，无法安装。");
        if (MessageBox.Show(owner, "第三方扩展会以当前用户权限运行，可能访问网络和本地数据。\n\n名称：" + S(ext, "name") + "\n版本：" + S(ext, "versionName") + "\n包名：" + package + "\n仓库：" + store + "\n\n仅检查来源元数据，不能证明扩展安全。确认继续？", "确认第三方扩展", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await SuwayomiService.Query("mutation { updateExtension(input:{id:" + J(package) + ",patch:{" + (update ? "update" : "install") + ":true}}) { extension { isInstalled } } }");
        await RefreshSources(); status.Text = "漫画源操作完成。";
    }
    async Task CheckSource(Dictionary<string, object> ext)
    {
        status.Text = "正在检测 " + S(ext, "name") + "…";
        var d = await SuwayomiService.Query("{ sources { nodes { id name lang extension { pkgName } } } }");
        var source = A(O(d["sources"])["nodes"]).Select(O).FirstOrDefault(x => x.ContainsKey("extension") && x["extension"] != null && S(O(x["extension"]), "pkgName") == S(ext, "pkgName"));
        if (source == null) throw new IOException("该扩展未提供可用来源。");
        await SuwayomiService.Query("mutation { fetchSourceManga(input:{source:" + J(S(source, "id")) + ",type:POPULAR,page:1}) { mangas { id } } }");
        status.Text = S(ext, "name") + "：连接正常。";
    }
    async Task SettingsPage()
    {
        toolbar.Children.Add(Text("设置", 25, true)); content.Children.Add(Text("漫画源仓库", 17, true));
        content.Children.Add(Text("仓库用于查找可安装的第三方漫画源。安装扩展时仍需逐项确认。", 13));
        var line = new WrapPanel(); var input = new TextBox { Text = "https://github.com/keiyoushi/extensions/raw/repo/index.pb", Width = 440, Height = 34, Padding = new Thickness(7, 4, 7, 4), Margin = new Thickness(0, 0, 8, 8) };
        line.Children.Add(input); line.Children.Add(Btn("添加仓库", async () => {
            Uri uri; if (!Uri.TryCreate(input.Text.Trim(), UriKind.Absolute, out uri) || uri.Scheme != "https") throw new IOException("请输入 HTTPS 仓库地址。");
            await SuwayomiService.Query("mutation { addExtensionStore(input:{indexUrl:" + J(uri.AbsoluteUri) + "}) { extensionStore { name } } }"); Navigate("settings");
        }));
        line.Children.Add(Btn("刷新列表", async () => { status.Text = "正在刷新…"; await SuwayomiService.Query("mutation { fetchExtensions(input:{}) { extensions { pkgName } } }"); status.Text = "漫画源列表已刷新。"; }));
        content.Children.Add(line);
        var data = await SuwayomiService.Query("{ extensionStores { nodes { name indexUrl } } }");
        foreach (var store in A(O(data["extensionStores"])["nodes"]).Select(O)) {
            var row = new DockPanel(); var remove = Btn("删除", async () => {
                if (MessageBox.Show(owner, "删除仓库“" + S(store, "name") + "”？", "确认", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                await SuwayomiService.Query("mutation { removeExtensionStore(input:{indexUrl:" + J(S(store, "indexUrl")) + "}) { extensionStore { name } } }"); Navigate("settings");
            }); DockPanel.SetDock(remove, Dock.Right); row.Children.Add(remove); row.Children.Add(Text(S(store, "name") + "\n" + S(store, "indexUrl"), 13)); content.Children.Add(row);
        }
        content.Children.Add(Text("日志位置：" + Path.Combine(state.Folder, "Suwayomi", "logs"), 12)); status.Text = ready ? "漫画服务已连接。" : "漫画服务未连接。";
    }
}

