using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

sealed class ReaderSpine : Adorner
{
    static readonly DependencyProperty ShadeProperty = DependencyProperty.Register("Shade", typeof(Brush),
        typeof(ReaderSpine), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    readonly StackPanel spread;
    double seam, height;

    public ReaderSpine(StackPanel spread) : base(spread)
    {
        this.spread = spread;
        IsHitTestVisible = false;
        SetResourceReference(ShadeProperty, "Spine");
        spread.LayoutUpdated += (s, e) => Refresh();
        var layer = AdornerLayer.GetAdornerLayer(spread);
        if (layer != null) layer.Add(this);
    }

    void Refresh()
    {
        double x = 0, h = 0;
        if (spread.Children.Count == 2)
        {
            var left = (FrameworkElement)spread.Children[0];
            var right = (FrameworkElement)spread.Children[1];
            x = left.ActualWidth;
            h = Math.Min(left.ActualHeight, right.ActualHeight);
        }
        if (x == seam && h == height) return;
        seam = x; height = h;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawing)
    {
        if (height <= 0 || seam <= 0) return;
        var theme = GetValue(ShadeProperty) as SolidColorBrush;
        if (theme == null) return;
        var dark = theme.Color;
        var edge = Color.FromArgb(0, dark.R, dark.G, dark.B);
        var gradient = new LinearGradientBrush(edge, dark, new Point(0, 0), new Point(1, 0));
        var reverse = new LinearGradientBrush(dark, edge, new Point(0, 0), new Point(1, 0));
        drawing.DrawRectangle(gradient, null, new Rect(seam - 11, 0, 11, height));
        drawing.DrawRectangle(reverse, null, new Rect(seam, 0, 11, height));
    }
}
