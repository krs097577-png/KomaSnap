using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

[assembly: AssemblyTitle("KomaSnap")]
[assembly: AssemblyProduct("KomaSnap")]
[assembly: AssemblyDescription("轻量级漫画与电子书阅读器")]
[assembly: AssemblyVersion("1.8.0.0")]
[assembly: AssemblyFileVersion("1.8.0.0")]
[assembly: AssemblyInformationalVersion("1.8")]

sealed class Reader
{
    readonly Window window;
    readonly ReaderState state;
    readonly Bookshelf bookshelf;
    readonly Grid readerView;
    readonly Grid shelfView;
    readonly ScrollViewer viewport;
    readonly ContinuousReader continuous;
    readonly StackPanel spread;
    readonly Grid stage;
    readonly ReadingChrome chrome;
    readonly ReaderAppearance appearance;
    readonly PageTurnEffect pageTurn;
    readonly PageSlideEffect slide;
    readonly ContextMenu motionMenu;
    readonly TextBox pageNumber;
    readonly Slider progress;
    readonly DispatcherTimer resizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
    readonly DispatcherTimer jumpTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(105) };
    readonly DispatcherTimer noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
    readonly DispatcherTimer positionTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(220) };
    ReaderBook book;
    bool? bookDirection;
    bool RightToLeft { get { return bookDirection ?? state.RightToLeft; } }
    bool IsScrolling { get { return state.Motion == PageMotion.Scroll; } }
    bool FitWidth { get { return IsScrolling ? state.ContinuousFitWidth : state.FitWidth; } }
    ScrollViewer ActiveViewport { get { return IsScrolling ? continuous.Viewport : viewport; } }
    List<int[]> spreads;
    CancellationTokenSource opening;
    string openingPath;
    CancellationTokenSource rendering;
    CancellationTokenSource preloading;
    CancellationTokenSource coverWork;
    string coverWorkPath;
    bool resetScrollOnShow;
    int position;
    int spreadIndex;
    int wheelDelta;
    int horizontalWheelDelta;
    int preloadDirection = 1;
    int lastTurnAt = unchecked(Environment.TickCount - 1000);
    double zoom = 1;
    bool changingProgress;
    bool fullscreen;
    bool pan;
    Point panStart;
    Point panOffsets;
    Point? slideOrigin;
    PageTurnEffect.Leaf[] dragFrom;
    PageTurnEffect.Leaf[] dragTo;
    int dragStep;
    int dragStarted;
    TouchDevice slideTouch;
    WindowState oldWindowState;

    [STAThread]
    public static void Main(string[] args)
    {
        int exitCode;
        // 先创建唤醒事件，快速重复启动时也能保留唤醒请求。
        using (var wake = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\KomaSnap.Reader.Restore"))
        {
            bool first;
            using (var instance = new Mutex(false, @"Local\KomaSnap.Reader.Instance", out first))
            {
                if (!first && args.Length == 0)
                {
                    wake.Set();
                    return;
                }
                var app = new Application();
                var reader = new Reader();
                if (args.Length > 0)
                {
                    RoutedEventHandler openOnLoad = null;
                    openOnLoad = async (s, e) =>
                    {
                        reader.window.Loaded -= openOnLoad;
                        await reader.OpenAsync(args[0]);
                    };
                    reader.window.Loaded += openOnLoad;
                }
                using (var tray = new ReaderTray(reader.window, reader.FlushPosition, first ? wake : null))
                    exitCode = app.Run(reader.window);
            }
        }
        ExitAfterWindowCloses(exitCode);
    }

    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool TerminateProcess(IntPtr process, uint exitCode);

    internal static void ExitAfterWindowCloses(int exitCode)
    {
        // WPF 已结束且阅读进度已写入。终止进程以释放仍在等待的系统 PDF 组件。
        if (!TerminateProcess(GetCurrentProcess(), unchecked((uint)exitCode))) Environment.Exit(exitCode);
    }

    public Reader(string storageFolder = null)
    {
        state = new ReaderState(storageFolder);
        bookshelf = new Bookshelf(state);
        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Reader.xaml"))
            window = (Window)XamlReader.Load(stream);
        appearance = new ReaderAppearance(window, storageFolder == null);
        readerView = Find<Grid>("ReaderView");
        shelfView = Find<Grid>("ShelfView");
        viewport = Find<ScrollViewer>("Viewport");
        continuous = new ContinuousReader(Find<ScrollViewer>("ContinuousViewport"), Find<Canvas>("ContinuousPages"));
        continuous.PositionChanged += TrackScrollPosition;
        spread = Find<StackPanel>("Spread");
        stage = Find<Grid>("Stage");
        new ReaderSpine(spread);
        pageTurn = new PageTurnEffect(stage.Background);
        stage.Children.Insert(1, pageTurn);
        slide = new PageSlideEffect(stage.Background);
        stage.Children.Insert(2, slide);
        motionMenu = ReaderAppearance.NewMenu(window);
        ReaderAppearance.EnableOptionKeys(motionMenu);
        var motionIcons = new[] { "M1,4 H15 V12 H1 Z M5,8 H11 M8,5 L11,8 L8,11",
            "M2,1 H14 V9 Q12,15 6,15 H2 Z M14,9 Q11,11 8,10 V15",
            "M3,1 H13 V15 H3 Z M8,4 V12 M6,6 L8,4 L10,6 M6,10 L8,12 L10,10" };
        foreach (PageMotion motion in Enum.GetValues(typeof(PageMotion)))
        {
            var item = ReaderAppearance.NewMenuItem(window, MotionLabel(motion), motionIcons[(int)motion]);
            item.IsCheckable = true;
            item.Tag = motion;
            item.Click += (s, e) => SetPageMotion((PageMotion)((MenuItem)s).Tag);
            motionMenu.Items.Add(item);
        }
        var cancelMotion = ReaderAppearance.NewMenuItem(window, "取消", null);
        cancelMotion.Click += (s, e) => motionMenu.IsOpen = false;
        motionMenu.Items.Add(cancelMotion);
        pageNumber = Find<TextBox>("PageNumber");
        progress = Find<Slider>("Progress");

        Bind("Open", ChooseFile);
        Find<Button>("ShowShelf").Click += (s, e) => ShowShelf();
        Bind("BackToBook", ReturnToBook);
        Bind("AddBooks", ChooseShelfBooks);
        Bind("EmptyAddBooks", ChooseShelfBooks);
        Bind("Layout", ToggleLayout);
        Bind("Direction", ToggleDirection);
        Bind("Cover", ToggleCover);
        Bind("Fit", ToggleFit);
        Bind("ZoomOut", () => ZoomBy(1 / 1.2));
        Bind("ZoomIn", () => ZoomBy(1.2));
        Find<Button>("PageMotion").Click += (s, e) => OpenMotionMenu();
        Bind("Full", ToggleFullscreen);
        Bind("Previous", () => Turn(-1));
        Bind("Next", () => Turn(1));
        pageNumber.GotKeyboardFocus += (s, e) => pageNumber.SelectAll();
        pageNumber.PreviewKeyDown += PageNumberKey;
        pageNumber.LostKeyboardFocus += (s, e) => SyncPageNumber();
        chrome = new ReadingChrome(window, Find<Border>("Header"), Find<Border>("Footer"), progress, pageNumber,
            motionMenu, appearance.ThemeMenu);
        progress.ValueChanged += (s, e) =>
        {
            if (changingProgress || book == null) return;
            jumpTimer.Stop();
            jumpTimer.Start();
        };
        jumpTimer.Tick += (s, e) =>
        {
            jumpTimer.Stop();
            GoTo((int)progress.Value - 1);
        };
        progress.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((s, e) =>
        {
            if (book == null) return;
            jumpTimer.Stop();
            GoTo((int)progress.Value - 1);
        }));
        stage.SizeChanged += (s, e) =>
        {
            pageTurn.Stop();
            CancelSlide();
            resizeTimer.Stop();
            resizeTimer.Start();
        };
        resizeTimer.Tick += (s, e) =>
        {
            resizeTimer.Stop();
            Refresh(false);
        };
        noticeTimer.Tick += (s, e) => SetNotice(null);
        positionTimer.Tick += (s, e) => FlushPosition();
        stage.PreviewMouseWheel += MouseWheel;
        stage.PreviewMouseLeftButtonDown += (s, e) => StartSlideGesture(e.GetPosition(stage));
        stage.PreviewMouseLeftButtonUp += ClickStage;
        stage.PreviewMouseDown += StartPan;
        stage.PreviewMouseMove += MovePan;
        stage.PreviewMouseUp += EndPan;
        stage.LostMouseCapture += (s, e) =>
        {
            pan = false;
            stage.ClearValue(FrameworkElement.CursorProperty);
            if (dragFrom != null && slideTouch == null) CancelSlide();
        };
        stage.PreviewTouchDown += StartTouchSlide;
        stage.PreviewTouchMove += MoveTouchSlide;
        stage.PreviewTouchUp += FinishTouchSlide;
        stage.LostTouchCapture += (s, e) => { if (slideTouch == e.TouchDevice) CancelSlide(); };
        window.PreviewKeyDown += WindowKey;
        window.PreviewDragOver += DragOver;
        window.PreviewDrop += Drop;
        window.SourceInitialized += (s, e) =>
        {
            var source = PresentationSource.FromVisual(window) as HwndSource;
            if (source != null) source.AddHook(HorizontalWheel);
        };
        window.Loaded += (s, e) => ReloadShelf();
        window.Closed += (s, e) =>
        {
            FlushPosition();
            continuous.Stop();
            pageTurn.Stop();
            CancelSlide();
            if (opening != null) opening.Cancel();
            if (rendering != null) rendering.Cancel();
            if (preloading != null) preloading.Cancel();
            if (coverWork != null) coverWork.Cancel();
            resizeTimer.Stop();
            jumpTimer.Stop();
            noticeTimer.Stop();
        };
        SyncControls();
    }

    T Find<T>(string name) where T : class { return (T)window.FindName(name); }

    void Bind(string name, Action action)
    {
        Find<Button>(name).Click += (s, e) => { action(); FocusReader(); };
    }

    void FocusReader() { Keyboard.Focus(window); }

    static OpenFileDialog BookDialog(bool many)
    {
        return new OpenFileDialog
        {
            Filter = "支持的文件 (*.pdf;*.epub;*.cbz;*.zip)|*.pdf;*.epub;*.cbz;*.zip|" +
                "PDF (*.pdf)|*.pdf|EPUB (*.epub)|*.epub|漫画图片包 (*.cbz;*.zip)|*.cbz;*.zip",
            CheckFileExists = true,
            Multiselect = many
        };
    }

    async void ChooseFile()
    {
        var dialog = BookDialog(false);
        if (dialog.ShowDialog(window) == true) await OpenAsync(dialog.FileName);
    }

    void ChooseShelfBooks()
    {
        var dialog = BookDialog(true);
        if (dialog.ShowDialog(window) == true) AddToShelf(dialog.FileNames);
    }

    static string[] DroppedBooks(DragEventArgs e)
    {
        var files = e.Data.GetData(DataFormats.FileDrop) as string[];
        return files == null ? new string[0] : files.Where(ReaderBook.Supports).ToArray();
    }

    void DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DroppedBooks(e).Length == 0 ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
    }

    async void Drop(object sender, DragEventArgs e)
    {
        var paths = DroppedBooks(e);
        e.Handled = true;
        if (paths.Length > 1)
        {
            SetShelfVisible(true);
            AddToShelf(paths);
        }
        else if (paths.Length == 1) await OpenAsync(paths[0]);
    }

    async Task OpenAsync(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        if (opening != null) opening.Cancel();
        var operation = new CancellationTokenSource();
        opening = operation;
        SetNotice("正在打开文件…", false);
        try
        {
            openingPath = Path.GetFullPath(path);
            var loaded = await ReaderBook.OpenAsync(openingPath, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            FlushPosition();
            if (rendering != null) rendering.Cancel();
            if (preloading != null) preloading.Cancel();
            jumpTimer.Stop();
            book = loaded;
            bookDirection = loaded.PreferredRightToLeft;
            position = Math.Max(0, Math.Min(book.Count - 1, state.ReadPosition(book.Path)));
            preloadDirection = 1;
            string shelfError = null;
            try { bookshelf.RecordOpened(loaded); }
            catch (Exception error)
            {
                if (!ReaderState.IsStorageError(error)) throw;
                shelfError = "无法将书加入书架。\n" + error.Message;
            }
            state.LastPath = book.Path;
            state.SaveSettings();
            zoom = 1;
            var title = Path.GetFileNameWithoutExtension(book.Path);
            Find<TextBlock>("BookTitle").Text = title;
            Find<TextBlock>("BookTitle").ToolTip = book.Path;
            window.Title = title + " · KomaSnap";
            SetShelfVisible(false);
            RebuildSpreads();
            SetNotice(shelfError);
            CacheShelfCover(loaded);
            FocusReader();
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (operation.IsCancellationRequested) return;
            string detail = Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase) &&
                error.HResult == unchecked((int)0x8007052B) ?
                "此 PDF 需要密码，请先在支持密码的应用中另存一份可直接打开的副本。" : error.Message;
            SetNotice("无法打开文件。\n" + detail, false);
        }
        finally
        {
            if (opening == operation)
            {
                opening = null;
                openingPath = null;
            }
            operation.Dispose();
        }
    }

    void SetNotice(string message, bool temporary = true)
    {
        noticeTimer.Stop();
        Find<TextBlock>("ShelfNoticeText").Text = message;
        Find<Border>("ShelfNotice").Visibility = message == null ? Visibility.Collapsed : Visibility.Visible;
        Find<TextBlock>("NoticeText").Text = message;
        Find<Border>("Notice").Visibility = book != null && message != null ? Visibility.Visible : Visibility.Collapsed;
        if (message != null && temporary) noticeTimer.Start();
    }

    bool IsShelfVisible { get { return shelfView.Visibility == Visibility.Visible; } }

    void SetShelfVisible(bool show)
    {
        if (show && fullscreen) ToggleFullscreen();
        pageTurn.Stop();
        CancelSlide();
        chrome.Hide();
        readerView.Visibility = show ? Visibility.Hidden : Visibility.Visible;
        shelfView.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show)
        {
            continuous.EndWheelScroll();
            FlushPosition();
            Find<Button>("BackToBook").Visibility = book == null ? Visibility.Collapsed : Visibility.Visible;
            ReloadShelf();
        }
    }

    void ShowShelf()
    {
        SetShelfVisible(true);
        FocusReader();
    }

    void ReturnToBook()
    {
        if (book == null) return;
        if (opening != null) opening.Cancel();
        SetNotice(null);
        SetShelfVisible(false);
        FocusReader();
    }

    void AddToShelf(IEnumerable<string> paths)
    {
        try
        {
            int added = bookshelf.Add(paths);
            ReloadShelf();
            SetNotice(added > 0 ? "已添加 " + added + " 本书。" : "选中的书已在书架中。");
        }
        catch (Exception error)
        {
            if (!ReaderState.IsStorageError(error)) throw;
            SetNotice("无法保存书架。\n" + error.Message);
        }
    }

    void ReloadShelf()
    {
        try
        {
            var books = bookshelf.Read();
            var items = Find<WrapPanel>("ShelfItems");
            items.Children.Clear();
            foreach (var item in books) items.Children.Add(ShelfCard(item));
            Find<StackPanel>("ShelfEmpty").Visibility = books.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            Find<TextBlock>("ShelfCount").Text = books.Count + " 本";
        }
        catch (Exception error)
        {
            if (!ReaderState.IsStorageError(error)) throw;
            SetNotice("无法读取书架。\n" + error.Message, false);
        }
    }

    Button ShelfCard(Bookshelf.Entry entry)
    {
        var content = new StackPanel { Width = 138 };
        var cover = new Border { Height = 182 };
        cover.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
        cover.SetResourceReference(Border.BackgroundProperty, "Cover");
        var bitmap = bookshelf.Cover(entry.Path);
        if (bitmap == null)
        {
            var format = new TextBlock
            {
                Text = Path.GetExtension(entry.Path).TrimStart('.').ToUpperInvariant(),
                FontSize = 22, FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            format.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            cover.Child = format;
        }
        else cover.Child = new Image { Source = bitmap, Stretch = Stretch.Uniform };
        content.Children.Add(cover);
        content.Children.Add(new TextBlock
        {
            Text = entry.Title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 9, 0, 4)
        });
        int page = Math.Max(0, state.ReadPosition(entry.Path));
        string progressText = entry.Pages > 0
            ? "第 " + (Math.Min(page, entry.Pages - 1) + 1) + " 页 / " + entry.Pages + " 页"
            : page > 0 ? "第 " + (page + 1L) + " 页" : entry.LastOpened > 0 ? "已打开" : "未读";
        var status = new TextBlock { Text = progressText, FontSize = 12 };
        status.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        content.Children.Add(status);
        string lastRead = entry.LastOpened > 0 && entry.LastOpened <= DateTime.MaxValue.Ticks
            ? "上次阅读 " + new DateTime(entry.LastOpened, DateTimeKind.Utc).ToLocalTime().ToString("MM-dd HH:mm") : "尚未阅读";
        var date = new TextBlock { Text = lastRead, FontSize = 11, Margin = new Thickness(0, 3, 0, 7) };
        date.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        content.Children.Add(date);
        var indicator = new Grid { Height = 3, HorizontalAlignment = HorizontalAlignment.Stretch };
        var track = new Border { CornerRadius = new CornerRadius(1.5) };
        track.SetResourceReference(Border.BackgroundProperty, "Track");
        var fill = new Border { Height = 3, CornerRadius = new CornerRadius(1.5), HorizontalAlignment = HorizontalAlignment.Left,
            Width = entry.Pages > 0 && entry.LastOpened > 0 ? 138 * (Math.Min(page, entry.Pages - 1) + 1.0) / entry.Pages : 0 };
        fill.SetResourceReference(Border.BackgroundProperty, "Accent");
        indicator.Children.Add(track);
        indicator.Children.Add(fill);
        content.Children.Add(indicator);
        var card = new Button { Style = (Style)window.FindResource("ShelfCardStyle"), Content = content, ToolTip = entry.Path };
        System.Windows.Automation.AutomationProperties.SetName(card, entry.Title + "，" + progressText);
        card.Click += async (s, e) =>
        {
            if (book != null && string.Equals(book.Path, entry.Path, StringComparison.OrdinalIgnoreCase)) ReturnToBook();
            else await OpenAsync(entry.Path);
        };
        var remove = ReaderAppearance.NewMenuItem(window, "移出书架", null);
        remove.Click += (s, e) => RemoveShelfBook(entry.Path);
        card.ContextMenu = ReaderAppearance.NewMenu(window);
        card.ContextMenu.Items.Add(remove);
        return card;
    }

    void RemoveShelfBook(string path)
    {
        try { bookshelf.Remove(path); }
        catch (Exception error)
        {
            if (!ReaderState.IsStorageError(error)) throw;
            SetNotice("无法移出书架。\n" + error.Message);
            return;
        }

        var comparison = StringComparison.OrdinalIgnoreCase;
        if (opening != null && string.Equals(openingPath, path, comparison)) opening.Cancel();
        if (coverWork != null && string.Equals(coverWorkPath, path, comparison)) coverWork.Cancel();
        if (book != null && string.Equals(book.Path, path, comparison))
        {
            FlushPosition();
            pageTurn.Stop();
            CancelSlide();
            jumpTimer.Stop();
            if (rendering != null) rendering.Cancel();
            if (preloading != null) preloading.Cancel();
            continuous.Stop();
            book.ClearCache();
            book = null;
            bookDirection = null;
            spreads = null;
            spread.Children.Clear();
            window.Title = "KomaSnap";
            Find<Button>("BackToBook").Visibility = Visibility.Collapsed;
            SyncControls();
        }
        ReloadShelf();
        try
        {
            bookshelf.DeleteCover(path);
            SetNotice("已移出书架并清理缓存。");
        }
        catch (Exception error)
        {
            if (!ReaderState.IsStorageError(error)) throw;
            SetNotice("已移出书架，封面缓存清理失败。\n" + error.Message, false);
        }
    }

    async void CacheShelfCover(ReaderBook opened)
    {
        if (coverWork != null) coverWork.Cancel();
        if (bookshelf.HasCover(opened.Path)) return;
        var operation = new CancellationTokenSource();
        coverWork = operation;
        coverWorkPath = opened.Path;
        var token = operation.Token;
        try
        {
            await Dispatcher.Yield(DispatcherPriority.Background);
            var image = await opened.RenderAsync(0, 224, token);
            token.ThrowIfCancellationRequested();
            bookshelf.SaveCover(opened.Path, image);
            if (IsShelfVisible) ReloadShelf();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { System.Diagnostics.Debug.WriteLine(error); }
        finally
        {
            if (coverWork == operation)
            {
                coverWork = null;
                coverWorkPath = null;
            }
            operation.Dispose();
        }
    }

    void RebuildSpreads()
    {
        spreads = book.MakeSpreads(!IsScrolling && state.Dual, state.SoloCover);
        SelectPosition(true);
    }

    void SelectPosition(bool resetScroll, bool? turnFromLeft = null, bool fromDrag = false)
    {
        spreadIndex = spreads.FindIndex(pages => Array.IndexOf(pages, position) >= 0);
        SyncControls();
        Refresh(resetScroll, turnFromLeft, fromDrag);
    }

    void GoTo(int newPosition)
    {
        Navigate(newPosition, null);
    }

    void Navigate(int newPosition, bool? turnFromLeft, bool fromDrag = false)
    {
        if (book == null) return;
        jumpTimer.Stop();
        newPosition = Math.Max(0, Math.Min(book.Count - 1, newPosition));
        if (position != newPosition)
        {
            preloadDirection = newPosition > position ? 1 : -1;
            position = newPosition;
            QueuePositionSave();
        }
        if (IsScrolling)
        {
            spreadIndex = position;
            SyncControls();
            continuous.JumpTo(position);
        }
        else if (Array.IndexOf(spreads[spreadIndex], newPosition) >= 0) SyncControls();
        else SelectPosition(true, turnFromLeft, fromDrag);
    }

    void TrackScrollPosition(int page)
    {
        if (!IsScrolling || IsShelfVisible || page == position) return;
        position = spreadIndex = page;
        SyncControls();
        QueuePositionSave();
    }

    void QueuePositionSave()
    {
        positionTimer.Stop();
        positionTimer.Start();
    }

    void FlushPosition()
    {
        if (!positionTimer.IsEnabled) return;
        positionTimer.Stop();
        if (book != null) state.SavePosition(book.Path, position);
    }

    void Turn(int step) { TurnWithMotion(step, false); }

    void TurnWithMotion(int step, bool fromDrag)
    {
        if (book == null) return;
        int next = spreadIndex + step;
        if (next < 0 || next >= spreads.Count) return;
        int now = Environment.TickCount;
        bool animate = fromDrag || (!IsScrolling && SystemParameters.ClientAreaAnimation &&
            (state.Motion == PageMotion.Slide || unchecked((uint)(now - lastTurnAt)) > 290) &&
            !pageTurn.IsPlaying && !slide.IsActive && rendering == null);
        lastTurnAt = now;
        Navigate(spreads[next][0], animate ? (bool?)(RightToLeft == (step > 0)) : null, fromDrag);
    }

    static string MotionLabel(PageMotion motion)
    {
        if (motion == PageMotion.Curl) return "仿真卷页";
        return motion == PageMotion.Scroll ? "上下滚动" : "左右滑动";
    }

    void OpenMotionMenu()
    {
        var button = Find<Button>("PageMotion");
        motionMenu.PlacementTarget = button.IsVisible ? (FrameworkElement)button : stage;
        motionMenu.Placement = button.IsVisible ? PlacementMode.Bottom : PlacementMode.MousePoint;
        motionMenu.IsOpen = true;
    }

    void SetPageMotion(PageMotion motion)
    {
        if (motion == state.Motion)
        {
            SyncControls();
            FocusReader();
            return;
        }
        bool reflow = IsScrolling != (motion == PageMotion.Scroll);
        FlushPosition();
        state.Motion = motion;
        pageTurn.Stop();
        CancelSlide();
        state.SaveSettings();
        if (reflow && book != null)
        {
            zoom = 1;
            wheelDelta = horizontalWheelDelta = 0;
            RebuildSpreads();
        }
        else SyncControls();
        FocusReader();
    }

    void ToggleLayout() { state.Dual = !state.Dual; state.SaveSettings(); RebuildSpreads(); }
    void ToggleCover() { state.SoloCover = !state.SoloCover; state.SaveSettings(); RebuildSpreads(); }

    void ToggleDirection()
    {
        if (bookDirection.HasValue) bookDirection = !bookDirection.Value;
        else
        {
            state.RightToLeft = !state.RightToLeft;
            state.SaveSettings();
        }
        SyncControls();
        Refresh(true);
    }

    void ToggleFit()
    {
        if (IsScrolling) state.ContinuousFitWidth = !state.ContinuousFitWidth;
        else state.FitWidth = !state.FitWidth;
        state.SaveSettings();
        zoom = 1;
        SyncControls();
        Refresh(true);
    }

    void ZoomBy(double multiplier)
    {
        zoom = Math.Max(.35, Math.Min(3, Math.Abs(zoom * multiplier - 1) < .001 ? 1 : zoom * multiplier));
        SyncControls();
        Refresh(false);
    }

    void ToggleFullscreen()
    {
        if (book == null) return;
        pageTurn.Stop();
        CancelSlide();
        fullscreen = !fullscreen;
        if (fullscreen)
        {
            oldWindowState = window.WindowState;
            window.WindowStyle = WindowStyle.None;
            window.ResizeMode = ResizeMode.NoResize;
            window.WindowState = WindowState.Maximized;
        }
        else
        {
            window.WindowStyle = WindowStyle.SingleBorderWindow;
            window.ResizeMode = ResizeMode.CanResize;
            window.WindowState = oldWindowState;
        }
        chrome.Hide();
        if (fullscreen) SetNotice("Esc 返回书架，F11 退出全屏");
        else SetNotice(null);
    }

    void SyncPageNumber()
    {
        pageNumber.Text = book == null ? "" : (position + 1).ToString();
    }

    void SyncControls()
    {
        Find<WrapPanel>("Options").IsEnabled = book != null;
        Find<Button>("PageMotion").ToolTip = "翻页方式 · " + MotionLabel(state.Motion) + "（S）";
        foreach (MenuItem item in motionMenu.Items)
            if (item.Tag is PageMotion) item.IsChecked = (PageMotion)item.Tag == state.Motion;
        Find<Button>("Layout").Content = state.Dual ? "双页" : "单页";
        Find<Button>("Layout").IsEnabled = !IsScrolling;
        Find<Button>("Direction").Content = RightToLeft ? "右到左" : "左到右";
        Find<Button>("Cover").Content = state.SoloCover ? "封面单页" : "封面配对";
        Find<Button>("Cover").IsEnabled = !IsScrolling && state.Dual;
        string fitLabel = FitWidth ? "适宽" : "适整页";
        Find<Button>("Fit").Content = zoom == 1 ? fitLabel : fitLabel + " · " + Math.Round(zoom * 100) + "%";
        Find<Button>("ZoomOut").IsEnabled = zoom > .35;
        Find<Button>("ZoomIn").IsEnabled = zoom < 3;
        if (book == null) return;
        Find<Button>("Previous").IsEnabled = spreadIndex > 0;
        Find<Button>("Next").IsEnabled = spreadIndex < spreads.Count - 1;
        pageNumber.IsEnabled = true;
        SyncPageNumber();
        Find<Button>("PageJump").Content = (position + 1) + " / " + book.Count;
        changingProgress = true;
        progress.Maximum = book.Count;
        progress.Value = position + 1;
        progress.IsEnabled = book.Count > 1;
        changingProgress = false;
    }

    async void Refresh(bool resetScroll, bool? turnFromLeft = null, bool fromDrag = false)
    {
        pageTurn.Stop();
        if (!fromDrag) CancelSlide();
        resetScrollOnShow |= resetScroll;
        if (book == null || stage.ActualWidth < 80 || stage.ActualHeight < 60) return;
        var previousRender = rendering;
        var previousPreload = preloading;
        double dpi = VisualTreeHelper.GetDpi(stage).DpiScaleX;
        if (IsScrolling)
        {
            if (previousRender != null) previousRender.Cancel();
            if (previousPreload != null) previousPreload.Cancel();
            viewport.Visibility = Visibility.Collapsed;
            continuous.ArrangeBook(book, stage.RenderSize, dpi, FitWidth, zoom, position, resetScrollOnShow);
            resetScrollOnShow = false;
            return;
        }
        if (continuous.Viewport.Visibility != Visibility.Collapsed) continuous.Stop();
        viewport.Visibility = Visibility.Visible;
        var operation = new CancellationTokenSource();
        rendering = operation;
        var token = operation.Token;
        var currentBook = book;
        PageMotion requestedMotion = state.Motion;
        int[] pages = spreads[spreadIndex];
        double scale = ScaleFor(currentBook, pages);
        try
        {
            var requests = new Task<BitmapSource>[pages.Length];
            for (int i = 0; i < pages.Length; i++)
                requests[i] = currentBook.RenderAsync(pages[i], currentBook.SizeOf(pages[i]).Width * scale * dpi, token);
            // 可见页先接手相同的预加载任务。旧请求取消时仍有阅读者的解码会继续。
            if (previousRender != null) previousRender.Cancel();
            if (previousPreload != null) previousPreload.Cancel();
            BitmapSource[] bitmaps = await Task.WhenAll(requests);
            token.ThrowIfCancellationRequested();

            var ready = new Border[pages.Length];
            for (int i = 0; i < pages.Length; i++)
            {
                Size dimensions = currentBook.SizeOf(pages[i]);
                var image = new Image { Source = bitmaps[i], Width = dimensions.Width * scale, Height = dimensions.Height * scale,
                    Stretch = Stretch.Fill, VerticalAlignment = VerticalAlignment.Top };
                int slot = RightToLeft ? pages.Length - 1 - i : i;
                ready[slot] = new Border { Child = image,
                    VerticalAlignment = VerticalAlignment.Top };
                ReaderAppearance.StylePage(ready[slot]);
            }

            // 整组图片准备后，在同一次界面更新中替换，避免绘制空白占位。
            var outgoing = turnFromLeft.HasValue && requestedMotion == state.Motion ?
                CaptureLeaves(requestedMotion == PageMotion.Curl) : null;
            spread.Children.Clear();
            foreach (var page in ready) spread.Children.Add(page);
            if (resetScrollOnShow)
            {
                resetScrollOnShow = false;
                viewport.ScrollToTop();
                if (outgoing != null || zoom > 1 || viewport.ScrollableWidth > 1) viewport.UpdateLayout();
                viewport.ScrollToHorizontalOffset(RightToLeft ? viewport.ScrollableWidth : 0);
            }
            var incoming = outgoing == null ? null : CaptureLeaves(requestedMotion == PageMotion.Curl);
            if (incoming == null)
            {
                if (fromDrag) slide.Stop();
            }
            else if (requestedMotion == PageMotion.Curl) pageTurn.Start(outgoing, incoming, turnFromLeft.Value);
            else slide.Finish(outgoing, incoming, turnFromLeft.Value, fromDrag);
            PreloadNeighbors(currentBook, dpi, pages, scale);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!token.IsCancellationRequested)
            {
                slide.Stop();
                SetNotice("无法显示当前页。\n" + error.Message);
            }
        }
        finally
        {
            if (rendering == operation) rendering = null;
            operation.Dispose();
        }
    }

    PageTurnEffect.Leaf[] CaptureLeaves(bool fitHeight)
    {
        if (spread.Children.Count == 0 || viewport.ScrollableWidth > 1 ||
            (fitHeight && viewport.ScrollableHeight > 1)) return null;
        return spread.Children.Cast<Border>().Select(border =>
        {
            var image = (Image)border.Child;
            var bounds = image.TransformToAncestor(stage).TransformBounds(new Rect(image.RenderSize));
            return new PageTurnEffect.Leaf(image.Source, bounds);
        }).ToArray();
    }

    double ScaleFor(ReaderBook currentBook, int[] pages)
    {
        double totalWidth = pages.Sum(p => currentBook.SizeOf(p).Width);
        double totalHeight = pages.Max(p => currentBook.SizeOf(p).Height);
        double widthScale = (stage.ActualWidth - 42 - (FitWidth || zoom > 1 ? SystemParameters.VerticalScrollBarWidth : 0)) / totalWidth;
        double heightScale = (stage.ActualHeight - 30) / totalHeight;
        return (FitWidth ? widthScale : Math.Min(widthScale, heightScale)) * zoom;
    }

    async void PreloadNeighbors(ReaderBook currentBook, double dpi, int[] currentPages, double scale)
    {
        var operation = new CancellationTokenSource();
        preloading = operation;
        var token = operation.Token;
        long reserved = currentPages.Sum(p => currentBook.EstimatedRenderBytes(p, currentBook.SizeOf(p).Width * scale * dpi));
        int currentIndex = spreadIndex;
        int direction = preloadDirection;
        try
        {
            foreach (int offset in new[] { direction, -direction, 2 * direction })
            {
                int index = currentIndex + offset;
                if (index < 0 || index >= spreads.Count) continue;
                int[] pages = spreads[index];
                double pageScale = ScaleFor(currentBook, pages);
                long estimated = pages.Sum(p => currentBook.EstimatedRenderBytes(p, currentBook.SizeOf(p).Width * pageScale * dpi));
                if (reserved + estimated > ReaderBook.CacheLimit) continue;
                reserved += estimated;
                foreach (int page in pages)
                {
                    await currentBook.RenderAsync(page, currentBook.SizeOf(page).Width * pageScale * dpi, token);
                    token.ThrowIfCancellationRequested();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            // 相邻页预加载失败时，留给实际翻到该页时处理。
            System.Diagnostics.Debug.WriteLine(error);
        }
        finally
        {
            if (preloading == operation) preloading = null;
            operation.Dispose();
        }
    }

    void PageNumberKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter && e.Key != Key.Escape) return;
        if (e.Key == Key.Enter)
        {
            int number;
            if (int.TryParse(pageNumber.Text, out number) && number >= 1 && number <= book.Count)
                GoTo(number - 1);
            else SetNotice("请输入 1 至 " + book.Count + " 之间的页码。");
        }
        SyncPageNumber();
        FocusReader();
        e.Handled = true;
    }

    void WindowKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ChooseFile();
            e.Handled = true;
            return;
        }
        if (IsShelfVisible)
        {
            if (book != null && e.Key == Key.B)
            {
                ReturnToBook();
                e.Handled = true;
            }
            return;
        }
        if (book == null || (pageNumber.IsKeyboardFocusWithin && e.Key != Key.F11)) return;
        switch (e.Key)
        {
            case Key.F11: ToggleFullscreen(); break;
            case Key.Escape:
                if (e.IsRepeat) return;
                goto case Key.B;
            case Key.Left: Turn(RightToLeft ? 1 : -1); break;
            case Key.Right: Turn(RightToLeft ? -1 : 1); break;
            case Key.PageDown: case Key.Space:
                if (IsScrolling) continuous.ScrollBy(continuous.Viewport.ViewportHeight * .88);
                else Turn(1);
                break;
            case Key.PageUp:
                if (IsScrolling) continuous.ScrollBy(-continuous.Viewport.ViewportHeight * .88);
                else Turn(-1);
                break;
            case Key.Down: case Key.Up:
                if (!IsScrolling) return;
                continuous.ScrollBy(e.Key == Key.Down ? 60 : -60);
                break;
            case Key.Home: GoTo(0); break;
            case Key.End: GoTo(book.Count - 1); break;
            case Key.D: if (!IsScrolling) ToggleLayout(); break;
            case Key.R: ToggleDirection(); break;
            case Key.C: if (!IsScrolling && state.Dual) ToggleCover(); break;
            case Key.W: ToggleFit(); break;
            case Key.B: ShowShelf(); break;
            case Key.S: OpenMotionMenu(); break;
            case Key.T:
                if (Keyboard.Modifiers != ModifierKeys.None) return;
                appearance.CycleTheme();
                break;
            case Key.Add: case Key.OemPlus: ZoomBy(1.2); break;
            case Key.Subtract: case Key.OemMinus: ZoomBy(1 / 1.2); break;
            default: return;
        }
        e.Handled = true;
    }

    void MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (book == null) return;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) ZoomBy(e.Delta > 0 ? 1.15 : 1 / 1.15);
        else if (IsScrolling) continuous.ScrollBy(-e.Delta * .8);
        else
        {
            if (viewport.ScrollableHeight > 1) return;
            if (Math.Sign(wheelDelta) != Math.Sign(e.Delta)) wheelDelta = 0;
            wheelDelta += e.Delta;
            if (Math.Abs(wheelDelta) >= 120)
            {
                Turn(wheelDelta < 0 ? 1 : -1);
                wheelDelta = 0;
            }
        }
        e.Handled = true;
    }

    void ClickStage(object sender, MouseButtonEventArgs e)
    {
        Point p = e.GetPosition(stage);
        if (FinishSlideGesture(p) || ClickReadingArea(p)) e.Handled = true;
    }

    bool InReadingArea(Point point)
    {
        var active = ActiveViewport;
        return book != null && !IsShelfVisible && point.X >= 0 && point.Y >= 0 &&
            point.Y < active.ViewportHeight && point.X < active.ViewportWidth;
    }

    bool ClickReadingArea(Point point)
    {
        if (!InReadingArea(point)) return false;
        double x = point.X;
        int physical = x < stage.ActualWidth * .22 ? -1 : x > stage.ActualWidth * .78 ? 1 : 0;
        if (physical == 0)
            chrome.Toggle();
        else Turn(RightToLeft ? -physical : physical);
        FocusReader();
        return true;
    }

    IntPtr HorizontalWheel(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int horizontalWheel = 0x020E;
        if (message != horizontalWheel || state.Motion != PageMotion.Slide ||
            viewport.ScrollableWidth > 1 || !viewport.IsVisible) return IntPtr.Zero;
        long coordinates = lParam.ToInt64();
        var cursor = stage.PointFromScreen(new Point(unchecked((short)coordinates), unchecked((short)(coordinates >> 16))));
        if (!InReadingArea(cursor)) return IntPtr.Zero;
        int delta = unchecked((short)(wParam.ToInt64() >> 16));
        if (Math.Sign(horizontalWheelDelta) != Math.Sign(delta)) horizontalWheelDelta = 0;
        horizontalWheelDelta += delta;
        if (Math.Abs(horizontalWheelDelta) >= 120)
        {
            Turn((horizontalWheelDelta > 0) == RightToLeft ? -1 : 1);
            horizontalWheelDelta = 0;
        }
        handled = true;
        return IntPtr.Zero;
    }

    bool StartSlideGesture(Point point)
    {
        if (!InReadingArea(point) || state.Motion != PageMotion.Slide || slide.IsActive ||
            rendering != null || pan || viewport.ScrollableWidth > 1) return false;
        slideOrigin = point;
        dragStarted = Environment.TickCount;
        return true;
    }

    bool MoveSlideGesture(Point point)
    {
        if (!slideOrigin.HasValue) return false;
        Vector movement = point - slideOrigin.Value;
        if (dragFrom == null)
        {
            if (Math.Abs(movement.X) < 9 || Math.Abs(movement.X) < Math.Abs(movement.Y) * 1.25) return false;
            dragFrom = CaptureLeaves(false);
            if (dragFrom == null) return false;
        }
        int step = (movement.X > 0) == RightToLeft ? 1 : -1;
        if (step != dragStep)
        {
            dragStep = step;
            dragTo = CachedLeaves(spreadIndex + step);
        }
        else if (dragTo == null) dragTo = CachedLeaves(spreadIndex + step);
        slide.Drag(dragFrom, dragTo, movement.X);
        return true;
    }

    PageTurnEffect.Leaf[] CachedLeaves(int index)
    {
        if (index < 0 || index >= spreads.Count) return null;
        int[] pages = spreads[index];
        double scale = ScaleFor(book, pages);
        double dpi = VisualTreeHelper.GetDpi(stage).DpiScaleX;
        double x = (viewport.ViewportWidth - pages.Sum(page => book.SizeOf(page).Width) * scale) / 2;
        double y = Math.Max(14, (viewport.ViewportHeight - pages.Max(page => book.SizeOf(page).Height) * scale) / 2);
        var leaves = new PageTurnEffect.Leaf[pages.Length];
        for (int i = 0; i < leaves.Length; i++)
        {
            int page = pages[RightToLeft ? pages.Length - 1 - i : i];
            Size size = book.SizeOf(page);
            double width = size.Width * scale;
            BitmapSource image;
            if (!book.TryGetCached(page, width * dpi, out image)) return null;
            leaves[i] = new PageTurnEffect.Leaf(image, new Rect(x, y, width, size.Height * scale));
            x += width;
        }
        return leaves;
    }

    bool FinishSlideGesture(Point point)
    {
        if (!slideOrigin.HasValue) return false;
        Vector movement = point - slideOrigin.Value;
        bool dragged = dragFrom != null && MoveSlideGesture(point);
        int step = dragStep;
        double distance = Math.Abs(movement.X);
        uint elapsed = Math.Max(1u, unchecked((uint)(Environment.TickCount - dragStarted)));
        bool turn = dragged && spreadIndex + step >= 0 && spreadIndex + step < spreads.Count &&
            (distance >= Math.Min(120, viewport.ViewportWidth * .16) || (distance >= 36 && distance / elapsed >= .55));
        ClearSlideGesture();
        if (stage.IsMouseCaptured && !pan) stage.ReleaseMouseCapture();
        if (turn) TurnWithMotion(step, true);
        else if (dragged) slide.SnapBack();
        return dragged || movement.Length > 12;
    }

    void ClearSlideGesture()
    {
        slideOrigin = null;
        dragFrom = dragTo = null;
        dragStep = 0;
    }

    void CancelSlide()
    {
        bool capturedDrag = dragFrom != null && stage.IsMouseCaptured && !pan;
        ClearSlideGesture();
        if (capturedDrag) stage.ReleaseMouseCapture();
        var touch = slideTouch;
        slideTouch = null;
        if (touch != null) touch.Capture(null);
        slide.Stop();
    }

    void StartTouchSlide(object sender, TouchEventArgs e)
    {
        if (slideTouch != null || viewport.ScrollableHeight > 1 || !StartSlideGesture(e.GetTouchPoint(stage).Position)) return;
        if (!e.TouchDevice.Capture(stage)) { ClearSlideGesture(); return; }
        slideTouch = e.TouchDevice;
        e.Handled = true;
    }

    void MoveTouchSlide(object sender, TouchEventArgs e)
    {
        if (slideTouch != e.TouchDevice) return;
        MoveSlideGesture(e.GetTouchPoint(stage).Position);
        e.Handled = true;
    }

    void FinishTouchSlide(object sender, TouchEventArgs e)
    {
        if (slideTouch != e.TouchDevice) return;
        Point point = e.GetTouchPoint(stage).Position;
        slideTouch = null;
        e.TouchDevice.Capture(null);
        if (!FinishSlideGesture(point)) ClickReadingArea(point);
        e.Handled = true;
    }

    void StartPan(object sender, MouseButtonEventArgs e)
    {
        var active = ActiveViewport;
        if (book == null || e.ChangedButton != MouseButton.Middle ||
            (active.ScrollableHeight <= 1 && active.ScrollableWidth <= 1)) return;
        continuous.EndWheelScroll();
        panStart = e.GetPosition(stage);
        panOffsets = new Point(active.HorizontalOffset, active.VerticalOffset);
        pan = stage.CaptureMouse();
        if (pan) stage.Cursor = Cursors.ScrollAll;
        e.Handled = pan;
    }

    void MovePan(object sender, MouseEventArgs e)
    {
        if (!pan && slideTouch == null && e.LeftButton == MouseButtonState.Pressed && MoveSlideGesture(e.GetPosition(stage)))
        {
            if (!stage.IsMouseCaptured) stage.CaptureMouse();
            e.Handled = true;
            return;
        }
        if (!pan) return;
        var delta = panStart - e.GetPosition(stage);
        ActiveViewport.ScrollToHorizontalOffset(panOffsets.X + delta.X);
        ActiveViewport.ScrollToVerticalOffset(panOffsets.Y + delta.Y);
        e.Handled = true;
    }

    void EndPan(object sender, MouseButtonEventArgs e)
    {
        if (!pan || e.ChangedButton != MouseButton.Middle) return;
        stage.ReleaseMouseCapture();
        e.Handled = true;
    }
}
