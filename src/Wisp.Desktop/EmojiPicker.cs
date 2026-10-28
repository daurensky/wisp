using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Wisp.Desktop;

public sealed record EmojiPickerItem(string Token, string Name, string? ImagePath = null);

/// <summary>A lightweight keyboard-style picker. Popup positioning is constrained by the current screen.</summary>
public sealed class EmojiPicker
{
    private readonly Popup popup;
    private readonly FrameworkElement anchor;
    public bool IsOpen => popup.IsOpen;

    public EmojiPicker(FrameworkElement anchor, IReadOnlyList<EmojiPickerItem> items, Action<EmojiPickerItem> selected, Action? manage = null)
    {
        this.anchor = anchor;
        var panel = new DockPanel();
        var heading = Ui.Label("Эмодзи", Ui.Subtitle); heading.Margin = Ui.Insets(Ui.M);
        DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading);
        if (manage is not null)
        {
            var edit = Buttons.Create("Управление эмодзи", () => { Close(); manage(); }, style: "Button.Ghost");
            edit.Margin = Ui.Insets(Ui.S); DockPanel.SetDock(edit, Dock.Bottom); panel.Children.Add(edit);
        }
        var grid = new WrapPanel { Margin = Ui.Insets(Ui.S), Width = 7 * 40 };
        foreach (var item in items)
        {
            var button = new Button { Width = 40, Height = 40, Margin = Ui.Insets(0), Padding = Ui.Insets(5), ToolTip = item.Name };
            Ui.Style(button, "Button.Ghost"); Ui.Name(button, item.Name);
            if (item.ImagePath is not null)
            {
                try
                {
                    var image = AvatarCatalog.Image(item.ImagePath, 28, mode: GifPlaybackMode.Hover, hoverScope: button);
                    image.Stretch = Stretch.Uniform; button.Content = image;
                }
                catch (Exception error) when (error is IOException or NotSupportedException or ArgumentException)
                {
                    button.Content = new Icon { Kind = "image", Width = 22, Height = 22 };
                    button.ToolTip = item.Name + " · изображение недоступно";
                    button.IsEnabled = false;
                }
            }
            else button.Content = new TextBlock { Text = item.Token, FontSize = 25, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            button.Click += (_, _) => { Close(); selected(item); };
            grid.Children.Add(button);
        }
        if (items.Count == 0)
        {
            var empty = Ui.Label("Нет эмодзи", secondary: true); empty.Margin = Ui.Insets(Ui.M); grid.Children.Add(empty);
        }
        panel.Children.Add(new ScrollViewer { Content = grid, MaxHeight = 280, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var surface = new Border { Child = panel, CornerRadius = Ui.Radius(), BorderThickness = new Thickness(Ui.Metric("Size.Hairline")), MinWidth = 7 * 40 + Ui.S * 2 };
        surface.SetResourceReference(Border.BackgroundProperty, "SurfaceElevated");
        surface.SetResourceReference(Border.BorderBrushProperty, "Border");
        popup = new Popup
        {
            Child = surface, PlacementTarget = anchor, Placement = PlacementMode.Custom,
            AllowsTransparency = true, StaysOpen = false, Focusable = false,
            CustomPopupPlacementCallback = (size, target, offset) =>
            [new CustomPopupPlacement(new Point(0, -size.Height - 6), PopupPrimaryAxis.Horizontal),
             new CustomPopupPlacement(new Point(0, target.Height + 6), PopupPrimaryAxis.Horizontal)]
        };
        surface.PreviewKeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape) return;
            args.Handled = true; Close(); anchor.Focus();
        };
        popup.Opened += (_, _) => { Motion.Fade(surface, 0, 1); grid.Children.OfType<Button>().FirstOrDefault()?.Focus(); };
        popup.Closed += (_, _) => { Motion.Stop(surface); anchor.Unloaded -= AnchorUnloaded; };
    }

    public static EmojiPicker Show(FrameworkElement anchor, IReadOnlyList<EmojiPickerItem> items, Action<EmojiPickerItem> selected, Action? manage = null)
    {
        var picker = new EmojiPicker(anchor, items, selected, manage);
        anchor.Unloaded += picker.AnchorUnloaded;
        picker.popup.IsOpen = true;
        return picker;
    }

    public void Close() => popup.IsOpen = false;
    private void AnchorUnloaded(object sender, RoutedEventArgs args) => Close();
}
