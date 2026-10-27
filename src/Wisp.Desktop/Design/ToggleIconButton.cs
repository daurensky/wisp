using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;

namespace Wisp.Desktop;

/// <summary>Controlled toggle: the action runs immediately; transitions are only feedback.</summary>
public sealed class ToggleIconButton : Button
{
    private readonly Icon on = new(), off = new();
    public static readonly DependencyProperty IsCheckedProperty = DependencyProperty.Register(nameof(IsChecked), typeof(bool), typeof(ToggleIconButton), new PropertyMetadata(false, StateChanged));
    public static readonly DependencyProperty IconOnProperty = DependencyProperty.Register(nameof(IconOn), typeof(string), typeof(ToggleIconButton), new PropertyMetadata("check", StateChanged));
    public static readonly DependencyProperty IconOffProperty = DependencyProperty.Register(nameof(IconOff), typeof(string), typeof(ToggleIconButton), new PropertyMetadata("x", StateChanged));
    public static readonly DependencyProperty ToolTipOnProperty = DependencyProperty.Register(nameof(ToolTipOn), typeof(string), typeof(ToggleIconButton), new PropertyMetadata("Выключить", StateChanged));
    public static readonly DependencyProperty ToolTipOffProperty = DependencyProperty.Register(nameof(ToolTipOff), typeof(string), typeof(ToggleIconButton), new PropertyMetadata("Включить", StateChanged));
    public static readonly DependencyProperty CheckedBrushProperty = DependencyProperty.Register(nameof(CheckedBrush), typeof(Brush), typeof(ToggleIconButton), new PropertyMetadata(null, StateChanged));
    public bool IsChecked { get => (bool)GetValue(IsCheckedProperty); set => SetValue(IsCheckedProperty, value); }
    public string IconOn { get => (string)GetValue(IconOnProperty); set => SetValue(IconOnProperty, value); }
    public string IconOff { get => (string)GetValue(IconOffProperty); set => SetValue(IconOffProperty, value); }
    public string ToolTipOn { get => (string)GetValue(ToolTipOnProperty); set => SetValue(ToolTipOnProperty, value); }
    public string ToolTipOff { get => (string)GetValue(ToolTipOffProperty); set => SetValue(ToolTipOffProperty, value); }
    public Brush? CheckedBrush { get => (Brush?)GetValue(CheckedBrushProperty); set => SetValue(CheckedBrushProperty, value); }
    public ToggleIconButton()
    {
        Ui.Style(this, "Button.Icon");
        var icons = new Grid { Width = Ui.Metric("Size.Icon"), Height = Ui.Metric("Size.Icon") };
        icons.Children.Add(off); icons.Children.Add(on); Content = icons;
        Loaded += (_, _) => Update(false);
        Unloaded += (_, _) => { Motion.Stop(on); Motion.Stop(off); };
        IsVisibleChanged += (_, _) => { if (!IsVisible) { Motion.Stop(on); Motion.Stop(off); } };
        Motion.Interactive(this);
        Update(false);
    }
    private static void StateChanged(DependencyObject element, DependencyPropertyChangedEventArgs args) => ((ToggleIconButton)element).Update(args.Property == IsCheckedProperty);
    protected override AutomationPeer OnCreateAutomationPeer() => new TogglePeer(this);
    private sealed class TogglePeer(ToggleIconButton owner) : ButtonAutomationPeer(owner), IToggleProvider
    {
        public override object? GetPattern(PatternInterface patternInterface) => patternInterface == PatternInterface.Toggle ? this : base.GetPattern(patternInterface);
        public ToggleState ToggleState => owner.IsChecked ? ToggleState.On : ToggleState.Off;
        public void Toggle()
        {
            if (!owner.IsEnabled) throw new ElementNotEnabledException();
            owner.RaiseEvent(new RoutedEventArgs(ClickEvent));
        }
    }
    private void Update(bool animate)
    {
        on.Kind = IconOn; off.Kind = IconOff;
        ToolTip = IsChecked ? ToolTipOn : ToolTipOff;
        Ui.Name(this, (string)ToolTip);
        if (IsChecked && CheckedBrush is not null) Foreground = CheckedBrush;
        else SetResourceReference(ForegroundProperty, IsChecked ? "Danger" : "TextSecondary");
        if (IsChecked) SetResourceReference(BackgroundProperty, CheckedBrush is null ? "DangerSurface" : "Selected");
        else Background = Brushes.Transparent;
        double show = IsChecked ? 1 : 0;
        if (animate && Motion.Allowed(this)) { Motion.Fade(on, 1 - show, show); Motion.Fade(off, show, 1 - show); if (Content is FrameworkElement icons) Motion.Pulse(icons); }
        else { Motion.Stop(on); Motion.Stop(off); on.Opacity = show; off.Opacity = 1 - show; }
    }
}
