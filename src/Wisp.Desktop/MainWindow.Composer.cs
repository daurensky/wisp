using System.Windows;
using Wisp.Core;

namespace Wisp.Desktop;

public sealed partial class MainWindow
{
    private void UpdateComposerState()
    {
        if(messageComposer is null)return;
        bool recording=recorder.IsRecording;
        messageComposer.IsRecording=recording;
        messageComposer.Placeholder=selectedChannel is null?"Выберите канал":"Написать "+selectedChannel.Name;
        composer.IsEnabled=selectedChannel is not null&&!sendingMessage;
        ((ToggleIconButton)recordButton).IsChecked=recording;
        recordButton.IsEnabled=selectedChannel is not null&&!sendingMessage&&!sendingRecording&&(recording||!voiceIntent.Connecting);
        cancelRecordButton.Visibility=recording||recordedPath is not null||pendingAttachment is{} id&&store.GetAsset(id)?.Kind=="voice"?Visibility.Visible:Visibility.Collapsed;
        bool attachment=pendingAttachment is{} assetId&&store.GetAsset(assetId)is{} asset&&File.Exists(asset.LocalPath);
        sendButton.IsEnabled=selectedChannel is not null&&!sendingMessage&&!sendingRecording&&(recording||attachment||!string.IsNullOrWhiteSpace(composer.Text));
    }

    private async Task SendComposer()
    {
        if(sendingMessage||sendingRecording)return;
        if(recorder.IsRecording)await RecordVoice();
        await SendMessage();
    }

    private void RefreshSelectedChannel()
    {
        if(community is null)return;
        var available=store.GetChannels(community.Id);
        if(selectedChannel is not null&&available.FirstOrDefault(c=>c.Id==selectedChannel.Id)is{} current)
        {
            selectedChannel=current;channelTitle.Text=current.Name;
        }
        else if((available.FirstOrDefault(c=>!c.IsVoice)??available.FirstOrDefault())is{} fallback)SelectChannel(fallback);
        else{selectedChannel=null;channelTitle.Text="Нет каналов";ClearMessages();}
        UpdateComposerState();
    }
}
