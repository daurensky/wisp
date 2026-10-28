using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Wisp.Desktop;

public sealed record SoundboardItem(string Id, string Name, float Volume = 1);

public sealed class SoundboardPopup
{
    private readonly Popup popup;
    private readonly FrameworkElement anchor;
    private readonly Window? owner;
    private readonly SoundboardPopupView view;
    private readonly Func<SoundboardItem,Task> play;
    private readonly Action<string> error;
    public bool IsOpen => popup.IsOpen;
    private SoundboardPopup(FrameworkElement anchor, IReadOnlyList<SoundboardItem> items, Func<SoundboardItem, Task> play, Action? manage, Action<string> error)
    {
        this.anchor = anchor; owner = Window.GetWindow(anchor);
        this.play=play;this.error=error;view=new SoundboardPopupView{Focusable=true};
        BuildItems(items);
        if (manage is not null) view.FooterItems.Children.Add(Buttons.Create("Управление звуками", () => { Close(); manage(); }, style: "Button.Ghost"));
        popup = new Popup
        {
            PlacementTarget = anchor, Child = view, AllowsTransparency = true, StaysOpen = false, Placement = PlacementMode.Custom,
            CustomPopupPlacementCallback = (size, target, _) => [new(new Point(0, -size.Height - 6), PopupPrimaryAxis.Horizontal), new(new Point(0, target.Height + 6), PopupPrimaryAxis.Horizontal)]
        };
        view.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); anchor.Focus(); } };
        popup.Opened += (_, _) => { Motion.Fade(view, 0, 1, scope: anchor); if(view.SoundItems.Children.OfType<Button>().FirstOrDefault() is{} first)first.Focus();else view.Focus(); };
        popup.Closed += (_, _) =>
        {
            Motion.Stop(view); anchor.Unloaded -= Unloaded; anchor.SizeChanged -= Resize;
            if (owner is not null) { owner.LocationChanged -= Reposition; owner.SizeChanged -= Resize; owner.Deactivated -= Deactivated; }
        };
        KeyboardNavigation.SetTabNavigation(view,KeyboardNavigationMode.Cycle);
        anchor.Unloaded += Unloaded; anchor.SizeChanged += Resize;
        if (owner is not null) { owner.LocationChanged += Reposition; owner.SizeChanged += Resize; owner.Deactivated += Deactivated; }
    }
    private void BuildItems(IReadOnlyList<SoundboardItem> items)
    {
        view.SoundItems.Children.Clear();
        foreach (var item in items)
        {
            var button = Buttons.Create(item.Name, () => { }, item.Name, "volume-2", "Button.Secondary"); button.Width = 98; button.Height = 52; button.Margin = Ui.Insets(2); button.Padding = Ui.Insets(Ui.S);
            button.Click += async (_, _) =>
            {
                if (!button.IsEnabled) return;
                button.IsEnabled = false;
                try { await play(item); }
                catch (Exception ex) { error(ex.Message); }
                finally { button.IsEnabled = true; }
            };
            view.SoundItems.Children.Add(button);
        }
        if (items.Count == 0) view.SoundItems.Children.Add(Ui.Label("Нет звуков", secondary: true));
    }
    public void SetLoading(bool loading)
    {
        view.StatusLabel.Text="Загрузка звуков…";view.StatusLabel.SetResourceReference(TextBlock.ForegroundProperty,"TextSecondary");
        view.StatusLabel.Visibility=loading?Visibility.Visible:Visibility.Collapsed;
        view.SoundItems.Visibility=loading?Visibility.Collapsed:Visibility.Visible;
    }
    public void SetItems(IReadOnlyList<SoundboardItem> items){BuildItems(items);SetLoading(false);}
    public void SetError(string message)
    {
        SetLoading(false);view.StatusLabel.Text=message;view.StatusLabel.SetResourceReference(TextBlock.ForegroundProperty,"Danger");view.StatusLabel.Visibility=Visibility.Visible;
    }
    public static SoundboardPopup Show(FrameworkElement anchor, IReadOnlyList<SoundboardItem> items, Func<SoundboardItem, Task> play, Action? manage, Action<string> error)
    {
        var value = new SoundboardPopup(anchor, items, play, manage, error); value.popup.IsOpen = true; return value;
    }
    public void Close() => popup.IsOpen = false;
    private void Reposition(object? sender, EventArgs args) { if (!IsOpen) return; popup.HorizontalOffset += .1; popup.HorizontalOffset -= .1; }
    private void Resize(object sender, SizeChangedEventArgs args) => Reposition(sender, args);
    private void Unloaded(object sender, RoutedEventArgs args) => Close();
    private void Deactivated(object? sender, EventArgs args) => Close();
}
