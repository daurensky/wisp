using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Text;

namespace Wisp.Desktop;

public sealed class SelectableChatText : RichTextBox
{
    public Paragraph Paragraph { get; }
    public SelectableChatText()
    {
        IsReadOnly = true; IsReadOnlyCaretVisible = false; IsDocumentEnabled = true;
        AutoWordSelection = false;
        BorderThickness = new Thickness(0); Padding = Margin = new Thickness(0);
        Background = System.Windows.Media.Brushes.Transparent; FocusVisualStyle = null;
        HorizontalScrollBarVisibility = VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        SetResourceReference(ForegroundProperty, "TextPrimary");
        SetResourceReference(SelectionBrushProperty, "Accent");
        Document = new FlowDocument { PagePadding = new Thickness(0), FontSize = Ui.Body, LineHeight = Ui.Heading };
        Paragraph = new Paragraph { Margin = new Thickness(0) }; Document.Blocks.Add(Paragraph);
        // Focusing a partly visible message must not scroll its first line into view.
        RequestBringIntoView += (_, args) => args.Handled = true;
        DataObject.AddCopyingHandler(this, (_, args) =>
        {
            // FlowDocument adds its own paragraph terminator. Copy actual inline
            // content instead, preserving user-authored whitespace and newlines.
            string selected = SelectedPlainText();
            args.DataObject.SetData(DataFormats.UnicodeText, selected);
            args.DataObject.SetData(DataFormats.Text, selected);
        });
        Ui.Name(this, "Текст сообщения");
    }

    public void AppendEmoji(UIElement image, string token)
        => Paragraph.Inlines.Add(new InlineUIContainer(image) { Tag = token, BaselineAlignment = BaselineAlignment.Center });

    public string SelectedPlainText()
    {
        if (Selection.IsEmpty) return "";
        var result = new StringBuilder();
        Append(Paragraph.Inlines);
        return result.ToString();

        void Append(InlineCollection inlines)
        {
            foreach (Inline inline in inlines)
            {
                if (Selection.End.CompareTo(inline.ElementStart) <= 0 || Selection.Start.CompareTo(inline.ElementEnd) >= 0) continue;
                if (inline is Run run)
                {
                    var start = Selection.Start.CompareTo(run.ContentStart) > 0 ? Selection.Start : run.ContentStart;
                    var end = Selection.End.CompareTo(run.ContentEnd) < 0 ? Selection.End : run.ContentEnd;
                    if (start.CompareTo(end) >= 0) continue;
                    int first = Math.Clamp(run.ContentStart.GetOffsetToPosition(start), 0, run.Text.Length);
                    int last = Math.Clamp(run.ContentStart.GetOffsetToPosition(end), first, run.Text.Length);
                    result.Append(run.Text, first, last - first);
                }
                else if (inline is Span span) Append(span.Inlines);
                else if (inline is InlineUIContainer { Tag: string token }) result.Append(token);
                else if (inline is LineBreak) result.Append('\n');
            }
        }
    }
}
