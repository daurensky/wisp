using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Wisp.Core;
using Wisp.Desktop.Media;

namespace Wisp.Desktop;

public enum MiniDockPosition { TopRight, BottomRight }

/// <summary>One stream surface: central viewing, internal draggable PiP, or explicit monitor fullscreen.</summary>
public sealed class ScreenViewer
{
    private readonly Window owner;
    private readonly Grid workspace;
    private readonly Grid overlay;
    private readonly Border surface;
    private readonly ScreenViewerSurface view=new();
    private readonly string title;
    private readonly bool ownStream;
    private readonly Dictionary<string,(Border Ring,PeerPresence Presence)> viewerRings=new(StringComparer.Ordinal);
    private WriteableBitmap? bitmap;
    private VoiceService? observedVoice;
    private string localUserId="",viewerKey="";
    private bool closed,fullscreen,loading=true,changingVolume,dragging;
    private float volume=1,previousVolume=1;
    private double aspect=16d/9;
    private Point dragPointer,dragOrigin;
    private (WindowStyle Style,ResizeMode Resize,WindowState State,Rect Bounds,bool Topmost,double MinWidth,double MinHeight) previous;
    public Image Target=>view.VideoImage;
    public bool IsMini { get;private set; }
    public bool IsFullscreen=>fullscreen;
    public bool IsClosed=>closed;
    public bool IsLoading=>loading;
    public bool IsVisible=>!closed&&surface.Visibility==Visibility.Visible;
    public float Volume=>volume;
    public MiniDockPosition MiniPosition { get;private set; }=MiniDockPosition.BottomRight;
    public event Action? Closed;
    public event Action? StopRequested;
    public event Action<bool>? MuteChanged;
    public event Action<float>? VolumeChanged;
    public event Action? PresentationChanged;

    public ScreenViewer(Window owner,string title):this(owner,title,ModalHost.Surface(owner)) { }
    public ScreenViewer(Window owner,string title,Grid centerHost,bool ownStream=false)
    {
        this.owner=owner;this.title=title;this.ownStream=ownStream;workspace=centerHost;overlay=ModalHost.Surface(owner);
        view.Caption.Text=title;view.Caption.MaxWidth=Math.Max(120,owner.ActualWidth/2);
        surface=new Border{Child=view,Focusable=true,BorderThickness=new Thickness(Ui.Metric("Size.Hairline"))};
        surface.SetResourceReference(Border.BackgroundProperty,"VideoBackground");surface.SetResourceReference(Border.BorderBrushProperty,"Border");
        view.MiniButton.Click+=(_,_)=>ToggleMini();view.FullscreenButton.Click+=(_,_)=>ToggleFullscreen();
        view.CloseButton.Click+=(_,_)=>RequestClose();
        view.MuteButton.Click+=(_,_)=>ChangeVolume(volume>0?0:previousVolume>0?previousVolume:1);
        view.VolumeSlider.ValueChanged+=(_,args)=>{if(!changingVolume)ChangeVolume((float)(args.NewValue/100));};
        view.MouseEnter+=(_,_)=>ShowControls();view.MouseLeave+=(_,_)=>{if(!view.IsKeyboardFocusWithin&&!IsMini)Motion.Fade(view.Toolbar,view.Toolbar.Opacity,0);};
        view.IsKeyboardFocusWithinChanged+=(_,_)=>{if(view.IsKeyboardFocusWithin)ShowControls();};
        surface.PreviewKeyDown+=(_,args)=>
        {
            if(args.Key==Key.Escape){args.Handled=true;if(fullscreen)ToggleFullscreen();else if(IsMini)RequestClose();else Close();}
            else if(args.Key==Key.F11){args.Handled=true;ToggleFullscreen();}
        };
        Target.MouseLeftButtonDown+=(_,args)=>{if(args.ClickCount==2){args.Handled=true;if(IsMini)ToggleMini();else ToggleFullscreen();}};
        surface.PreviewMouseLeftButtonDown+=BeginDrag;surface.PreviewMouseMove+=Drag;surface.PreviewMouseLeftButtonUp+=EndDrag;
        surface.LostMouseCapture+=(_,_)=>{if(dragging){dragging=false;DockMini(MiniPosition,false);}};
        surface.SizeChanged+=(_,_)=>ClipSurface();owner.SizeChanged+=OwnerResized;
        workspace.Children.Add(surface);surface.Focus();ShowControls();
    }

    private void RequestClose()
    {
        if(ownStream&&IsMini&&StopRequested is{} stop){stop();return;}
        Close();
    }
    private void ShowControls()=>Motion.Fade(view.Toolbar,view.Toolbar.Opacity,1);
    public void SetVolume(float value)=>ApplyVolume(value,false);
    private void ChangeVolume(float value)=>ApplyVolume(value,true);
    private void ApplyVolume(float value,bool notify)
    {
        value=float.IsFinite(value)?Math.Clamp(value,0,1):1;
        bool wasMuted=volume==0;
        volume=value;if(value>0)previousVolume=value;
        changingVolume=true;view.VolumeSlider.Value=value*100;changingVolume=false;
        view.MuteButton.Content=new Icon{Kind=value==0?"volume-x":"volume-2",Width=18,Height=18};
        string label=value==0?"Включить звук демонстрации":"Выключить звук демонстрации";
        view.MuteButton.ToolTip=label;Ui.Name(view.MuteButton,label);view.VolumeSlider.ToolTip="Громкость демонстрации · "+(value*100).ToString("0")+"%";
        if(notify){VolumeChanged?.Invoke(value);if(wasMuted!=(value==0))MuteChanged?.Invoke(value==0);}
    }

    public void SetVideoAspect(int width,int height)
    {
        if(width<=0||height<=0)return;
        aspect=Math.Clamp((double)width/height,.1,10);
        if(IsMini)DockMini(MiniPosition,false);
    }
    public void ToggleMini()
    {
        if(closed)return;
        if(fullscreen)ToggleFullscreen();IsMini=!IsMini;
        view.Caption.Visibility=IsMini?Visibility.Collapsed:Visibility.Visible;
        view.CloseButton.SetResourceReference(Control.BackgroundProperty,IsMini?"Danger":"VideoControlSurface");
        view.CloseButton.ToolTip=IsMini&&ownStream?"Завершить демонстрацию":"Закрыть просмотр";Ui.Name(view.CloseButton,(string)view.CloseButton.ToolTip);
        view.MiniButton.Content=new Icon{Kind=IsMini?"arrow-up-left":"arrow-down-right",Width=18,Height=18};
        view.MiniButton.ToolTip=IsMini?"Развернуть в центральной области":"Уменьшить";
        if(IsMini)
        {
            MoveSurface(overlay);Panel.SetZIndex(surface,1000);surface.CornerRadius=Ui.Radius("Radius.Dialog");
            var shadow=new DropShadowEffect{Color=Colors.Black,Opacity=.25,BlurRadius=12,ShadowDepth=2,RenderingBias=RenderingBias.Performance};shadow.Freeze();surface.Effect=shadow;
            DockMini(MiniPosition,false);
        }
        else
        {
            dragging=false;surface.ReleaseMouseCapture();MoveSurface(workspace);Panel.SetZIndex(surface,0);Motion.Stop(surface);surface.RenderTransform=Transform.Identity;
            surface.Width=surface.Height=double.NaN;surface.Margin=Ui.Insets(0);surface.HorizontalAlignment=HorizontalAlignment.Stretch;surface.VerticalAlignment=VerticalAlignment.Stretch;surface.CornerRadius=new CornerRadius(0);surface.Effect=null;surface.Clip=null;
        }
        ShowControls();PresentationChanged?.Invoke();
    }
    private Size OverlaySize()=>new(Math.Max(1,overlay.ActualWidth>0?overlay.ActualWidth:owner.ActualWidth>0?owner.ActualWidth:owner.Width),Math.Max(1,overlay.ActualHeight>0?overlay.ActualHeight:owner.ActualHeight>0?owner.ActualHeight:owner.Height));
    public void DockMini(MiniDockPosition position,bool animate=true)
    {
        if(!IsMini||closed)return;
        MiniPosition=position;var bounds=OverlaySize();double edge=Math.Min(Ui.L,Math.Min(bounds.Width,bounds.Height)/8);
        double width=Math.Min(Ui.Metric("Width.PictureInPicture"),Math.Max(1,bounds.Width-edge*2));double height=width/aspect;
        if(height>bounds.Height-edge*2){height=Math.Max(1,bounds.Height-edge*2);width=height*aspect;}
        surface.Width=width;surface.Height=height;surface.HorizontalAlignment=HorizontalAlignment.Left;surface.VerticalAlignment=VerticalAlignment.Top;
        double x=Math.Max(edge,bounds.Width-width-edge);double y=position==MiniDockPosition.TopRight?edge:Math.Max(edge,bounds.Height-height-edge);
        var from=surface.Margin;Motion.Stop(surface);surface.Margin=new Thickness(x,y,0,0);
        var transform=new TranslateTransform();surface.RenderTransform=transform;
        var duration=animate?Motion.Duration(surface,"Normal"):new Duration(TimeSpan.Zero);
        if(duration.TimeSpan>TimeSpan.Zero)
        {
            var ease=(IEasingFunction)Application.Current.FindResource("Motion.EaseOut");
            transform.BeginAnimation(TranslateTransform.XProperty,new DoubleAnimation(from.Left-x,0,duration){EasingFunction=ease,FillBehavior=FillBehavior.Stop});
            transform.BeginAnimation(TranslateTransform.YProperty,new DoubleAnimation(from.Top-y,0,duration){EasingFunction=ease,FillBehavior=FillBehavior.Stop});
        }
        ClipSurface();
    }
    private void ClipSurface()
    {
        if(!IsMini){surface.Clip=null;return;}
        if(surface.ActualWidth>0&&surface.ActualHeight>0)surface.Clip=new RectangleGeometry(new Rect(0,0,surface.ActualWidth,surface.ActualHeight),Ui.M,Ui.M);
    }
    private static bool IsControl(DependencyObject? element)
    {
        while(element is not null)
        {
            if(element is ButtonBase or Slider or Thumb)return true;
            element=element is Visual?VisualTreeHelper.GetParent(element):LogicalTreeHelper.GetParent(element);
        }
        return false;
    }
    private void BeginDrag(object sender,MouseButtonEventArgs args)
    {
        if(!IsMini||args.ClickCount!=1||IsControl(args.OriginalSource as DependencyObject))return;
        dragOrigin=surface.TranslatePoint(new Point(),overlay);Motion.Stop(surface);surface.RenderTransform=Transform.Identity;surface.Margin=new Thickness(dragOrigin.X,dragOrigin.Y,0,0);
        dragPointer=args.GetPosition(overlay);dragging=true;surface.CaptureMouse();args.Handled=true;
    }
    private void Drag(object sender,MouseEventArgs args)
    {
        if(!dragging||args.LeftButton!=MouseButtonState.Pressed)return;
        var point=args.GetPosition(overlay);var bounds=OverlaySize();double x=Math.Clamp(dragOrigin.X+point.X-dragPointer.X,0,Math.Max(0,bounds.Width-surface.Width));double y=Math.Clamp(dragOrigin.Y+point.Y-dragPointer.Y,0,Math.Max(0,bounds.Height-surface.Height));
        surface.Margin=new Thickness(x,y,0,0);args.Handled=true;
    }
    private void EndDrag(object sender,MouseButtonEventArgs args)
    {
        if(!dragging)return;dragging=false;surface.ReleaseMouseCapture();var bounds=OverlaySize();
        DockMini(surface.Margin.Top+surface.Height/2<bounds.Height/2?MiniDockPosition.TopRight:MiniDockPosition.BottomRight);args.Handled=true;
    }
    private void OwnerResized(object sender,SizeChangedEventArgs args){if(IsMini&&!dragging)DockMini(MiniPosition,false);view.Caption.MaxWidth=Math.Max(120,owner.ActualWidth/2);}

    public void UpdateViewers(IReadOnlyList<PeerPresence> viewers,Func<UserProfile,string?> avatarPath,VoiceService voice,string localUserId)
    {
        if(closed)return;
        this.localUserId=localUserId;
        if(observedVoice!=voice){if(observedVoice is not null)observedVoice.SpeakingChanged-=OnSpeech;observedVoice=voice;voice.SpeakingChanged+=OnSpeech;}
        string key=string.Join("|",viewers.Select(person=>$"{person.Profile.Id}:{person.Profile.Username}:{person.Profile.AvatarRevision}:{person.Profile.AvatarCoverFrame}:{person.Muted}:{person.Deafened}"));
        if(key!=viewerKey)
        {
            viewerKey=key;viewerRings.Clear();view.ViewerAvatars.Children.Clear();
            foreach(var person in viewers)
            {
                var avatar=AvatarCatalog.Circle(avatarPath(person.Profile),24,GifPlaybackMode.Hover,coverFrame:person.Profile.AvatarCoverFrame);
                var ring=new Border{Child=avatar,Width=30,Height=30,CornerRadius=new CornerRadius(15),BorderThickness=new Thickness(2),Margin=new Thickness(2),ToolTip=person.Profile.Username};ring.SetResourceReference(Border.BackgroundProperty,"VideoControlSurface");GifPlayback.SetScope(avatar,ring);viewerRings[person.Profile.Id]=(ring,person);view.ViewerAvatars.Children.Add(ring);
            }
        }
        foreach(var id in viewerRings.Keys)UpdateSpeech(id);
        view.ViewersScroll.Visibility=viewers.Count==0?Visibility.Collapsed:Visibility.Visible;
    }
    private void OnSpeech(string id,bool speaking)
    {
        if(closed||owner.Dispatcher.HasShutdownStarted)return;
        owner.Dispatcher.BeginInvoke(()=>{if(!closed)UpdateSpeech(id==VoiceService.LocalSpeakerId?localUserId:id);});
    }
    private void UpdateSpeech(string id)
    {
        if(!viewerRings.TryGetValue(id,out var entry))return;
        bool speaking=!entry.Presence.Muted&&!entry.Presence.Deafened&&observedVoice?.IsSpeaking(id==localUserId?VoiceService.LocalSpeakerId:id)==true;
        if(speaking)entry.Ring.SetResourceReference(Border.BorderBrushProperty,"Success");else entry.Ring.BorderBrush=Brushes.Transparent;
    }

    public void ToggleFullscreen()
    {
        if(closed)return;
        if(fullscreen)
        {
            fullscreen=false;owner.WindowStyle=previous.Style;owner.ResizeMode=previous.Resize;owner.Topmost=previous.Topmost;
            owner.WindowState=WindowState.Normal;owner.MinWidth=previous.MinWidth;owner.MinHeight=previous.MinHeight;owner.Left=previous.Bounds.Left;owner.Top=previous.Bounds.Top;owner.Width=previous.Bounds.Width;owner.Height=previous.Bounds.Height;owner.WindowState=previous.State;
            if(owner is ThemedWindow restored)restored.IsImmersive=false;
            MoveSurface(workspace);Panel.SetZIndex(surface,0);view.FullscreenButton.Content=new Icon{Kind="maximize",Width=18,Height=18};PresentationChanged?.Invoke();return;
        }
        if(IsMini)ToggleMini();
        previous=(owner.WindowStyle,owner.ResizeMode,owner.WindowState,owner.WindowState==WindowState.Normal?new Rect(owner.Left,owner.Top,owner.Width,owner.Height):owner.RestoreBounds,owner.Topmost,owner.MinWidth,owner.MinHeight);
        var handle=new WindowInteropHelper(owner).Handle;var bounds=System.Windows.Forms.Screen.FromHandle(handle).Bounds;
        owner.MinWidth=0;owner.MinHeight=0;owner.WindowState=WindowState.Normal;owner.WindowStyle=WindowStyle.None;owner.ResizeMode=ResizeMode.NoResize;owner.Topmost=true;
        if(owner is ThemedWindow immersive)immersive.IsImmersive=true;
        MoveSurface(overlay);Panel.SetZIndex(surface,900);SetWindowPos(handle,new IntPtr(-1),bounds.Left,bounds.Top,bounds.Width,bounds.Height,0x0040);fullscreen=true;view.FullscreenButton.Content=new Icon{Kind="minimize",Width=18,Height=18};surface.Focus();PresentationChanged?.Invoke();
    }
    private void MoveSurface(Grid host){if(surface.Parent is Panel previousHost)previousHost.Children.Remove(surface);host.Children.Add(surface);}
    public void SetPaused(bool paused)=>view.Caption.Text=title+(paused?" · окно свёрнуто":"");
    public void SetUnavailable(string reason="Трансляция завершена")
    {
        if(closed)return;loading=false;Target.Source=null;bitmap=null;view.Status.Text=reason;view.Status.Visibility=Visibility.Visible;view.Caption.Text=title;
    }
    public void Present(byte[] bgra,int width,int height)
    {
        if(closed||width<=0||height<=0||(long)width*height*4>bgra.Length)return;
        if(bitmap is null||bitmap.PixelWidth!=width||bitmap.PixelHeight!=height){bitmap=new WriteableBitmap(width,height,96,96,PixelFormats.Bgr32,null);Target.Source=bitmap;SetVideoAspect(width,height);}
        bitmap.WritePixels(new Int32Rect(0,0,width,height),bgra,width*4,0);loading=false;view.Status.Visibility=Visibility.Collapsed;
    }
    public void PresentImage(ImageSource image){if(closed)return;bitmap=null;Target.Source=image;if(image.Width>0&&image.Height>0)SetVideoAspect((int)image.Width,(int)image.Height);loading=false;view.Status.Visibility=Visibility.Collapsed;}
    public void Close()
    {
        if(closed)return;if(fullscreen)ToggleFullscreen();closed=true;dragging=false;surface.ReleaseMouseCapture();Motion.Stop(surface);Motion.Stop(view.Toolbar);owner.SizeChanged-=OwnerResized;if(observedVoice is not null){observedVoice.SpeakingChanged-=OnSpeech;observedVoice=null;}viewerRings.Clear();view.ViewerAvatars.Children.Clear();if(surface.Parent is Panel parent)parent.Children.Remove(surface);Target.Source=null;bitmap=null;Closed?.Invoke();
    }
    public void Hide(){if(closed)return;if(fullscreen)ToggleFullscreen();surface.Visibility=Visibility.Collapsed;PresentationChanged?.Invoke();}
    public void Show(){if(closed)return;surface.Visibility=Visibility.Visible;MoveSurface(fullscreen||IsMini?overlay:workspace);Panel.SetZIndex(surface,IsMini?1000:fullscreen?900:0);if(IsMini)DockMini(MiniPosition,false);PresentationChanged?.Invoke();}
    [DllImport("user32.dll")]private static extern bool SetWindowPos(IntPtr window,IntPtr insertAfter,int x,int y,int width,int height,uint flags);
}
