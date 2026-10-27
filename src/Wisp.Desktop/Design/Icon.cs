using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Wisp.Desktop;

/// <summary>Only pinned Lucide paths, rendered as a single frozen geometry.</summary>
public sealed class Icon : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(string), typeof(Icon), new FrameworkPropertyMetadata("info", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(Icon), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));
    public string Kind { get => (string)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public Icon()
    {
        Width = Height = Ui.Metric("Size.Icon");
        IsHitTestVisible = false;
        Focusable = false;
    }
    protected override void OnRender(DrawingContext drawing)
    {
        if (TryFindResource("Icon." + Kind) is not Geometry geometry) return;
        var pen = new Pen(Foreground, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        if (pen.CanFreeze) pen.Freeze();
        var transform = new ScaleTransform(RenderSize.Width / 24, RenderSize.Height / 24);
        transform.Freeze();
        drawing.PushTransform(transform);
        drawing.DrawGeometry(null, pen, geometry);
        drawing.Pop();
    }
}
