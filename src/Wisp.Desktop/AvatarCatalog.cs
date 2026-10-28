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
    public static Image Image(string path, double size, bool animate = true, GifPlaybackMode mode = GifPlaybackMode.Hover, FrameworkElement? hoverScope = null, int coverFrame = 0)
    {
        var image=new Image{Width=size,Height=size,Stretch=Stretch.UniformToFill};
        GifPlayback.SetMode(image,mode);GifPlayback.SetScope(image,hoverScope);
        var bytes=AvatarImageCache.Encoded(path);int width=Math.Max(32,(int)size*2);
        BitmapSource cover=AvatarImageCache.First(path,width);image.Source=cover;
        bool gif=bytes.Length>=6&&Encoding.ASCII.GetString(bytes,0,6).StartsWith("GIF8",StringComparison.Ordinal);
        if(!gif)return image;
        coverFrame=Math.Clamp(coverFrame,0,499);
        bool coverRequested=false;
        BitmapSource? animatedSource=null;
        bool rewind=false,playing=false,failed=false;
        Window? owner=null;FrameworkElement? scope=null;
        void Reset()
        {
            playing=false;rewind=true;AnimationBehavior.SetAutoStart(image,false);
            if(AnimationBehavior.GetAnimator(image)is{} animator){animator.Pause();animator.Rewind();}
            image.Source=cover;
        }
        void BindScope()
        {
            var next=GifPlayback.GetScope(image)??image;
            if(scope==next)return;
            if(scope is not null){scope.MouseEnter-=HoverChanged;scope.MouseLeave-=HoverChanged;}
            scope=next;scope.MouseEnter+=HoverChanged;scope.MouseLeave+=HoverChanged;
        }
        void UpdatePlayback()
        {
            if(!image.IsLoaded)return;
            BindScope();
            if(failed||!animate||!GifPlayback.Allowed(image))
            {
                bool reset=failed||GifPlayback.GetMode(image)!=GifPlaybackMode.Always||!animate||Motion.Reduce;
                AnimationBehavior.SetAutoStart(image,false);AnimationBehavior.GetAnimator(image)?.Pause();playing=false;
                if(reset)Reset();else if(image.Source is null)image.Source=cover;
                return;
            }
            AnimationBehavior.SetAutoStart(image,true);
            if(AnimationBehavior.GetSourceStream(image)is null)
            {playing=true;AnimationBehavior.SetSourceStream(image,new MemoryStream(bytes,false));if(image.Source is null)image.Source=cover;return;}
            if(AnimationBehavior.GetAnimator(image)is{} animator)
            {
                if(rewind){animator.Pause();animator.Rewind();rewind=false;}
                if(animatedSource is not null)image.Source=animatedSource;
                if(animator.IsPaused)animator.Play();playing=true;
            }
        }
        async Task LoadCover()
        {
            if(coverRequested)return;coverRequested=true;
            try
            {
                cover=await AvatarImageCache.CoverAsync(path,coverFrame,width);
                if(!playing)image.Source=cover;
            }
            catch(Exception error)when(error is IOException or InvalidDataException or ArgumentException or NotSupportedException)
            {DiagnosticLog.Write("gif-cover",error);}
        }
        void WindowChanged(object? sender,EventArgs args)=>UpdatePlayback();
        void HoverChanged(object? sender,System.Windows.Input.MouseEventArgs args)=>UpdatePlayback();
        if(animate)
        {
            AnimationBehavior.SetAutoStart(image,false);AnimationBehavior.SetRepeatBehavior(image,RepeatBehavior.Forever);
            AnimationBehavior.SetCacheFramesInMemory(image,false);
            AnimationBehavior.AddLoadedHandler(image,(_,_)=>{animatedSource=image.Source as BitmapSource;UpdatePlayback();});
            AnimationBehavior.AddErrorHandler(image,(_,args)=>
            {
                // A malformed animation keeps its static cover and does not repeatedly
                // recreate an animator while the message/row is still on screen.
                if(failed)return;failed=true;Reset();
                DiagnosticLog.Write("gif-playback",args.Exception);
            });
        }
        GifPlayback.SetUpdate(image,UpdatePlayback);GifPlayback.SetReset(image,Reset);
        image.Loaded+=(_,_)=>
        {
            owner=Window.GetWindow(image);BindScope();
            if(owner is not null){owner.Activated+=WindowChanged;owner.Deactivated+=WindowChanged;owner.StateChanged+=WindowChanged;}
            Motion.Changed+=UpdatePlayback;UpdatePlayback();_=LoadCover();
        };
        image.Unloaded+=(_,_)=>
        {
            if(scope is not null){scope.MouseEnter-=HoverChanged;scope.MouseLeave-=HoverChanged;scope=null;}
            if(owner is not null){owner.Activated-=WindowChanged;owner.Deactivated-=WindowChanged;owner.StateChanged-=WindowChanged;owner=null;}
            Motion.Changed-=UpdatePlayback;AnimationBehavior.GetAnimator(image)?.Pause();playing=false;animatedSource=null;
        };
        image.IsVisibleChanged+=(_,_)=>UpdatePlayback();
        return image;
    }
    public static FrameworkElement Circle(string? path, double size, GifPlaybackMode mode = GifPlaybackMode.Hover, FrameworkElement? hoverScope = null, int coverFrame = 0)
    {
        Image image;
        try { image = path is null ? new Image { Width = size, Height = size, Stretch = Stretch.UniformToFill, Source = new BitmapImage(new Uri("pack://application:,,,/Wisp;component/Assets/wisp.png")) } : Image(path, size, mode: mode, hoverScope: hoverScope, coverFrame: coverFrame); }
        catch { image = new Image { Width = size, Height = size, Stretch = Stretch.UniformToFill, Source = new BitmapImage(new Uri("pack://application:,,,/Wisp;component/Assets/wisp.png")) }; }
        image.Clip = new EllipseGeometry(new Point(size / 2, size / 2), size / 2, size / 2);
        return image;
    }
}
