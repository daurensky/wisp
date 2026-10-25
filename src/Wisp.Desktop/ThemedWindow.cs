using System.Windows;

namespace Wisp.Desktop;

/// <summary>Explicitly applies the shared style to every derived application window.</summary>
public class ThemedWindow : Window
{
    public ThemedWindow()
    {
        // WPF implicit Window styles do not match custom Window subclasses.
        SetResourceReference(StyleProperty, "WispWindowStyle");
    }
}
