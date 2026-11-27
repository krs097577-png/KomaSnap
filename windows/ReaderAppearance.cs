using System;
using System.Collections;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

sealed class ReaderAppearance
{
    const string PreferencePath = @"Software\KomaSnap\Appearance";
    const string PreferenceName = "Theme";
    const string DefaultTheme = "Paper";
    const int TransitionMs = 200;
    static readonly string[] Names = { "Light", "Paper", "EyeCare", "Mint", "Dark", "Black" };
    static readonly string[] Labels = { "纯白原色", "实体单行本", "牛皮暖纸", "薄荷护眼", "柔光暗夜", "极夜纯黑" };
    readonly bool useRegistry;
    readonly Window window;
    readonly ResourceDictionary palettes;
    readonly ResourceDictionary brushes = new ResourceDictionary();
    readonly SolidColorBrush canvasPaint = new SolidColorBrush();
    readonly ContextMenu menu;
    readonly Button shelfTheme, readerTheme;
    readonly Stopwatch captionClock = new Stopwatch();
    IntPtr captionHandle;
    uint? lastCaption, lastCaptionText;
    public string Current { get; private set; }
    public Effect PageEffect { get; private set; }
    public event Action<Effect> PageEffectChanged;
    public ContextMenu ThemeMenu { get { return menu; } }

    public ReaderAppearance(Window window, bool useRegistry)
    {
        this.useRegistry = useRegistry;
        this.window = window;
        using (var file = Assembly.GetExecutingAssembly().GetManifestResourceStream("ThemeTokens.xaml"))
            palettes = (ResourceDictionary)XamlReader.Load(file);
        window.Resources.MergedDictionaries.Add(brushes);
        // 独立的弹出窗口使用同一组可变主题资源。
        Application.Current.Resources.MergedDictionaries.Add(brushes);
        window.SourceInitialized += (s, e) =>
        {
            if (SystemParameters.HighContrast) return;
            captionHandle = new WindowInteropHelper(window).Handle;
            if (!UpdateCaption()) captionHandle = IntPtr.Zero;
        };
        window.Closed += (s, e) =>
        {
            StopCaptionTransition();
            Application.Current.Resources.MergedDictionaries.Remove(brushes);
        };
        menu = NewMenu(window);
        EnableOptionKeys(menu);
        shelfTheme = (Button)window.FindName("ThemeButton");
        readerTheme = (Button)window.FindName("ReaderThemeButton");
        for (int i = 0; i < Names.Length; i++)
        {
            string theme = Names[i];
            var item = NewMenuItem(window, Labels[i], null);
            var preview = (ResourceDictionary)palettes[theme];
            item.Icon = new Ellipse { Width = 12, Height = 12, StrokeThickness = 1.2,
                HorizontalAlignment = HorizontalAlignment.Left,
                Fill = new SolidColorBrush((Color)preview["PreviewFill"]),
                Stroke = new SolidColorBrush((Color)preview["PreviewStroke"]) };
            item.Tag = theme;
            item.IsCheckable = true;
            item.Click += (s, e) => SelectTheme(theme);
            menu.Items.Add(item);
        }
        foreach (var button in new[] { shelfTheme, readerTheme })
        {
            button.ContextMenu = menu;
            button.Click += (s, e) =>
            {
                menu.PlacementTarget = (Button)s;
                menu.Placement = PlacementMode.Bottom;
                menu.IsOpen = true;
            };
        }
        Apply(useRegistry ? ReadPreference() : DefaultTheme, false);
        ((Grid)window.FindName("Stage")).Background = canvasPaint;
    }

    public void CycleTheme()
    {
        SelectTheme(Names[(Array.IndexOf(Names, Current) + 1) % Names.Length]);
    }

    void SelectTheme(string theme)
    {
        Apply(theme, true);
        if (useRegistry) WritePreference(theme);
    }

    public void Apply(string name, bool transition)
    {
        if (Array.IndexOf(Names, name) < 0) name = DefaultTheme;
        Current = name;
        var palette = (ResourceDictionary)palettes[name];
        bool animated = transition && SystemParameters.ClientAreaAnimation;
        foreach (DictionaryEntry item in palette)
        {
            string key = (string)item.Key;
            if (key == "Shadow" || key.StartsWith("Preview", StringComparison.Ordinal)
                || key == "PageMultiply" || key == "PageBrightness") continue;
            var color = (Color)item.Value;
            var before = brushes[key] as SolidColorBrush;
            var paint = new SolidColorBrush(color);
            if (animated && key != "Paper" && before != null && before.Color != color)
                Fade(paint, before.Color, color);
            brushes[key] = paint;
            if (key == "Canvas")
            {
                // 翻页遮罩持有这支画刷，与阅读画布同步更新。
                Color old = canvasPaint.Color;
                canvasPaint.BeginAnimation(SolidColorBrush.ColorProperty, null);
                canvasPaint.Color = color;
                if (animated && old != color) Fade(canvasPaint, old, color);
            }
        }
        PageEffect = ReaderColorEffect.FromPalette((Color)palette["PageMultiply"], (double)palette["PageBrightness"]);
        brushes["PageColorEffect"] = PageEffect;
        if (PageEffectChanged != null) PageEffectChanged(PageEffect);
        var shadow = (Color)palette["Shadow"];
        brushes["PopupShadow"] = new DropShadowEffect { BlurRadius = 13, ShadowDepth = 2, Direction = 270,
            Color = Color.FromRgb(shadow.R, shadow.G, shadow.B), Opacity = shadow.A / 255.0 };
        foreach (MenuItem item in menu.Items) item.IsChecked = (string)item.Tag == name;
        string label = "当前主题 · " + Labels[Array.IndexOf(Names, name)];
        shelfTheme.ToolTip = label;
        readerTheme.ToolTip = label + "（T）";
        StopCaptionTransition();
        if (captionHandle != IntPtr.Zero)
        {
            UpdateCaption();
            if (animated && window.IsVisible)
            {
                captionClock.Start();
                CompositionTarget.Rendering += UpdateCaptionFrame;
            }
        }
    }

    static void Fade(SolidColorBrush brush, Color from, Color to)
    {
        brush.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(from, to, TimeSpan.FromMilliseconds(TransitionMs))
            {
                FillBehavior = FillBehavior.Stop
            });
    }

    void UpdateCaptionFrame(object sender, EventArgs e)
    {
        UpdateCaption();
        if (captionClock.ElapsedMilliseconds > TransitionMs + 50) StopCaptionTransition();
    }

    void StopCaptionTransition()
    {
        CompositionTarget.Rendering -= UpdateCaptionFrame;
        captionClock.Reset();
    }

    bool UpdateCaption()
    {
        // Windows 11 支持系统标题栏颜色。旧系统保留系统标题栏样式。
        uint caption = ColorRef(((SolidColorBrush)brushes["Surface"]).Color);
        uint text = ColorRef(((SolidColorBrush)brushes["TextPrimary"]).Color);
        if (lastCaption != caption)
        {
            if (DwmSetWindowAttribute(captionHandle, 35, ref caption, sizeof(uint)) != 0) return false;
            lastCaption = caption;
        }
        if (lastCaptionText != text)
        {
            DwmSetWindowAttribute(captionHandle, 36, ref text, sizeof(uint));
            lastCaptionText = text;
        }
        return true;
    }

    static uint ColorRef(Color color)
    {
        return (uint)(color.R | color.G << 8 | color.B << 16);
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref uint value, int size);

    static string ReadPreference()
    {
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(PreferencePath))
                return key == null ? DefaultTheme : key.GetValue(PreferenceName, DefaultTheme) as string;
        }
        catch (UnauthorizedAccessException) { return DefaultTheme; }
        catch (System.Security.SecurityException) { return DefaultTheme; }
        catch (System.IO.IOException) { return DefaultTheme; }
    }

    static void WritePreference(string theme)
    {
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(PreferencePath))
                if (key != null) key.SetValue(PreferenceName, theme, RegistryValueKind.String);
        }
        catch (UnauthorizedAccessException) { }
        catch (System.Security.SecurityException) { }
        catch (System.IO.IOException) { }
    }

    public static ContextMenu NewMenu(Window window)
    {
        return new ContextMenu { Style = (Style)window.FindResource("ReaderMenu") };
    }

    public static MenuItem NewMenuItem(Window window, string label, string glyph)
    {
        var item = new MenuItem { Header = label, Style = (Style)window.FindResource("ReaderMenuItem") };
        if (glyph != null)
        {
            var icon = new Path { Width = 15, Height = 15, StrokeThickness = 1.4, Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Left, Data = Geometry.Parse(glyph) };
            icon.SetResourceReference(Shape.StrokeProperty, "TextSecondary");
            item.Icon = icon;
        }
        return item;
    }

    public static void EnableOptionKeys(ContextMenu menu)
    {
        menu.Opened += (s, e) => menu.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!menu.IsOpen) return;
            var checkedItem = menu.Items.OfType<MenuItem>().FirstOrDefault(item => item.IsChecked);
            if (checkedItem != null) checkedItem.Focus();
        }));
        menu.PreviewKeyDown += (s, e) =>
        {
            if (e.Key == Key.Escape) { menu.IsOpen = false; e.Handled = true; return; }
            if (e.Key != Key.Up && e.Key != Key.Down && e.Key != Key.Left && e.Key != Key.Right && e.Key != Key.Enter) return;
            var options = menu.Items.OfType<MenuItem>().Where(item => item.IsCheckable && item.IsEnabled).ToArray();
            if (options.Length == 0) return;
            var focus = menu.Items.OfType<MenuItem>().FirstOrDefault(item => item.IsKeyboardFocusWithin);
            if (e.Key == Key.Enter)
            {
                if (focus == null) return;
                focus.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                menu.IsOpen = false;
            }
            else
            {
                int from = Array.IndexOf(options, focus);
                int delta = e.Key == Key.Up || e.Key == Key.Left ? -1 : 1;
                int next = from < 0 ? (delta < 0 ? options.Length - 1 : 0) : (from + delta + options.Length) % options.Length;
                options[next].Focus();
            }
            e.Handled = true;
        };
    }

    public static void StylePage(Border frame)
    {
        frame.SetResourceReference(Border.BackgroundProperty, "Paper");
        frame.SetResourceReference(UIElement.EffectProperty, "PageColorEffect");
    }
}

sealed class ReadingChrome
{
    readonly Window window;
    readonly Border header, footer;
    readonly Popup jump, bubble;
    readonly Slider slider;
    readonly TextBlock bubbleText;
    readonly ContextMenu[] menus;
    readonly DispatcherTimer idle = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
    bool shown, dragging;

    public ReadingChrome(Window window, Border header, Border footer, Slider slider, TextBox pageNumber, params ContextMenu[] menus)
    {
        this.window = window;
        this.header = header;
        this.footer = footer;
        this.slider = slider;
        this.menus = menus;
        jump = (Popup)window.FindName("JumpPopup");
        var jumpButton = (Button)window.FindName("PageJump");
        jumpButton.Click += (s, e) =>
        {
            jump.IsOpen = true;
            window.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (jump.IsOpen) { pageNumber.Focus(); pageNumber.SelectAll(); }
            }));
        };
        pageNumber.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler((s, e) =>
        {
            if (e.Key == Key.Enter || e.Key == Key.Escape) jump.IsOpen = false;
        }), true);

        bubbleText = new TextBlock();
        bubbleText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        var tip = new Border { Style = (Style)window.FindResource("PopupPanel"), Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(7), Child = bubbleText };
        bubble = new Popup { Placement = PlacementMode.Top, AllowsTransparency = true, StaysOpen = true,
            IsHitTestVisible = false, VerticalOffset = -3, Child = tip };
        slider.MouseEnter += (s, e) => UpdateBubble();
        slider.MouseLeave += (s, e) => UpdateBubble();
        slider.ValueChanged += (s, e) => UpdateBubble();
        slider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((s, e) =>
        {
            dragging = true; UpdateBubble(); Pulse();
        }));
        slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((s, e) =>
        {
            dragging = false; UpdateBubble(); Pulse();
        }), true);
        slider.IsVisibleChanged += (s, e) => UpdateBubble();

        idle.Tick += (s, e) => { idle.Stop(); if (shown && !Paused) SetVisible(false, true); };
        window.AddHandler(UIElement.PreviewMouseMoveEvent, new MouseEventHandler((s, e) => Pulse()), true);
        window.AddHandler(UIElement.PreviewMouseDownEvent, new MouseButtonEventHandler((s, e) => Pulse()), true);
        window.AddHandler(UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler((s, e) => Pulse()), true);
        window.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler((s, e) => Pulse()), true);
        window.Activated += (s, e) => Pulse();
        window.Deactivated += (s, e) => Pulse();
        window.IsEnabledChanged += (s, e) => Pulse();
        foreach (var bar in new[] { header, footer })
        {
            bar.MouseEnter += (s, e) => Pulse();
            bar.MouseLeave += (s, e) => Pulse();
        }
        jump.Opened += (s, e) => Pulse();
        jump.Closed += (s, e) => Pulse();
        foreach (var menu in menus)
        {
            menu.Opened += (s, e) => Pulse();
            menu.Closed += (s, e) => Pulse();
        }
        window.Closed += (s, e) => SetVisible(false, false);
    }

    bool Paused { get { return !window.IsActive || !window.IsEnabled || header.IsMouseOver || footer.IsMouseOver ||
        dragging || jump.IsOpen || menus.Any(menu => menu.IsOpen); } }

    void Pulse()
    {
        idle.Stop();
        if (shown && !Paused) idle.Start();
    }

    public void Toggle() { SetVisible(!shown, true); }
    public void Hide() { SetVisible(false, false); }

    void SetVisible(bool visible, bool animated)
    {
        shown = visible;
        if (!visible) { jump.IsOpen = false; bubble.IsOpen = false; }
        foreach (var bar in new[] { header, footer })
        {
            double old = bar.Visibility == Visibility.Visible ? bar.Opacity : 0;
            bar.BeginAnimation(UIElement.OpacityProperty, null);
            bar.IsHitTestVisible = visible;
            bar.Opacity = visible ? 1 : 0;
            if (visible) bar.Visibility = Visibility.Visible;
            if (animated && SystemParameters.ClientAreaAnimation && old != bar.Opacity)
            {
                var fade = new DoubleAnimation(old, bar.Opacity, TimeSpan.FromMilliseconds(175)) { FillBehavior = FillBehavior.Stop };
                fade.Completed += (s, e) => { if (!shown) bar.Visibility = Visibility.Collapsed; };
                bar.BeginAnimation(UIElement.OpacityProperty, fade);
            }
            else if (!visible) bar.Visibility = Visibility.Collapsed;
        }
        if (!visible && (header.IsKeyboardFocusWithin || footer.IsKeyboardFocusWithin)) Keyboard.Focus(window);
        Pulse();
    }

    void UpdateBubble()
    {
        if (!shown || !slider.IsVisible || !slider.IsEnabled || !(dragging || slider.IsMouseOver))
        {
            bubble.IsOpen = false;
            return;
        }
        var track = slider.Template.FindName("PART_Track", slider) as Track;
        if (track == null) return;
        bubbleText.Text = ((int)slider.Value) + " / " + ((int)slider.Maximum);
        bubble.PlacementTarget = track.Thumb;
        bool wasOpen = bubble.IsOpen;
        bubble.IsOpen = true;
        if (wasOpen)
        {
            // 让 Popup 随移动中的滑块重新计算位置。
            bubble.HorizontalOffset = bubble.HorizontalOffset == 0 ? .01 : 0;
        }
    }
}

