using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using Microsoft.Win32;

sealed class OnlineMangaWindow
{
    readonly Window window;
    readonly string folder;
    readonly ReaderState state;
    readonly TextBox address, query;
    readonly ComboBox sources;
    readonly ListBox entries;
    readonly TextBlock status;
    readonly Button open, browser, retryPages;
    OnlineManga.Catalog catalog;
    OnlineManga.Entry[] searchResults = new OnlineManga.Entry[0];
    OnlineManga.Entry[] chapterResults = new OnlineManga.Entry[0];
    OnlineRule[] rules;
    OnlineManga.Entry manga;
    bool oldestFirst;
    string searchStatus;
    CancellationTokenSource pending;
    CancellationTokenSource preparing;
    OnlineManga.Entry preparedChapter;
    OnlineManga.Page[] preparedPages;
    bool preparationFailed;
    bool closed;

    OnlineMangaWindow(Window owner, ReaderState state)
    {
        this.state = state;
        folder = state.Folder;
        var assembly = Assembly.GetExecutingAssembly();
        using (var file = new StreamReader(assembly.GetManifestResourceStream("OnlineMangaWindow.xaml")))
            window = (Window)XamlReader.Parse(file.ReadToEnd()
                .Replace("__KOMASNAP_ASSEMBLY__", assembly.GetName().Name));
        window.Owner = owner;
        window.Resources[typeof(Button)] = owner.FindResource(typeof(Button));
        address = Find<TextBox>("CatalogAddress");
        query = Find<TextBox>("Query");
        sources = Find<ComboBox>("Sources");
        entries = Find<ListBox>("Entries");
        status = Find<TextBlock>("Status");
        open = Find<Button>("OpenSelection");
        browser = Find<Button>("OpenInBrowser");
        retryPages = Find<Button>("RetryPages");
        rules = OnlineRule.ReadSaved(folder);
        UseCatalog(OnlineManga.LoadCatalog(folder));
        status.Text = "输入漫画名称搜索。";
        var trim = Find<CheckBox>("TrimMargins");
        trim.IsChecked = state.OnlineTrim;
        trim.Click += (s, e) =>
        {
            state.OnlineTrim = trim.IsChecked == true;
            state.SaveSettings();
        };

        Find<Button>("SearchButton").Click += (s, e) => Search();
        Find<Button>("Back").Click += (s, e) => ShowResults();
        Find<Button>("ChapterOrder").Click += (s, e) =>
        {
            oldestFirst = !oldestFirst;
            ShowChapters(entries.SelectedItem as OnlineManga.Entry);
        };
        Find<Button>("ImportRules").Click += (s, e) => ImportRules();
        Find<Button>("ImportCatalog").Click += (s, e) => Import(address.Text.Trim());
        Find<Button>("ResetCatalog").Click += (s, e) => Run("正在恢复默认索引…", token =>
        {
            SetCatalog(OnlineManga.ResetCatalog(folder), "已恢复默认索引。");
            return Task.FromResult(true);
        });
        query.KeyDown += (s, e) => { if (e.Key == Key.Enter) { e.Handled = true; Search(); } };
        address.KeyDown += (s, e) => { if (e.Key == Key.Enter) { e.Handled = true; Import(address.Text.Trim()); } };
        entries.KeyDown += (s, e) => { if (e.Key == Key.Enter) { e.Handled = true; OpenSelection(); } };
        entries.MouseDoubleClick += (s, e) =>
        {
            if (e.OriginalSource is DependencyObject &&
                ItemsControl.ContainerFromElement(entries, (DependencyObject)e.OriginalSource) is ListBoxItem)
                OpenSelection();
        };
        entries.SelectionChanged += (s, e) => PrepareSelection();
        open.Click += (s, e) => OpenSelection();
        browser.Click += (s, e) => OpenInBrowser(entries.SelectedItem as OnlineManga.Entry);
        retryPages.Click += (s, e) => PrepareSelection(true);
        window.Loaded += (s, e) => query.Focus();
        window.Closed += (s, e) =>
        {
            closed = true;
            if (pending != null) pending.Cancel();
            CancelPreparation();
        };
    }

    public static OnlineBook Show(Window owner, ReaderState state)
    {
        var dialog = new OnlineMangaWindow(owner, state);
        dialog.window.ShowDialog();
        return dialog.Book;
    }

    OnlineBook Book { get; set; }

    T Find<T>(string name) where T : class { return (T)window.FindName(name); }

    void UseCatalog(OnlineManga.Catalog value)
    {
        catalog = value;
        address.Text = value.Url.AbsoluteUri;
        sources.Items.Clear();
        sources.Items.Add("全部来源");
        foreach (var source in OnlineManga.SearchSources(value).Where(source => source.Rule == null ||
            !rules.Any(rule => string.Equals(rule.id, source.Rule.id, StringComparison.OrdinalIgnoreCase)))
            .Concat(rules.Select(rule => rule.AsSource())))
            sources.Items.Add(source);
        sources.SelectedIndex = 0;
        Find<TextBlock>("CatalogSummary").Text = value.CatalogCount + " 个索引，" + rules.Length + " 个 JSON 图源。索引来源 " +
            string.Join("、", value.Sources.Select(source => source.Name)) +
            (value.UnsupportedCount == 0 ? "。" : "。最近导入的索引中另有 " + value.UnsupportedCount +
                (value.IsExtensionCatalog ? " 条扩展暂未适配。" : " 个来源暂未适配。"));
    }

    void ImportRules()
    {
        if (pending != null) return;
        var picker = new OpenFileDialog { Title = "导入 KomaSnap 漫画源规则", Filter = "JSON 规则文件|*.json" };
        if (picker.ShowDialog(window) != true) return;
        Run("正在校验图源规则…", token =>
        {
            rules = OnlineRule.Import(folder, picker.FileName);
            UseCatalog(catalog);
            searchStatus = "图源规则已保存。可在来源下拉框选择刚导入的图源。";
            ShowResults();
            return Task.FromResult(true);
        });
    }

    void Import(string url)
    {
        Run("正在读取漫画源索引…", async token =>
        {
            var updated = await OnlineManga.FetchCatalog(url, token);
            token.ThrowIfCancellationRequested();
            var saved = OnlineManga.SaveCatalog(folder, updated);
            string message = (updated.MigratedFromYuzono ? "Yūzōnō 已迁移至 Keiyoushi。" : "") +
                "匹配到 " + updated.Sources.Length + " 个可用来源。";
            SetCatalog(saved, message);
        });
    }

    void SetCatalog(OnlineManga.Catalog value, string message)
    {
        UseCatalog(value);
        searchResults = new OnlineManga.Entry[0];
        searchStatus = message;
        ShowResults();
    }

    void Search()
    {
        string text = query.Text.Trim();
        if (text.Length == 0) { status.Text = "请输入漫画名称。"; query.Focus(); return; }
        var selected = sources.SelectedItem as OnlineManga.Source;
        var active = selected == null ? sources.Items.OfType<OnlineManga.Source>().ToArray() : new[] { selected };
        Run("正在搜索…", async token =>
        {
            var hits = await Task.WhenAll(active.Select(async source =>
            {
                try { return Tuple.Create(await OnlineManga.Search(source, text, token), (string)null); }
                catch (Exception error)
                {
                    if (!IsExpected(error)) throw;
                    var fallback = OnlineManga.BrowserSearch(source, text);
                    return Tuple.Create(fallback == null ? new OnlineManga.Entry[0] : new[] { fallback },
                        source.Name + " " + Reason(error));
                }
            }));
            token.ThrowIfCancellationRequested();
            searchResults = hits.SelectMany(hit => hit.Item1).ToArray();
            var errors = hits.Where(hit => hit.Item2 != null).Select(hit => hit.Item2).ToArray();
            int matches = hits.Where(hit => hit.Item2 == null).Sum(hit => hit.Item1.Length);
            searchStatus = (matches > 0 ? "找到 " + matches + " 条结果。" :
                searchResults.Length > 0 ? "可打开来源网页继续搜索。" :
                errors.Length == active.Length ? "搜索暂不可用。" :
                "没有搜到结果，可试试英文、日文书名或更短的关键词。")
                + (errors.Length == 0 ? "" : "\n" + string.Join("　", errors));
            ShowResults();
            if (searchResults.Length != 0)
            {
                entries.SelectedIndex = 0;
                entries.Focus();
            }
        });
    }

    void ShowResults()
    {
        CancelPreparation();
        manga = null;
        Find<DockPanel>("SearchBar").Visibility = Visibility.Visible;
        Find<DockPanel>("ChapterBar").Visibility = Visibility.Collapsed;
        entries.ItemTemplate = (DataTemplate)window.Resources["MangaResult"];
        entries.ItemsSource = searchResults;
        status.Text = searchStatus;
        UpdateControls();
    }

    void OpenSelection()
    {
        var selection = entries.SelectedItem as OnlineManga.Entry;
        if (selection == null || pending != null) return;
        bool hasImageRule = manga != null && selection.Source.Rule != null;
        if (hasImageRule && (preparedChapter != selection || preparedPages == null))
        {
            if (preparationFailed) PrepareSelection(true);
            return;
        }
        if (!hasImageRule && selection.BrowserOnly)
        {
            status.Text = "这条结果可使用「浏览器打开」。";
            return;
        }
        if (manga == null)
        {
            Run("正在读取章节…", async token =>
            {
                var chapters = await OnlineManga.Chapters(selection, token);
                token.ThrowIfCancellationRequested();
                if (chapters.Length == 0)
                {
                    status.Text = "来源未返回可读取的章节。可以用浏览器打开所选漫画。";
                    return;
                }
                var rule = selection.Source.Rule;
                if (string.IsNullOrWhiteSpace(selection.Latest) && (rule == null || !rule.IsStandard || rule.chapters.reverse))
                    selection.UpdateDetails(null, null, rule != null && rule.IsStandard ? chapters.Last().Title : chapters[0].Title);
                manga = selection;
                chapterResults = chapters;
                if (rule != null && rule.IsStandard) oldestFirst = false;
                Find<TextBlock>("SeriesTitle").Text = manga.Title;
                Find<DockPanel>("SearchBar").Visibility = Visibility.Collapsed;
                Find<DockPanel>("ChapterBar").Visibility = Visibility.Visible;
                status.Text = "共 " + chapters.Length + " 话。" +
                    (rule == null && chapters.All(item => item.BrowserOnly) ? "该来源可在浏览器阅读。" : "选择一话开始阅读。");
                ShowChapters();
            });
        }
        else
        {
            var pages = hasImageRule ? preparedPages : null;
            Run("正在加载章节首页…", async token =>
            {
                var book = await OnlineBook.OpenChapter(manga, selection, token, state.OnlineTrim, pages);
                token.ThrowIfCancellationRequested();
                Book = book;
                window.DialogResult = true;
            });
        }
    }

    void ShowChapters(OnlineManga.Entry selection = null)
    {
        bool standard = manga.Source.Rule != null && manga.Source.Rule.IsStandard;
        Find<Button>("ChapterOrder").Content = standard ? (oldestFirst ? "反向排序" : "按规则排序") :
            (oldestFirst ? "最早在前" : "最新在前");
        entries.ItemTemplate = (DataTemplate)window.Resources["ChapterResult"];
        entries.ItemsSource = oldestFirst ? chapterResults.Reverse().ToArray() : chapterResults;
        if (selection != null) entries.SelectedItem = selection;
        if (entries.SelectedIndex < 0 && entries.Items.Count > 0) entries.SelectedIndex = 0;
        if (entries.SelectedItem != null) entries.ScrollIntoView(entries.SelectedItem);
        PrepareSelection();
    }

    void CancelPreparation()
    {
        if (preparing != null)
        {
            var operation = preparing;
            preparing = null;
            operation.Cancel();
        }
        preparedChapter = null;
        preparedPages = null;
        preparationFailed = false;
    }

    async void PrepareSelection(bool retry = false)
    {
        var selected = entries.SelectedItem as OnlineManga.Entry;
        if (closed || (!retry && selected != null && preparedChapter == selected))
        {
            if (!closed) UpdateControls();
            return;
        }
        CancelPreparation();
        if (manga == null || selected == null || selected.Source.Rule == null)
        {
            UpdateControls();
            return;
        }

        preparedChapter = selected;
        using (var operation = new CancellationTokenSource())
        {
            preparing = operation;
            status.Text = "正在解析这一话的图片地址…";
            UpdateControls();
            try
            {
                var pages = await OnlineManga.PageImages(selected, operation.Token);
                operation.Token.ThrowIfCancellationRequested();
                if (closed || preparing != operation) return;
                if (pages.Length == 0)
                {
                    preparationFailed = true;
                    status.Text = "图片规则没有匹配到地址。可重试解析或手动打开浏览器。";
                }
                else
                {
                    preparedPages = pages;
                    status.Text = "已解析 " + pages.Length + " 页。可以开始阅读。";
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                if (!IsExpected(error)) throw;
                if (!closed && preparing == operation)
                {
                    preparationFailed = true;
                    status.Text = "解析章节失败。" + Reason(error);
                }
            }
            finally
            {
                if (preparing == operation)
                {
                    preparing = null;
                    if (!closed) UpdateControls();
                }
            }
        }
    }

    void OpenInBrowser(OnlineManga.Entry selection)
    {
        if (selection == null) return;
        try { Process.Start(new ProcessStartInfo(selection.Url.AbsoluteUri) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { status.Text = "无法打开默认浏览器。"; }
    }

    async void Run(string message, Func<CancellationToken, Task> action)
    {
        if (pending != null) return;
        using (var operation = new CancellationTokenSource())
        {
            pending = operation;
            status.Text = message;
            UpdateControls();
            try { await action(operation.Token); }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                if (!IsExpected(error)) throw;
                if (!closed) status.Text = Reason(error);
            }
            finally
            {
                pending = null;
                if (!closed) UpdateControls();
            }
        }
    }

    void UpdateControls()
    {
        bool idle = pending == null;
        foreach (string name in new[] { "SearchBar", "Back", "ImportCatalog", "ResetCatalog", "ImportRules", "ChapterOrder", "TrimMargins" })
            Find<FrameworkElement>(name).IsEnabled = idle;
        address.IsEnabled = entries.IsEnabled = idle;
        var selection = entries.SelectedItem as OnlineManga.Entry;
        bool browserOnly = selection != null && selection.BrowserOnly;
        bool hasImageRule = manga != null && selection != null && selection.Source.Rule != null;
        bool ready = hasImageRule && selection == preparedChapter && preparedPages != null;
        browser.IsEnabled = idle && selection != null;
        open.IsEnabled = idle && selection != null && (hasImageRule ? ready : !browserOnly);
        retryPages.Visibility = hasImageRule && preparationFailed ? Visibility.Visible : Visibility.Collapsed;
        retryPages.IsEnabled = idle && hasImageRule && preparationFailed;
        open.Content = manga == null ? "查看章节" : "开始阅读";
        open.ToolTip = hasImageRule && !ready ? "解析到图片地址后可开始阅读" :
            browserOnly && !ready ? "这条结果可手动使用「浏览器打开」" : null;
    }

    static bool IsExpected(Exception error)
    {
        return ReaderState.IsStorageError(error) || error is HttpRequestException ||
            error is ArgumentException || error is InvalidOperationException || error is NotSupportedException ||
            error is System.Text.RegularExpressions.RegexMatchTimeoutException;
    }

    static string Reason(Exception error)
    {
        if (error is HttpRequestException) return "无法连接，请检查网络后重试。";
        if (error is NotSupportedException) return "Windows 无法解码这张漫画图片，可以试试用浏览器打开。";
        if (error is ArgumentException) return "来源返回的数据格式不受支持。";
        return error.Message;
    }
}

