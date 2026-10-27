using System.Windows.Media;

namespace Wisp.Desktop;

public static class ThemeManager
{
    private static string? preferencePath;
    public static bool IsLight { get; private set; }
    public static event Action? Changed;

    public static void Initialize(string rootDirectory)
    {
        Motion.Initialize(rootDirectory);
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
            ? new[] { "#FFFFFF", "#F1F2F6", "#E2E3EC", "#181923", "#626577", "#6350DA", "#D2D4DF", "#8E919E", "#7563E9", "#5140BC", "#B3263B", "#FBE8EC", "#18794E", "#E8E9F0", "#E4DFFA", "#8A5900" }
            : new[] { "#000000", "#101114", "#1C1D23", "#EDEDF2", "#9899AA", "#8B78FF", "#2A2C34", "#686B7A", "#A294FF", "#7261D9", "#F07B86", "#3C2028", "#65C99E", "#24262F", "#302A48", "#E5BD66" };
        var keys = new[] { "Background", "Surface", "SurfaceElevated", "TextPrimary", "TextSecondary", "Accent", "Border", "TextDisabled", "AccentHover", "AccentPressed", "Danger", "DangerSurface", "Success", "Hover", "Selected", "Warning" };
        var resources = System.Windows.Application.Current.Resources;
        for (int i = 0; i < keys.Length; i++)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
            brush.Freeze();
            resources[keys[i]] = brush;
        }
        var aliases = new[] { "Bg", "Panel", "Card", "Fg", "Muted" };
        for (int i = 0; i < aliases.Length; i++) resources[aliases[i]] = resources[keys[i]];
        Changed?.Invoke();
    }
}
