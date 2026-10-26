using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Wisp.Desktop;

public sealed class NotificationArea : UserControl
{
    private readonly StackPanel entries = new();
    private readonly Button clear = new() { Content = "Очистить уведомления" };
    private string? last;
    private DateTimeOffset lastAt;
    public event Action<int>? Changed;
    public int Count => entries.Children.Count;
    public NotificationArea()
    {
        var dock = new DockPanel { MinHeight = 160, MaxHeight = 500, Margin = new Thickness(16) };
        clear.Click += (_, _) => { entries.Children.Clear(); Changed?.Invoke(0); };
        DockPanel.SetDock(clear, Dock.Bottom); dock.Children.Add(clear);
        dock.Children.Add(new ScrollViewer { Content = entries, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }); Content = dock;
    }
    public void Add(string message, bool error)
    {
        if (last == message && DateTimeOffset.UtcNow - lastAt < TimeSpan.FromSeconds(5)) return;
        last = message; lastAt = DateTimeOffset.UtcNow;
        var row = new StackPanel { Margin = new Thickness(4, 8, 8, 8) };
        row.Children.Add(new TextBlock { Text = DateTime.Now.ToString("HH:mm:ss"), FontSize = 10, Opacity = .5 });
        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
        if (error) text.Foreground = Brushes.Salmon; row.Children.Add(text); entries.Children.Insert(0, row);
        while (Count > 100) entries.Children.RemoveAt(Count - 1); Changed?.Invoke(Count);
    }
}
