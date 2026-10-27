using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Data;
using System.Windows.Threading;
using System.Runtime.CompilerServices;

namespace Wisp.Desktop;

public static class Buttons
{
    private static readonly ConditionalWeakTable<Button, Action> flashes = new();
    public static Button Create(string text, Action action, string? tooltip = null, string? icon = null, string style = "Button.Secondary")
    {
        var button = new Button();
        Ui.Style(button, style);
        Label(button, text, icon);
        button.ToolTip = tooltip ?? text;
        Ui.Name(button, tooltip ?? text);
        button.Click += (_, _) => { action(); if (Clean(text).StartsWith("Скопировать", StringComparison.Ordinal)) Flash(button, "check", "copy"); };
        Motion.Interactive(button);
        return button;
    }
    public static Button Tool(string icon, string name, Action action)
    {
        var button = Create(name, action, name, icon, "Button.Icon");
        button.Content = new Icon { Kind = icon };
        return button;
    }
    public static void Flash(Button button, string kind, string restore = "send")
    {
        if (flashes.TryGetValue(button, out var previous)) previous();
        var glyph = button.Content as Icon ?? (button.Content as Panel)?.Children.OfType<Icon>().FirstOrDefault();
        if (glyph is null) return;
        glyph.Kind = kind; Motion.Pulse(glyph);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        RoutedEventHandler? unloaded = null;
        void Finish()
        {
            timer.Stop(); button.Unloaded -= unloaded; flashes.Remove(button);
            if (glyph.Kind == kind) glyph.Kind = restore;
        }
        unloaded = (_, _) => Finish(); button.Unloaded += unloaded;
        flashes.Add(button, Finish); timer.Tick += (_, _) => Finish(); timer.Start();
    }
    public static void Label(Button button, string text, string? icon = null)
    {
        text = Clean(text);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var glyph = new Icon { Kind = icon ?? ForLabel(text), Width = Ui.Metric("Size.InlineIcon"), Height = Ui.Metric("Size.InlineIcon"), Margin = Ui.Insets(0, 0, Ui.S, 0), VerticalAlignment = VerticalAlignment.Center };
        var label = Ui.Label(text);
        label.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(Control.Foreground)) { Source = button });
        Grid.SetColumn(label, 1);
        grid.Children.Add(glyph); grid.Children.Add(label);
        button.Content = grid;
        Ui.Name(button, text);
    }
    public static string Clean(string text) => text.TrimStart(' ', '+', '↗', '◐', '⚙', '✕', '●', '■', '▶', '↓', '♫', '#').Trim();
    public static string ForLabel(string label)
    {
        var value = label.ToLowerInvariant();
        if (value.Contains("отмен") || value.Contains("закры") || value.Contains("убрать")) return "x";
        if (value.Contains("удал") || value.Contains("очист")) return "trash";
        if (value.Contains("сохран") || value.Contains("примен") || value.Contains("готово")) return "check";
        if (value.Contains("созда") || value.Contains("добав") || value.Contains("канал")) return "plus";
        if (value.Contains("скоп")) return "copy";
        if (value.Contains("эмодзи")) return "face-slightly-smiling";
        if (value.Contains("загруз")) return "download";
        if (value.Contains("отправ")) return "send";
        if (value.Contains("профил") || value.Contains("редакт")) return "pencil";
        if (value.Contains("пригла") || value.Contains("присоедин")) return "users";
        if (value.Contains("звук")) return "volume-2";
        if (value.Contains("микр") || value.Contains("голос")) return "mic";
        if (value.Contains("демонст") || value.Contains("смотр")) return "monitor";
        if (value.Contains("выйти")) return "log-out";
        if (value.Contains("устрой") || value.Contains("настрой")) return "settings";
        return "arrow-right";
    }
}
