using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Wisp.Desktop;

/// <summary>Finite, retargetable wheel easing. Pixel virtualization and native thumb/touch input stay intact.</summary>
public static class SmoothScroll
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(SmoothScroll), new PropertyMetadata(false, Changed));
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached("State", typeof(State), typeof(SmoothScroll));
    public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject d, bool value) => d.SetValue(EnabledProperty, value);
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer view) return;
        (view.GetValue(StateProperty) as State)?.Dispose();
        view.SetValue(StateProperty, (bool)e.NewValue ? new State(view) : null);
    }
    private sealed class State : IDisposable
    {
        private readonly ScrollViewer view;
        private readonly DispatcherTimer timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        private double from, target, applied;
        private long began;
        private bool horizontal;
        private Window? window;
        public State(ScrollViewer view)
        {
            this.view = view;
            timer.Tick += Tick;
            view.PreviewMouseWheel += Wheel; view.PreviewMouseDown += DirectInput;
            view.PreviewKeyDown += KeyInput; view.ScrollChanged += Scrolled;
            view.Loaded += Loaded; view.Unloaded += Unloaded;
            if (view.IsLoaded) Loaded(view, new RoutedEventArgs());
        }
        private void Loaded(object sender, RoutedEventArgs e)
        {
            window = Window.GetWindow(view);
            if (window is not null) window.Deactivated += Deactivated;
        }
        private void Unloaded(object sender, RoutedEventArgs e)
        {
            Stop(); if (window is not null) window.Deactivated -= Deactivated; window = null;
        }
        private void Deactivated(object? sender, EventArgs e) => Stop();
        private void DirectInput(object sender, MouseButtonEventArgs e) => Stop();
        private void KeyInput(object sender, KeyEventArgs e) => Stop();
        private void Stop() { timer.Stop(); }
        private void Scrolled(object sender, ScrollChangedEventArgs e)
        {
            // Programmatic chat-to-end/keyboard/drag scrolling takes precedence.
            if (timer.IsEnabled && Math.Abs((horizontal ? view.HorizontalOffset : view.VerticalOffset) - applied) > 1) Stop();
        }
        private void Wheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Handled || e.Delta == 0 || (Keyboard.Modifiers & ModifierKeys.Control) != 0 || Mouse.Captured is not null) return;
            bool x = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            ScrollViewer? nearest = null;
            for (DependencyObject? p = e.OriginalSource as DependencyObject; p is not null; p = Parent(p))
                if (p is ScrollViewer candidate && CanMove(candidate, x, e.Delta)) { nearest = candidate; break; }
            if (!ReferenceEquals(nearest, view)) return;
            int lines = SystemParameters.WheelScrollLines;
            if (lines == 0) return;
            double extent = x ? view.ScrollableWidth : view.ScrollableHeight;
            double current = x ? view.HorizontalOffset : view.VerticalOffset;
            double step = lines < 0 ? (x ? view.ViewportWidth : view.ViewportHeight) : Math.Max(1, lines) * 20;
            double next = Math.Clamp((timer.IsEnabled && horizontal == x ? target : current) - e.Delta / 120d * step, 0, extent);
            if (!Motion.Allowed(view)) { Stop(); if (x) view.ScrollToHorizontalOffset(next); else view.ScrollToVerticalOffset(next); e.Handled = true; return; }
            horizontal = x; from = applied = current; target = next; began = Stopwatch.GetTimestamp();
            timer.Start(); e.Handled = true;
        }
        private static bool CanMove(ScrollViewer v, bool x, int delta)
        {
            double offset = x ? v.HorizontalOffset : v.VerticalOffset, max = x ? v.ScrollableWidth : v.ScrollableHeight;
            return delta > 0 ? offset > 0 : offset < max;
        }
        private static DependencyObject? Parent(DependencyObject d) => d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        private void Tick(object? sender, EventArgs e)
        {
            if (!Motion.Allowed(view)) { Stop(); return; }
            double progress = Math.Clamp(Stopwatch.GetElapsedTime(began).TotalMilliseconds / 150, 0, 1);
            double max = horizontal ? view.ScrollableWidth : view.ScrollableHeight;
            target = Math.Clamp(target, 0, max);
            applied = from + (target - from) * (1 - Math.Pow(1 - progress, 3));
            if (horizontal) view.ScrollToHorizontalOffset(applied); else view.ScrollToVerticalOffset(applied);
            if (progress >= 1 || Math.Abs(target - applied) < .25) { if (horizontal) view.ScrollToHorizontalOffset(target); else view.ScrollToVerticalOffset(target); Stop(); }
        }
        public void Dispose()
        {
            Unloaded(view, new RoutedEventArgs());
            view.PreviewMouseWheel -= Wheel; view.PreviewMouseDown -= DirectInput; view.PreviewKeyDown -= KeyInput;
            view.ScrollChanged -= Scrolled; view.Loaded -= Loaded; view.Unloaded -= Unloaded; timer.Tick -= Tick;
        }
    }
}
