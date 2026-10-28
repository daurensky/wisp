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
    public async Task<bool> FetchAsync(ServerBackend cloud, UserProfile p)
    {
        var path = CachePath(p); if (path is null || File.Exists(path) || (p.Id == localId && p.AvatarPath is not null && File.Exists(p.AvatarPath))) return false;
        var media = await cloud.GetAsync<AssetTransfer>($"profiles/{p.Id}/Images/avatar");
        if (media is null || media.Data is null || media.Data.Length > 4 * 1024 * 1024 || Convert.ToHexString(SHA256.HashData(media.Data)).ToLowerInvariant() != p.AvatarRevision) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllBytesAsync(path, media.Data);
        try { Validate(path); } catch { File.Delete(path); throw; } return true;
    }
    public async Task<string?> FetchBannerAsync(ServerBackend cloud,UserProfile profile)
    {
        if(profile.BannerAssetId is null||!System.Text.RegularExpressions.Regex.IsMatch(profile.BannerAssetId,"^[a-f0-9]{64}$"))return null;
        var path=Path.Combine(root,"banners",Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profile.Id)))+"_"+profile.BannerAssetId+".img");
        if(File.Exists(path))return path;
        var asset=await cloud.GetAsync<AssetTransfer>($"profiles/{profile.Id}/Images/banner");
        if(asset?.Data is null||asset.Kind!="banner"||asset.Data.Length>4*1024*1024||Convert.ToHexString(SHA256.HashData(asset.Data)).ToLowerInvariant()!=profile.BannerAssetId)return null;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);await File.WriteAllBytesAsync(path,asset.Data);
        try{Validate(path);}catch{File.Delete(path);throw;}return path;
    }
    public static void Validate(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        if (decoder.Frames.Count > 500 || decoder.Frames[0].PixelWidth > 2048 || decoder.Frames[0].PixelHeight > 2048)
            throw new InvalidDataException("Аватар: максимум 2048×2048 и 500 кадров GIF.");
    }
    public static Image Image(string path, double size, bool animate = true, GifPlaybackMode mode = GifPlaybackMode.Hover, FrameworkElement? hoverScope = null)
    {
        var image = new Image { Width = size, Height = size, Stretch = Stretch.UniformToFill };
        GifPlayback.SetMode(image, mode); GifPlayback.SetScope(image, hoverScope);
        using var file = File.OpenRead(path); var header = new byte[6]; _ = file.Read(header);
        if (animate && Encoding.ASCII.GetString(header).StartsWith("GIF8"))
        {
            AnimationBehavior.SetAutoStart(image, false);
            AnimationBehavior.SetRepeatBehavior(image, RepeatBehavior.Forever);
            // Decode only frame zero until this visible avatar is actually hovered.
            var bytes=File.ReadAllBytes(path);
            BitmapSource first;
            using(var encoded=new MemoryStream(bytes,false))
            {
                var frame=new BitmapImage();frame.BeginInit();frame.CacheOption=BitmapCacheOption.OnLoad;frame.DecodePixelWidth=Math.Max(32,(int)size*2);frame.StreamSource=encoded;frame.EndInit();frame.Freeze();first=frame;
            }
            image.Source=first;
            BitmapSource? animatedSource=null;
            bool playing=false;
            Window? owner=null;FrameworkElement? scope=null;
            void Reset()
            {
                playing=false;AnimationBehavior.SetAutoStart(image,false);
                if(AnimationBehavior.GetAnimator(image)is{} animator){animator.Pause();animator.Rewind();}
                image.Source=first;
            }
            void UpdatePlayback()
            {
                if(!GifPlayback.Allowed(image)){if(playing||image.Source!=first)Reset();return;}
                AnimationBehavior.SetAutoStart(image,true);
                if(AnimationBehavior.GetSourceStream(image)is null)
                {playing=true;AnimationBehavior.SetSourceStream(image,new MemoryStream(bytes,false));return;}
                if(AnimationBehavior.GetAnimator(image)is{} animator)
                {
                    if(!playing){animator.Pause();animator.Rewind();}
                    if(animatedSource is not null)image.Source=animatedSource;
                    if(animator.IsPaused)animator.Play();playing=true;
                }
            }
            void WindowChanged(object? sender,EventArgs args)=>UpdatePlayback();
            void HoverChanged(object? sender,System.Windows.Input.MouseEventArgs args)=>UpdatePlayback();
            GifPlayback.SetUpdate(image,UpdatePlayback);GifPlayback.SetReset(image,Reset);
            image.Loaded+=(_,_)=>
            {
                owner=Window.GetWindow(image);scope=GifPlayback.GetScope(image)??image;
                scope.MouseEnter+=HoverChanged;scope.MouseLeave+=HoverChanged;
                if(owner is not null){owner.Activated+=WindowChanged;owner.Deactivated+=WindowChanged;owner.StateChanged+=WindowChanged;}
                Motion.Changed+=UpdatePlayback;UpdatePlayback();
            };
            image.Unloaded+=(_,_)=>
            {
                if(scope is not null){scope.MouseEnter-=HoverChanged;scope.MouseLeave-=HoverChanged;scope=null;}
                if(owner is not null){owner.Activated-=WindowChanged;owner.Deactivated-=WindowChanged;owner.StateChanged-=WindowChanged;owner=null;}
                Motion.Changed-=UpdatePlayback;Reset();animatedSource=null;
            };
            image.IsVisibleChanged+=(_,_)=>UpdatePlayback();
            AnimationBehavior.AddLoadedHandler(image,(_,_)=>{animatedSource=image.Source as BitmapSource;playing=false;UpdatePlayback();});

        }
        else
        {
            var source = new BitmapImage(); source.BeginInit(); source.CacheOption = BitmapCacheOption.OnLoad; source.DecodePixelWidth = Math.Max(32, (int)size * 2); source.StreamSource = file; file.Position = 0; source.EndInit(); source.Freeze(); image.Source = source;
        }
        return image;
    }
    public static FrameworkElement Circle(string? path, double size, GifPlaybackMode mode = GifPlaybackMode.Hover, FrameworkElement? hoverScope = null)
    {
        Image image;
        try { image = path is null ? new Image { Width = size, Height = size, Stretch = Stretch.UniformToFill, Source = new BitmapImage(new Uri("pack://application:,,,/Wisp;component/Assets/wisp.png")) } : Image(path, size, mode: mode, hoverScope: hoverScope); }
        catch { image = new Image { Width = size, Height = size, Stretch = Stretch.UniformToFill, Source = new BitmapImage(new Uri("pack://application:,,,/Wisp;component/Assets/wisp.png")) }; }
        image.Clip = new EllipseGeometry(new Point(size / 2, size / 2), size / 2, size / 2);
        return image;
    }
}
