using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Wisp.Desktop;

public sealed class NotificationArea : UserControl
{
    private readonly StackPanel entries = new();
    private readonly Button clear;
    private readonly Dictionary<string, (TextBlock Label, int Count)> repeats = new();
    public event Action<int>? Changed;
    public int Count => entries.Children.Count;
    public NotificationArea()
    {
        var dock = new DockPanel { MaxHeight = Ui.Metric("Height.Notifications"), Margin = Ui.Insets(Ui.Xl, 0, Ui.Xl, Ui.L) };
        clear = Buttons.Create("Очистить уведомления", () => { entries.Children.Clear(); repeats.Clear(); Changed?.Invoke(0); }, icon: "trash", style: "Button.Ghost");
        clear.Margin = Ui.Insets(0, Ui.L, 0, 0);
        DockPanel.SetDock(clear, Dock.Bottom); dock.Children.Add(clear);
        dock.Children.Add(new ScrollViewer { Content = entries, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }); Content = dock;
    }
    public bool Add(string message, bool error)
    {
        if (repeats.TryGetValue(message, out var repeated)) { var count = Math.Min(int.MaxValue - 1, repeated.Count) + 1; repeated.Label.Text = $"{DateTime.Now:HH:mm:ss} · повторов: {count}"; repeats[message] = (repeated.Label, count); return false; }
        var row = new StackPanel { Margin = Ui.Insets(0, 0, 0, Ui.L) };
        var label = Ui.Label(DateTime.Now.ToString("HH:mm:ss"), Ui.Caption, true); row.Children.Add(label); repeats[message] = (label, 1); row.Tag = message;
        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = Ui.Insets(0, Ui.Xs, 0, 0) };
        if (error) text.SetResourceReference(TextBlock.ForegroundProperty, "Danger"); row.Children.Add(text); entries.Children.Insert(0, row);
        while (Count > 100) { var old = (FrameworkElement)entries.Children[Count - 1]; repeats.Remove((string)old.Tag); entries.Children.RemoveAt(Count - 1); } Changed?.Invoke(Count); return true;
    }
}
