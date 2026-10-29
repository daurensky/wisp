using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Wisp.Desktop;

public sealed record ModalOptions(bool FullWindow = false, bool Headerless = false, bool CloseOnOutside = true);

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
            var grid = new Grid();
            grid.SetResourceReference(Panel.BackgroundProperty, "Background");
            if (old is not null) grid.Children.Add(old);
            window.Content = grid; return grid;
        });
    }
    public static ModalHandle Open(Window owner, string title, UIElement content, double width = 0, ModalOptions? options = null)
    {
        var layer = Layer(owner);
        // The scrim can fade independently; an opaque card must never expose the
        // previous page (or a white attachment behind it) during a transition.
        var shade = new Grid { Focusable = true, Background = Brushes.Transparent, Name = "ModalOverlay" };
        var scrim = new Border { IsHitTestVisible = false, Name = "ModalScrim" };
        scrim.SetResourceReference(Border.BackgroundProperty, "Overlay");
        shade.Children.Add(scrim);
        var panel = new DockPanel();
        panel.SetResourceReference(Panel.BackgroundProperty, "Surface");
        options ??= new ModalOptions();
        Button? close = null;
        if (!options.Headerless)
        {
            var header = new DockPanel { Margin = Ui.Insets(Ui.Xl, Ui.L, Ui.L, Ui.L) };
            close = Buttons.Tool("x", "Закрыть · Esc", () => { });
            DockPanel.SetDock(close, Dock.Right); header.Children.Add(close);
            header.Children.Add(new TextBlock { Text = title, FontSize = Ui.Heading, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = title });
            var headerSurface = new Border { Name = "ModalHeader", Child = header, BorderThickness = new Thickness(0, 0, 0, Ui.Metric("Size.Hairline")) };
            headerSurface.SetResourceReference(Border.BackgroundProperty, "Surface");
            headerSurface.SetResourceReference(Border.BorderBrushProperty, "Border");
            DockPanel.SetDock(headerSurface, Dock.Top); panel.Children.Add(headerSurface);
        }
        panel.Children.Add(content);
        double requested = width > 0 ? width : Ui.Metric("Width.DialogLarge");
        var card = new Border { Name = "ModalCard", Child = panel, CornerRadius = Ui.Radius("Radius.Dialog"), BorderThickness = new Thickness(Ui.Metric("Size.Hairline")), Opacity = 1 };
        card.SizeChanged += (_, _) =>
        {
            if (card.ActualWidth > 0 && card.ActualHeight > 0)
                card.Clip = new RectangleGeometry(new Rect(0, 0, card.ActualWidth, card.ActualHeight), card.CornerRadius.TopLeft, card.CornerRadius.TopLeft);
        };
        if (options.FullWindow)
        {
            card.SetResourceReference(FrameworkElement.MarginProperty, "Margin.SettingsModal");
            card.VerticalAlignment = VerticalAlignment.Stretch; card.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
        else
        {
            card.VerticalAlignment = VerticalAlignment.Center; card.HorizontalAlignment = HorizontalAlignment.Center;
            void Fit()
            {
                double availableWidth = shade.ActualWidth > 0 ? shade.ActualWidth : owner.ActualWidth;
                double availableHeight = shade.ActualHeight > 0 ? shade.ActualHeight : owner.ActualHeight;
                card.Width = Math.Max(0, Math.Min(requested, availableWidth - Ui.L * 2));
                card.MaxHeight = Math.Max(0, availableHeight - Ui.L * 2);
            }
            shade.SizeChanged += (_, _) => Fit(); Fit();
        }
        card.SetResourceReference(Border.BackgroundProperty, "Surface");
        card.SetResourceReference(Border.BorderBrushProperty, "Border"); shade.Children.Add(card);
        var oldFocus = Keyboard.FocusedElement;
        var handle = new ModalHandle(layer, shade, scrim, card, oldFocus, () => panel.Children.Remove(content));
        if (close is not null) close.Click += (_, _) => handle.Close();
        shade.MouseDown += (_, e) => { if (options.CloseOnOutside && ReferenceEquals(e.OriginalSource, shade)) handle.Close(); };
        shade.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; handle.Close(); } };
        System.Windows.Automation.AutomationProperties.SetName(shade, title.Length == 0 ? "Диалог" : title);
        layer.Children.Add(shade); KeyboardNavigation.SetTabNavigation(shade, KeyboardNavigationMode.Cycle);
        shade.Focus();
        // Translate an opaque, already themed surface. Do not fade both this
        // container and its child: their opacity would multiply and reveal the
        // underlying content for the entire opening/closing animation.
        var duration = Motion.Duration(card, "Normal");
        if (duration.TimeSpan > TimeSpan.Zero)
        {
            var transform = new TranslateTransform(); card.RenderTransform = transform;
            transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(Ui.S, 0, duration)
            {
                EasingFunction = (IEasingFunction)Application.Current.FindResource("Motion.EaseOut"), FillBehavior = FillBehavior.Stop
            });
        }
        Motion.Fade(scrim, 0, 1, "Normal");
        return handle;
    }
}

public sealed class ModalHandle
{
    private readonly Grid layer, shade;
    private readonly FrameworkElement scrim;
    private readonly FrameworkElement card;
    private readonly IInputElement? previousFocus;
    private readonly Action detach;
    private bool closed, closing;
    public event Action? Closed;
    internal ModalHandle(Grid layer, Grid shade, FrameworkElement scrim, FrameworkElement card, IInputElement? previousFocus, Action detach) { this.layer = layer; this.shade = shade; this.scrim = scrim; this.card = card; this.previousFocus = previousFocus; this.detach = detach; }
    public void Close()
    {
        if (closed || closing) return;
        closing = true;
        // Disable actions, but keep the overlay hit-testable until removal.
        card.IsEnabled = false;
        Motion.Stop(card); card.Opacity = 1;
        Motion.Fade(scrim, scrim.Opacity, 0, "Fast", () =>
        {
            if (closed) return;
            closed = true; Motion.Stop(card); Motion.Stop(scrim);
            bool wasTop = layer.Children.IndexOf(shade) == layer.Children.Count - 1;
            detach(); layer.Children.Remove(shade); Closed?.Invoke();
            if (wasTop && previousFocus is UIElement element && element.IsVisible && element.IsEnabled) Keyboard.Focus(previousFocus);
        });
    }
    public void Hide() { if (closed || closing) return; Motion.Stop(card); Motion.Stop(scrim); shade.Visibility = Visibility.Collapsed; }
    public void Show() { if (closed || closing) return; shade.Visibility = Visibility.Visible; }
}
