using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace Wisp.Desktop;

public sealed class SelectableChatText : RichTextBox
{
    public Paragraph Paragraph { get; }
    public SelectableChatText()
    {
        IsReadOnly = true; IsReadOnlyCaretVisible = false; IsDocumentEnabled = true;
        BorderThickness = new Thickness(0); Padding = Margin = new Thickness(0);
        Background = System.Windows.Media.Brushes.Transparent; FocusVisualStyle = null;
        HorizontalScrollBarVisibility = VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        SetResourceReference(ForegroundProperty, "TextPrimary");
        SetResourceReference(SelectionBrushProperty, "Accent");
        Document = new FlowDocument { PagePadding = new Thickness(0), FontSize = Ui.Body, LineHeight = Ui.Heading };
        Paragraph = new Paragraph { Margin = new Thickness(0) }; Document.Blocks.Add(Paragraph);
        // Focusing a partly visible message must not scroll its first line into view.
        RequestBringIntoView += (_, args) => args.Handled = true;
        Ui.Name(this, "Текст сообщения");
    }
}
