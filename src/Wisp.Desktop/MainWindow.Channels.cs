using System.Windows;
using Wisp.Core;

namespace Wisp.Desktop;

public sealed partial class MainWindow
{
    private async Task LeaveRemovedVoiceChannel(CommunitySnapshot snapshot)
    {
        if(voiceCommunity?.Id!=snapshot.Community.Id||voiceChannelId is not{} active||snapshot.Channels.Any(c=>c.Id==active&&c.IsVoice))return;
        await LeaveVoice();Notify("Голосовой канал удалён администратором.");
    }

    private void CreateChannel(bool voiceType)
    {
        if(community is not{} target||!store.CanManageCommunity(target.Id,profile.Id))return;
        var dialog=new FormDialog(this,voiceType?"Создать голосовой канал":"Создать текстовый канал");
        var name=dialog.Field("Название");dialog.Accept("Создать");if(dialog.ShowDialog()!=true)return;
        Run(async()=>
        {
            if(cloud is not null)
            {
                await cloud.ManageCommunityAsync(target.Id,new("createChannel",Name:name.Text.Trim(),ChannelId:Guid.NewGuid().ToString("N"),IsVoice:voiceType));
                await ReloadCommunityMetadata(target.Id);
            }
            else{store.CreateChannel(target.Id,name.Text,voiceType);channelRenderKey="";RenderChannels();}
            UpdateStatus();
        });
    }

    private void EditChannel(Channel target)
    {
        if(!store.CanManageCommunity(target.CommunityId,profile.Id))return;
        var dialog=new FormDialog(this,"Настройки канала",560);var name=dialog.Field("Название",target.Name);
        dialog.Action("Сохранить",()=>Run(async()=>
        {
            if(cloud is not null)await cloud.ManageCommunityAsync(target.CommunityId,new("renameChannel",Name:name.Text.Trim(),ChannelId:target.Id));
            else store.UpdateChannel(target with{Name=name.Text.Trim()});
            await ReloadCommunityMetadata(target.CommunityId);RefreshSelectedChannel();channelRenderKey="";RenderChannels();dialog.Close();UpdateStatus();
        }));
        dialog.FooterAction("Удалить канал",()=>Run(async()=>
        {
            if(!ConfirmCommunityAction("Удалить канал «"+target.Name+"»? Участники будут отключены от него."))return;
            if(selectedChannel?.Id==target.Id)await CancelRecording();
            if(cloud is not null)await cloud.ManageCommunityAsync(target.CommunityId,new("deleteChannel",ChannelId:target.Id));
            else store.DeleteChannel(target.Id);
            if(voiceChannelId==target.Id)await LeaveVoice();
            await ReloadCommunityMetadata(target.CommunityId);RefreshSelectedChannel();channelRenderKey="";RenderChannels();dialog.Close();UpdateStatus();
        }));
        dialog.Accept("Закрыть");dialog.ShowDialog();
    }

}
