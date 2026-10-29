using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;

namespace Wisp.Desktop;

/// <summary>Extends native text selection across the currently realised message rows.</summary>
public sealed class ChatTextSelection
{
    private readonly FrameworkElement host;
    private readonly HashSet<SelectableChatText> realised = new();
    private SelectableChatText? anchor;
    private TextPointer? start;
    private bool dragging, multiple;

    public ChatTextSelection(FrameworkElement host)
    {
        this.host = host;
        host.AddHandler(Mouse.PreviewMouseMoveEvent, new MouseEventHandler(Move), true);
        host.AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler((_, args) =>
        {
            if (dragging && multiple) args.Handled = true;
            dragging = false;
        }), true);
        host.AddHandler(CommandManager.PreviewExecutedEvent, new ExecutedRoutedEventHandler((_, args) =>
        {
            if (!multiple || args.Command != ApplicationCommands.Copy) return;
            Clipboard.SetText(SelectedPlainText()); args.Handled = true;
        }), true);
    }

    public void Register(SelectableChatText text)
    {
        text.IsInactiveSelectionHighlightEnabled = true;
        text.Loaded += (_, _) => realised.Add(text);
        text.Unloaded += (_, _) => { realised.Remove(text); if (anchor == text) { anchor = null; dragging = multiple = false; } };
        text.PreviewMouseLeftButtonDown += (_, args) =>
        {
            foreach (var other in realised.Where(other => other != text)) other.Selection.Select(other.Document.ContentStart, other.Document.ContentStart);
            anchor = text; start = text.GetPositionFromPoint(args.GetPosition(text), true);
            dragging = args.ClickCount == 1 && start is not null; multiple = false;
        };
    }

    private SelectableChatText[] Ordered() => realised.Where(text => text.IsVisible).OrderBy(text => text.TranslatePoint(new Point(), host).Y).ToArray();

    private void Move(object sender, MouseEventArgs args)
    {
        if (!dragging || anchor is null || start is null || args.LeftButton != MouseButtonState.Pressed) return;
        var point = args.GetPosition(host);
        var rows = Ordered();
        var target = rows.LastOrDefault(text => text.TranslatePoint(new Point(), host).Y <= point.Y) ?? rows.FirstOrDefault();
        if (target is null || target == anchor && !multiple) return;
        var position = target.GetPositionFromPoint(host.TranslatePoint(point, target), true);
        if (position is null) return;
        SelectBetween(anchor, start, target, position);
        args.Handled = true;
    }

    public void SelectBetween(SelectableChatText from, TextPointer fromPosition, SelectableChatText to, TextPointer toPosition)
    {
        var rows = Ordered(); int first = Array.IndexOf(rows, from), last = Array.IndexOf(rows, to);
        if (first < 0 || last < 0) return;
        multiple = from != to;
        foreach (var row in rows) row.Selection.Select(row.Document.ContentStart, row.Document.ContentStart);
        if (from == to) { from.Selection.Select(fromPosition, toPosition); return; }
        bool forward = first < last;
        from.Selection.Select(fromPosition, forward ? from.Document.ContentEnd : from.Document.ContentStart);
        to.Selection.Select(forward ? to.Document.ContentStart : to.Document.ContentEnd, toPosition);
        for (int i = Math.Min(first, last) + 1; i < Math.Max(first, last); i++) rows[i].SelectAll();
    }

    public string SelectedPlainText() => string.Join("\n", Ordered().Where(text => !text.Selection.IsEmpty).Select(text => text.SelectedPlainText()));
}
