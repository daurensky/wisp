using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Wisp.Desktop;

/// <summary>Code-built views use the same design resources as XAML templates.</summary>
public static class Ui
{
    public static double Metric(string key) => (double)Application.Current.FindResource(key);
    public static double Xs => Metric("Spacing.XS");
    public static double S => Metric("Spacing.S");
    public static double M => Metric("Spacing.M");
    public static double L => Metric("Spacing.L");
    public static double Xl => Metric("Spacing.XL");
    public static double Xxl => Metric("Spacing.XXL");
    public static double Caption => Metric("Font.Caption");
    public static double Body => Metric("Font.Body");
    public static double Subtitle => Metric("Font.Subtitle");
    public static double Heading => Metric("Font.Heading");
    public static double ControlHeight => Metric("Size.Control");
    public static double RowHeight => Metric("Size.Row");
    public static Thickness Insets(double all) => new(all);
    public static Thickness Insets(double horizontal, double vertical) => new(horizontal, vertical, horizontal, vertical);
    public static Thickness Insets(double left, double top, double right, double bottom) => new(left, top, right, bottom);
    public static CornerRadius Radius(string key = "Radius.Control") => (CornerRadius)Application.Current.FindResource(key);
    public static void Style(FrameworkElement element, string key) => element.SetResourceReference(FrameworkElement.StyleProperty, key);
    public static void Secondary(TextBlock label) => label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
    public static void Name(DependencyObject element, string name) => AutomationProperties.SetName(element, name);
    public static TextBlock Label(string text, double? size = null, bool secondary = false)
    {
        var label = new TextBlock { Text = text, FontSize = size ?? Body, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = text };
        if (secondary) Secondary(label);
        return label;
    }
    public static Border Panel(UIElement child, string color = "Surface", bool divider = true)
    {
        var panel = new Border { Child = child, BorderThickness = new Thickness(0, 0, divider ? Metric("Size.Hairline") : 0, 0) };
        panel.SetResourceReference(Border.BackgroundProperty, color);
        panel.SetResourceReference(Border.BorderBrushProperty, "Border");
        return panel;
    }
}
