using System.Windows;
using System.Windows.Controls;

namespace Wisp.Desktop;

/// <summary>Composes existing editor/actions without owning message or microphone state.</summary>
public partial class MessageComposerView : UserControl
{
    private readonly TextBox editor;
    private bool recording;
    public string Placeholder { get => PlaceholderText.Text; set => PlaceholderText.Text = value; }
    public bool IsRecording
    {
        get => recording;
        set
        {
            recording = value;
            TextEditorHost.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            Recording.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            Tools.IsEnabled = !value;
            UpdatePlaceholder();
        }
    }

    public MessageComposerView(TextBox editor, UIElement tools, UIElement actions, UIElement attachments, UIElement recordingView)
    {
        InitializeComponent(); this.editor = editor;
        TextEditorHost.Content = editor; Tools.Content = tools; Actions.Content = actions;
        Attachments.Content = attachments; Recording.Content = recordingView;
        editor.TextChanged += (_, _) => UpdatePlaceholder(); UpdatePlaceholder();
    }

    private void UpdatePlaceholder() => PlaceholderText.Visibility = !recording && editor.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
}
