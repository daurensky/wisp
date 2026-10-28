using System.Windows;
using System.Windows.Controls;
using Wisp.Core;

namespace Wisp.Desktop;

public sealed class CommunitySettingsContext
{
    public required string UserId { get; init; }
    public required CommunitySnapshot Snapshot { get; set; }
    public bool Online { get; init; }
    public required Func<Task<CommunitySnapshot>> Reload { get; init; }
    public required Func<CommunityManagement,Task> Apply { get; init; }
    public required Func<Community,FrameworkElement> Icon { get; init; }
    public required Func<EditedImage,Task> SaveIcon { get; init; }
    public required Func<string,Task<IReadOnlyList<MediaAsset>>> Assets { get; init; }
    public required Func<string,string,string,float,Task> Upload { get; init; }
    public required Action<string,float> Preview { get; init; }
    public Action<string,float>? PreviewVolume { get; init; }
    public required Func<string,bool> Confirm { get; init; }
    public Action<string>? ReportError { get; init; }
}

/// <summary>UI composition only; permissions and mutations remain in the supplied backend adapter.</summary>
public sealed class CommunitySettingsView : UserControl,IDisposable
{
    private readonly CommunitySettingsContext context;
    private readonly SettingsShell shell=new();
    private readonly TextBlock feedback=new(){TextWrapping=TextWrapping.Wrap,Visibility=Visibility.Collapsed};
    private EditedImage? draftIcon;
    private int request;
    private bool busy,disposed;
    public SettingsShell Shell=>shell;

    public CommunitySettingsView(CommunitySettingsContext context,string initialSection="overview")
    {
        this.context=context;
        foreach(var (key,label,icon) in new[]{("overview","Обзор сервера","settings"),("members","Участники и права","users"),("emoji","Эмодзи","face-slightly-smiling"),("sound","Звуковая панель","volume-2")})shell.Sections.Add(new(key,label,icon));
        shell.SectionSelected+=key=>_ = SelectSectionAsync(key);
        var footer=new StackPanel();footer.Children.Add(Ui.Label(context.Snapshot.Community.Name,Ui.Subtitle));footer.Children.Add(Ui.Label("Настройки сервера",Ui.Caption,true));shell.Footer=footer;
        feedback.SetResourceReference(TextBlock.ForegroundProperty,"Danger");feedback.Margin=Ui.Insets(Ui.L,Ui.S);
        var root=new DockPanel();DockPanel.SetDock(feedback,Dock.Bottom);root.Children.Add(feedback);root.Children.Add(shell);Content=root;
        Unloaded+=(_,_)=>Dispose();_ = SelectSectionAsync(initialSection);
    }

    public async Task SelectSectionAsync(string section)
    {
        if(disposed)return;
        int generation=++request;shell.Select(section);feedback.Visibility=Visibility.Collapsed;
        shell.PageContent=Ui.Label("Загрузка…",secondary:true);
        try
        {
            var snapshot=await context.Reload();
            if(disposed||generation!=request)return;
            context.Snapshot=snapshot;RequireManagement(snapshot);
            var footer=new StackPanel();footer.Children.Add(Ui.Label(context.Snapshot.Community.Name,Ui.Subtitle));footer.Children.Add(Ui.Label("Настройки сервера",Ui.Caption,true));shell.Footer=footer;
            FrameworkElement content;
            if(section is "emoji" or "sound")
            {
                var assets=await context.Assets(section);if(disposed||generation!=request)return;content=MediaPage(section,assets);
            }
            else content=section=="members"?MembersPage():OverviewPage();
            shell.PageContent=content;
        }
        catch(Exception error){if(!disposed&&generation==request){shell.PageContent=Ui.Label("Не удалось открыть настройки.",secondary:true);Error(error);}}
    }
    private void Error(Exception error){feedback.Text=error.Message;feedback.Visibility=Visibility.Visible;DiagnosticLog.Write("community-settings",error);context.ReportError?.Invoke(error.Message);}
    private void RequireManagement(CommunitySnapshot snapshot)
    {
        if(snapshot.Community.OwnerId!=context.UserId&&snapshot.Roles?.GetValueOrDefault(context.UserId)!="admin")
            throw new UnauthorizedAccessException("Нет права управлять этим сервером.");
    }
    private async Task RunAsync(Func<Task> action)
    {
        if(busy||disposed)return;busy=true;shell.IsEnabled=false;feedback.Visibility=Visibility.Collapsed;
        try{RequireManagement(context.Snapshot);await action();}
        catch(Exception error){if(!disposed)Error(error);}
        finally{busy=false;if(!disposed)shell.IsEnabled=true;else{draftIcon?.Dispose();draftIcon=null;}}
    }
    private static StackPanel Page(string title,string? note=null)
    {
        var page=new StackPanel();page.Children.Add(new TextBlock{Text=title,FontSize=Ui.Heading,FontWeight=FontWeights.SemiBold,Margin=Ui.Insets(0,0,0,Ui.L)});
        if(note is not null){var text=Ui.Label(note,secondary:true);text.TextWrapping=TextWrapping.Wrap;text.Margin=Ui.Insets(0,0,0,Ui.L);page.Children.Add(text);}return page;
    }
    private Button ActionButton(string title,Func<Task> action,string style="Button.Secondary")
    {
        var button=Buttons.Create(title,()=>_ = RunAsync(action),style:style);button.HorizontalAlignment=HorizontalAlignment.Left;button.Margin=Ui.Insets(0,Ui.S,Ui.S,0);return button;
    }
    private FrameworkElement OverviewPage()
    {
        var target=context.Snapshot.Community;var page=Page("Обзор сервера");
        var icon=new ContentControl{Content=draftIcon is null?context.Icon(target):AvatarCatalog.Circle(draftIcon.Path,96,coverFrame:draftIcon.CoverFrame),HorizontalAlignment=HorizontalAlignment.Left,Margin=Ui.Insets(0,0,0,Ui.L)};page.Children.Add(icon);
        page.Children.Add(ActionButton("Изменить значок",()=>
        {
            var owner=Window.GetWindow(this);var file=new Microsoft.Win32.OpenFileDialog{Filter="Изображения|*.png;*.jpg;*.jpeg;*.gif"};
            if(owner is null||file.ShowDialog(owner)!=true)return Task.CompletedTask;
            ProfileEditor.ValidateImage(file.FileName);var edited=ImageEditor.Show(owner,file.FileName,ImageEditKind.ServerIcon);
            if(edited is null)return Task.CompletedTask;draftIcon?.Dispose();draftIcon=edited;icon.Content=AvatarCatalog.Circle(edited.Path,96,coverFrame:edited.CoverFrame);return Task.CompletedTask;
        }));
        page.Children.Add(Ui.Label("Название сервера",Ui.Caption,true));var name=new TextBox{Text=target.Name,MaxLength=80,Margin=Ui.Insets(0,Ui.Xs,0,Ui.L)};Ui.Name(name,"Название сервера");page.Children.Add(name);
        page.Children.Add(Ui.Label($"Участников: {context.Snapshot.Members.Count}",Ui.Caption,true));
        page.Children.Add(ActionButton("Сохранить изменения",async()=>
        {
            if(string.IsNullOrWhiteSpace(name.Text))throw new ArgumentException("Укажите название сервера.");
            await context.Apply(new("rename",Name:name.Text.Trim()));
            if(draftIcon is not null){await context.SaveIcon(draftIcon);draftIcon.Dispose();draftIcon=null;}
            await SelectSectionAsync("overview");
        },"Button.Primary"));return page;
    }
    private string Role(string id)=>id==context.Snapshot.Community.OwnerId?"owner":context.Snapshot.Roles?.GetValueOrDefault(id)??"member";
    private static string RoleLabel(string role)=>role switch{"owner"=>"Владелец","admin"=>"Администратор",_=>"Участник"};
    private FrameworkElement MembersPage()
    {
        var page=Page("Участники и права","Владелец назначает администраторов. Администратор управляет сервером, медиа и обычными участниками.");
        bool owner=Role(context.UserId)=="owner";
        foreach(var member in context.Snapshot.Members)
        {
            var role=Role(member.Id);var row=new DockPanel{Margin=Ui.Insets(0,Ui.S)};var actions=new WrapPanel();DockPanel.SetDock(actions,Dock.Right);row.Children.Add(actions);
            if(context.Online&&owner&&role!="owner")actions.Children.Add(ActionButton(role=="admin"?"Снять администратора":"Назначить администратора",async()=>
            {
                if(!context.Confirm($"Изменить роль {member.Username}?"))return;
                await context.Apply(new("role",UserId:member.Id,Role:role=="admin"?"member":"admin"));await SelectSectionAsync("members");
            }));
            if(context.Online&&member.Id!=context.UserId&&role!="owner"&&(owner||role!="admin"))actions.Children.Add(ActionButton("Исключить",async()=>
            {
                if(!context.Confirm($"Исключить {member.Username} из сервера?"))return;
                await context.Apply(new("kick",UserId:member.Id));await SelectSectionAsync("members");
            },"Button.Danger"));
            var label=new StackPanel{VerticalAlignment=VerticalAlignment.Center};label.Children.Add(Ui.Label(member.Username));label.Children.Add(Ui.Label(RoleLabel(role),Ui.Caption,true));row.Children.Add(label);page.Children.Add(row);
        }
        return page;
    }
    private FrameworkElement MediaPage(string kind,IReadOnlyList<MediaAsset> assets)
    {
        var page=Page(kind=="emoji"?"Эмодзи сервера":"Звуковая панель");
        page.Children.Add(ActionButton(kind=="emoji"?"Загрузить эмодзи":"Загрузить звук",()=>Upload(kind),"Button.Primary"));
        if(assets.Count==0){var empty=Ui.Label("Нет файлов",secondary:true);empty.Margin=Ui.Insets(0,Ui.L);page.Children.Add(empty);}
        foreach(var asset in assets.OrderBy(a=>a.Name))
        {
            var row=new DockPanel{Margin=Ui.Insets(0,Ui.M)};var actions=new WrapPanel();DockPanel.SetDock(actions,Dock.Right);row.Children.Add(actions);
            if(kind=="sound")actions.Children.Add(ActionButton("Прослушать",()=>{context.Preview(asset.LocalPath,asset.Volume);return Task.CompletedTask;}));
            actions.Children.Add(ActionButton("Изменить",()=>EditAsset(kind,asset)));
            actions.Children.Add(ActionButton("Удалить",async()=>{if(!context.Confirm($"Удалить «{(kind=="emoji"?EmojiName(asset.Name):asset.Name)}»?"))return;await context.Apply(new("deleteAsset",Kind:kind,AssetId:asset.Id));await SelectSectionAsync(kind);},"Button.Danger"));
            if(kind=="emoji")
            {
                var image=AvatarCatalog.Image(asset.LocalPath,36,mode:GifPlaybackMode.Hover);image.Margin=Ui.Insets(0,0,Ui.M,0);DockPanel.SetDock(image,Dock.Left);row.Children.Add(image);
            }
            var label=Ui.Label(kind=="emoji"?EmojiName(asset.Name):asset.Name);label.VerticalAlignment=VerticalAlignment.Center;row.Children.Add(label);page.Children.Add(row);
        }
        return page;
    }
    public static string EmojiName(string name)
    {
        string plain=Path.GetFileNameWithoutExtension(name).Trim(':');plain=new string(plain.Select(c=>char.IsLetterOrDigit(c)||c is '_' or '-'?c:'_').Take(32).ToArray());return ":"+(plain.Length==0?"emoji":plain)+":";
    }
    private async Task EditAsset(string kind,MediaAsset asset)
    {
        var owner=Window.GetWindow(this);if(owner is null)return;
        var form=new FormDialog(owner,kind=="emoji"?"Название эмодзи":"Настройки звука");var name=form.Field(kind=="emoji"?"Название без двоеточий":"Название",kind=="emoji"?EmojiName(asset.Name).Trim(':'):asset.Name);
        float volume=asset.Volume;
        if(kind=="sound")AddVolume(form,asset.LocalPath,volume,value=>volume=value);
        form.Accept();if(form.ShowDialog()!=true)return;
        string next=kind=="emoji"?name.Text.Trim().Trim(':'):name.Text.Trim();if(string.IsNullOrWhiteSpace(next))throw new ArgumentException("Укажите название.");
        await context.Apply(new("renameAsset",Name:next,Kind:kind,AssetId:asset.Id,Volume:kind=="sound"?volume:null));await SelectSectionAsync(kind);
    }
    private void AddVolume(FormDialog form,string path,float initial,Action<float> changed)
    {
        var label=Ui.Label("Громкость · "+(initial*100).ToString("0")+"%",Ui.Caption,true);form.Add(label);
        var slider=new Slider{Minimum=0,Maximum=100,Value=initial*100,Margin=Ui.Insets(0,Ui.S)};Ui.Name(slider,"Громкость серверного звука");form.Add(slider);
        slider.ValueChanged+=(_,args)=>{float gain=(float)(args.NewValue/100);label.Text="Громкость · "+args.NewValue.ToString("0")+"%";changed(gain);context.PreviewVolume?.Invoke(path,gain);};
        form.Action("Прослушать",()=>context.Preview(path,(float)(slider.Value/100)));
    }
    private async Task Upload(string kind)
    {
        var owner=Window.GetWindow(this);if(owner is null)return;
        var pick=new Microsoft.Win32.OpenFileDialog{Filter=kind=="emoji"?"Изображения|*.png;*.gif;*.jpg;*.jpeg":"Аудио|*.wav;*.mp3"};if(pick.ShowDialog(owner)!=true)return;
        long maximum=kind=="emoji"?256*1024:2*1024*1024;if(new FileInfo(pick.FileName).Length>maximum)throw new InvalidOperationException(kind=="emoji"?"Эмодзи: до 256 КиБ.":"Звук: до 2 МиБ.");if(kind=="emoji")AvatarCatalog.Validate(pick.FileName);
        var form=new FormDialog(owner,"Предварительный просмотр");var name=form.Field("Название",kind=="emoji"?Path.GetFileNameWithoutExtension(pick.FileName):Path.GetFileName(pick.FileName));float gain=1;
        if(kind=="emoji")form.Add(AvatarCatalog.Image(pick.FileName,96));else AddVolume(form,pick.FileName,1,value=>gain=value);
        form.Accept("Загрузить");if(form.ShowDialog()!=true)return;
        await context.Upload(kind,pick.FileName,name.Text.Trim().Trim(':'),gain);await SelectSectionAsync(kind);
    }
    public void Dispose(){if(disposed)return;disposed=true;request++;if(!busy){draftIcon?.Dispose();draftIcon=null;}}
}
