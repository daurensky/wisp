using System.Windows.Media;

namespace Wisp.Desktop;

public static class ThemeManager
{
    private static string? preferencePath;
    public static bool IsLight { get; private set; }

    public static void Initialize(string rootDirectory)
    {
        preferencePath = Path.Combine(rootDirectory, "theme.txt");
        var light = false;
        try { light = File.Exists(preferencePath) && File.ReadAllText(preferencePath).Trim() == "light"; }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        Apply(light);
    }

    public static void Toggle()
    {
        Apply(!IsLight);
        if (preferencePath is not null) File.WriteAllText(preferencePath, IsLight ? "light" : "dark");
    }

    private static void Apply(bool light)
    {
        IsLight = light;
        var colors = light
            ? new[] { "#FFFFFF", "#F1F2F6", "#E2E3EC", "#181923", "#626577", "#6350DA" }
            : new[] { "#000000", "#101114", "#1C1D23", "#EDEDF2", "#9899AA", "#8B78FF" };
        var keys = new[] { "Bg", "Panel", "Card", "Fg", "Muted", "Accent" };
        var resources = System.Windows.Application.Current.Resources;
        for (int i = 0; i < keys.Length; i++)
            resources[keys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
    }
}
