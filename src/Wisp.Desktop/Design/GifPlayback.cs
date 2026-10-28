using System.Windows;

namespace Wisp.Desktop;

public enum GifPlaybackMode { Always, Hover, Speaking }

public static class GifPlayback
{
    public static readonly DependencyProperty ModeProperty = DependencyProperty.RegisterAttached("Mode", typeof(GifPlaybackMode), typeof(GifPlayback), new PropertyMetadata(GifPlaybackMode.Hover, Changed));
    public static readonly DependencyProperty ScopeProperty = DependencyProperty.RegisterAttached("Scope", typeof(FrameworkElement), typeof(GifPlayback), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty SpeakingProperty = DependencyProperty.RegisterAttached("Speaking", typeof(bool), typeof(GifPlayback), new PropertyMetadata(false, Changed));
    private static readonly DependencyProperty UpdateProperty = DependencyProperty.RegisterAttached("Update", typeof(Action), typeof(GifPlayback));
    private static readonly DependencyProperty ResetProperty = DependencyProperty.RegisterAttached("Reset", typeof(Action), typeof(GifPlayback));
    public static void SetMode(DependencyObject d, GifPlaybackMode mode) => d.SetValue(ModeProperty, mode==GifPlaybackMode.Speaking?GifPlaybackMode.Hover:mode);
    public static GifPlaybackMode GetMode(DependencyObject d) => (GifPlaybackMode)d.GetValue(ModeProperty);
    public static void SetScope(DependencyObject d, FrameworkElement? scope) => d.SetValue(ScopeProperty, scope);
    public static FrameworkElement? GetScope(DependencyObject d) => (FrameworkElement?)d.GetValue(ScopeProperty);
    public static void SetSpeaking(DependencyObject d, bool speaking) => d.SetValue(SpeakingProperty, speaking);
    public static bool GetSpeaking(DependencyObject d) => (bool)d.GetValue(SpeakingProperty);
    internal static void SetUpdate(DependencyObject d, Action callback) => d.SetValue(UpdateProperty, callback);
    internal static void SetReset(DependencyObject d,Action callback)=>d.SetValue(ResetProperty,callback);
    public static void Reset(DependencyObject d)=>(d.GetValue(ResetProperty)as Action)?.Invoke();
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs args) => (d.GetValue(UpdateProperty) as Action)?.Invoke();
    public static bool Allowed(FrameworkElement image) => Motion.Allowed(image) && (GetMode(image) switch
    {
        GifPlaybackMode.Hover => (GetScope(image) ?? image).IsMouseOver,
        GifPlaybackMode.Speaking => (GetScope(image) ?? image).IsMouseOver,
        _ => true
    });
}
