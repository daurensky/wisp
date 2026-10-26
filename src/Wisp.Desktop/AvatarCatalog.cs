using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Wisp.Core;
using XamlAnimatedGif;

namespace Wisp.Desktop;

public sealed class AvatarCatalog(string root, string localId)
{
    public static string Revision(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private string? CachePath(UserProfile p) => p.AvatarRevision is not null && Regex.IsMatch(p.AvatarRevision, "^[a-f0-9]{64}$")
        ? Path.Combine(root, "avatar-cache", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(p.Id))), p.AvatarRevision + ".image") : null;
    public string? Resolve(UserProfile p, LocalStore store)
    {
        if (p.Id == localId && p.AvatarPath is not null && File.Exists(p.AvatarPath)) return p.AvatarPath;
        var cached = CachePath(p); if (cached is not null && File.Exists(cached)) return cached;
        var legacy = p.AvatarAssetId is null ? null : store.GetAsset(p.AvatarAssetId)?.LocalPath;
        return legacy is not null && File.Exists(legacy) ? legacy : null;
    }
    public async Task<bool> FetchAsync(FirebaseBackend cloud, UserProfile p)
    {
        var path = CachePath(p); if (path is null || File.Exists(path) || (p.Id == localId && p.AvatarPath is not null && File.Exists(p.AvatarPath))) return false;
        var media = await cloud.GetAsync<AssetTransfer>($"profiles/{p.Id}/Images/{p.AvatarRevision}");
        if (media is null || media.Data is null || media.Data.Length > 4 * 1024 * 1024 || Convert.ToHexString(SHA256.HashData(media.Data)).ToLowerInvariant() != p.AvatarRevision) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllBytesAsync(path, media.Data);
        try { Validate(path); } catch { File.Delete(path); throw; } return true;
    }
    public static void Validate(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        if (decoder.Frames.Count > 500 || decoder.Frames[0].PixelWidth > 2048 || decoder.Frames[0].PixelHeight > 2048)
            throw new InvalidDataException("Аватар: максимум 2048×2048 и 500 кадров GIF.");
    }
    public static Image Image(string path, double size, bool animate = true)
    {
        var image = new Image { Width = size, Height = size, Stretch = Stretch.UniformToFill };
        using var file = File.OpenRead(path); var header = new byte[6]; _ = file.Read(header);
        if (animate && Encoding.ASCII.GetString(header).StartsWith("GIF8"))
        {
            AnimationBehavior.SetRepeatBehavior(image, RepeatBehavior.Forever);
            AnimationBehavior.SetSourceUri(image, new Uri(Path.GetFullPath(path)));
        }
        else
        {
            var source = new BitmapImage(); source.BeginInit(); source.CacheOption = BitmapCacheOption.OnLoad; source.DecodePixelWidth = Math.Max(32, (int)size * 2); source.StreamSource = file; file.Position = 0; source.EndInit(); source.Freeze(); image.Source = source;
        }
        return image;
    }
    public static FrameworkElement Circle(string? path, double size)
    {
        Image image;
        try { image = path is null ? new Image { Width = size, Height = size, Stretch = Stretch.UniformToFill, Source = new BitmapImage(new Uri("pack://application:,,,/Wisp;component/Assets/wisp.png")) } : Image(path, size); }
        catch { image = new Image { Width = size, Height = size, Stretch = Stretch.UniformToFill, Source = new BitmapImage(new Uri("pack://application:,,,/Wisp;component/Assets/wisp.png")) }; }
        image.Clip = new EllipseGeometry(new Point(size / 2, size / 2), size / 2, size / 2);
        return image;
    }
}
