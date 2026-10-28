using System.Windows;
using System.Windows.Controls;

namespace Wisp.Desktop;

/// <summary>Measures a responsive media region without distorting its contents.</summary>
public sealed class AspectRatioFrame : Decorator
{
    public double Ratio { get; set; } = 16d / 9;
    protected override Size MeasureOverride(Size constraint)
    {
        double width = double.IsFinite(constraint.Width) ? Math.Max(0, constraint.Width) : Child?.DesiredSize.Width ?? 0;
        double height = width / Math.Max(.01, Ratio);
        Child?.Measure(new Size(width, height));
        return new Size(width, height);
    }
    protected override Size ArrangeOverride(Size arrangeSize)
    {
        Child?.Arrange(new Rect(new Point(), arrangeSize));
        return arrangeSize;
    }
}
