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
    private ColumnDefinition callColumn=null!;
    private Border callPanel=null!;
    private readonly TextBlock callTitle=new(){TextWrapping=TextWrapping.Wrap,FontWeight=FontWeights.SemiBold};
    private readonly TextBlock callSubtitle=new(){TextWrapping=TextWrapping.Wrap};
    private readonly VirtualRows callParticipants=new();
    private readonly StackPanel callStreams=new();
    private ModalHandle? callPopup;
    private VirtualRows? callModalParticipants;
    private string memberSearch="",callRenderKey="";
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
    private Border BuildCallPanel()
    {
        var dock=new DockPanel{Margin=Ui.Insets(Ui.M)};
        var header=new StackPanel{Margin=Ui.Insets(Ui.Xs,Ui.M)};header.Children.Add(Ui.Label("ТЕКУЩИЙ ЗВОНОК",Ui.Caption,true));header.Children.Add(callTitle);Ui.Secondary(callSubtitle);header.Children.Add(callSubtitle);DockPanel.SetDock(header,Dock.Top);dock.Children.Add(header);
        var controls=new StackPanel{Orientation=Orientation.Horizontal,Margin=Ui.Insets(0,Ui.S)};
        controls.Children.Add(Tool("mic","Микрофон",ToggleMic));controls.Children.Add(Tool("headphones","Наушники",ToggleDeafen));controls.Children.Add(Tool("screen-share","Демонстрация",ShareScreen));controls.Children.Add(Tool("x","Завершить звонок",LeaveVoice));DockPanel.SetDock(controls,Dock.Bottom);dock.Children.Add(controls);
        var streamArea=new ScrollViewer{Content=callStreams,MaxHeight=180,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};DockPanel.SetDock(streamArea,Dock.Bottom);dock.Children.Add(streamArea);dock.Children.Add(callParticipants);
        return Ui.Panel(dock,"AsideSurface");
    }
    private void UpdateResponsivePanels()
    {
        bool home=homeWorkspace.Visibility==Visibility.Visible;
        bool compact=ActualWidth<Ui.Metric("Width.CompactBreakpoint")||home;
        memberPanel.Visibility=compact?Visibility.Collapsed:Visibility.Visible;memberColumn.Width=new GridLength(compact?0:Ui.Metric("Width.Members"));
        bool show=voiceChannelId is not null&&ActualWidth>=Ui.Metric("Width.CallBreakpoint");
        callPanel.Visibility=show?Visibility.Visible:Visibility.Collapsed;callColumn.Width=new GridLength(show?Ui.Metric("Width.CallPanel"):0);
    }
    private void RenderCallPanel()
    {
        if(callPanel is null)return;
        var people=VoicePresence;
        callTitle.Text=voiceCommunity?.Name??"Нет активного звонка";
        callSubtitle.Text=voiceCommunity is null?"Открой голосовой канал сообщества":(store.GetChannels(voiceCommunity.Id).FirstOrDefault(c=>c.Id==voiceChannelId)?.Name??"Подключение")+" · "+people.Count+" участников";
        var key=VoiceRoomKey+":"+string.Join("|",people.Select(p=>$"{p.Profile.Id}:{p.Profile.Username}:{p.Profile.AvatarRevision}:{p.Muted}:{p.Deafened}"))+":"+sharing+":"+string.Join("|",remoteStreams.Keys);
        if(key==callRenderKey)return;callRenderKey=key;callParticipants.Clear();callStreams.Children.Clear();
        foreach(var p in people)callParticipants.Add(p.Profile.Id,()=>new VoiceParticipantRow(p,avatars.Resolve(p.Profile,store),voice,p.Profile.Id==profile.Id,()=>ViewProfile(p.Profile),()=>Run(()=>{voice.SavePeerVolumes();return Task.CompletedTask;})));
        if(callModalParticipants is{} modalRows){modalRows.Clear();foreach(var p in people)modalRows.Add(p.Profile.Id,()=>new VoiceParticipantRow(p,avatars.Resolve(p.Profile,store),voice,p.Profile.Id==profile.Id,()=>ViewProfile(p.Profile),()=>Run(()=>{voice.SavePeerVolumes();return Task.CompletedTask;})));}
        if(sharing)callStreams.Children.Add(Ui.Label("Ты демонстрируешь экран",Ui.Caption));
        foreach(var id in remoteStreams.Keys.ToArray())
        {
            var name=people.FirstOrDefault(p=>p.Profile.Id==id)?.Profile.Username??"Участник";
            callStreams.Children.Add(Btn("Смотреть · "+name,()=>{dismissedScreens.Remove(id);OpenVideoPeer(id);if(viewers.TryGetValue(id,out var viewer))viewer.Show();}));
        }
    }
    private void OpenCallPanel()
    {
        if(voiceChannelId is null){Notify("Подключись к голосовому каналу сообщества.");return;}
        var body=new StackPanel();body.Children.Add(Ui.Label(voiceCommunity?.Name??"Текущий звонок",Ui.Heading));
        body.Children.Add(Ui.Label(store.GetChannels(voiceCommunity!.Id).FirstOrDefault(c=>c.Id==voiceChannelId)?.Name??"Подключение",Ui.Subtitle));
        var roster=new VirtualRows{Height=280};callPopup?.Close();callModalParticipants=roster;body.Children.Add(roster);
        foreach(var p in VoicePresence)roster.Add(p.Profile.Id,()=>new VoiceParticipantRow(p,avatars.Resolve(p.Profile,store),voice,p.Profile.Id==profile.Id,()=>ViewProfile(p.Profile),()=>Run(()=>{voice.SavePeerVolumes();return Task.CompletedTask;})));
        body.Children.Add(Btn("Смотреть трансляции",WatchScreens));body.Children.Add(Btn("Демонстрация экрана",ShareScreen));body.Children.Add(Btn("Выйти из канала",LeaveVoice));
        callPopup=ModalHost.Open(this,"Текущий звонок",body,480);callPopup.Closed+=()=>{callPopup=null;callModalParticipants=null;};
    }
    private void UpdateTray()=>trayIndicator?.Update(voiceChannelId is not null&&rtc?.Joined==true,activeVoiceFailed,muted,deafened,voice.IsSpeaking(VoiceService.LocalSpeakerId));
    private void ShowHome()
    {
        EnsureNoRecording();community=null;selectedChannel=null;channelRenderKey="";memberRenderKey="";cloudPresence=Array.Empty<PeerPresence>();presence=Array.Empty<PeerPresence>();channels.Clear();members.Clear();ClearMessages();
        serverTitle.Text="Wisp";channelTitle.Text="Главная";chatWorkspace.Visibility=Visibility.Collapsed;homeWorkspace.Visibility=Visibility.Visible;
        var body=new StackPanel{Margin=Ui.Insets(Ui.Xl)};body.Children.Add(Ui.Label("Место для своих",24));body.Children.Add(Ui.Label("Твои сообщества, разговоры и встречи — в одном окне.",Ui.Body,true));
        var account=Ui.Panel(new ProfileView(profile,avatars.Resolve(profile,store),profile.BannerPath,EditProfile),"Surface");account.Margin=Ui.Insets(0,Ui.L);body.Children.Add(account);
        var actions=new WrapPanel();actions.Children.Add(Btn("Создать сообщество",CreateCommunity));actions.Children.Add(Btn("Вступить по приглашению",JoinCommunity));actions.Children.Add(Btn("Настройки",OpenSettings));if(voiceChannelId is not null)actions.Children.Add(Btn("Текущий звонок",OpenCallPanel));body.Children.Add(actions);
        body.Children.Add(Ui.Label("ТВОИ СООБЩЕСТВА",Ui.Caption,true));
        foreach(var c in MyCommunities())
        {
            var card=Btn(c.Name,()=>SetCommunity(c));Ui.Style(card,"Button.Row");card.Margin=Ui.Insets(0,Ui.Xs);card.HorizontalContentAlignment=HorizontalAlignment.Left;body.Children.Add(card);
        }
        if(MyCommunities().Count==0)body.Children.Add(Ui.Label("Создай первое сообщество или используй приглашение друга.",Ui.Body,true));
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
            Profile=profile,CurrentProfile=()=>profile,ResolveAvatar=p=>avatars.Resolve(p,store),ServerSettings=EditVoiceServer,AvatarPath=avatars.Resolve(profile,store),BannerPath=profile.BannerPath,Audio=voice.Devices,NoiseSuppressionAvailable=voice.NoiseSuppressionAvailable,ApplyAudio=ApplyAudio,
            EditProfile=()=>{handle?.Close();EditProfile();},ShowNotifications=()=>{handle?.Close();ShowNotifications();},ConnectionDetails=()=>{handle?.Close();ConnectionDetails();},
            ShareScreen=()=>{handle?.Close();Run(ShareScreen);},CanShareScreen=rtc?.Joined==true,SignOut=()=>{handle?.Close();Run(SignOut);},ServerAddress=cloud?.Config.Url??"",ConnectionStatus=()=>connection.Text,
            NotificationsEnabled=sounds.NotificationsEnabled,NotificationVolume=sounds.NotificationVolume,ApplyNotifications=(enabled,volume)=>{sounds.NotificationsEnabled=enabled;sounds.NotificationVolume=volume;new SoundNotificationPreferences(enabled,volume).Save(store.RootDirectory);}
        };
        handle=ModalHost.Open(this,"Настройки",new SettingsView(context),880);
    }
}
