using System.Windows;
using System.Windows.Interop;
using System.Runtime.InteropServices;

namespace Wisp.Desktop;

/// <summary>Explicitly applies the shared style to every derived application window.</summary>
public class ThemedWindow : Window
{
    public ThemedWindow()
    {
        // WPF implicit Window styles do not match custom Window subclasses.
        SetResourceReference(StyleProperty, "WispWindowStyle");
        SourceInitialized += (_, _) => ApplyTitleBar();
        ThemeManager.Changed += ApplyTitleBar;
        Closed += (_, _) => ThemeManager.Changed -= ApplyTitleBar;
    }
    private void ApplyTitleBar()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        int dark = ThemeManager.IsLight ? 0 : 1;
        if (DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int)) != 0)
            DwmSetWindowAttribute(handle, 19, ref dark, sizeof(int));
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
