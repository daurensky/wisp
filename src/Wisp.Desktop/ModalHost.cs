using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Wisp.Desktop;

public static class ModalHost
{
    private static readonly ConditionalWeakTable<Window, Grid> layers = new();
    public static Grid Surface(Window owner) => Layer(owner);
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
    public static ModalHandle Open(Window owner, string title, UIElement content, double width = 0)
    {
        var layer = Layer(owner);
        var shade = new Grid { Focusable = true };
        shade.SetResourceReference(Panel.BackgroundProperty, "Overlay");
        var panel = new DockPanel();
        var header = new DockPanel { Margin = Ui.Insets(Ui.Xl, Ui.L, Ui.L, Ui.L) };
        var close = Buttons.Tool("x", "Закрыть · Esc", () => { });
        DockPanel.SetDock(close, Dock.Right); header.Children.Add(close);
        var logo = Brand.Image(Ui.Metric("Size.Icon")); logo.Margin = Ui.Insets(0, 0, Ui.S, 0); logo.VerticalAlignment = VerticalAlignment.Center; DockPanel.SetDock(logo, Dock.Left); header.Children.Add(logo);
        header.Children.Add(new TextBlock { Text = title, FontSize = Ui.Heading, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = title });
        DockPanel.SetDock(header, Dock.Top); panel.Children.Add(header); panel.Children.Add(content);
        double requested = width > 0 ? width : Ui.Metric("Width.DialogLarge");
        var card = new Border { Child = panel, Width = Math.Min(requested, Math.Max(Ui.Metric("Width.DialogSmall"), owner.ActualWidth - Ui.Xl * 2)), MaxHeight = Math.Max(Ui.Metric("Height.PictureInPicture"), owner.ActualHeight - Ui.Xxl * 2), CornerRadius = Ui.Radius("Radius.Dialog"), BorderThickness = new Thickness(Ui.Metric("Size.Hairline")), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        card.SetResourceReference(Border.BackgroundProperty, "Surface");
        card.SetResourceReference(Border.BorderBrushProperty, "Border"); shade.Children.Add(card);
        var oldFocus = Keyboard.FocusedElement;
        var handle = new ModalHandle(layer, shade, card, oldFocus, () => panel.Children.Remove(content));
        close.Click += (_, _) => handle.Close();
        shade.MouseDown += (_, e) => { if (ReferenceEquals(e.OriginalSource, shade)) handle.Close(); };
        shade.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; handle.Close(); } };
        System.Windows.Automation.AutomationProperties.SetName(shade, title.Length == 0 ? "Диалог" : title);
        layer.Children.Add(shade); KeyboardNavigation.SetTabNavigation(shade, KeyboardNavigationMode.Cycle);
        shade.Focus();
        Motion.Enter(card);
        Motion.Fade(shade, 0, 1, "Normal");
        return handle;
    }
}

public sealed class ModalHandle
{
    private readonly Grid layer, shade;
    private readonly FrameworkElement card;
    private readonly IInputElement? previousFocus;
    private readonly Action detach;
    private bool closed, closing;
    public event Action? Closed;
    internal ModalHandle(Grid layer, Grid shade, FrameworkElement card, IInputElement? previousFocus, Action detach) { this.layer = layer; this.shade = shade; this.card = card; this.previousFocus = previousFocus; this.detach = detach; }
    public void Close()
    {
        if (closed || closing) return;
        closing = true;
        // Disable actions, but keep the overlay hit-testable until removal.
        card.IsEnabled = false;
        Motion.Fade(card, card.Opacity, 0, "Fast");
        Motion.Fade(shade, shade.Opacity, 0, "Fast", () =>
        {
            if (closed) return;
            closed = true; Motion.Stop(card); Motion.Stop(shade);
            bool wasTop = layer.Children.IndexOf(shade) == layer.Children.Count - 1;
            detach(); layer.Children.Remove(shade); Closed?.Invoke();
            if (wasTop && previousFocus is UIElement element && element.IsVisible && element.IsEnabled) Keyboard.Focus(previousFocus);
        });
    }
    public void Hide() { Motion.Stop(card); Motion.Stop(shade); shade.Visibility = Visibility.Collapsed; }
    public void Show() { if (closed || closing) return; shade.Visibility = Visibility.Visible; }
}
