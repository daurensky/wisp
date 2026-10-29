using System.Collections;
using System.Windows;

namespace Wisp.Desktop;

public static class ThemeManager
{
    private static string? preferencePath;
    public static bool IsLight { get; private set; }
    public static event Action? Changed;

    public static void Initialize(string rootDirectory)
    {
        Motion.Initialize(rootDirectory);
        Typography.Initialize();
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
        var palette=new ResourceDictionary {Source=new Uri("pack://application:,,,/Wisp;component/Design/Palette."+(light?"Light":"Dark")+".xaml")};
        var resources=Application.Current.Resources;
        foreach(DictionaryEntry entry in palette)resources[entry.Key]=entry.Value;
        Changed?.Invoke();
    }
}
