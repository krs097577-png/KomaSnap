using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

enum PageMotion { Slide, Curl, Scroll }

sealed class PageSlideEffect : FrameworkElement
{
    const double PageGap = 24;
    readonly Stopwatch clock = new Stopwatch();
    readonly Brush background;
    readonly DrawingVisual backdrop = new DrawingVisual();
    readonly DrawingVisual outgoingVisual = new DrawingVisual();
    readonly DrawingVisual incomingVisual = new DrawingVisual();
    readonly TranslateTransform outgoingOffset = new TranslateTransform();
    readonly TranslateTransform incomingOffset = new TranslateTransform();
    readonly VisualCollection layers;
    PageTurnEffect.Leaf[] from;
    PageTurnEffect.Leaf[] to;
    double distance, offset, start, end, duration;
    int direction;

    public PageSlideEffect(Brush background)
    {
        this.background = background;
        IsHitTestVisible = false;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.Linear);
        backdrop.Opacity = 0;
        incomingVisual.Transform = incomingOffset;
        outgoingVisual.Transform = outgoingOffset;
        layers = new VisualCollection(this) { backdrop, incomingVisual, outgoingVisual };
    }

    public bool IsActive { get { return from != null; } }

    public Effect PaperEffect
    {
        set { outgoingVisual.Effect = incomingVisual.Effect = value; }
    }

    public void Drag(PageTurnEffect.Leaf[] outgoing, PageTurnEffect.Leaf[] incoming, double delta)
    {
        SetScene(outgoing, incoming, delta > 0);
        offset = Math.Min(distance * .97, Math.Abs(delta) * (incoming == null ? .24 : 1));
        MoveLayers();
    }

    public void Finish(PageTurnEffect.Leaf[] outgoing, PageTurnEffect.Leaf[] incoming, bool fromLeft, bool useDrag)
    {
        double pulled = useDrag && direction == (fromLeft ? 1 : -1) ? offset : 0;
        SetScene(outgoing, incoming, fromLeft);
        offset = Math.Min(pulled, distance);
        AnimateTo(distance, useDrag ? 175 : 245);
    }

    public void SnapBack()
    {
        if (IsActive) AnimateTo(0, 160);
    }

    public void Stop()
    {
        if (clock.IsRunning)
        {
            CompositionTarget.Rendering -= NextFrame;
            clock.Reset();
        }
        if (!IsActive) return;
        from = to = null;
        offset = 0;
        backdrop.Opacity = 0;
        DrawPages(outgoingVisual, null);
        DrawPages(incomingVisual, null);
    }

    void SetScene(PageTurnEffect.Leaf[] outgoing, PageTurnEffect.Leaf[] incoming, bool fromLeft)
    {
        int nextDirection = fromLeft ? 1 : -1;
        if (from == outgoing && to == incoming && direction == nextDirection) return;
        if (from != outgoing) DrawPages(outgoingVisual, outgoing);
        if (to != incoming) DrawPages(incomingVisual, incoming);
        backdrop.Opacity = 1;
        from = outgoing;
        to = incoming;
        direction = nextDirection;
        if (incoming == null) distance = Math.Max(140, ActualWidth * .5);
        else
        {
            Rect oldBounds = BoundsOf(outgoing);
            Rect newBounds = BoundsOf(incoming);
            distance = Math.Max(1, PageGap + (fromLeft ? newBounds.Right - oldBounds.Left : oldBounds.Right - newBounds.Left));
        }
    }

    void AnimateTo(double target, double milliseconds)
    {
        start = offset;
        end = target;
        duration = milliseconds;
        if (!clock.IsRunning) CompositionTarget.Rendering += NextFrame;
        clock.Restart();
        MoveLayers();
    }

    void NextFrame(object sender, EventArgs e)
    {
        double time = clock.Elapsed.TotalMilliseconds / duration;
        if (time >= 1) { Stop(); return; }
        double remaining = 1 - time;
        offset = end + (start - end) * remaining * remaining * remaining;
        MoveLayers();
    }

    protected override int VisualChildrenCount { get { return layers == null ? 0 : layers.Count; } }
    protected override Visual GetVisualChild(int index) { return layers[index]; }

    protected override void OnRenderSizeChanged(SizeChangedInfo change)
    {
        base.OnRenderSizeChanged(change);
        using (var drawing = backdrop.RenderOpen())
            drawing.DrawRectangle(background, null, new Rect(change.NewSize));
    }

    void MoveLayers()
    {
        // 位图只在换页时提交，动画帧只更新合成层的位置和透明度。
        double fraction = offset / distance;
        incomingOffset.X = (offset - distance) * direction;
        outgoingOffset.X = offset * direction;
        incomingVisual.Opacity = to == null ? 0 : Math.Min(1, fraction * 7);
        outgoingVisual.Opacity = to == null ? 1 : Math.Min(1, (1 - fraction) * 6);
    }

    static void DrawPages(DrawingVisual visual, PageTurnEffect.Leaf[] pages)
    {
        using (var drawing = visual.RenderOpen())
        {
            if (pages == null) return;
            foreach (var page in pages)
            {
                drawing.DrawRectangle(Brushes.White, null, page.Bounds);
                drawing.DrawImage(page.Source, page.Bounds);
            }
        }
    }

    static Rect BoundsOf(PageTurnEffect.Leaf[] pages)
    {
        Rect bounds = pages[0].Bounds;
        for (int i = 1; i < pages.Length; i++) bounds.Union(pages[i].Bounds);
        return bounds;
    }
}

