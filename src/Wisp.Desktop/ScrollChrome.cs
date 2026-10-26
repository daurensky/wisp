using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Wisp.Desktop;

public static class ScrollChrome
{
    public static readonly DependencyProperty AutoHideProperty = DependencyProperty.RegisterAttached("AutoHide", typeof(bool), typeof(ScrollChrome), new PropertyMetadata(false, Enabled));
    public static readonly DependencyProperty IsScrollingProperty = DependencyProperty.RegisterAttached("IsScrolling", typeof(bool), typeof(ScrollChrome), new PropertyMetadata(false));
    public static bool GetAutoHide(DependencyObject d) => (bool)d.GetValue(AutoHideProperty);
    public static void SetAutoHide(DependencyObject d, bool value) => d.SetValue(AutoHideProperty, value);
    public static bool GetIsScrolling(DependencyObject d) => (bool)d.GetValue(IsScrollingProperty);
    public static void SetIsScrolling(DependencyObject d, bool value) => d.SetValue(IsScrollingProperty, value);
    private static void Enabled(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer view || !(bool)e.NewValue) return;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        timer.Tick += (_, _) => { if (Mouse.LeftButton == MouseButtonState.Pressed && view.IsMouseOver) return; timer.Stop(); SetIsScrolling(view, false); };
        view.ScrollChanged += (_, changed) => { if (changed.VerticalChange == 0 && changed.HorizontalChange == 0) return; SetIsScrolling(view, true); timer.Stop(); timer.Start(); };
        view.Unloaded += (_, _) => { timer.Stop(); SetIsScrolling(view, false); };
    }
}
