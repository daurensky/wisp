using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Wisp.Desktop;

public static class ModalHost
{
    private static readonly ConditionalWeakTable<Window, Grid> layers = new();
    public static void Prepare(Window owner) => _ = Layer(owner);
    private static Grid Layer(Window owner)
    {
        return layers.GetValue(owner, window =>
        {
            var old = window.Content as UIElement; window.Content = null;
            var grid = new Grid(); if (old is not null) grid.Children.Add(old);
            window.Content = grid; return grid;
        });
    }
    public static ModalHandle Open(Window owner, string title, UIElement content, double width = 700)
    {
        var layer = Layer(owner);
        var shade = new Grid { Background = new SolidColorBrush(Color.FromArgb(190, 0, 0, 0)), Focusable = true };
        var panel = new DockPanel();
        var header = new DockPanel { Margin = new Thickness(16, 8, 12, 8) };
        var close = new Button { Content = "✕", ToolTip = "Закрыть", HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(close, Dock.Right); header.Children.Add(close);
        header.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold });
        DockPanel.SetDock(header, Dock.Top); panel.Children.Add(header); panel.Children.Add(content);
        var card = new Border { Child = panel, Width = Math.Min(width, Math.Max(300, owner.ActualWidth - 48)), MaxHeight = Math.Max(250, owner.ActualHeight - 64), CornerRadius = new CornerRadius(12), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        card.SetResourceReference(Border.BackgroundProperty, "Panel"); shade.Children.Add(card);
        var oldFocus = Keyboard.FocusedElement;
        var handle = new ModalHandle(layer, shade, oldFocus, () => panel.Children.Remove(content));
        close.Click += (_, _) => handle.Close();
        shade.MouseDown += (_, e) => { if (ReferenceEquals(e.OriginalSource, shade)) handle.Close(); };
        shade.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; handle.Close(); } };
        System.Windows.Automation.AutomationProperties.SetName(shade, title.Length == 0 ? "Диалог" : title);
        layer.Children.Add(shade); KeyboardNavigation.SetTabNavigation(shade, KeyboardNavigationMode.Cycle);
        shade.Focus();
        return handle;
    }
}

public sealed class ModalHandle
{
    private readonly Grid layer, shade;
    private readonly IInputElement? previousFocus;
    private readonly Action detach;
    private bool closed;
    public event Action? Closed;
    internal ModalHandle(Grid layer, Grid shade, IInputElement? previousFocus, Action detach) { this.layer = layer; this.shade = shade; this.previousFocus = previousFocus; this.detach = detach; }
    public void Close() { if (closed) return; closed = true; detach(); layer.Children.Remove(shade); Closed?.Invoke(); if (previousFocus is not null) Keyboard.Focus(previousFocus); }
    public void Hide() => shade.Visibility = Visibility.Collapsed;
    public void Show() => shade.Visibility = Visibility.Visible;
}
