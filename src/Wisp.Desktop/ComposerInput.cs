using System.Windows.Input;

namespace Wisp.Desktop;

public static class ComposerInput
{
    public static bool ShouldSend(Key key, ModifierKeys modifiers) => key == Key.Enter && !modifiers.HasFlag(ModifierKeys.Shift);
}
