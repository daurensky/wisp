using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Runtime.CompilerServices;

namespace Wisp.Desktop;

/// <summary>Finite render-only transitions. No idle rendering loop or animation timer.</summary>
public static class Motion
{
    public static readonly DependencyProperty PopupTransitionProperty = DependencyProperty.RegisterAttached("PopupTransition", typeof(bool), typeof(Motion), new PropertyMetadata(false, PopupEnabled));
    public static void SetPopupTransition(DependencyObject element, bool value) => element.SetValue(PopupTransitionProperty, value);
    public static bool GetPopupTransition(DependencyObject element) => (bool)element.GetValue(PopupTransitionProperty);
    private static void PopupEnabled(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not FrameworkElement popup || !(bool)args.NewValue) return;
        popup.Loaded += (_, _) => { Fade(popup, 0, 1); };
        popup.Unloaded += (_, _) => Stop(popup);
    }
    private static string? preferencePath;
    private static bool observingSystem;
    private static readonly ConditionalWeakTable<FrameworkElement, Completion> completions = new();
    private sealed class Completion(Action action)
    {
        private bool finished;
        public void Finish() { if (finished) return; finished = true; action(); }
    }
    public static bool Reduce { get; private set; }
    public static event Action? Changed;
    public static void Initialize(string root)
    {
        preferencePath = Path.Combine(root, "reduce-motion.txt");
        if (!observingSystem)
        {
            observingSystem = true;
            SystemParameters.StaticPropertyChanged += (_, args) => { if (args.PropertyName == nameof(SystemParameters.ClientAreaAnimation)) Changed?.Invoke(); };
        }
        try { Reduce = File.Exists(preferencePath) && File.ReadAllText(preferencePath).Trim() == "true"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Reduce = false; }
    }
    public static void SetReduced(bool value)
    {
        if (preferencePath is not null) File.WriteAllText(preferencePath, value ? "true" : "false");
        Reduce = value;
        Changed?.Invoke();
    }
    public static bool Allowed(FrameworkElement element)
    {
        var window = Window.GetWindow(element);
        if (window is null && element is ToolTip tip && tip.PlacementTarget is DependencyObject tipTarget) window = Window.GetWindow(tipTarget);
        if (window is null && element is ContextMenu menu && menu.PlacementTarget is DependencyObject menuTarget) window = Window.GetWindow(menuTarget);
        return !Reduce && SystemParameters.ClientAreaAnimation && element.IsVisible &&
            window is { IsActive: true } && window.WindowState != WindowState.Minimized;
    }
    public static Duration Duration(FrameworkElement element, string speed = "Fast") =>
        Allowed(element) ? (Duration)Application.Current.FindResource("Motion." + speed) : new Duration(TimeSpan.Zero);

    public static void Fade(FrameworkElement element, double from, double to, string speed = "Fast", Action? completed = null)
    {
        if (completions.TryGetValue(element, out var previous)) { completions.Remove(element); previous.Finish(); }
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = to;
        var duration = Duration(element, speed);
        if (duration.TimeSpan == TimeSpan.Zero) { completed?.Invoke(); return; }
        var animation = new DoubleAnimation(from, to, duration)
        {
            EasingFunction = (IEasingFunction)Application.Current.FindResource(to > from ? "Motion.EaseOut" : "Motion.EaseIn"),
            FillBehavior = FillBehavior.Stop
        };
        var completion = new Completion(() => { completions.Remove(element); element.BeginAnimation(UIElement.OpacityProperty, null); completed?.Invoke(); });
        completions.Add(element, completion);
        animation.Completed += (_, _) => completion.Finish();
        element.BeginAnimation(UIElement.OpacityProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }
    public static void Enter(FrameworkElement element)
    {
        if (!Allowed(element)) return;
        Fade(element, 0, 1, "Normal");
        var transform = new TranslateTransform();
        element.RenderTransform = transform;
        transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(Ui.S, 0, Duration(element, "Normal"))
        {
            EasingFunction = (IEasingFunction)Application.Current.FindResource("Motion.EaseOut"), FillBehavior = FillBehavior.Stop
        });
    }
    public static void Pulse(FrameworkElement element)
    {
        if (!Allowed(element)) return;
        var scale = new ScaleTransform(1, 1);
        element.RenderTransformOrigin = new Point(.5, .5); element.RenderTransform = scale;
        var animation = new DoubleAnimation(.85, 1, Duration(element)) { EasingFunction = (IEasingFunction)Application.Current.FindResource("Motion.EaseOut"), FillBehavior = FillBehavior.Stop };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }
    public static void Stop(FrameworkElement element)
    {
        if (completions.TryGetValue(element, out var completion)) { completions.Remove(element); completion.Finish(); }
        element.BeginAnimation(UIElement.OpacityProperty, null);
        if (element.RenderTransform is TranslateTransform { IsFrozen: false } translate) translate.BeginAnimation(TranslateTransform.YProperty, null);
        if (element.RenderTransform is ScaleTransform { IsFrozen: false } scale)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        }
    }
    public static void Interactive(Button button)
    {
        FrameworkElement? root = null;
        FrameworkElement? hover = null;
        ScaleTransform? scale = null;
        button.Loaded += (_, _) =>
        {
            root = button.Template.FindName("Root", button) as FrameworkElement;
            scale = root?.RenderTransform as ScaleTransform;
            if (scale?.IsFrozen == true && root is not null) { scale = scale.Clone(); root.RenderTransform = scale; }
            hover = button.Template.FindName("HoverSurface", button) as FrameworkElement;
        };
        void Press(bool pressed)
        {
            if (scale is null) return;
            double to = pressed ? .94 : 1;
            var duration = Duration(button);
            if (hover is not null) Fade(hover, hover.Opacity, pressed ? 1 : button.IsMouseOver ? .7 : 0);
            scale.ScaleX = scale.ScaleY = to;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(to, duration) { FillBehavior = FillBehavior.Stop });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(to, duration) { FillBehavior = FillBehavior.Stop });
        }
        button.PreviewMouseLeftButtonDown += (_, _) => Press(true);
        button.PreviewMouseLeftButtonUp += (_, _) => Press(false);
        button.MouseLeave += (_, _) => Press(false);
        button.MouseEnter += (_, _) => { if (hover is not null) Fade(hover, hover.Opacity, .7); };
        button.Unloaded += (_, _) => { if (root is not null) Stop(root); root = null; scale = null; };
    }
}
