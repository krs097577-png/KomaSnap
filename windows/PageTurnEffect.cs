using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

sealed class PageTurnEffect : FrameworkElement
{
    public sealed class Leaf
    {
        public readonly ImageSource Source;
        public readonly Rect Bounds;

        public Leaf(ImageSource source, Rect bounds)
        {
            Source = source;
            Bounds = bounds;
        }
    }

    const double Duration = 320;
    static readonly LinearGradientBrush[] Shadows = Gradients(
        StopAt(0, 0, 0, 0, 0), StopAt(.55, 96, 0, 0, 0), StopAt(.87, 57, 0, 0, 0), StopAt(1, 0, 0, 0, 0));
    static readonly LinearGradientBrush[] Shading = Gradients(
        StopAt(0, 135, 18, 14, 10), StopAt(.20, 90, 31, 25, 20), StopAt(.43, 65, 255, 255, 250),
        StopAt(.65, 36, 255, 248, 230), StopAt(.86, 43, 48, 37, 27), StopAt(1, 110, 40, 30, 20));
    static readonly Pen FoldEdge = MakeFoldEdge();
    readonly Stopwatch clock = new Stopwatch();
    readonly Brush matte;
    Leaf front;
    Leaf facing;
    Leaf back;
    Geometry newMargins;
    bool fromLeft;
    double progress;

    public PageTurnEffect(Brush matte)
    {
        this.matte = matte;
        IsHitTestVisible = false;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.Linear);
    }

    public bool IsPlaying { get { return clock.IsRunning; } }

    public void Start(Leaf[] outgoing, Leaf[] incoming, bool fromLeft)
    {
        Stop();
        this.fromLeft = fromLeft;
        front = outgoing[fromLeft ? 0 : outgoing.Length - 1];
        facing = outgoing.Length > 1 ? outgoing[fromLeft ? outgoing.Length - 1 : 0] : null;
        back = incoming[fromLeft ? incoming.Length - 1 : 0];
        Rect newBounds = incoming[0].Bounds;
        foreach (Leaf leaf in incoming) newBounds.Union(leaf.Bounds);
        Geometry oldPaper = new RectangleGeometry(front.Bounds);
        if (facing != null)
            oldPaper = new CombinedGeometry(GeometryCombineMode.Union, oldPaper, new RectangleGeometry(facing.Bounds));
        newMargins = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(newBounds), oldPaper);
        newMargins.Freeze();
        progress = 0;
        clock.Restart();
        CompositionTarget.Rendering += NextFrame;
        InvalidateVisual();
    }

    public void Stop()
    {
        if (!IsPlaying) return;
        CompositionTarget.Rendering -= NextFrame;
        clock.Reset();
        front = facing = back = null;
        newMargins = null;
        InvalidateVisual();
    }

    void NextFrame(object sender, EventArgs e)
    {
        progress = clock.Elapsed.TotalMilliseconds / Duration;
        if (progress >= 1) Stop();
        else InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawing)
    {
        if (front == null) return;

        double t = progress * progress * (3 - 2 * progress);
        double width = front.Bounds.Width;
        double height = front.Bounds.Height;
        double bend = Math.Sin(t * Math.PI);
        double top = width * (t - .12 * bend);
        double bottom = width * Math.Min(1, t + .18 * bend);
        double middle = (top + bottom) * .5 - width * .045 * bend;
        double tipTop = top * (1.76 + .20 * t);
        double tipBottom = bottom * (1.76 + .20 * t);
        double lift = height * .065 * bend;
        double shadow = width * .09 * bend;

        // 新旧版面宽度不同（例如从单页封面进入双页）时，逐步露出新增的画面。
        drawing.PushOpacity(1 - Smooth(t, .03, .34));
        drawing.DrawGeometry(matte, null, newMargins);
        drawing.Pop();

        if (facing != null)
        {
            double far = width + facing.Bounds.Width + 2;
            double settle = Smooth(t, .78, 1);
            double wipeTop = tipTop + (far - tipTop) * settle;
            double wipeBottom = tipBottom + (far - tipBottom) * settle;
            double facingEdge = Math.Max(height, facing.Bounds.Bottom - front.Bounds.Top);
            var remaining = Outline(Edge(wipeTop, 0), path =>
            {
                path.LineTo(Edge(far, 0), true, false);
                path.LineTo(Edge(far, facingEdge), true, false);
                path.LineTo(Edge(wipeBottom, facingEdge), true, false);
                path.QuadraticBezierTo(Edge((wipeTop + wipeBottom) * .5 - width * .05 * bend, facingEdge * .5),
                    Edge(wipeTop, 0), true, false);
            });
            drawing.PushClip(remaining);
            DrawLeaf(drawing, facing);
            drawing.Pop();
        }

        var flat = Outline(Edge(top, 0), path =>
        {
            path.LineTo(Edge(width, 0), true, false);
            path.LineTo(Edge(width, height), true, false);
            path.LineTo(Edge(bottom, height), true, false);
            path.QuadraticBezierTo(Edge(middle, height * .52), Edge(top, 0), true, false);
        });
        drawing.PushClip(flat);
        DrawLeaf(drawing, front);
        drawing.Pop();
        if (t <= 0) return;

        var flap = Outline(Edge(top, 0), path =>
        {
            path.QuadraticBezierTo(Edge(middle, height * .52), Edge(bottom, height), true, false);
            path.QuadraticBezierTo(Edge(tipBottom - width * .07 * bend, height + lift * .25),
                Edge(tipBottom, height - lift), true, false);
            path.QuadraticBezierTo(Edge((tipTop + tipBottom) * .5 - width * .11 * bend, height * .48),
                Edge(tipTop, lift * .24), true, false);
            path.QuadraticBezierTo(Edge((tipTop + top) * .5, -lift * .12), Edge(top, 0), true, false);
        });

        drawing.PushOpacity(1 - Smooth(t, .81, 1));
        if (shadow > 0)
        {
            var cast = Outline(Edge(top + shadow * .25, 0), path =>
            {
                path.LineTo(Edge(tipTop + shadow, 0), true, false);
                path.QuadraticBezierTo(Edge((tipTop + tipBottom) * .5 + shadow, height * .52),
                    Edge(tipBottom + shadow, height), true, false);
                path.LineTo(Edge(bottom + shadow * .25, height), true, false);
            });
            drawing.DrawGeometry(Shadows[fromLeft ? 0 : 1], null, cast);
        }

        Rect sheet = new Rect(Edge(top, 0), Edge(tipBottom, height));
        drawing.PushClip(flap);
        drawing.DrawRectangle(Brushes.FloralWhite, null, sheet);
        drawing.PushOpacity(.88);
        drawing.DrawImage(back.Source, sheet);
        drawing.Pop();
        drawing.DrawRectangle(Shading[fromLeft ? 0 : 1], null, sheet);
        drawing.Pop();
        drawing.DrawGeometry(null, FoldEdge, flap);
        drawing.Pop();
    }

    Point Edge(double x, double y)
    {
        Rect rect = front.Bounds;
        return new Point(fromLeft ? rect.Left + x : rect.Right - x, rect.Top + y);
    }

    static LinearGradientBrush[] Gradients(params GradientStop[] stops)
    {
        var gradients = new LinearGradientBrush[2];
        for (int i = 0; i < gradients.Length; i++)
        {
            var brush = new LinearGradientBrush(new GradientStopCollection(stops), new Point(i, .5), new Point(1 - i, .5));
            brush.Freeze();
            gradients[i] = brush;
        }
        return gradients;
    }

    static Pen MakeFoldEdge()
    {
        var edge = new Pen(new SolidColorBrush(Color.FromArgb(95, 66, 54, 40)), .7);
        edge.Freeze();
        return edge;
    }

    static GradientStop StopAt(double offset, byte alpha, byte red, byte green, byte blue)
    {
        return new GradientStop(Color.FromArgb(alpha, red, green, blue), offset);
    }

    static StreamGeometry Outline(Point start, Action<StreamGeometryContext> draw)
    {
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(start, true, true);
            draw(path);
        }
        geometry.Freeze();
        return geometry;
    }

    static void DrawLeaf(DrawingContext drawing, Leaf leaf)
    {
        drawing.DrawRectangle(Brushes.White, null, leaf.Bounds);
        drawing.DrawImage(leaf.Source, leaf.Bounds);
    }

    static double Smooth(double value, double start, double end)
    {
        double x = Math.Max(0, Math.Min(1, (value - start) / (end - start)));
        return x * x * (3 - 2 * x);
    }
}
