using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wisp.Core;

namespace Wisp.Desktop;

/// <summary>Read-only user card; edit controls exist only in the explicit editor.</summary>
public sealed class ProfileView : UserControl
{
    public ProfileView(UserProfile profile, string? avatarPath, string? bannerPath, Action? editProfile = null)
    {
        var panel = new StackPanel();
        panel.Children.Add(new ProfileArtwork(avatarPath,bannerPath,profile.AvatarCoverFrame,profile.BannerCoverFrame));
        var body = new StackPanel { Margin=Ui.Insets(Ui.L,Ui.L,Ui.L,Ui.Xl) };
        body.Children.Add(new TextBlock { Text=profile.Username,FontSize=Ui.Heading,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap });
        var presence=new StackPanel{Orientation=Orientation.Horizontal,Margin=Ui.Insets(0,Ui.S,0,Ui.L)};
        string status=profile.Status switch{"idle"=>"Нет на месте","dnd"=>"Не беспокоить","offline" or "invisible"=>"Не в сети",_=>"В сети"};
        var dot=new Border{Width=8,Height=8,CornerRadius=new CornerRadius(4),VerticalAlignment=VerticalAlignment.Center,Margin=Ui.Insets(0,0,Ui.S,0)};
        dot.SetResourceReference(Border.BackgroundProperty,profile.Status switch{"idle"=>"Warning","dnd"=>"Danger","offline" or "invisible"=>"TextDisabled",_=>"Success"});
        presence.Children.Add(dot);presence.Children.Add(Ui.Label(status,Ui.Caption,true));body.Children.Add(presence);
        void Section(string title,string text)
        {
            var label=Ui.Label(title,Ui.Caption,true);label.FontWeight=FontWeights.SemiBold;label.Margin=Ui.Insets(0,Ui.M,0,Ui.S);body.Children.Add(label);
            body.Children.Add(new TextBlock{Text=text,TextWrapping=TextWrapping.Wrap,FontSize=Ui.Body});
        }
        if(!string.IsNullOrWhiteSpace(profile.Game))Section("АКТИВНОСТЬ","Играет в "+profile.Game);
        Section("ОБО МНЕ",string.IsNullOrWhiteSpace(profile.About)?"Не указано":profile.About);
        if(editProfile is not null)
        {
            var edit=Buttons.Create("Редактировать профиль",editProfile,icon:"pencil",style:"Button.Primary");edit.HorizontalAlignment=HorizontalAlignment.Left;edit.Margin=Ui.Insets(0,Ui.Xl,0,0);body.Children.Add(edit);
        }
        panel.Children.Add(body);Content=panel;
    }
}

/// <summary>One shared 16:9 banner and overlapping avatar composition for view and edit.</summary>
internal sealed class ProfileArtwork : Grid
{
    public ProfileArtwork(string? avatarPath,string? bannerPath,int avatarCover,int bannerCover,Action? editAvatar=null,Action? editBanner=null)
    {
        double size=Ui.Metric("Size.ProfileAvatar")*2;
        RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
        RowDefinitions.Add(new RowDefinition{Height=new GridLength(size/2+Ui.S)});
        var banner=new Border{ClipToBounds=true};banner.SetResourceReference(Border.BackgroundProperty,"SurfaceElevated");
        if(bannerPath is not null&&File.Exists(bannerPath))
        {
            try
            {
                var image=AvatarCatalog.Image(bannerPath,Ui.Metric("Width.DialogMedium"),mode:GifPlaybackMode.Always,coverFrame:bannerCover);
                image.Width=image.Height=double.NaN;image.Stretch=Stretch.UniformToFill;banner.Child=image;
            }
            catch(Exception error)when(error is IOException or NotSupportedException or ArgumentException){DiagnosticLog.Write("profile-image",error);}
        }
        FrameworkElement bannerContent=editBanner is null?banner:EditableImage(banner,"Изменить баннер",editBanner,false);
        Children.Add(new AspectRatioFrame{Ratio=16d/9,Child=bannerContent});
        var avatarImage=AvatarCatalog.Circle(avatarPath,size,GifPlaybackMode.Always,coverFrame:avatarCover);
        FrameworkElement avatarContent=editAvatar is null?avatarImage:EditableImage(avatarImage,"Изменить аватар",editAvatar,true);
        var avatar=new Border{Child=avatarContent,Width=size+8,Height=size+8,BorderThickness=new Thickness(4),CornerRadius=new CornerRadius(size/2+4),HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Top,Margin=Ui.Insets(Ui.L,-size/2-4,0,0)};
        avatar.SetResourceReference(Border.BorderBrushProperty,"Surface");avatar.SetResourceReference(Border.BackgroundProperty,"Surface");Grid.SetRow(avatar,1);Children.Add(avatar);
    }

    private static FrameworkElement EditableImage(FrameworkElement image,string label,Action action,bool circle)
    {
        var layer=new Grid();layer.Children.Add(image);
        var overlay=new Border{Visibility=Visibility.Collapsed,IsHitTestVisible=false,CornerRadius=circle?new CornerRadius(Ui.Metric("Size.ProfileAvatar")):Ui.Radius()};
        overlay.SetResourceReference(Border.BackgroundProperty,"Overlay");
        var pencil=new Icon{Kind="pencil",HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};pencil.SetResourceReference(Icon.ForegroundProperty,"OnDanger");overlay.Child=pencil;layer.Children.Add(overlay);
        var button=Buttons.Create(label,action,label,style:"Button.Ghost");button.Content=layer;button.Margin=Ui.Insets(0);button.Padding=Ui.Insets(0);button.BorderThickness=new Thickness(0);button.HorizontalContentAlignment=HorizontalAlignment.Stretch;button.VerticalContentAlignment=VerticalAlignment.Stretch;
        void Update()=>overlay.Visibility=button.IsMouseOver||button.IsKeyboardFocusWithin?Visibility.Visible:Visibility.Collapsed;
        button.MouseEnter+=(_,_)=>Update();button.MouseLeave+=(_,_)=>Update();button.IsKeyboardFocusWithinChanged+=(_,_)=>Update();
        return button;
    }
}

/// <summary>An explicit cancellable draft. Cropped temporary images are owned until save/cancel.</summary>
public sealed class ProfileEditor : UserControl
{
    private readonly Func<UserProfile,Task<UserProfile>> save;
    private readonly Action<string>? reportError;
    private readonly ContentControl body=new();
    private readonly TextBlock feedback=new(){TextWrapping=TextWrapping.Wrap,Visibility=Visibility.Collapsed};
    private readonly Dictionary<bool,EditedImage> editedImages=new();
    private UserProfile profile;
    private string? avatarPath,bannerPath;
    private TextBox? name,about;
    private bool saving,unloaded;
    public bool IsEditing{get;private set;}
    public event Action<UserProfile>? Saved;

    public ProfileEditor(UserProfile profile,string? avatarPath,string? bannerPath,Func<UserProfile,Task<UserProfile>> save,bool initialEditing=false,Action<string>? reportError=null)
    {
        this.profile=profile;this.avatarPath=avatarPath;this.bannerPath=bannerPath;this.save=save;this.reportError=reportError;
        var root=new StackPanel();root.Children.Add(body);feedback.SetResourceReference(TextBlock.ForegroundProperty,"Danger");feedback.Margin=Ui.Insets(Ui.L,Ui.S);root.Children.Add(feedback);Content=root;
        Loaded+=(_,_)=>unloaded=false;Unloaded+=(_,_)=>{unloaded=true;if(!saving)CleanupImages();};
        if(initialEditing)BeginEdit();else View();
    }
    private void CleanupImages(){foreach(var image in editedImages.Values)image.Dispose();editedImages.Clear();}
    private void View(){IsEditing=false;body.Content=new ProfileView(profile,avatarPath,bannerPath,BeginEdit);}
    private void BeginEdit()
    {
        if(saving)return;IsEditing=true;feedback.Visibility=Visibility.Collapsed;
        var panel=new StackPanel();var artwork=new ContentControl();panel.Children.Add(artwork);
        string? draftAvatar=avatarPath,draftBanner=bannerPath;
        int avatarCover=profile.AvatarCoverFrame,bannerCover=profile.BannerCoverFrame;
        void Error(Exception error){feedback.Text=error.Message;feedback.Visibility=Visibility.Visible;DiagnosticLog.Write("profile-editor",error);reportError?.Invoke(error.Message);}
        void RenderImages()=>artwork.Content=new ProfileArtwork(draftAvatar,draftBanner,avatarCover,bannerCover,()=>Pick(true),()=>Pick(false));
        void Pick(bool isAvatar)
        {
            try
            {
                var picker=new Microsoft.Win32.OpenFileDialog{Filter="Изображения|*.png;*.jpg;*.jpeg;*.gif",CheckFileExists=true};
                var owner=Window.GetWindow(this);if(owner is null||picker.ShowDialog(owner)!=true)return;
                ValidateImage(picker.FileName);
                var edited=ImageEditor.Show(owner,picker.FileName,isAvatar?ImageEditKind.UserAvatar:ImageEditKind.ProfileBanner);
                if(edited is null)return;
                if(editedImages.Remove(isAvatar,out var previous))previous.Dispose();editedImages[isAvatar]=edited;
                if(isAvatar){draftAvatar=edited.Path;avatarCover=edited.CoverFrame;}else{draftBanner=edited.Path;bannerCover=edited.CoverFrame;}
                RenderImages();feedback.Visibility=Visibility.Collapsed;
            }
            catch(Exception error){Error(error);}
        }
        RenderImages();var fields=new StackPanel{Margin=Ui.Insets(Ui.L,Ui.M,Ui.L,Ui.L)};
        var imageActions=new WrapPanel();
        imageActions.Children.Add(Buttons.Create("Удалить аватар",()=>{draftAvatar=null;avatarCover=0;RenderImages();},style:"Button.Ghost"));
        imageActions.Children.Add(Buttons.Create("Удалить баннер",()=>{draftBanner=null;bannerCover=0;RenderImages();},style:"Button.Ghost"));fields.Children.Add(imageActions);
        fields.Children.Add(Ui.Label("Имя пользователя",Ui.Caption,true));name=new TextBox{Text=profile.Username,MaxLength=32,Margin=Ui.Insets(0,Ui.Xs,0,Ui.M)};Ui.Name(name,"Имя пользователя");fields.Children.Add(name);
        fields.Children.Add(Ui.Label("Статус",Ui.Caption,true));string[] codes=["online","idle","dnd","invisible"];
        var status=new ComboBox{ItemsSource=new[]{"В сети","Нет на месте","Не беспокоить","Невидимый"},SelectedIndex=Math.Max(0,Array.IndexOf(codes,profile.Status)),Margin=Ui.Insets(0,Ui.Xs,0,Ui.M)};Ui.Name(status,"Статус профиля");fields.Children.Add(status);
        fields.Children.Add(Ui.Label("О себе",Ui.Caption,true));about=new TextBox{Text=profile.About,MaxLength=2000,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MinHeight=Ui.Metric("Height.Multiline"),VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Margin=Ui.Insets(0,Ui.Xs,0,Ui.M)};Ui.Name(about,"О себе");fields.Children.Add(about);
        fields.Children.Add(Ui.Label("PNG, JPEG или GIF · до 4 МБ · до 2048×2048",Ui.Caption,true));
        var actions=new StackPanel{Orientation=Orientation.Horizontal,Margin=Ui.Insets(0,Ui.L,0,0)};
        var cancel=Buttons.Create("Отмена",()=>{if(saving)return;CleanupImages();feedback.Visibility=Visibility.Collapsed;View();},style:"Button.Ghost");
        var accept=Buttons.Create("Сохранить",()=>{},style:"Button.Primary");
        accept.Click+=async(_,_)=>
        {
            if(saving)return;
            try
            {
                saving=true;body.IsEnabled=false;Buttons.Label(accept,"Сохранение…","check");ValidateDraft(name.Text,about.Text,draftAvatar,draftBanner);
                var proposed=profile with{Username=name.Text.Trim(),About=about.Text,Status=codes[status.SelectedIndex],AvatarPath=draftAvatar,BannerPath=draftBanner,AvatarCoverFrame=avatarCover,BannerCoverFrame=bannerCover};
                var saved=await save(proposed);profile=saved;avatarPath=saved.AvatarPath;bannerPath=saved.BannerPath;CleanupImages();feedback.Visibility=Visibility.Collapsed;View();Saved?.Invoke(saved);
            }
            catch(Exception error){Error(error);}
            finally{saving=false;body.IsEnabled=true;Buttons.Label(accept,"Сохранить","check");if(unloaded)CleanupImages();}
        };
        actions.Children.Add(accept);actions.Children.Add(cancel);fields.Children.Add(actions);panel.Children.Add(fields);body.Content=panel;
    }

    public static void ValidateDraft(string username,string about,string? avatarPath,string? bannerPath)
    {
        if(!System.Text.RegularExpressions.Regex.IsMatch(username.Trim(),@"^[\p{L}\p{N}_\-.]{3,32}$",System.Text.RegularExpressions.RegexOptions.CultureInvariant))throw new ArgumentException("Имя: 3–32 буквы, цифры, _, - или точка.");
        if(about.Length>2000)throw new ArgumentException("Описание: не более 2000 символов.");
        if(avatarPath is not null)ValidateImage(avatarPath);if(bannerPath is not null)ValidateImage(bannerPath);
    }
    public static void ValidateImage(string path)
    {
        var extension=Path.GetExtension(path).ToLowerInvariant();
        if(extension is not ".png" and not ".jpg" and not ".jpeg" and not ".gif" and not ".image" and not ".img")throw new InvalidDataException("Поддерживаются PNG, JPEG и GIF.");
        var info=new FileInfo(path);if(!info.Exists||info.Length==0)throw new InvalidDataException("Файл изображения отсутствует или пуст.");if(info.Length>4*1024*1024)throw new InvalidDataException("Изображение профиля должно быть не больше 4 МБ.");
        using(var stream=File.OpenRead(path))
        {
            Span<byte> header=stackalloc byte[8];int count=stream.Read(header);
            bool supported=count>=8&&header.SequenceEqual(new byte[]{137,80,78,71,13,10,26,10})||count>=6&&(header[..6].SequenceEqual("GIF87a"u8)||header[..6].SequenceEqual("GIF89a"u8))||count>=3&&header[0]==255&&header[1]==216&&header[2]==255;
            if(!supported)throw new InvalidDataException("Содержимое файла должно быть изображением PNG, JPEG или GIF.");
        }
        AvatarCatalog.Validate(path);
    }
}
