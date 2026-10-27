using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace Wisp.Desktop;

public static class Brand
{
    private const string Root = "pack://application:,,,/Wisp;component/Assets/";
    public static ImageSource Logo { get; } = Load("app-logo.png");
    public static ImageSource WindowIcon { get; } = Load("wisp.ico");
    private static ImageSource Load(string name)
    {
        var image = new BitmapImage(); image.BeginInit(); image.UriSource = new Uri(Root + name);
        image.CacheOption = BitmapCacheOption.OnLoad; image.EndInit(); image.Freeze(); return image;
    }
    public static Image Image(double size) => new() { Source = Logo, Width = size, Height = size, Stretch = Stretch.Uniform };
    public static System.Drawing.Icon TrayIcon()
    {
        using var resource = Application.GetResourceStream(new Uri(Root + "wisp.ico"))!.Stream;
        using var icon = new System.Drawing.Icon(resource, new System.Drawing.Size(32,32));
        return (System.Drawing.Icon)icon.Clone();
    }
}
