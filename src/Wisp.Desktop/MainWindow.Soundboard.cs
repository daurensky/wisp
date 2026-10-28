using System.Text;
using System.Text.RegularExpressions;
using Wisp.Core;

namespace Wisp.Desktop;

public sealed partial class MainWindow
{
    private async Task SoundPanel()
    {
        if(soundboardPopup?.IsOpen==true){soundboardPopup.Close();return;}
        var target=voiceCommunity??community;if(target is null)return;
        var popup=SoundboardPopup.Show(soundboardButton,Array.Empty<SoundboardItem>(),async item=>
        {
            if(deafened)return;
            var asset=await EnsureSoundAsync(target.Id,item.Id);if(asset is null)return;
            sounds.PlayFile(asset.LocalPath,asset.Volume);
            if(voiceCommunity?.Id==target.Id&&voiceChannelId is not null)
                await SendPacket(new WireMessage("sound",profile.Id,voiceChannelId,Encoding.UTF8.GetBytes(asset.Id)));
        },store.CanManageCommunity(target.Id,profile.Id)?()=>CommunityMediaSettings(target,"sound"):null,error=>Notify(error,true));
        soundboardPopup=popup;popup.SetLoading(true);
        try
        {
            var catalog=cloud is null?store.GetAssets(target.Id,"sound").Select(a=>new SoundboardItem(a.Id,a.Name,a.Volume)).ToArray():
                (await cloud.GetAsync<Dictionary<string,AssetTransfer>>($"communities/{target.Id}/sounds")??new()).Values.Select(a=>new SoundboardItem(a.Id,a.Name,a.Volume)).ToArray();
            if(popup.IsOpen)popup.SetItems(catalog);
        }
        catch(Exception error){if(popup.IsOpen)popup.SetError(error.Message);Notify(error.Message,true);}
    }

    private async Task<MediaAsset?> EnsureSoundAsync(string cid,string id)
    {
        var existing=store.GetAsset(id);
        if(existing is not null&&(existing.CommunityId!=cid||existing.Kind!="sound"))return null;
        if(cloud is null)return existing;
        if(!Regex.IsMatch(id,"^[a-f0-9]{64}$"))return null;
        // Refresh gain/name even for cached audio: a manager can edit either without changing its immutable id.
        var transfer=await cloud.GetAsync<AssetTransfer>($"communities/{cid}/sounds/{id}");
        if(transfer?.Data is null||transfer.Id!=id||transfer.Kind!="sound"||transfer.Data.Length>2*1024*1024||
            transfer.Name!=Path.GetFileName(transfer.Name)||!float.IsFinite(transfer.Volume)||transfer.Volume is <0 or >1)return null;
        if(existing is null||!File.Exists(existing.LocalPath))
        {
            var directory=Path.Combine(store.RootDirectory,"incoming",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
            try
            {
                var path=Path.Combine(directory,"sound.tmp");await File.WriteAllBytesAsync(path,transfer.Data);
                var imported=store.ImportAsset(cid,path,"sound",transfer.Name);
                if(imported.Id!=id){store.RemoveAsset(imported.Id);throw new InvalidDataException("Звуковой файл повреждён.");}
                existing=imported;
            }
            finally{Directory.Delete(directory,true);}
        }
        store.RenameAsset(id,transfer.Name);store.SetAssetVolume(id,transfer.Volume);
        return existing with{Name=transfer.Name,Volume=transfer.Volume};
    }
}
