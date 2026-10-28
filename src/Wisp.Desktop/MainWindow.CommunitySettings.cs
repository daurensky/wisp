using System.Windows;
using System.Windows.Controls;
using Wisp.Core;

namespace Wisp.Desktop;

public sealed partial class MainWindow
{
    private ContextMenu? CommunityContextMenu(Community target)
    {
        if(!store.CanManageCommunity(target.Id,profile.Id))return null;
        var menu=new ContextMenu();var item=new MenuItem{Header="Настройки сервера"};item.Click+=(_,_)=>OpenCommunitySettings(target);menu.Items.Add(item);return menu;
    }
    private FrameworkElement CommunityIcon(Community target,FrameworkElement hoverScope)
    {
        var asset=target.IconAssetId is null?null:store.GetAsset(target.IconAssetId);
        return ServerRailItem.CreateAvatar(target.Name,asset?.LocalPath,Ui.Metric("Size.RailItem")-8,hoverScope,target.IconCoverFrame);
    }
    private async Task RefreshCommunityIcon(Community target)
    {
        if(cloud is null||target.IconAssetId is null||store.GetAsset(target.IconAssetId)is not null)return;
        var transfer=await cloud.GetAsync<AssetTransfer>($"communities/{target.Id}/icon");
        if(transfer is null||transfer.Kind!="icon"||transfer.Id!=target.IconAssetId||transfer.Data.Length>4*1024*1024)return;
        string temporary=Path.Combine(store.RootDirectory,Guid.NewGuid().ToString("N")+".tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary,transfer.Data);AvatarCatalog.Validate(temporary);var imported=store.ImportAsset(target.Id,temporary,"icon",transfer.Name);
            if(imported.Id!=target.IconAssetId){store.RemoveAsset(imported.Id);throw new InvalidDataException("Значок сервера повреждён.");}
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    private CommunitySnapshot LocalCommunitySnapshot(string id)
    {
        var target=store.GetCommunities().FirstOrDefault(c=>c.Id==id)??throw new InvalidOperationException("Сервер не найден.");
        var people=store.GetMembers(id);return new(target,store.GetChannels(id),people,people.ToDictionary(p=>p.Id,p=>store.GetMemberRole(id,p.Id)));
    }
    private void RequireCommunityManagement(string id)
    {
        if(!store.CanManageCommunity(id,profile.Id))throw new UnauthorizedAccessException("Нет права управлять этим сервером.");
    }
    private void OpenCommunitySettings(Community target)=>OpenCommunitySettingsSection(target,"overview");
    private void CommunityMediaSettings(Community target,string kind)=>OpenCommunitySettingsSection(target,kind=="sound"?"sound":"emoji");
    private void OpenCommunitySettingsSection(Community target,string section)=>Run(async()=>
    {
        RequireCommunityManagement(target.Id);
        var snapshot=await ManagedCommunitySnapshot(target.Id);
        var context=new CommunitySettingsContext
        {
            UserId=profile.Id,Online=cloud is not null,Snapshot=snapshot,
            Reload=()=>ManagedCommunitySnapshot(target.Id),
            Apply=command=>ApplyCommunityManagement(target.Id,command),
            Icon=current=>ServerRailItem.CreateAvatar(current.Name,current.IconAssetId is{} id?store.GetAsset(id)?.LocalPath:null,96,coverFrame:current.IconCoverFrame),
            SaveIcon=async edited=>
            {
                RequireCommunityManagement(target.Id);ProfileEditor.ValidateImage(edited.Path);
                var icon=store.ImportAsset(target.Id,edited.Path,"icon");
                if(cloud is not null)await cloud.ManageCommunityAsync(target.Id,new("icon",Asset:new(icon.Id,icon.Name,"icon",await File.ReadAllBytesAsync(icon.LocalPath)),CoverFrame:edited.CoverFrame));
                else{var local=LocalCommunitySnapshot(target.Id);store.SaveSnapshot(local with{Community=local.Community with{IconAssetId=icon.Id,IconCoverFrame=edited.CoverFrame}});}
                await ReloadCommunityMetadata(target.Id);
            },
            Assets=kind=>ManagedCommunityAssets(target.Id,kind),
            Upload=async(kind,path,name,gain)=>
            {
                RequireCommunityManagement(target.Id);if(string.IsNullOrWhiteSpace(name))throw new ArgumentException("Укажите название.");
                var asset=store.ImportAsset(target.Id,path,kind,name);store.SetAssetVolume(asset.Id,gain);asset=asset with{Volume=gain};
                if(cloud is not null)
                {
                    if(kind=="emoji")
                    {
                        await CommunityAssets.UploadEmojiAsync(cloud,asset);
                        await cloud.ManageCommunityAsync(target.Id,new("renameAsset",Name:asset.Name,Kind:kind,AssetId:asset.Id));
                    }
                    else if(!await cloud.TryCreateAsync($"communities/{target.Id}/sounds/{asset.Id}",new AssetTransfer(asset.Id,asset.Name,kind,await File.ReadAllBytesAsync(asset.LocalPath),gain)))
                        await cloud.ManageCommunityAsync(target.Id,new("renameAsset",Name:asset.Name,Kind:kind,AssetId:asset.Id,Volume:gain));
                }
            },
            Preview=(path,gain)=>{if(!deafened)sounds.PlayFile(path,gain);},PreviewVolume=(path,gain)=>sounds.SetFileVolume(path,gain),Confirm=ConfirmCommunityAction,ReportError=message=>Notify(message,true)
        };
        ModalHost.Open(this,"Настройки сервера",new CommunitySettingsView(context,section),options:new ModalOptions(FullWindow:true));
    });
    private async Task<CommunitySnapshot> ManagedCommunitySnapshot(string id)
    {
        RequireCommunityManagement(id);
        var snapshot=cloud is null?LocalCommunitySnapshot(id):await cloud.GetCommunityManagementAsync(id)??throw new InvalidOperationException("Сервер не найден.");
        store.SaveSnapshot(snapshot);await RefreshCommunityIcon(snapshot.Community);
        if(community?.Id==id){community=snapshot.Community;serverTitle.Text=community.Name;RefreshSelectedChannel();memberRenderKey="";channelRenderKey="";RenderChannels();RenderMembers();}
        if(voiceCommunity?.Id==id)voiceCommunity=snapshot.Community;RenderServers();UpdateStatus();return snapshot;
    }
    private async Task ReloadCommunityMetadata(string id)=>_ = await ManagedCommunitySnapshot(id);
    private async Task ApplyCommunityManagement(string id,CommunityManagement command)
    {
        RequireCommunityManagement(id);
        if(cloud is not null)await cloud.ManageCommunityAsync(id,command);
        else
        {
            switch(command.Action)
            {
                case "rename":var snapshot=LocalCommunitySnapshot(id);store.SaveSnapshot(snapshot with{Community=snapshot.Community with{Name=command.Name!.Trim()}});break;
                case "renameAsset":if(command.Name is not null)store.RenameAsset(command.AssetId!,command.Name);if(command.Volume is{} gain)store.SetAssetVolume(command.AssetId!,gain);break;
                case "deleteAsset":store.RemoveAsset(command.AssetId!);break;
                default:throw new InvalidOperationException("Для этого действия нужно подключение к серверу.");
            }
        }
        if(command.Action=="renameAsset"){if(command.Name is not null)store.RenameAsset(command.AssetId!,command.Name);if(command.Volume is{} gain)store.SetAssetVolume(command.AssetId!,gain);}
        if(command.Action=="deleteAsset")store.RemoveAsset(command.AssetId!);
        await ReloadCommunityMetadata(id);
    }
    private async Task<IReadOnlyList<MediaAsset>> ManagedCommunityAssets(string id,string kind)
    {
        RequireCommunityManagement(id);if(cloud is null)return store.GetAssets(id,kind);
        string collection=kind=="emoji"?"emojis":"sounds";
        var remote=await cloud.GetAsync<Dictionary<string,AssetTransfer>>($"communities/{id}/{collection}")??new();
        if(kind=="emoji")await FetchEmojiAssets(remote.Keys,id);
        var result=new List<MediaAsset>();
        foreach(var entry in remote.Values)
        {
            var asset=kind=="sound"?await EnsureSoundAsync(id,entry.Id):store.GetAsset(entry.Id);
            if(asset is null||asset.CommunityId!=id||asset.Kind!=kind)continue;
            store.RenameAsset(asset.Id,entry.Name);store.SetAssetVolume(asset.Id,entry.Volume);result.Add(asset with{Name=entry.Name,Volume=entry.Volume});
        }
        return result;
    }
    private bool ConfirmCommunityAction(string text)
    {
        var confirm=new FormDialog(this,"Подтверждение");confirm.Note(text);confirm.Accept("Подтвердить");return confirm.ShowDialog()==true;
    }
}
