using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

sealed class ContinuousReader
{
    const double Gap = 14;
    const double Inset = 20;
    readonly Canvas canvas;
    readonly Dictionary<int, PageSlot> slots = new Dictionary<int, PageSlot>();
    readonly Stopwatch wheelClock = new Stopwatch();
    ReaderBook book;
    Rect[] pages;
    double dpi;
    bool arranging;
    int visibleFirst = -1, visibleLast = -1;
    double wheelPosition, wheelTarget, lastFrameTime;

    sealed class PageSlot
    {
        public Border Frame;
        public Image Image;
        public int RequestedWidth;
        public CancellationTokenSource Loading;
    }

    public ContinuousReader(ScrollViewer viewport, Canvas canvas)
    {
        Viewport = viewport;
        this.canvas = canvas;
        viewport.ScrollChanged += OnScrollChanged;
        viewport.PreviewMouseDown += (s, e) => EndWheelScroll();
        viewport.PreviewTouchDown += (s, e) => EndWheelScroll();
    }

    public ScrollViewer Viewport { get; private set; }
    public event Action<int> PositionChanged;

    public void ArrangeBook(ReaderBook current, Size available, double scaleDpi, bool fitWidth, double zoom,
        int position, bool startAtTop)
    {
        double relativeTop = 0;
        if (book == current && !startAtTop)
            relativeTop = Math.Max(0, (Viewport.VerticalOffset + Gap - pages[position].Top) / pages[position].Height);
        if (book != current) Stop();
        EndWheelScroll();

        arranging = true;
        try
        {
            book = current;
            dpi = scaleDpi;
            double usableWidth = Math.Max(1, available.Width - Inset * 2 - SystemParameters.VerticalScrollBarWidth);
            double usableHeight = Math.Max(1, available.Height - Gap * 2);
            double widest = usableWidth;
            double top = Gap;
            var layout = new Rect[current.Count];
            for (int i = 0; i < layout.Length; i++)
            {
                Size page = current.SizeOf(i);
                double scale = usableWidth / page.Width;
                if (!fitWidth) scale = Math.Min(scale, usableHeight / page.Height);
                double width = page.Width * scale * zoom;
                double height = page.Height * scale * zoom;
                layout[i] = new Rect(0, top, width, height);
                widest = Math.Max(width, widest);
                top += height + Gap;
            }
            for (int i = 0; i < layout.Length; i++) layout[i].X = Inset + (widest - layout[i].Width) / 2;
            pages = layout;
            visibleFirst = visibleLast = -1;
            canvas.Width = widest + Inset * 2;
            // 给较短的最后一页留出空间，使跳转到末页时也能对齐上沿。
            canvas.Height = Math.Max(top, pages[pages.Length - 1].Top - Gap + available.Height);
            Viewport.Visibility = Visibility.Visible;
            Viewport.UpdateLayout();
            double target = pages[position].Top - Gap + Math.Min(relativeTop, .99) * pages[position].Height;
            Viewport.ScrollToVerticalOffset(target);
            Viewport.UpdateLayout();
        }
        finally { arranging = false; }
        UpdatePages();
    }

    public void Stop()
    {
        EndWheelScroll();
        book = null;
        visibleFirst = visibleLast = -1;
        foreach (var slot in slots.Values)
            if (slot.Loading != null) slot.Loading.Cancel();
        slots.Clear();
        canvas.Children.Clear();
        Viewport.Visibility = Visibility.Collapsed;
    }

    public void JumpTo(int page)
    {
        EndWheelScroll();
        if (book != null) Viewport.ScrollToVerticalOffset(pages[page].Top - Gap);
    }

    public void ScrollBy(double distance)
    {
        if (book == null) return;
        if (!SystemParameters.ClientAreaAnimation)
        {
            EndWheelScroll();
            Viewport.ScrollToVerticalOffset(Viewport.VerticalOffset + distance);
            return;
        }
        double previousTarget = wheelClock.IsRunning ? wheelTarget : Viewport.VerticalOffset;
        wheelTarget = Math.Max(0, Math.Min(Viewport.ScrollableHeight, previousTarget + distance));
        if (wheelClock.IsRunning || wheelTarget == previousTarget) return;
        wheelPosition = previousTarget;
        lastFrameTime = 0;
        wheelClock.Start();
        CompositionTarget.Rendering += WheelFrame;
    }

    public void EndWheelScroll()
    {
        if (!wheelClock.IsRunning) return;
        CompositionTarget.Rendering -= WheelFrame;
        wheelClock.Reset();
    }

    void WheelFrame(object sender, EventArgs e)
    {
        double now = wheelClock.Elapsed.TotalMilliseconds;
        double elapsed = now - lastFrameTime;
        if (elapsed < .5) return;
        lastFrameTime = now;
        double remaining = wheelTarget - wheelPosition;
        if (Math.Abs(remaining) < .7)
        {
            wheelPosition = wheelTarget;
            EndWheelScroll();
        }
        else wheelPosition += remaining * (1 - Math.Exp(-elapsed / 32));
        Viewport.ScrollToVerticalOffset(wheelPosition);
    }

    void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (book == null || arranging) return;
        UpdatePages();
        if (e.VerticalChange != 0 && PositionChanged != null)
            PositionChanged(PageAt(Viewport.VerticalOffset + Gap + .5));
    }

    int PageAt(double offset)
    {
        int low = 0, high = pages.Length - 1;
        while (low < high)
        {
            int middle = low + (high - low + 1) / 2;
            if (pages[middle].Top <= offset) low = middle;
            else high = middle - 1;
        }
        return low;
    }

    void UpdatePages()
    {
        double height = Viewport.ViewportHeight;
        if (height <= 0) return;
        double offset = Viewport.VerticalOffset;
        int first = PageAt(offset);
        int last = PageAt(offset + height);
        if (visibleFirst == first && visibleLast == last) return;
        visibleFirst = first;
        visibleLast = last;
        int keepFrom = Math.Max(0, first - 1);
        int keepThrough = Math.Min(pages.Length - 1, last + 2);

        foreach (int page in slots.Keys.Where(page => page < keepFrom || page > keepThrough).ToArray())
        {
            var slot = slots[page];
            if (slot.Loading != null) slot.Loading.Cancel();
            canvas.Children.Remove(slot.Frame);
            slots.Remove(page);
        }
        for (int page = first; page <= last; page++) PlacePage(page);
        for (int page = last + 1; page <= keepThrough; page++) PlacePage(page);
        for (int page = first - 1; page >= keepFrom; page--) PlacePage(page);
    }

    void PlacePage(int page)
    {
        PageSlot slot;
        if (!slots.TryGetValue(page, out slot))
        {
            var image = new Image { Stretch = Stretch.Fill };
            var frame = new Border { Child = image };
            frame.SetResourceReference(Border.BackgroundProperty, "PaperPending");
            slot = new PageSlot { Frame = frame, Image = image };
            slots.Add(page, slot);
            canvas.Children.Add(frame);
        }
        Rect bounds = pages[page];
        Canvas.SetLeft(slot.Frame, bounds.Left);
        Canvas.SetTop(slot.Frame, bounds.Top);
        slot.Frame.Width = bounds.Width;
        slot.Frame.Height = bounds.Height;
        int pixels = Math.Max(1, (int)Math.Ceiling(bounds.Width * dpi));
        if (slot.RequestedWidth != pixels) Render(book, page, pixels, slot);
    }

    async void Render(ReaderBook owner, int page, int pixels, PageSlot slot)
    {
        if (slot.Loading != null) slot.Loading.Cancel();
        slot.RequestedWidth = pixels;
        var operation = new CancellationTokenSource();
        slot.Loading = operation;
        var token = operation.Token;
        try
        {
            var image = await owner.RenderAsync(page, pixels, token);
            token.ThrowIfCancellationRequested();
            slot.Image.Source = image;
            ReaderAppearance.StylePage(slot.Frame);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!token.IsCancellationRequested)
            {
                slot.Frame.SetResourceReference(Border.BackgroundProperty, "Cover");
                slot.Frame.ToolTip = "无法显示第 " + (page + 1) + " 页。\n" + error.Message;
            }
        }
        finally
        {
            if (slot.Loading == operation) slot.Loading = null;
            operation.Dispose();
        }
    }
}
