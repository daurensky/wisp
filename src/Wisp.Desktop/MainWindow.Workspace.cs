using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wisp.Core;
using Wisp.Desktop.Media;

namespace Wisp.Desktop;

public sealed partial class MainWindow
{
    private DockPanel chatWorkspace=null!;
    private ScrollViewer homeWorkspace=null!;
    private long voiceJoinedAt;
    private string VoiceRoomKey=>(voiceCommunity?.Id??"")+"/"+(voiceChannelId??"");
    private IReadOnlyList<PeerPresence> VoicePresence
    {
        get
        {
            if(voiceCommunity is null||voiceChannelId is null)return Array.Empty<PeerPresence>();
            var people=store.GetMembers(voiceCommunity.Id).ToDictionary(p=>p.Id);people[profile.Id]=profile;
            var result=(rtc?.Participants??Array.Empty<RtcSeat>()).Where(s=>people.ContainsKey(s.UserId)).Select(s=>new PeerPresence(people[s.UserId],voiceChannelId,s.Muted||s.Deafened,s.Deafened)).ToList();
            if(!result.Any(p=>p.Profile.Id==profile.Id))result.Add(new(profile,voiceChannelId,muted||deafened,deafened));
            return result;
        }
    }
    private void UpdateResponsivePanels(double? viewportWidth=null)
    {
        double width=viewportWidth??ActualWidth;
        bool compact=width<Ui.Metric("Width.CompactBreakpoint")||homeWorkspace.Visibility==Visibility.Visible;
        memberPanel.Visibility=compact?Visibility.Collapsed:Visibility.Visible;
        memberColumn.Width=new GridLength(compact?0:Ui.Metric("Width.Members"));
    }
    private void UpdateTray()=>trayIndicator?.Update(voiceChannelId is not null&&rtc?.Joined==true,activeVoiceFailed,muted,deafened,voice.IsSpeaking(VoiceService.LocalSpeakerId));
    private void ShowHome()
    {
        EnsureNoRecording();soundboardPopup?.Close();emojiPicker?.Close();community=null;selectedChannel=null;channelRenderKey="";memberRenderKey="";cloudPresence=Array.Empty<PeerPresence>();presence=Array.Empty<PeerPresence>();channels.Clear();members.Clear();ClearMessages();
        serverTitle.Text="Wisp";channelTitle.Text="Главная";chatWorkspace.Visibility=Visibility.Collapsed;homeWorkspace.Visibility=Visibility.Visible;
        var body=new StackPanel{Margin=Ui.Insets(Ui.Xl),HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
        var brand=new StackPanel{HorizontalAlignment=HorizontalAlignment.Center,Margin=Ui.Insets(0,0,0,Ui.Xl)};
        brand.Children.Add(new HomeBrandView());var wordmark=Ui.Label("Wisp",32);wordmark.FontWeight=FontWeights.SemiBold;wordmark.HorizontalAlignment=HorizontalAlignment.Center;wordmark.Margin=Ui.Insets(0,Ui.M,0,0);brand.Children.Add(wordmark);
        brand.Loaded+=(_,_)=>Motion.Enter(brand);brand.Unloaded+=(_,_)=>Motion.Stop(brand);body.Children.Add(brand);
        var actions=new WrapPanel{HorizontalAlignment=HorizontalAlignment.Center};
        actions.Children.Add(Btn("Создать сервер",CreateCommunity));actions.Children.Add(Btn("Присоединиться",JoinCommunity));body.Children.Add(actions);
        homeWorkspace.VerticalContentAlignment=VerticalAlignment.Center;homeWorkspace.HorizontalContentAlignment=HorizontalAlignment.Stretch;
        homeWorkspace.Content=body;RenderServers();UpdateStatus();
    }
    private async Task ApplyAudio(AudioDeviceSettings settings)
    {
        settings.Save(store.RootDirectory);VoiceMessagePlayer.ConfigureOutput(settings.OutputId);sharePlayback.ConfigureOutput(settings.OutputId);sounds.ConfigureOutput(settings.OutputId);await voice.ConfigureDevicesAsync(settings);
    }
    private void OpenSettings()
    {
        ModalHandle? handle=null;
        var context=new SettingsContext
        {
            Profile=profile,ProfileSurface=()=>CreateProfileEditor(),CurrentProfile=()=>profile,ResolveAvatar=p=>avatars.Resolve(p,store),AvatarPath=avatars.Resolve(profile,store),BannerPath=profile.BannerPath,Audio=voice.Devices,NoiseSuppressionAvailable=voice.NoiseSuppressionAvailable,ApplyAudio=ApplyAudio,
            EditProfile=()=>{handle?.Close();EditProfile();},ShowNotifications=()=>{handle?.Close();ShowNotifications();},ReportError=error=>Notify(error,true),
            SignOut=()=>{handle?.Close();Run(SignOut);},
            NotificationsEnabled=sounds.NotificationsEnabled,NotificationVolume=sounds.NotificationVolume,ApplyNotifications=(enabled,volume)=>{sounds.NotificationsEnabled=enabled;sounds.NotificationVolume=volume;new SoundNotificationPreferences(enabled,volume).Save(store.RootDirectory);}
        };
        handle=ModalHost.Open(this,"Настройки",new SettingsView(context), options:new ModalOptions(FullWindow:true));
    }
}
