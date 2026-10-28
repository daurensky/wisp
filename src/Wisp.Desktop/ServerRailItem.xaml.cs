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

    public ServerRailItem(string title, string? iconPath, bool selected, Action activate, ContextMenu? menu = null, int coverFrame = 0)
    {
        InitializeComponent();
        ServerButton.Content = CreateAvatar(title, iconPath, Ui.Metric("Size.RailItem"), ServerButton, coverFrame);
        ServerButton.ToolTip = title; Ui.Name(ServerButton, title);
        ServerButton.ContextMenu = menu;
        ServerButton.Click += (_, _) => { if (!IsSelected) activate(); };
        MouseEnter += (_, _) => UpdatePill(); MouseLeave += (_, _) => UpdatePill();
        Loaded += (_, _) => { if (IsSelected) ActivePill.Height = 0; UpdatePill(); }; Unloaded += (_, _) => ActivePill.BeginAnimation(HeightProperty, null);
        IsSelected = selected;
    }

    private static void SelectionChanged(DependencyObject value, DependencyPropertyChangedEventArgs _) => ((ServerRailItem)value).UpdatePill();

    private void UpdatePill()
    {
        if (ActivePill is null) return;
        double target = IsSelected ? Ui.Metric("Height.ServerPillActive") : IsMouseOver ? Ui.Metric("Height.ServerPillHover") : 0;
        double from = ActivePill.Height;
        ActivePill.BeginAnimation(HeightProperty, null); ActivePill.Height = target;
        var duration = Motion.Duration(this);
        if (duration.TimeSpan > TimeSpan.Zero)
            ActivePill.BeginAnimation(HeightProperty, new DoubleAnimation(from, target, duration)
            {
                EasingFunction = (IEasingFunction)FindResource("Motion.EaseOut"), FillBehavior = FillBehavior.Stop
            });
    }

    public static FrameworkElement CreateAvatar(string title, string? path, double size, FrameworkElement? hoverScope = null, int coverFrame = 0)
    {
        if (path is not null && File.Exists(path))
        {
            try
            {
                var picture = AvatarCatalog.Image(path, size, mode: GifPlaybackMode.Hover, hoverScope: hoverScope, coverFrame: coverFrame);
                picture.Clip = new EllipseGeometry(new Point(size / 2, size / 2), size / 2, size / 2);
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
        var avatar = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Child = label, ClipToBounds = true };
        avatar.SetResourceReference(Border.BackgroundProperty, "ServerPlaceholderBackground");
        return avatar;
    }
}
