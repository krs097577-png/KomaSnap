using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

static class Verify
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [STAThread]
    static int Main(string[] args)
    {
        var app = new Application();
        bool formatsOnly = args[0] == "formats-only";
        bool shelfOnly = args[0] == "shelf-only";
        bool modesOnly = args[0] == "modes-only";
        var privateState = Path.Combine(args[1], modesOnly ? "verification-modes" : shelfOnly ? "verification-shelf" : "verification-state");
        var reader = new Reader(privateState);
        var window = Field<Window>(reader, "window");
        window.ShowActivated = false;
        var state = Field<ReaderState>(reader, "state");
        state.Dual = true;
        state.RightToLeft = true;
        state.SoloCover = true;
        state.FitWidth = false;
        state.ContinuousFitWidth = true;
        state.Motion = PageMotion.Slide;
        if (!formatsOnly && !shelfOnly && !modesOnly) state.SavePosition(args[0], 0);
        window.Loaded += async (s, e) =>
        {
            try
            {
                await Task.Delay(200);
                if (modesOnly)
                {
                    await VerifySharedRender();
                    string file = Path.Combine(args[1], "import-fixtures", "漫画样本.cbz");
                    await (Task)Call(reader, "OpenAsync", file);
                    await Ready(reader);
                    await VerifySlides(reader, window, state, args[1]);
                    await VerifyScroll(reader, window, state, args[1]);
                    Console.WriteLine("PASS: shared rendering, bounded decoding, horizontal slides, curl, accumulated scrolling and resume.");
                    app.Shutdown(0);
                    return;
                }
                if (shelfOnly)
                {
                    await VerifyShelf(reader, window, state, args[1]);
                    Console.WriteLine("PASS: shelf startup, bulk add, deduplication, covers, continue, reload, removal and reopening.");
                    app.Shutdown(0);
                    return;
                }
                if (formatsOnly)
                {
                    await VerifyImports(reader, window, state, args[1]);
                    Console.WriteLine("PASS: EPUB images and text, CBZ and ZIP ordering, archive errors and resume.");
                    app.Shutdown(0);
                    return;
                }
                Capture(window, Path.Combine(args[1], "reader-empty.png"));
                await (Task)Call(reader, "OpenAsync", args[0]);
                await Ready(reader);
                Check(Field<PdfBook>(reader, "book").Count == 186, "页数");
                Check(Field<StackPanel>(reader, "spread").Children.Count == 1, "封面单页");
                var footer = (Border)window.FindName("Footer");
                Check(footer.Visibility == Visibility.Collapsed, "打开漫画时收起进度栏");
                await WaitUntilIdle(reader, "preloading");
                var stage = Field<Grid>(reader, "stage");
                Call(reader, "ClickReadingArea", new Point(stage.ActualWidth * .1, stage.ActualHeight * .5));
                Check(!footer.IsVisible, "点击侧边翻页时进度栏保持收起");
                Check(Field<object>(reader, "rendering") == null, "预加载命中后同步显示下一组");
                Check(Field<int>(reader, "position") == 1, "双页翻页");
                CheckOrder(reader, 2, 1);
                Click(window, "Previous");
                Check(Field<object>(reader, "rendering") == null, "回翻命中缓存");
                CheckOrder(reader, 0);
                Click(window, "Next");
                CheckOrder(reader, 2, 1);
                var panel = Field<StackPanel>(reader, "spread");
                var displayed = panel.Children.Cast<UIElement>().ToArray();
                Call(reader, "GoTo", 70);
                Check(Field<object>(reader, "rendering") != null, "远页跳转时渲染缓存外的页面");
                Check(panel.Children.Cast<UIElement>().SequenceEqual(displayed), "缓存未命中时保留旧画面");
                Call(reader, "GoTo", 82);
                Check(panel.Children.Cast<UIElement>().SequenceEqual(displayed), "连续跳页时保留旧画面");
                await Ready(reader);
                CheckOrder(reader, 82, 81);
                Call(reader, "GoTo", 1);
                await Ready(reader);
                CheckOrder(reader, 2, 1);
                Click(window, "Direction");
                await Ready(reader);
                CheckOrder(reader, 1, 2);
                Click(window, "Direction");
                Click(window, "Cover");
                await Ready(reader);
                CheckOrder(reader, 1, 0);
                Click(window, "Cover");
                Call(reader, "GoTo", 10);
                await Ready(reader);
                CheckOrder(reader, 10, 9);
                Capture(window, Path.Combine(args[1], "reader-book.png"));
                Size readingSize = stage.RenderSize;
                var pagesBeforeOverlay = panel.Children.Cast<UIElement>().ToArray();
                TapCenter(reader);
                await Task.Delay(160);
                Check(footer.IsVisible && stage.RenderSize == readingSize &&
                    panel.Children.Cast<UIElement>().SequenceEqual(pagesBeforeOverlay), "显示进度栏时阅读区保持原样");
                Capture(window, Path.Combine(args[1], "reader-progress.png"));
                TapCenter(reader);
                Check(!footer.IsVisible && stage.RenderSize == readingSize, "再次点击收起进度栏");
                await Until(() => state.ReadPosition(args[0]) == 10, "保存进度");
                await WaitUntilIdle(reader, "preloading");
                var effect = Field<PageTurnEffect>(reader, "pageTurn");
                SelectMode(reader, PageMotion.Curl);
                if (SystemParameters.ClientAreaAnimation)
                {
                    Click(window, "Next");
                    Check(effect.IsPlaying && (bool)typeof(PageTurnEffect).GetField("fromLeft", Private).GetValue(effect),
                        "右到左阅读从左侧卷页");
                    await Task.Delay(130);
                    Capture(window, Path.Combine(args[1], "reader-turn-rtl.png"));
                    Click(window, "Previous");
                    Check(!effect.IsPlaying, "连续翻页跳过动画");
                    Click(window, "Direction");
                    await WaitUntilIdle(reader, "preloading");
                    await Task.Delay(340);
                    Click(window, "Next");
                    Check(effect.IsPlaying && !(bool)typeof(PageTurnEffect).GetField("fromLeft", Private).GetValue(effect),
                        "左到右阅读从右侧卷页");
                    await Task.Delay(145);
                    Capture(window, Path.Combine(args[1], "reader-turn-ltr.png"));
                    await Task.Delay(330);
                    Check(!effect.IsPlaying, "仿真翻页动画结束");
                    Click(window, "Direction");
                }
                SelectMode(reader, PageMotion.Slide);
                Check(new ReaderState(privateState).Motion == PageMotion.Slide, "保存左右滑动设置");
                await WaitUntilIdle(reader, "preloading");
                await Task.Delay(340);
                Click(window, "Previous");
                Check(!effect.IsPlaying, "左右滑动时没有启动卷页");
                Call(reader, "GoTo", 10);
                await Ready(reader);
                Click(window, "Layout");
                await Ready(reader);
                CheckOrder(reader, 10);
                Click(window, "Fit");
                await Ready(reader);
                var viewport = Field<ScrollViewer>(reader, "viewport");
                Check(viewport.ScrollableHeight > 100, "适宽滚动");
                Click(window, "ZoomIn");
                await Ready(reader);
                Check(viewport.ScrollableWidth > 1, "缩放滚动");
                Click(window, "Full");
                await Ready(reader);
                Check(((Border)window.FindName("Header")).Visibility == Visibility.Collapsed, "隐藏全屏工具栏");
                TapCenter(reader);
                Check(footer.IsVisible, "在全屏中点击也能显示进度栏");
                Call(reader, "ToggleFullscreen");
                Check(((Border)window.FindName("Header")).Visibility == Visibility.Visible && !footer.IsVisible, "退出全屏时收起进度栏");
                await (Task)Call(reader, "OpenAsync", args[0]);
                await Ready(reader);
                Check(Field<int>(reader, "position") == 10, "重新打开恢复进度");
                TapCenter(reader);
                Check(footer.IsVisible, "重新打开后可以调出进度栏");
                var slider = (Slider)window.FindName("Progress");
                slider.Value = 186;
                await Task.Delay(300);
                await Ready(reader);
                Check(Field<int>(reader, "position") == 185, "进度条跳页");
                Check(!((Button)window.FindName("Next")).IsEnabled, "末页边界");
                if (!state.Dual) Click(window, "Layout");
                if (state.FitWidth) Click(window, "Fit");
                await VerifySlides(reader, window, state, args[1]);
                await VerifyScroll(reader, window, state, args[1]);
                await (Task)Call(reader, "OpenAsync", Path.Combine(args[1], "missing.pdf"));
                Check(Field<int>(reader, "position") == 185, "打开失败保留原书");
                Console.WriteLine("PASS: slides, curl, continuous scrolling, progress, resume and preload. System animation: "
                    + SystemParameters.ClientAreaAnimation);
                app.Shutdown(0);
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                var notice = (TextBlock)window.FindName("ShelfNoticeText");
                Console.Error.WriteLine(notice.Text + " " + ((TextBlock)window.FindName("NoticeText")).Text);
                app.Shutdown(1);
            }
        };
        int exit = app.Run(window);
        Console.WriteLine("已关闭阅读窗口，WPF 消息循环已退出。");
        Reader.ExitAfterWindowCloses(exit);
        return exit;
    }

    static void Click(Window window, string name)
    {
        ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }

    static void SelectMode(Reader reader, PageMotion motion)
    {
        var menu = Field<ContextMenu>(reader, "motionMenu");
        var option = menu.Items.Cast<MenuItem>().Single(item => (PageMotion)item.Tag == motion);
        option.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check(menu.Items.Cast<MenuItem>().Where(item => item.IsChecked).SequenceEqual(new[] { option }), "翻页菜单只有一个选中项");
    }

    sealed class RenderProbe : ReaderBook
    {
        public readonly TaskCompletionSource<bool> Release = new TaskCompletionSource<bool>();
        public readonly int[] Started = new int[3];
        public readonly CancellationToken[] Lifetime = new CancellationToken[3];

        public RenderProbe() : base("probe", new[] { new Size(10, 20), new Size(10, 20), new Size(10, 20) }) { }

        protected override async Task<BitmapSource> RenderPageAsync(int index, int width, CancellationToken token)
        {
            Started[index]++;
            Lifetime[index] = token;
            var interrupted = new TaskCompletionSource<bool>();
            using (token.Register(() => interrupted.TrySetResult(true)))
                await Task.WhenAny(Release.Task, interrupted.Task);
            token.ThrowIfCancellationRequested();
            var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
            bitmap.Freeze();
            return bitmap;
        }
    }

    static async Task VerifySharedRender()
    {
        var book = new RenderProbe();
        using (var cancelledPrefetch = new CancellationTokenSource())
        using (var cancelledQueuedPage = new CancellationTokenSource())
        {
            var prefetch = book.RenderAsync(0, 300, cancelledPrefetch.Token);
            var foreground = book.RenderAsync(0, 300, CancellationToken.None);
            var parallel = book.RenderAsync(1, 300, CancellationToken.None);
            var queued = book.RenderAsync(2, 300, cancelledQueuedPage.Token);
            Check(book.Started.SequenceEqual(new[] { 1, 1, 0 }), "同页合并且最多并行渲染两页");
            cancelledPrefetch.Cancel();
            cancelledQueuedPage.Cancel();
            try { await prefetch; throw new Exception("预加载取消未生效"); }
            catch (OperationCanceledException) { }
            try { await queued; throw new Exception("排队的页面未取消"); }
            catch (OperationCanceledException) { }
            Check(!book.Lifetime[0].IsCancellationRequested, "取消预加载不会取消正在等待的可见页");
            book.Release.SetResult(true);
            var bitmap = await foreground;
            await parallel;
            Check(ReferenceEquals(bitmap, await book.RenderAsync(0, 300, CancellationToken.None)) && book.Started[0] == 1,
                "完成后复用同一张缓存位图");
            await book.RenderAsync(2, 300, CancellationToken.None);
            Check(book.Started[2] == 1, "取消的排队请求可重新渲染");
        }
    }

    static async Task VerifySlides(Reader reader, Window window, ReaderState state, string folder)
    {
        var current = Field<ReaderBook>(reader, "book");
        var effect = Field<PageSlideEffect>(reader, "slide");
        var stage = Field<Grid>(reader, "stage");
        var menu = Field<ContextMenu>(reader, "motionMenu");
        Click(window, "PageMotion");
        Check(menu.IsOpen && menu.Items.Count == 3 &&
            menu.Items.Cast<MenuItem>().Select(item => item.Header.ToString())
                .SequenceEqual(new[] { "左右滑动", "仿真卷页", "上下滚动" }), "同一个菜单显示三个选项");
        menu.IsOpen = false;
        SelectMode(reader, PageMotion.Curl);
        SelectMode(reader, PageMotion.Slide);
        Check(new ReaderState(state.Folder).Motion == PageMotion.Slide, "切换菜单选项持久化");

        int start = current.Count > 10 ? 9 : 0;
        Call(reader, "GoTo", start);
        await Ready(reader);
        var spreads = Field<List<int[]>>(reader, "spreads");
        int next = spreads[Field<int>(reader, "spreadIndex") + 1][0];
        await WaitUntilIdle(reader, "preloading");
        await Task.Delay(320);
        Click(window, "Next");
        if (SystemParameters.ClientAreaAnimation)
        {
            Check(effect.IsActive && (int)typeof(PageSlideEffect).GetField("direction", Private).GetValue(effect) == 1,
                "右到左阅读向右推送旧页");
            await Until(() => !effect.IsActive, "键盘和按钮翻页使用的滑动动画结束");
        }
        Check(Field<int>(reader, "position") == next, "滑动自动翻到下一组");
        Call(reader, "GoTo", start);
        await Ready(reader);
        await WaitUntilIdle(reader, "preloading");

        Point center = new Point(stage.ActualWidth / 2, stage.ActualHeight / 2);
        Check((bool)Call(reader, "StartSlideGesture", center), "开始横向拖动");
        Call(reader, "MoveSlideGesture", new Point(center.X + 22, center.Y));
        Call(reader, "FinishSlideGesture", new Point(center.X + 22, center.Y));
        Check(effect.IsActive && Field<int>(reader, "position") == start, "短拖动回弹，页码不变");
        await Until(() => !effect.IsActive, "回弹结束");
        Check((bool)Call(reader, "StartSlideGesture", center), "再次拖动");
        var end = new Point(center.X + 230, center.Y);
        Check((bool)Call(reader, "MoveSlideGesture", end) &&
            typeof(PageSlideEffect).GetField("to", Private).GetValue(effect) != null, "横向拖动时相邻缓存页跟随出现");
        await Task.Delay(40);
        Capture(window, Path.Combine(folder, current.Count > 10 ? "reader-slide-pdf.png" : "reader-slide.png"));
        Call(reader, "FinishSlideGesture", end);
        Check(Field<int>(reader, "position") == next && effect.IsActive, "松手后向下一页吸附");
        await Until(() => !effect.IsActive, "滑动吸附结束");
        Call(reader, "GoTo", start);
        Click(window, "Direction");
        await Ready(reader);
        await WaitUntilIdle(reader, "preloading");
        await Task.Delay(320);
        Click(window, "Next");
        if (SystemParameters.ClientAreaAnimation)
            Check(effect.IsActive && (int)typeof(PageSlideEffect).GetField("direction", Private).GetValue(effect) == -1,
                "左到右阅读向左推送旧页");
        Click(window, "Direction");

        var legacy = Path.Combine(state.Folder, "v16");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "settings.xml"), "<reader><continuousScroll>true</continuousScroll></reader>");
        Check(new ReaderState(legacy).Motion == PageMotion.Scroll, "保留旧版本的上下滚动设置");
    }

    static async Task VerifyScroll(Reader reader, Window window, ReaderState state, string folder)
    {
        var current = Field<ReaderBook>(reader, "book");
        int start = current.Count > 10 ? 10 : 0;
        Call(reader, "GoTo", start);
        await Ready(reader);
        SelectMode(reader, PageMotion.Scroll);
        var scroller = Field<ContinuousReader>(reader, "continuous");
        var view = scroller.Viewport;
        var canvas = (Canvas)window.FindName("ContinuousPages");
        Check(view.IsVisible && !((ScrollViewer)window.FindName("Viewport")).IsVisible, "开启连续滚动视图");
        await Until(() => canvas.Children.Count >= 2 && canvas.Children.Cast<Border>().All(frame => ((Image)frame.Child).Source != null),
            "渲染当前页和预载相邻页");
        Check(Field<int>(reader, "position") == start, "切换时保留当前页");
        var positions = (Rect[])typeof(ContinuousReader).GetField("pages", Private).GetValue(scroller);
        Check(positions[start + 1].Top > positions[start].Bottom, "图片按顺序垂直排布");
        Check(canvas.Children.Count <= 8, "只挂载滚动位置附近的页面");

        var stage = Field<Grid>(reader, "stage");
        double previousOffset = view.VerticalOffset;
        var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
            { RoutedEvent = Mouse.PreviewMouseWheelEvent, Source = stage };
        stage.RaiseEvent(wheel);
        double expectedOffset = previousOffset + 96;
        if (SystemParameters.ClientAreaAnimation)
        {
            stage.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
                { RoutedEvent = Mouse.PreviewMouseWheelEvent, Source = stage });
            expectedOffset += 96;
        }
        await Until(() => Math.Abs(view.VerticalOffset - expectedOffset) < 1, "连续滚轮事件累加距离");
        scroller.EndWheelScroll();
        Check(view.VerticalOffset < positions[start + 1].Top, "单次滚轮只滚动一段");
        view.ScrollToVerticalOffset(positions[start + 1].Top - 180);
        await Until(() => Math.Abs(view.VerticalOffset - positions[start + 1].Top + 180) < 2, "滚到页缝附近");
        Capture(window, Path.Combine(folder, current.Count > 10 ? "reader-scrolling-pdf.png" : "reader-scrolling.png"));
        view.ScrollToVerticalOffset(positions[start + 1].Top + 40);
        await Until(() => Field<int>(reader, "position") == start + 1, "跨页时更新阅读进度");
        await Until(() => state.ReadPosition(current.Path) == start + 1, "保存滚动进度");
        Click(window, "ZoomIn");
        await Task.Delay(180);
        Check(Field<int>(reader, "position") == start + 1, "滚动缩放保持当前页");
        var footer = (Border)window.FindName("Footer");
        bool wasVisible = footer.IsVisible;
        TapCenter(reader);
        Check(footer.IsVisible != wasVisible, "滚动时可切换进度条");
        if (!footer.IsVisible) TapCenter(reader);
        ((Slider)window.FindName("Progress")).Value = current.Count;
        await Until(() => Field<int>(reader, "position") == current.Count - 1, "进度条跳到最后一页");
        positions = (Rect[])typeof(ContinuousReader).GetField("pages", Private).GetValue(scroller);
        await Until(() => Math.Abs(view.VerticalOffset - positions[current.Count - 1].Top + 14) < 2, "末页对齐视口");
        SelectMode(reader, PageMotion.Curl);
        await Ready(reader);
        Check(!view.IsVisible && canvas.Children.Count == 0 && Field<int>(reader, "position") == current.Count - 1, "切回翻页时定位到末页");
        SelectMode(reader, PageMotion.Scroll);
        Check(new ReaderState(state.Folder).Motion == PageMotion.Scroll, "保存连续滚动模式");
        await (Task)Call(reader, "OpenAsync", current.Path);
        await Until(() => view.IsVisible && Field<int>(reader, "position") == current.Count - 1,
            "重新打开后续读");
    }

    static async Task Until(Func<bool> condition, string label)
    {
        for (int i = 0; i < 160 && !condition(); i++) await Task.Delay(35);
        Check(condition(), label);
    }

    static async Task VerifyShelf(Reader reader, Window window, ReaderState state, string folder)
    {
        var shelf = (Grid)window.FindName("ShelfView");
        var items = (WrapPanel)window.FindName("ShelfItems");
        var cbz = Path.Combine(folder, "import-fixtures", "漫画样本.cbz");
        var epub = Path.Combine(folder, "import-fixtures", "图片样本.epub");
        var direct = Path.Combine(folder, "import-fixtures", "文字样本.epub");
        var older = new ReaderState(Path.Combine(state.Folder, "old"));
        older.LastPath = cbz;
        Check(new Bookshelf(older).Read().Single().Path == cbz, "迁入旧版本最近阅读的书");
        Check(shelf.IsVisible && !((Grid)window.FindName("ReaderView")).IsVisible, "默认进入书架");
        var store = new Bookshelf(state);
        store.Remove(cbz);
        store.Remove(epub);
        store.Remove(direct);
        state.SavePosition(cbz, 0);
        state.SavePosition(epub, 0);
        Call(reader, "AddToShelf", (object)new[] { cbz, epub, cbz });
        Check(items.Children.Count == 2 && new Bookshelf(new ReaderState(state.Folder)).Read().Count == 2, "批量添加去重及持久化");
        Button bookCard = FindShelfCard(items, cbz);
        bookCard.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await WaitUntilIdle(reader, "opening");
        await Ready(reader);
        Check(!shelf.IsVisible && Field<ReaderBook>(reader, "book").Path == cbz, "点击卡片打开书");
        await WaitUntilIdle(reader, "coverWork");
        Check(store.Cover(cbz) != null, "读取缓存封面");
        Call(reader, "GoTo", 1);
        await Ready(reader);
        Click(window, "ShowShelf");
        Check(shelf.IsVisible && store.Read().First().Path == cbz, "书架显示最近阅读的书");
        var label = (TextBlock)((StackPanel)FindShelfCard(items, cbz).Content).Children[2];
        Check(label.Text.Contains("第 2 页 / 3"), "卡片显示阅读进度");
        FindShelfCard(items, epub).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await WaitUntilIdle(reader, "opening");
        await Ready(reader);
        Check(Field<ReaderBook>(reader, "book").Path == epub, "从书架切换书籍");
        await WaitUntilIdle(reader, "coverWork");
        Click(window, "ShowShelf");
        await Task.Delay(150);
        Check(items.ActualHeight > 0 && ((Button)items.Children[0]).ActualHeight > 0, "书架卡片完成布局");
        Capture(window, Path.Combine(folder, "reader-bookshelf.png"));
        FindShelfCard(items, cbz).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await WaitUntilIdle(reader, "opening");
        await Ready(reader);
        Check(Field<int>(reader, "position") == 1, "从书架续读");
        await (Task)Call(reader, "OpenAsync", direct);
        await Ready(reader);
        Check(store.Read().Any(entry => entry.Path == direct), "直接打开的文件自动加入书架");
        Click(window, "ShowShelf");
        var remove = (MenuItem)FindShelfCard(items, cbz).ContextMenu.Items[0];
        remove.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check(store.Read().All(entry => entry.Path != cbz) && File.Exists(cbz) && state.ReadPosition(cbz) == 1,
            "移出书架保留原文件和阅读进度");
        Click(window, "BackToBook");
        Check(!shelf.IsVisible && Field<ReaderBook>(reader, "book").Path == direct, "返回当前书");
    }

    static Button FindShelfCard(WrapPanel items, string path)
    {
        return items.Children.Cast<Button>().Single(card => Equals(card.ToolTip, path));
    }

    static async Task VerifyImports(Reader reader, Window window, ReaderState state, string folder)
    {
        var fixtures = Path.Combine(folder, "import-fixtures");
        var panel = Field<StackPanel>(reader, "spread");
        foreach (string name in new[] { "漫画样本.cbz", "漫画样本.zip", "图片样本.epub", "文字样本.epub" })
        {
            string file = Path.Combine(fixtures, name);
            state.SavePosition(file, 0);
            await (Task)Call(reader, "OpenAsync", file);
            await Ready(reader);
            var loaded = Field<ReaderBook>(reader, "book");
            Check(loaded != null && loaded.Path == file, "通过正常入口打开 " + name);
            CheckOrder(reader, 0);
            if (name == "文字样本.epub")
            {
                Check(loaded.Count >= 3 && loaded.SizeOf(0) == loaded.SizeOf(1) &&
                    loaded.SizeOf(loaded.Count - 1) == new Size(300, 500), "文字 EPUB 分页和插图顺序");
                Check(loaded.PreferredRightToLeft == false, "文字书默认从左到右翻页");
                Check(HasInk((BitmapSource)((Image)((Border)panel.Children[0]).Child).Source), "页面绘制中文和英文");
                Capture(window, Path.Combine(folder, "reader-epub-text.png"));
                Call(reader, "GoTo", 1);
                await Ready(reader);
                await (Task)Call(reader, "OpenAsync", file);
                await Ready(reader);
                Check(Field<int>(reader, "position") == 1, "EPUB 恢复阅读进度");
                Check(HasInk(await loaded.RenderAsync(1, 920, CancellationToken.None)), "第二页文字及高分辨率重绘");
            }
            else if (name.EndsWith(".epub", StringComparison.Ordinal))
            {
                Check(loaded.Count == 3 && loaded.SizeOf(0) == new Size(500, 300) &&
                    loaded.SizeOf(1).Width == 300 && loaded.SizeOf(2).Width == 320, "图片 EPUB 按书内顺序");
                Check(loaded.PreferredRightToLeft == true, "读取 EPUB 从右到左的设置");
            }
            else
                Check(loaded.Count == 3 && loaded.SizeOf(0).Width == 300 && loaded.SizeOf(1).Width == 320 &&
                    loaded.SizeOf(2).Width == 500, "图片包按自然页码排序并忽略系统文件");
        }
        var before = Field<ReaderBook>(reader, "book");
        await (Task)Call(reader, "OpenAsync", Path.Combine(fixtures, "无图片.zip"));
        Check(ReferenceEquals(before, Field<ReaderBook>(reader, "book")), "打开无图压缩包时保留原书");
        Check(((TextBlock)window.FindName("NoticeText")).Text.Contains("没有支持的图片"), "提示无图片的错误");
    }

    static bool HasInk(BitmapSource bitmap)
    {
        var image = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[image.PixelWidth * image.PixelHeight * 4];
        image.CopyPixels(pixels, image.PixelWidth * 4, 0);
        int dark = 0;
        for (int p = 0; p < pixels.Length; p += 4)
            if (pixels[p] < 100 && pixels[p + 1] < 100 && pixels[p + 2] < 100 && ++dark >= 1000) return true;
        return false;
    }

    static void TapCenter(Reader reader)
    {
        var stage = Field<Grid>(reader, "stage");
        Check((bool)Call(reader, "ClickReadingArea", new Point(stage.ActualWidth * .5, stage.ActualHeight * .5)),
            "阅读区中央可以点击");
    }

    static T Field<T>(Reader reader, string name)
    {
        return (T)typeof(Reader).GetField(name, Private).GetValue(reader);
    }

    static object Call(Reader reader, string name, params object[] args)
    {
        return typeof(Reader).GetMethod(name, Private).Invoke(reader, args);
    }

    static async Task Ready(Reader reader)
    {
        await Task.Delay(180);
        await WaitUntilIdle(reader, "rendering");
        Check(Field<StackPanel>(reader, "spread").Children.Count > 0, "PDF 渲染");
    }

    static async Task WaitUntilIdle(Reader reader, string operation)
    {
        for (int i = 0; i < 240 && Field<object>(reader, operation) != null; i++)
            await Task.Delay(50);
        Check(Field<object>(reader, operation) == null, "等待渲染任务完成");
    }

    static void CheckOrder(Reader reader, params int[] expected)
    {
        var book = Field<ReaderBook>(reader, "book");
        var panel = Field<StackPanel>(reader, "spread");
        double dpi = VisualTreeHelper.GetDpi(Field<Grid>(reader, "stage")).DpiScaleX;
        Check(panel.Children.Count == expected.Length, "显示页数");
        for (int i = 0; i < expected.Length; i++)
        {
            var image = (Image)((Border)panel.Children[i]).Child;
            var cached = book.RenderAsync(expected[i], image.Width * dpi, CancellationToken.None);
            Check(cached.IsCompleted && ReferenceEquals(image.Source, cached.Result), "页面顺序和缓存内容");
        }
    }

    static void Capture(Window window, string path)
    {
        var visual = (Visual)window.Content;
        var size = ((FrameworkElement)visual).RenderSize;
        var target = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));
        using (var file = File.Create(path)) encoder.Save(file);
    }

    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception(label);
    }
}
