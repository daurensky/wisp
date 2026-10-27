using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Wisp.Desktop;

/// <summary>Pointer clicks have no lingering outline; keyboard navigation keeps a visible focus cue.</summary>
public static class PointerFocus
{
    public static readonly DependencyProperty TrackProperty = DependencyProperty.RegisterAttached("Track", typeof(bool), typeof(PointerFocus), new PropertyMetadata(false, Attach));
    public static readonly DependencyProperty KeyboardFocusProperty = DependencyProperty.RegisterAttached("KeyboardFocus", typeof(bool), typeof(PointerFocus), new PropertyMetadata(false));
    public static void SetTrack(DependencyObject d, bool value) => d.SetValue(TrackProperty, value);
    public static bool GetTrack(DependencyObject d) => (bool)d.GetValue(TrackProperty);
    public static void SetKeyboardFocus(DependencyObject d, bool value) => d.SetValue(KeyboardFocusProperty, value);
    public static bool GetKeyboardFocus(DependencyObject d) => (bool)d.GetValue(KeyboardFocusProperty);
    private static void Attach(DependencyObject d, DependencyPropertyChangedEventArgs args)
    {
        if (d is not Control control || !(bool)args.NewValue) return;
        control.PreviewMouseDown += (_, _) => SetKeyboardFocus(control, false);
        control.GotKeyboardFocus += (_, _) => SetKeyboardFocus(control, Keyboard.IsKeyDown(Key.Tab) || Keyboard.IsKeyDown(Key.Left) || Keyboard.IsKeyDown(Key.Right));
        control.PreviewKeyDown += (_, _) => SetKeyboardFocus(control, true);
        control.LostKeyboardFocus += (_, _) => SetKeyboardFocus(control, false);
    }
}
