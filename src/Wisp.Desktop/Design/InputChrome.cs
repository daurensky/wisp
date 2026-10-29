using System.Windows;
using System.Windows.Media;

namespace Wisp.Desktop;

/// <summary>Presentation-only input options consumed by the shared TextBox template.</summary>
public static class InputChrome
{
    public static readonly DependencyProperty FocusBrushProperty = DependencyProperty.RegisterAttached("FocusBrush", typeof(Brush), typeof(InputChrome), new FrameworkPropertyMetadata(null));
    public static void SetFocusBrush(DependencyObject target, Brush value) => target.SetValue(FocusBrushProperty, value);
    public static Brush? GetFocusBrush(DependencyObject target) => (Brush?)target.GetValue(FocusBrushProperty);
}
