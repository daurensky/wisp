using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Wisp.Desktop;

/// <summary>A server entry whose selected state does not alter avatar bounds or add a border.</summary>
public sealed partial class ServerRailItem : UserControl
{
    public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.Register(nameof(IsSelected), typeof(bool), typeof(ServerRailItem), new PropertyMetadata(false, SelectionChanged));
    public bool IsSelected { get => (bool)GetValue(IsSelectedProperty); set => SetValue(IsSelectedProperty, value); }
    public Button Button => ServerButton;

    private Action activate = () => { };
    private string? artworkKey;
    private double pillTarget = double.NaN;
    private long pillTransition;

    public ServerRailItem(string title, string? iconPath, bool selected, Action activate, ContextMenu? menu = null, int coverFrame = 0)
    {
        InitializeComponent();
        PillGapColumn.Width = new GridLength(Ui.Metric("Width.ServerPillGap"));
        ServerButton.Click += (_, _) => { if (!IsSelected) this.activate(); };
        MouseEnter += (_, _) => UpdatePill(); MouseLeave += (_, _) => UpdatePill();
        Loaded += (_, _) => UpdatePill(false);
        Unloaded += (_, _) =>
        {
            // Recycled/temporarily detached rows settle at their intended state;
            // loading the same selected server must never replay a zero-height entry.
            ++pillTransition;
            ActivePill.BeginAnimation(HeightProperty, null);
            if (!double.IsNaN(pillTarget)) ActivePill.Height = pillTarget;
        };
        Update(title, iconPath, selected, activate, menu, coverFrame);
    }

    /// <summary>Refresh metadata in place; stable server ids retain their visual and animation state.</summary>
    public void Update(string title, string? iconPath, bool selected, Action activate, ContextMenu? menu = null, int coverFrame = 0)
    {
        this.activate = activate;
        string nextArtwork;
        if (iconPath is not null && File.Exists(iconPath))
        {
            var info = new FileInfo(iconPath);
            nextArtwork = info.FullName + ":" + info.LastWriteTimeUtc.Ticks + ":" + info.Length + ":" + coverFrame;
        }
        else nextArtwork = "placeholder:" + title;
        if (artworkKey != nextArtwork)
        {
            artworkKey = nextArtwork;
            ServerButton.Content = CreateAvatar(title, iconPath, Ui.Metric("Size.RailItem"), ServerButton, coverFrame);
        }
        ServerButton.ToolTip = title; Ui.Name(ServerButton, title);
        if (menu is null && ServerButton.ContextMenu is { } oldMenu) oldMenu.IsOpen = false;
        if (menu is null || ServerButton.ContextMenu?.IsOpen != true) ServerButton.ContextMenu = menu;
        IsSelected = selected;
        UpdatePill();
    }

    private static void SelectionChanged(DependencyObject value, DependencyPropertyChangedEventArgs _) => ((ServerRailItem)value).UpdatePill();

    private void UpdatePill(bool animate = true)
    {
        if (ActivePill is null) return;
        double target = IsSelected ? Ui.Metric("Height.ServerPillActive") : IsMouseOver ? Ui.Metric("Height.ServerPillHover") : 0;
        // Hovering a selected server, refreshing metadata or switching settings
        // sections does not change this target and must not create another clock.
        if (target == pillTarget) return;
        pillTarget = target;
        long transition = ++pillTransition;
        double from = ActivePill.Height;
        ActivePill.BeginAnimation(HeightProperty, null); ActivePill.Height = target;
        var duration = animate && IsLoaded ? Motion.Duration(this) : new Duration(TimeSpan.Zero);
        if (duration.TimeSpan <= TimeSpan.Zero) return;
        var animation = new DoubleAnimation(from, target, duration)
        {
            EasingFunction = (IEasingFunction)FindResource("Motion.EaseOut"), FillBehavior = FillBehavior.Stop
        };
        animation.Completed += (_, _) =>
        {
            if (transition == pillTransition) ActivePill.BeginAnimation(HeightProperty, null);
        };
        ActivePill.BeginAnimation(HeightProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    public static FrameworkElement CreateAvatar(string title, string? path, double size, FrameworkElement? hoverScope = null, int coverFrame = 0)
    {
        if (path is not null && File.Exists(path))
        {
            try
            {
                var picture = AvatarCatalog.Image(path, size, mode: GifPlaybackMode.Hover, hoverScope: hoverScope, coverFrame: coverFrame);
                var radius = Ui.Radius("Radius.ServerAvatar");
                var clip = new RectangleGeometry(new Rect(0, 0, size, size), radius.TopLeft, radius.TopLeft); clip.Freeze();
                picture.Clip = clip;
                return picture;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or NotSupportedException or ArgumentException) { DiagnosticLog.Write("server-avatar", error); }
        }
        var initials = new List<string>();
        var elements = StringInfo.GetTextElementEnumerator(title.Trim());
        while (initials.Count < 2 && elements.MoveNext()) initials.Add(elements.GetTextElement());
        var label = new TextBlock
        {
            Text = string.Concat(initials).ToUpperInvariant(), FontSize = Math.Max(Ui.Caption, size * .33),
            FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "ServerPlaceholderForeground");
        var avatar = new Border { Width = size, Height = size, CornerRadius = Ui.Radius("Radius.ServerAvatar"), Child = label, ClipToBounds = true };
        avatar.SetResourceReference(Border.BackgroundProperty, "ServerPlaceholderBackground");
        return avatar;
    }
}
