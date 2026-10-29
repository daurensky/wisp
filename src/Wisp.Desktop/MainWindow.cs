using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Documents;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Wisp.Core;
using Wisp.Desktop.Media;
using Button = System.Windows.Controls.Button;
using Image = System.Windows.Controls.Image;

namespace Wisp.Desktop;

public sealed partial class MainWindow : ThemedWindow
{
    private readonly LocalStore store;
    private readonly AvatarCatalog avatars;
    private readonly NotificationArea notifications = new();
    private readonly StackPanel attachmentPreview = new();
    private readonly Dictionary<string,MediaAsset> draftEmojis = new();
    private readonly HashSet<string> publishedEmojis = new();
    private readonly HashSet<string> publishedFiles = new(), unavailableFiles = new();
    private readonly HashSet<string> dismissedScreens = new();
    private readonly Dictionary<string,byte[]> remoteScreens = new();
    private readonly Button notificationButton, cancelRecordButton,inviteButton,serverSettingsButton;
    private readonly Button sendButton;
    private ModalHandle? notificationPopup;
    private string memberRenderKey = "";
    private bool sendingMessage;

    private readonly ServerBackend? cloud;
    private string lastConnectionPaths = "";
    private readonly DispatcherTimer cloudTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private IReadOnlyList<PeerPresence> cloudPresence = Array.Empty<PeerPresence>();
    private Dictionary<string, JsonElement> cloudVoiceRooms = new();
    private string? voiceRoomsCommunityId;
    private string? cloudChannelId;
    private bool cloudPolling;
    private string? savedRefreshToken;
    private readonly HashSet<string> renderedMessages = new();
    private readonly HashSet<string> creatingCommunities=new();
    private readonly VoiceJoinIntent voiceIntent = new();
    private readonly SemaphoreSlim voiceTransitions = new(1, 1);
    private string channelRenderKey = "";
    private IReadOnlyList<PeerPresence> DisplayPresence
    {
        get
        {
            var online = presence.Concat(cloudPresence).GroupBy(p => p.Profile.Id).Select(group => group.First()).Where(p => p.Profile.Id != profile.Id).ToDictionary(p => p.Profile.Id);
            var latestSeat = new Dictionary<string, long>();
            void ApplySeats(string channelId, IReadOnlyList<RtcSeat> seats, bool preferLatest = false)
            {
                var ids = seats.Select(seat => seat.UserId).ToHashSet();
                foreach (var person in online.Values.Where(p => p.VoiceChannelId == channelId && !ids.Contains(p.Profile.Id)).ToArray())
                    online[person.Profile.Id] = person with { VoiceChannelId = null, Muted = false, Deafened = false };
                foreach (var seat in seats)
                {
                    if (seat.UserId == profile.Id) continue;
                    if (preferLatest && latestSeat.TryGetValue(seat.UserId, out var seenAt) && seenAt > seat.SeenAt) continue;
                    latestSeat[seat.UserId] = seat.SeenAt;
                    var person = online.GetValueOrDefault(seat.UserId)?.Profile ?? (community is null ? null : store.GetMembers(community.Id).FirstOrDefault(p => p.Id == seat.UserId));
                    if (person is not null) online[seat.UserId] = new PeerPresence(person, channelId, seat.Muted || seat.Deafened, seat.Deafened);
                }
            }
            if (cloud is not null && community is not null && voiceRoomsCommunityId == community.Id)
                foreach (var channel in store.GetChannels(community.Id).Where(channel => channel.IsVoice))
                    ApplySeats(channel.Id, cloudVoiceRooms.TryGetValue(channel.Id, out var data) ? RoomRoster.ActiveParticipants(data, cloud.ServerNow()) : Array.Empty<RtcSeat>(), true);
            if (rtc?.ChannelId is { } rtcChannel && rtcChannel == voiceChannelId && voiceCommunity?.Id == community?.Id) ApplySeats(rtcChannel, rtc.Participants);
            online[profile.Id] = new PeerPresence(profile, voiceCommunity?.Id == community?.Id ? voiceChannelId ?? cloudChannelId : null, muted || deafened, deafened);
            return online.Values.ToArray();
        }
    }
    private UserProfile profile;
    private readonly IVoiceRoom? rtc;
    private CancellationTokenSource? reconnect;
    private readonly VoiceService voice = new();
    private readonly VoiceRecorder recorder = new();
    private readonly ScreenShareService screen = new();
    private readonly ShareAudioCapture shareAudio = new();
    private readonly ShareAudioPlayback sharePlayback = new();
    private readonly Dictionary<string, ScreenStreamInfo> remoteStreams = new();
    private readonly CommandRateGate screenKeyRequests = new();
    private sealed record ScreenCommand(string TargetId, string SessionId, bool Paused = false, bool? Viewing = null, bool Restoring = false);
    private readonly StreamViewerPresence streamViewers = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ScreenVideoReceiver> videoReceivers = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (ScreenStreamInfo Info, byte[] Pixels)> videoFrames = new();
    private int videoScheduled;
    private string sharedPeers = "";
    private readonly SoundService sounds = new();
    private readonly DispatcherTimer activityTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly VirtualRows servers = new(), channels = new(), members = new(), messages = new();
    private ChatMessage? lastRenderedMessage;
    private bool loadingHistory;
    private readonly TextBlock serverTitle = new() { FontSize = Ui.Subtitle, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock channelTitle = new() { FontSize = Ui.Subtitle, FontWeight = FontWeights.SemiBold };
    private readonly Button connectionButton = new();
    private readonly TextBlock connection = new() { FontSize = Ui.Caption, TextWrapping = TextWrapping.Wrap };
    private Button? identityAvatar;
    private string? identityAvatarKey;
    private readonly TextBlock userName = new() { FontWeight = FontWeights.SemiBold };
    private readonly TextBlock notice = new() { TextWrapping = TextWrapping.Wrap, FontSize = Ui.Caption, Margin = Ui.Insets(Ui.Xl, Ui.S) };
    private readonly TextBox composer = new() { AcceptsReturn = true, MaxHeight = Ui.Metric("Height.ComposerMaximum"), MinHeight = Ui.ControlHeight, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxLength = 8000 };
    private readonly Border memberPanel;
    private readonly ColumnDefinition memberColumn;
    private readonly Button micButton, deafenButton, recordButton, shareButton, leaveButton, watchButton, soundboardButton;
    private readonly Border voiceActionBlock;
    private Button emojiButton = null!;
    private EmojiPicker? emojiPicker;
    private ChatTextSelection chatTextSelection=null!;
    private Grid streamWorkspace = null!;
    private readonly System.Windows.Forms.NotifyIcon tray;
    private bool exitRequested;
    private readonly Dictionary<string, ScreenViewer> viewers = new();
    private readonly SemaphoreSlim inbound = new(1, 1);
    private readonly System.Threading.Channels.Channel<(int Epoch, WireMessage Message)> incoming = System.Threading.Channels.Channel.CreateBounded<(int, WireMessage)>(8);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, WireMessage> latestFrames = new();
    private readonly CancellationTokenSource receiveLifetime = new();
    private long queuedBytes;
    private int frameScheduled, overflowed, sessionEpoch;
    private Community? community;
    private Community? voiceCommunity;
    private bool activeVoiceFailed, voiceWasConnected;
    private readonly SoundNotificationPolicy soundEvents;
    private TrayIndicator? trayIndicator;
    private Channel? selectedChannel;
    private string? voiceChannelId, pendingAttachment;
    private bool muted, deafened, sharing, closing;
    private long shareGeneration;
    private string? recordedPath;
    private bool sendingRecording;
    private MessageComposerView messageComposer=null!;
    private RecordingWaveformView recordingWaveform=null!;
    private SoundboardPopup? soundboardPopup;
    private IReadOnlyList<PeerPresence> presence = Array.Empty<PeerPresence>();

    public MainWindow(LocalStore store, UserProfile profile, ServerBackend? cloud = null)
    {
        if (profile.AvatarRevision is null && profile.AvatarPath is not null && File.Exists(profile.AvatarPath)) profile = profile with { AvatarRevision = AvatarCatalog.Revision(profile.AvatarPath) };
        this.store = store; avatars = new AvatarCatalog(store.RootDirectory, profile.Id); this.profile = profile; this.cloud = cloud; savedRefreshToken = cloud?.RefreshToken;
        rtc = cloud is null ? null : new SfuRoom(cloud,cloud.Config.VoiceSettings());
        var voicePreferences=VoicePreferences.Load(store.RootDirectory);muted=voicePreferences.Muted;deafened=voicePreferences.Deafened;voice.Muted=muted;voice.Deafened=deafened;sharePlayback.Deafened=deafened;
        soundEvents=new SoundNotificationPolicy(profile.Id);soundEvents.Emitted+=sounds.Play;soundEvents.ObserveLocalAudio(muted,deafened,false);
        var notificationPrefs=SoundNotificationPreferences.Load(store.RootDirectory);sounds.NotificationsEnabled=notificationPrefs.Enabled;sounds.NotificationVolume=notificationPrefs.Volume;
        Title = "Wisp";
        Width = Ui.Metric("Width.Window"); Height = Ui.Metric("Height.Window");
        MinWidth = Ui.Metric("Width.WindowMinimum"); MinHeight = Ui.Metric("Height.WindowMinimum");
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Ui.Metric("Width.Rail")) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Ui.Metric("Width.Channels")) });
        root.ColumnDefinitions.Add(new ColumnDefinition());
        memberColumn = new ColumnDefinition { Width = new GridLength(Ui.Metric("Width.Members")) };
        root.ColumnDefinitions.Add(memberColumn);
        Content = root;

        var rail = new DockPanel { Margin = Ui.Insets(Ui.S, Ui.M) };
        var home = Brand.Image(Ui.Metric("Size.NavigationIcon")); home.ToolTip = "Wisp"; home.HorizontalAlignment = HorizontalAlignment.Center;
        home.Width = home.Height = Ui.Metric("Size.NavigationIcon"); home.Margin = Ui.Insets(0, 0, 0, Ui.M);
        var homeButton=Btn("Главная Wisp",ShowHome,"Главная Wisp");homeButton.Content=home;Ui.Style(homeButton,"Button.Ghost");DockPanel.SetDock(homeButton,Dock.Top);rail.Children.Add(homeButton);
        var railActions = new StackPanel(); DockPanel.SetDock(railActions, Dock.Bottom); rail.Children.Add(railActions);
        railActions.Children.Add(Tool("plus", "Создать сообщество", CreateCommunity));
        railActions.Children.Add(Tool("arrow-up-right", "Присоединиться по приглашению", JoinCommunity));
        rail.Children.Add(servers); var railPanel=Ui.Panel(rail,"RailSurface");railPanel.BorderThickness=new Thickness(0,0,Ui.Metric("Size.Hairline"),0);railPanel.SetResourceReference(Border.BorderBrushProperty,"Border");root.Children.Add(railPanel);

        var side = new DockPanel();
        var heading = new DockPanel { Height = Ui.Metric("Size.SectionHeader"), Margin = Ui.Insets(Ui.M, 0) };
        var channelActions = new StackPanel { Orientation = Orientation.Horizontal };
        channelActions.Children.Add(inviteButton=Tool("users-plus", "Пригласить в сообщество", HostCommunity));
        channelActions.Children.Add(serverSettingsButton=Tool("settings","Настройки сервера",()=>{if(community is{} target&&store.CanManageCommunity(target.Id,profile.Id))OpenCommunitySettings(target);}));
        DockPanel.SetDock(channelActions, Dock.Right); heading.Children.Add(channelActions);
        serverTitle.TextTrimming = TextTrimming.CharacterEllipsis; serverTitle.VerticalAlignment = VerticalAlignment.Center;
        heading.Children.Add(serverTitle); DockPanel.SetDock(heading, Dock.Top); side.Children.Add(heading);
        var self = new StackPanel { Margin = Ui.Insets(Ui.S) };
        Ui.Secondary(connection); connection.Margin = Ui.Insets(0);
        Ui.Style(connectionButton, "Button.Ghost"); connectionButton.Padding = Ui.Insets(Ui.Xs); connectionButton.Margin = Ui.Insets(0, 0, 0, Ui.Xs);
        connectionButton.HorizontalContentAlignment = HorizontalAlignment.Left; connectionButton.Content = connection;
        Ui.Name(connectionButton, "Информация о подключении"); connectionButton.ToolTip = "Подключение, сервер и пинг";
        connectionButton.Click += (_, _) => ConnectionDetails();
        chatTextSelection=new ChatTextSelection(messages);
        messages.SetResourceReference(ItemsControl.ItemContainerStyleProperty, "ListItem.Chat");
        servers.SetResourceReference(ItemsControl.ItemContainerStyleProperty, "ListItem.Chat");
        channels.SetResourceReference(ItemsControl.ItemContainerStyleProperty, "ListItem.Chat");
        var voiceBlock=new StackPanel();voiceBlock.Children.Add(connectionButton);
        var identity = new DockPanel();
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        micButton = VoiceToggle("mic", "mic-off", "Выключить микрофон", "Включить микрофон", ToggleMic);
        deafenButton = VoiceToggle("headphones", "headphone-off", "Выключить наушники и микрофон", "Включить наушники", ToggleDeafen);
        controls.Children.Add(micButton);controls.Children.Add(deafenButton);
        var settingsButton=Tool("settings", "Настройки Wisp", OpenSettings);settingsButton.Margin=Ui.Insets(Ui.Xs,0,0,0);controls.Children.Add(settingsButton);
        DockPanel.SetDock(controls,Dock.Right);identity.Children.Add(controls);
        identityAvatar=Btn("Мой профиль",()=>ViewProfile(this.profile));identityAvatar.Padding=Ui.Insets(0);identityAvatar.Margin=Ui.Insets(0);Ui.Style(identityAvatar,"Button.Ghost");identityAvatar.HorizontalContentAlignment=HorizontalAlignment.Left;
        var identityContent=new DockPanel();var identityPicture=new ContentControl{Name="IdentityPicture",Width=32,Height=32,Margin=Ui.Insets(0,0,Ui.S,0)};DockPanel.SetDock(identityPicture,Dock.Left);identityContent.Children.Add(identityPicture);
        userName.VerticalAlignment=VerticalAlignment.Center;userName.TextTrimming=TextTrimming.CharacterEllipsis;identityContent.Children.Add(userName);identityAvatar.Content=identityContent;identity.Children.Add(identityAvatar);

        var voiceTools=new System.Windows.Controls.Primitives.UniformGrid{Name="VoiceActionsGrid",Columns=4,Rows=1,Margin=Ui.Insets(0,Ui.Xs,0,0)};
        shareButton=VoiceToggle("screen-share","screen-share-off","Начать демонстрацию","Остановить демонстрацию",ShareScreen);
        ((ToggleIconButton)shareButton).SetResourceReference(ToggleIconButton.CheckedBrushProperty,"Danger");voiceTools.Children.Add(shareButton);
        watchButton=Tool("monitor","Смотреть демонстрации",WatchScreens);voiceTools.Children.Add(watchButton);
        soundboardButton=Tool("volume-2","Звуковая панель",SoundPanel);voiceTools.Children.Add(soundboardButton);
        leaveButton=Tool("phone-hangup","Выйти из голосового канала",LeaveVoice);voiceTools.Children.Add(leaveButton);voiceBlock.Children.Add(voiceTools);
        foreach(var action in new[]{shareButton,watchButton,soundboardButton,leaveButton}){action.Width=double.NaN;action.HorizontalAlignment=HorizontalAlignment.Stretch;action.HorizontalContentAlignment=HorizontalAlignment.Center;action.VerticalContentAlignment=VerticalAlignment.Center;action.Margin=Ui.Insets(Ui.Xs,0);}
        voiceActionBlock=new Border{Name="VoiceActionsBlock",Child=voiceBlock,Padding=Ui.Insets(0,0,0,Ui.S),BorderThickness=new Thickness(0,0,0,Ui.Metric("Size.Hairline")),Margin=Ui.Insets(0,0,0,Ui.S),Visibility=Visibility.Collapsed};voiceActionBlock.SetResourceReference(Border.BorderBrushProperty,"Border");self.Children.Add(voiceActionBlock);self.Children.Add(identity);
        var selfPanel=Ui.Panel(self,"SurfaceElevated",false);selfPanel.BorderThickness=new Thickness(0,Ui.Metric("Size.Hairline"),0,0);selfPanel.SetResourceReference(Border.BorderBrushProperty,"Border");DockPanel.SetDock(selfPanel,Dock.Bottom);side.Children.Add(selfPanel);
        side.Children.Add(channels);var sidebar=Ui.Panel(side,"ChannelSurface");sidebar.BorderThickness=new Thickness(0,0,Ui.Metric("Size.Hairline"),0);sidebar.SetResourceReference(Border.BorderBrushProperty,"Border");Grid.SetColumn(sidebar,1);root.Children.Add(sidebar);

        var center=new DockPanel();chatWorkspace=center;center.SetResourceReference(Panel.BackgroundProperty,"ChatSurface");
        var workspace=new Grid();workspace.Children.Add(center);streamWorkspace=new Grid{IsHitTestVisible=true};workspace.Children.Add(streamWorkspace);homeWorkspace=new ScrollViewer{VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Visibility=Visibility.Collapsed};workspace.Children.Add(homeWorkspace);workspace.Children.Remove(streamWorkspace);workspace.Children.Add(streamWorkspace);Grid.SetColumn(workspace,2);root.Children.Add(workspace);
        var top = new DockPanel { Height = Ui.Metric("Size.SectionHeader"), Margin = Ui.Insets(Ui.L, 0) };
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        notificationButton = Tool("bell", "Уведомления", ShowNotifications); toolbar.Children.Add(notificationButton);
        var bell = new Grid(); bell.Children.Add(new Icon { Kind = "bell", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        var badgeText = Ui.Label("", Ui.Caption); badgeText.SetResourceReference(TextBlock.ForegroundProperty, "OnAccent");
        var badge = new Border { Child = badgeText, CornerRadius = Ui.Radius(), Padding = Ui.Insets(Ui.Xs, 0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed };
        badge.SetResourceReference(Border.BackgroundProperty, "Accent"); bell.Children.Add(badge); notificationButton.Content = bell;
        notifications.Changed += count => { bool reveal = badge.Visibility != Visibility.Visible && count > 0; badge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed; badgeText.Text = count > 99 ? "99+" : count.ToString(); if (reveal) Motion.Pulse(badge); notificationButton.ToolTip = count == 0 ? "Уведомления" : $"Уведомления · {count}"; Ui.Name(notificationButton, (string)notificationButton.ToolTip); notificationButton.SetResourceReference(Control.ForegroundProperty, count == 0 ? "TextSecondary" : "Accent"); };
        DockPanel.SetDock(toolbar, Dock.Right); top.Children.Add(toolbar);
        channelTitle.TextTrimming = TextTrimming.CharacterEllipsis; channelTitle.VerticalAlignment = VerticalAlignment.Center; top.Children.Add(channelTitle);
        var topPanel = new Border { Child = top, BorderThickness = new Thickness(0, 0, 0, Ui.Metric("Size.Hairline")) }; topPanel.SetResourceReference(Border.BorderBrushProperty, "Border");
        DockPanel.SetDock(topPanel, Dock.Top); center.Children.Add(topPanel);
        notice.Visibility = Visibility.Collapsed; DockPanel.SetDock(notice, Dock.Top); center.Children.Add(notice);
        var inputTools=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Bottom};
        inputTools.Children.Add(Tool("paperclip","Прикрепить файл",AttachFile));inputTools.Children.Add(emojiButton=Tool("face-slightly-smiling","Добавить эмодзи",EmojiPanel));
        composer.Margin=Ui.Insets(0);composer.Padding=Ui.Insets(Ui.M,Ui.S);Ui.Name(composer,"Сообщение");Ui.Style(composer,"Input.Composer");
        var composeActions=new StackPanel{Orientation=Orientation.Horizontal};
        recordButton=VoiceToggle("mic","square","Записать голосовое","Завершить запись",RecordVoice);composeActions.Children.Add(recordButton);
        cancelRecordButton=Tool("x","Отменить запись",CancelRecording);cancelRecordButton.Visibility=Visibility.Collapsed;composeActions.Children.Add(cancelRecordButton);
        sendButton=Tool("send","Отправить сообщение",SendComposer);Ui.Style(sendButton,"Button.Primary");sendButton.Width=Ui.ControlHeight;sendButton.Padding=Ui.Insets(0);composeActions.Children.Add(sendButton);
        recordingWaveform=new RecordingWaveformView();recordingWaveform.Bind(recorder);
        messageComposer=new MessageComposerView(composer,inputTools,composeActions,attachmentPreview,recordingWaveform){Margin=Ui.Insets(Ui.L,Ui.S,Ui.L,Ui.L)};
        composer.TextChanged+=(_,_)=>UpdateComposerState();DockPanel.SetDock(messageComposer,Dock.Bottom);center.Children.Add(messageComposer);
        messages.Margin=Ui.Insets(0);center.Children.Add(messages);

        var right = new DockPanel();var memberHeader = Ui.Label("УЧАСТНИКИ", Ui.Caption, true); memberHeader.FontWeight = FontWeights.SemiBold;
        memberHeader.Height=Ui.Metric("Size.Control");memberHeader.Margin=Ui.Insets(Ui.L,Ui.S,Ui.L,0); DockPanel.SetDock(memberHeader, Dock.Top); right.Children.Add(memberHeader); right.Children.Add(members);
        memberPanel = Ui.Panel(right,"AsideSurface");memberPanel.BorderThickness=new Thickness(Ui.Metric("Size.Hairline"),0,0,0);memberPanel.SetResourceReference(Border.BorderBrushProperty,"Border");Grid.SetColumn(memberPanel,3);root.Children.Add(memberPanel);
        SizeChanged += (_, _) => UpdateResponsivePanels();
        composer.PreviewKeyDown += (_, e) => { if (ComposerInput.ShouldSend(e.Key, Keyboard.Modifiers)) { e.Handled = true; Run(SendComposer); } };
        _ = Task.Run(ProcessIncoming);
        if (rtc is not null)
        {
            if(rtc is SfuRoom native)native.DiagnosticError+=error=>DiagnosticLog.Write("sfu",error);
            rtc.MessageReceived += EnqueueIncoming;
            rtc.Error += message => Dispatcher.BeginInvoke(() => { if (!closing) {activeVoiceFailed=voiceWasConnected;Notify(message,true);UpdateStatus();} });
            rtc.Changed += () => Dispatcher.BeginInvoke(() =>
            {
                if (closing) return;
                if (!rtc.Joined && !voiceIntent.Connecting && voiceChannelId is { } lost && voiceCommunity is { } current)
                {
                    var target=store.GetChannels(current.Id).FirstOrDefault(c=>c.Id==lost);
                    cloudChannelId=null;soundEvents.ConnectionLost();StopVoice();
                    if(target is not null)BeginReconnect(current.Id,target);
                }
                var active = rtc.ConnectedPeerIds; voice.SynchronizePeers(active);ReconcileStreamViewers();
                if(rtc.Joined&&!voiceIntent.Connecting)soundEvents.ObserveVoiceRoster(VoiceRoomKey,rtc.Participants);
                var paths = string.Join(", ", rtc.ConnectionPaths.Values.OrderBy(path => path));
                if (paths.Length > 0 && paths != lastConnectionPaths) Notify("Путь соединения: " + paths);
                lastConnectionPaths = paths;
                foreach (var id in remoteStreams.Keys.Where(id => !active.Contains(id)).ToList()) StopVideoPeer(id,true);
                var peerKey = string.Join("|", active.OrderBy(id => id));
                if (sharing && peerKey != sharedPeers && screen.CurrentInfo is { } info) { sharedPeers = peerKey; Run(() => SendPacket(new WireMessage("screen-start", profile.Id, voiceChannelId, JsonSerializer.SerializeToUtf8Bytes(info)))); }
                foreach (var id in remoteScreens.Keys.Where(id => !active.Contains(id)).ToList()) { remoteScreens.Remove(id); dismissedScreens.Remove(id); }
                RenderChannels();UpdateStatus();
            });
        }
        sounds.Error+=message=>Dispatcher.BeginInvoke(()=>{if(!closing)Notify(message,true);});
        voice.PacketReady += p => SendRealtime("audio", p); voice.Error += e => Dispatcher.BeginInvoke(() => Notify(e, true));
        var audioSettings = AudioDeviceSettings.Load(store.RootDirectory);
        VoiceMessagePlayer.ConfigureOutput(audioSettings.OutputId);sharePlayback.ConfigureOutput(audioSettings.OutputId);sounds.ConfigureOutput(audioSettings.OutputId);
        voice.LoadPeerVolumes(store.RootDirectory);
        voice.InitializeMicrophoneProcessing(audioSettings.NoiseSuppression);
        _ = voice.ConfigureDevicesAsync(audioSettings);
        screen.FrameReady += p => SendRealtime("screen-h264", p);
        screen.PreviewReady+=(pixels,_,_)=>{if(screen.CurrentInfo is{} info)QueueVideoFrame(this.profile.Id,info,pixels);};
        screen.Status += text => Dispatcher.BeginInvoke(() => { if (!closing && sharing) Notify(text); });
        screen.PauseChanged += paused => { if (screen.CurrentInfo is { } info) SendRealtime("screen-state", JsonSerializer.SerializeToUtf8Bytes(new ScreenCommand(profile.Id, info.SessionId, paused))); };
        shareAudio.PacketReady += p => { if (!deafened) SendRealtime("screen-audio", p); };
        shareAudio.Error += e => Dispatcher.BeginInvoke(() => Notify(e, true));
        sharePlayback.Error += e => Dispatcher.BeginInvoke(() => Notify(e, true)); screen.Error += e => Dispatcher.BeginInvoke(() => { StopSharing(); Notify(e, true); });
        recorder.Completed += path => Dispatcher.BeginInvoke(() => { if (closing || sendingRecording || !File.Exists(path)) return; Run(() => { recordedPath = path; StageRecording(path); Notify("Запись завершена. Можно прослушать и отправить."); return Task.CompletedTask; }); });
        recorder.Error += message => Dispatcher.BeginInvoke(() => { if(!closing){Notify(message,true);UpdateComposerState();} });
        activityTimer.Tick += (_, _) => Run(UpdateGame); activityTimer.Start();
        tray = new System.Windows.Forms.NotifyIcon { Text = "Wisp", Visible = true };
        trayIndicator=new TrayIndicator(tray);
        voice.SpeakingChanged+=(id,_)=>{if(id==VoiceService.LocalSpeakerId&&!Dispatcher.HasShutdownStarted)Dispatcher.BeginInvoke(UpdateTray);};
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Открыть Wisp", null, (_, _) => Dispatcher.BeginInvoke(RestoreFromTray));
        menu.Items.Add("Выйти из аккаунта", null, (_, _) => Dispatcher.BeginInvoke(() => Run(SignOut)));
        menu.Items.Add("Закрыть Wisp", null, (_, _) => Dispatcher.BeginInvoke(Quit));
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(RestoreFromTray);
        cloudTimer.Tick += (_, _) => Run(RefreshCloud); if (cloud is not null) cloudTimer.Start();
        Closing += OnClosing;
        Loaded += (_, _) => { if (cloud is not null && this.profile.AvatarPath is not null) Run(PublishProfile); };
        UpdateConnectionStatus();
        userName.Text = profile.Username; RenderServers();
        ShowHome();
        UpdateStatus(); ModalHost.Prepare(this);
    }

    private void EnqueueIncoming(WireMessage m)
    {
        if (closing) return;
        if (m.Type == "audio") { if (voiceChannelId == m.ChannelId) voice.Receive(m.SenderId, m.Payload, m.MissingAudioFrames); return; }
        if (m.Type == "screen-audio") { if (voiceChannelId == m.ChannelId && videoReceivers.ContainsKey(m.SenderId)) sharePlayback.Receive(m.SenderId, m.Payload); return; }
        if (m.Type == "screen-h264")
        {
            if (voiceChannelId != m.ChannelId || m.Payload.Length > 2 * 1024 * 1024 + 4 || (rtc is not null && !rtc.ConnectedPeerIds.Contains(m.SenderId))) return;
            if (videoReceivers.TryGetValue(m.SenderId, out var receiver)) { var data = m.Payload; if (rtc is null && !H264Frame.TryUnpack(data, out _, out data)) return; receiver.Enqueue(data); } return;
        }
        if (m.Type == "screen")
        {
            if (voiceChannelId != m.ChannelId || m.Payload.Length > 2 * 1024 * 1024 || (rtc is not null && !rtc.ConnectedPeerIds.Contains(m.SenderId))) return;
            latestFrames[m.SenderId] = m;
            if (Interlocked.Exchange(ref frameScheduled, 1) == 0) Dispatcher.BeginInvoke(() =>
            {
                Interlocked.Exchange(ref frameScheduled, 0);
                foreach (var id in latestFrames.Keys) if (latestFrames.TryRemove(id, out var frame) && !closing && frame.ChannelId == voiceChannelId && (rtc is null || rtc.ConnectedPeerIds.Contains(frame.SenderId))) ShowFrame(id, frame.Payload);
            });
            return;
        }
        var bytes = Interlocked.Add(ref queuedBytes, m.Payload.Length);
        if (bytes <= 32 * 1024 * 1024 && incoming.Writer.TryWrite((Volatile.Read(ref sessionEpoch), m))) return;
        Interlocked.Add(ref queuedBytes, -m.Payload.Length);
        if (Interlocked.Exchange(ref overflowed, 1) == 0) Dispatcher.BeginInvoke(() => Run(async () => { await Disconnect(); Notify("Сессия остановлена: слишком много входящих данных.", true); Interlocked.Exchange(ref overflowed, 0); }));
    }
    private async Task ProcessIncoming()
    {
        try
        {
            await foreach (var item in incoming.Reader.ReadAllAsync(receiveLifetime.Token))
            {
                var m = item.Message;
                Interlocked.Add(ref queuedBytes, -m.Payload.Length);
                if (closing) break;
                try { await Dispatcher.InvokeAsync(() => item.Epoch == sessionEpoch ? Receive(m) : Task.CompletedTask).Task.Unwrap(); }
                catch (Exception ex) { if (!closing) await Dispatcher.InvokeAsync(() => Notify(ex.Message, true)); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private Button Btn(string text, Action action, string? tip = null) => Buttons.Create(text, () => { try { action(); } catch (Exception ex) { Notify(ex.Message, true); } }, tip);
    private Button Btn(string text, Func<Task> action, string? tip = null) => Btn(text, () => Run(action), tip);
    private Button Tool(string icon, string tip, Action action) => Buttons.Tool(icon, tip, () => { try { action(); } catch (Exception ex) { Notify(ex.Message, true); } });
    private Button Tool(string icon, string tip, Func<Task> action) => Tool(icon, tip, () => Run(action));
    private ToggleIconButton VoiceToggle(string normal, string active, string normalTip, string activeTip, Action action)
    {
        var toggle = new ToggleIconButton { IconOff = normal, IconOn = active, ToolTipOff = normalTip, ToolTipOn = activeTip };
        toggle.Click += (_, _) => { try { action(); } catch (Exception ex) { Notify(ex.Message, true); } }; return toggle;
    }
    private ToggleIconButton VoiceToggle(string normal, string active, string normalTip, string activeTip, Func<Task> action) => VoiceToggle(normal, active, normalTip, activeTip, () => Run(action));
    private async void Run(Func<Task> action) { if (closing) return; try { await action(); } catch (Exception ex) { DiagnosticLog.Write("action",ex);if (!closing) Notify(ex.Message, true); } }
    private void Notify(string text, bool error = false) { notifications.Add(text,error); }
    internal void ReportNotification(string text, bool error = false) { Notify(text, error); if (error) ShowNotifications(); }
    private void ShowNotifications()
    {
        if (notificationPopup is not null) { notificationPopup.Show(); return; }
        notificationPopup = ModalHost.Open(this, "Уведомления", notifications, 560);
        notificationPopup.Closed += () => notificationPopup = null;
    }
    private void UpdateRecordingControls() => UpdateComposerState();
    private List<Community> MyCommunities() => cloud is null ? store.GetCommunities() : store.GetCommunities().Where(c => store.GetMembers(c.Id).Any(m => m.Id == profile.Id)).ToList();
    private void RenderServers()
    {
        servers.Reconcile(MyCommunities().Select(c=>
        {
            var asset=c.IconAssetId is null?null:store.GetAsset(c.IconAssetId);
            Action activate=()=>{try{SetCommunity(c);}catch(Exception error){Notify(error.Message,true);}};
            ContextMenu? Menu()=>store.CanManageCommunity(c.Id,profile.Id)?CommunityContextMenu(c):null;
            return new UiRow(c.Id,()=>new ServerRailItem(c.Name,asset?.LocalPath,community?.Id==c.Id,activate,Menu(),c.IconCoverFrame),
                Update:element=>((ServerRailItem)element).Update(c.Name,asset?.LocalPath,community?.Id==c.Id,activate,Menu(),c.IconCoverFrame));
        }));
    }
    private void SetCommunity(Community c) { if(community?.Id==c.Id)return;EnsureNoRecording();soundboardPopup?.Close();emojiPicker?.Close();if(community?.Id!=c.Id){cloudPresence=Array.Empty<PeerPresence>();presence=Array.Empty<PeerPresence>();cloudVoiceRooms=new();voiceRoomsCommunityId=null;memberRenderKey="";channelRenderKey="";} community = c;chatWorkspace.Visibility=Visibility.Visible;homeWorkspace.Visibility=Visibility.Collapsed;serverTitle.Text = c.Name; RenderServers(); RenderChannels(); var first = store.GetChannels(c.Id).FirstOrDefault(); if (first is not null) SelectChannel(first); RenderMembers(); UpdateStatus(); }
    private void EnsureNoRecording() { if (recorder.IsRecording || recordedPath is not null) throw new InvalidOperationException("Сначала отправь голосовую запись."); }
    private void RenderChannels()
    {
        var visiblePresence=DisplayPresence;
        bool canManage=community is not null&&store.CanManageCommunity(community.Id,profile.Id);
        var nextKey=(community?.Id??"")+":"+canManage+":"+selectedChannel?.Id+":"+voiceChannelId+":"+voiceIntent.Connecting+":"+
            string.Join("|",community is null?Array.Empty<string>():store.GetChannels(community.Id).Select(c=>$"{c.Id}:{c.Name}:{c.IsVoice}"))+":"+
            sharing+":"+string.Join("|",remoteStreams.Select(p=>p.Key+":"+p.Value.SessionId))+":"+
            string.Join("|",visiblePresence.OrderBy(p=>p.Profile.Id).Select(p=>$"{p.Profile.Id}:{p.Profile.Username}:{p.Profile.AvatarRevision}:{p.Profile.AvatarCoverFrame}:{avatars.Resolve(p.Profile,store)}:{p.VoiceChannelId}:{p.Muted}:{p.Deafened}"));
        if(nextKey==channelRenderKey)return;
        channelRenderKey=nextKey;
        var rows=new List<UiRow>();
        if(community is null){channels.Reconcile(rows);return;}
        foreach(bool voiceKind in new[]{false,true})
        {
            rows.Add(new UiRow(voiceKind?"voice-heading":"text-heading",()=>
            {
                var row=new DockPanel{Margin=Ui.Insets(Ui.S,Ui.S,Ui.Xs,0)};
                if(canManage){var add=Tool("plus",voiceKind?"Создать голосовой канал":"Создать текстовый канал",()=>CreateChannel(voiceKind));DockPanel.SetDock(add,Dock.Right);row.Children.Add(add);}
                var heading=Ui.Label(voiceKind?"ГОЛОСОВЫЕ КАНАЛЫ":"ТЕКСТОВЫЕ КАНАЛЫ");Ui.Style(heading,"Text.Section");heading.VerticalAlignment=VerticalAlignment.Center;row.Children.Add(heading);return row;
            }));
            foreach(var channel in store.GetChannels(community.Id).Where(c=>c.IsVoice==voiceKind))
            {
                int count=visiblePresence.Count(p=>p.VoiceChannelId==channel.Id);
                bool selected=selectedChannel?.Id==channel.Id;
                Action openChat=()=>Run(()=>{SelectChannel(channel);return Task.CompletedTask;});
                Action activate=channel.IsVoice?()=>Run(()=>JoinVoice(channel)):openChat;
                Action edit=()=>Run(()=>{EditChannel(channel);return Task.CompletedTask;});
                rows.Add(new UiRow(channel.Id,()=>new ChannelRow(channel,count,selected,canManage,activate,edit,openChat),
                    Update:element=>((ChannelRow)element).Update(channel,count,selected,canManage,activate,edit,openChat)));
                if(voiceKind)foreach(var person in visiblePresence.Where(p=>p.VoiceChannelId==channel.Id))
                    rows.Add(new UiRow(channel.Id+":"+person.Profile.Id,()=>
                    {
                        var row=new DockPanel();
                        if((person.Profile.Id==profile.Id&&sharing)||remoteStreams.ContainsKey(person.Profile.Id))
                        {
                            var live=Btn("В ЭФИРЕ",()=>WatchStream(person.Profile.Id),"Смотреть демонстрацию");Ui.Style(live,"Button.Danger");live.Padding=Ui.Insets(Ui.Xs,0);live.Margin=Ui.Insets(0,0,Ui.Xs,0);live.FontSize=Ui.Caption;live.MinHeight=0;live.VerticalAlignment=VerticalAlignment.Center;
                            DockPanel.SetDock(live,Dock.Right);row.Children.Add(live);
                        }
                        row.Children.Add(new VoiceParticipantRow(person,avatars.Resolve(person.Profile,store),voice,person.Profile.Id==profile.Id,()=>ViewProfile(person.Profile),()=>voice.SavePeerVolumes()));return row;
                    }));
            }
        }
        channels.Reconcile(rows);
    }
    private void ClearMessages() { messages.Clear();messageAvatarViews.Clear(); renderedMessages.Clear(); lastRenderedMessage = null; }
    private void LoadHistory(IEnumerable<ChatMessage> history)
    {
        ClearMessages(); loadingHistory = true;
        try { foreach (var message in history) AddMessage(message); } finally { loadingHistory = false; }
    }
    private void SelectChannel(Channel c)
    {
        if (recorder.IsRecording || recordedPath is not null) throw new InvalidOperationException("Сначала отправь голосовую запись.");
        selectedChannel = c; channelTitle.Text = c.Name; channelTitle.ToolTip = c.Name;
        pendingAttachment=null;draftEmojis.Clear();UpdateAttachmentPreview();LoadHistory(store.GetMessages(c.Id));RenderChannels();UpdateComposerState();
    }
    private void RenderMembers()
    {
        if (community is null) { members.Clear(); return; }
        var saved = store.GetMembers(community.Id).ToDictionary(x => x.Id); saved[profile.Id] = profile;
        foreach (var p in DisplayPresence) saved[p.Profile.Id] = p.Profile;
        saved[profile.Id] = profile;
        RefreshMessageAvatars(saved);
        var ordered = saved.Values.OrderByDescending(x => x.Id == profile.Id || DisplayPresence.Any(p => p.Profile.Id == x.Id)).ThenBy(x => x.Username).ToList();
        var key = string.Join("|", ordered.Select(p => $"{p.Id}:{p.Username}:{p.Game}:{p.About}:{p.BannerAssetId}:{p.BannerPath}:{p.BannerCoverFrame}:{p.AvatarRevision}:{p.AvatarCoverFrame}:{avatars.Resolve(p,store)}:{DisplayPresence.Any(s => s.Profile.Id == p.Id)}"));
        if (key == memberRenderKey) return;
        var oldKeys = members.Keys.ToHashSet(); memberRenderKey = key; members.Clear();
        foreach (var p in ordered)
        {
            bool online = p.Id == profile.Id || DisplayPresence.Any(s => s.Profile.Id == p.Id);
            string? game = p.Id == profile.Id ? profile.Game : DisplayPresence.FirstOrDefault(s => s.Profile.Id == p.Id)?.Profile.Game;
            members.Add(p.Id, () =>
            {
                var row = new DockPanel();
                var picture = AvatarCatalog.Circle(avatars.Resolve(p, store), Ui.Metric("Size.Avatar"), GifPlaybackMode.Hover,coverFrame:p.AvatarCoverFrame); picture.Margin = Ui.Insets(0, 0, Ui.S, 0); DockPanel.SetDock(picture, Dock.Left); row.Children.Add(picture);
                var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; info.Children.Add(Ui.Label(p.Username));
                var status = Ui.Label(game is null ? online ? "В сети" : "Не в сети" : $"Играет в {game}", Ui.Caption, true);
                if (online && game is null) status.SetResourceReference(TextBlock.ForegroundProperty, "Success"); info.Children.Add(status); row.Children.Add(info);
                var button = Btn(p.Username, () => ViewProfile(p), p.Username); Ui.Style(button, "Button.Row"); button.Height = Ui.RowHeight; button.Padding = Ui.Insets(Ui.S, Ui.Xs); button.Margin = Ui.Insets(0); button.Content = row; GifPlayback.SetScope(picture, button);
                if (!online) button.Opacity = .6; return button;
            }, oldKeys.Count > 0 && !oldKeys.Contains(p.Id));
        }
    }
    private void UpdateStatus()
    {
        bool inVoice = voiceChannelId is not null;
        voiceActionBlock.Visibility=inVoice||voiceIntent.Connecting?Visibility.Visible:Visibility.Collapsed;
        inviteButton.IsEnabled=community is not null&&cloud is not null;serverSettingsButton.Visibility=community is not null&&store.CanManageCommunity(community.Id,profile.Id)?Visibility.Visible:Visibility.Collapsed;
        leaveButton.Visibility=inVoice||cloudChannelId is not null?Visibility.Visible:Visibility.Collapsed;
        micButton.Visibility=deafenButton.Visibility=shareButton.Visibility=Visibility.Visible;
        soundboardButton.IsEnabled=inVoice&&rtc?.Joined==true;watchButton.IsEnabled=inVoice&&(remoteStreams.Count>0||remoteScreens.Count>0);
        connection.Text = voiceIntent.Connecting ? "Соединение" : voiceChannelId is not null ? "В канале: " + (community is null ? "" : store.GetChannels((voiceCommunity??community).Id).FirstOrDefault(c => c.Id == voiceChannelId)?.Name) + (rtc is null ? "" : $" · {rtc.TransportName}: {rtc.ConnectedCount} соединений") : cloudChannelId is not null ? "В канале · голос ещё не подключён" : cloud is not null ? "Сервер подключён" : "Локальный режим";
        UpdateConnectionStatus();
        userName.Text = profile.Username;var avatarPath=avatars.Resolve(profile,store);var avatarKey=profile.AvatarRevision+":"+profile.AvatarCoverFrame+":"+avatarPath;if(identityAvatar is not null&&avatarKey!=identityAvatarKey){identityAvatarKey=avatarKey;if(identityAvatar.Content is DockPanel identityBody&&identityBody.Children.OfType<ContentControl>().FirstOrDefault() is{} picture)picture.Content=AvatarCatalog.Circle(avatarPath,32,GifPlaybackMode.Hover,identityAvatar,profile.AvatarCoverFrame);}
        ((ToggleIconButton)micButton).IsChecked = muted || deafened;
        ((ToggleIconButton)deafenButton).IsChecked = deafened;
        ((ToggleIconButton)shareButton).IsChecked = sharing;
        shareButton.IsEnabled = inVoice && !voiceIntent.Connecting;
        UpdateComposerState();RefreshViewerAvatars();UpdateTray();UpdateResponsivePanels();
    }
    private void UpdateConnectionStatus()
    {
        connection.SetResourceReference(TextBlock.ForegroundProperty,"TextSecondary");
        if(voiceIntent.Connecting){connection.Text="SFU · Подключение";connection.SetResourceReference(TextBlock.ForegroundProperty,"Warning");}
        else if(voiceChannelId is not null&&rtc?.Joined==true)
        {
            var ping=rtc.ConnectionInfo?.PingMilliseconds;
            var name=voiceCommunity is null?null:store.GetChannels(voiceCommunity.Id).FirstOrDefault(c=>c.Id==voiceChannelId)?.Name;
            connection.Text="SFU · Подключено · "+(ping is{} ms?Math.Max(1,Math.Ceiling(ms)).ToString("0")+" мс":"пинг —")+"\n"+(voiceCommunity?.Name??"")+" · "+name;
            var color=rtc.ConnectionInfo?.Quality switch {ConnectionQuality.Fair=>"Warning",ConnectionQuality.Poor=>"Warning",ConnectionQuality.Good=>"Success",_=>"Warning"};connection.SetResourceReference(TextBlock.ForegroundProperty,color);
        }
        else if(activeVoiceFailed){connection.Text="SFU · Соединение потеряно";connection.SetResourceReference(TextBlock.ForegroundProperty,"Danger");}
        else connection.Text="Не подключён";
        connectionButton.ToolTip=voiceChannelId is not null&&rtc?.Joined==true?"Сведения о текущем звонке":"Сведения о сервере";
    }
    private void ConnectionDetails()
    {
        var dialog = new FormDialog(this, "Подключение", 560);
        if (rtc?.ConnectionInfo is { } info)
        {
            dialog.Note(rtc.Joined ? "Подключён к голосовому серверу" : voiceIntent.Connecting ? "Соединение с сервером" : "Соединение с сервером закрыто");
            dialog.Note("Сервер: " + info.Endpoint);
            dialog.Note("Пинг: " + (info.PingMilliseconds is { } ping ? $"{ping:0.0} мс" : "нет свежего измерения"));
            dialog.Note("Это время ответа HTTPS (RTT), включая обработку на сервере; измеряется каждые 5 секунд. Зелёный: <100 мс; жёлтый: 100–199 мс или проверка недоступна; красный: от 200 мс или разрыв связи.");
            if (info.MeasuredAt is { } measured) dialog.Note("Последняя проверка: " + measured.LocalDateTime.ToString("HH:mm:ss"));
            dialog.Note("Медиа: WebRTC → SFU → участники · UDP · DTLS/SRTP");
            dialog.Note("Сигналинг: WSS/TLS · проверка SHA-256 сертификата");
            dialog.Note("ICE: " + (info.CandidateTypes ?? "согласование маршрута"));
            dialog.Note("Голос: Opus · 48 кГц · 20 мс; демонстрация: H.264 + отдельная дорожка Opus");
        }
        else dialog.Note(voiceChannelId is null ? "Активного голосового подключения нет." : "Подключён через сервер · WebRTC SFU · DTLS/SRTP");
        if (voiceChannelId is not null)
        {
            dialog.Note("Канал: " + (community is null ? voiceChannelId : store.GetChannels((voiceCommunity??community).Id).FirstOrDefault(c => c.Id == voiceChannelId)?.Name));
            dialog.Note($"Участников: {(rtc?.Participants.Count ?? DisplayPresence.Count(p => p.VoiceChannelId == voiceChannelId))} · Соединений с участниками: {rtc?.ConnectedCount ?? 0}");
        }
        dialog.Accept("Закрыть"); dialog.ShowDialog();
    }
    private void CreateCommunity()
    {
        EnsureNoRecording();
        var dialog=new FormDialog(this,"Создать сервер");var name=dialog.Field("Название");dialog.Accept("Создать");if(dialog.ShowDialog()!=true)return;
        var created=store.CreateCommunity(name.Text,profile.Id);
        if(cloud is not null)creatingCommunities.Add(created.Id);
        SetCommunity(created);
        if(cloud is not null)Run(async()=>
        {
            try{await cloud.CreateCommunityAsync(new CommunitySnapshot(created,store.GetChannels(created.Id),new List<UserProfile>{profile}));await RefreshCloud();}
            finally{creatingCommunities.Remove(created.Id);}
        });
    }
    private async Task HostCommunity()
    {
        if(community is null||cloud is null)throw new InvalidOperationException("Нужно подключение к серверу.");
        string code=await cloud.CreateInviteAsync(community.Id);
        var dialog=new FormDialog(this,"Пригласить в сообщество");dialog.Field("Приглашение · действует 7 дней",code);dialog.Action("Скопировать",()=>Clipboard.SetText(code));dialog.Accept("Готово");dialog.ShowDialog();
    }
    private async Task JoinCommunity()
    {
        EnsureNoRecording();if(cloud is null)throw new InvalidOperationException("Нужно подключение к серверу.");
        var dialog=new FormDialog(this,"Присоединиться");var invite=dialog.Field("Приглашение wisp-invite:","",true);dialog.Accept("Подключиться");if(dialog.ShowDialog()!=true)return;
        string id=await cloud.JoinCommunityAsync(invite.Text.Trim());
        var snapshot=await cloud.GetAsync<CommunitySnapshot>($"communities/{id}/snapshot")??throw new InvalidOperationException("Сообщество не найдено.");
        store.SaveSnapshot(snapshot);store.UpsertMember(id,profile);SetCommunity(snapshot.Community);await RefreshCloud();
    }
    private async Task JoinVoice(Channel c,bool automatic=false,CancellationToken cancellation=default)
    {
        if(!automatic&&voiceChannelId==c.Id)return;
        if(!automatic)CancelReconnect();EnsureNoRecording();var joiningCommunity=automatic?voiceCommunity:community;if(joiningCommunity is null)return;
        if(!automatic){activeVoiceFailed=false;voiceWasConnected=false;soundEvents.ConfirmVoiceLeft();}StopVoice();voiceCommunity=joiningCommunity;long request=voiceIntent.Begin(c.Id);
        voiceChannelId=c.Id;cloudChannelId=c.Id;RenderChannels();UpdateStatus();
        await Dispatcher.Yield(DispatcherPriority.Render);await voiceTransitions.WaitAsync(cancellation);
        try
        {
            if(!voiceIntent.IsCurrent(request))return;
            if(rtc is null||cloud is null)throw new InvalidOperationException("Нужно подключение к серверу Wisp.");
            await rtc.EnterAsync(joiningCommunity.Id,c.Id,muted,deafened,cancellation);
            if(!voiceIntent.IsCurrent(request))return;await rtc.SetMutedAsync(muted,deafened);
            if(!voiceIntent.IsCurrent(request))return;
            voice.Muted=muted;voice.Deafened=deafened;voice.Start();voiceIntent.Complete(request);voiceWasConnected=true;activeVoiceFailed=false;voiceJoinedAt=(cloud?.ServerNow()??DateTimeOffset.UtcNow).ToUnixTimeMilliseconds();soundEvents.ConfirmVoiceConnected(VoiceRoomKey,rtc.Participants,automatic);RenderChannels();UpdateStatus();
            try {await cloud!.PresenceAsync(joiningCommunity.Id,profile,c.Id);if(voiceIntent.IsCurrent(request))await RefreshCloud();}
            catch(Exception ex){DiagnosticLog.Write("voice-presence",ex);if(voiceIntent.IsCurrent(request))Notify("Голос подключён, но не удалось обновить список участников или чат: "+ex.Message,true);}
            Notify("Подключён к серверу. Голос и демонстрация передаются через SFU.");
        }
        catch
        {
            if(voiceIntent.IsCurrent(request)){cloudChannelId=null;StopVoice();if(rtc is not null)await rtc.LeaveAsync();RenderChannels();UpdateStatus();}
            throw;
        }
        finally{voiceTransitions.Release();}
    }
    private async Task LeaveVoice()
    {
        foreach(var peer in remoteStreams.Where(p=>viewers.ContainsKey(p.Key)).ToArray())await SendPacket(new WireMessage("screen-state",profile.Id,voiceChannelId,JsonSerializer.SerializeToUtf8Bytes(new ScreenCommand(peer.Key,peer.Value.SessionId,Viewing:false))));
        CancelReconnect();var leavingCommunity=voiceCommunity;activeVoiceFailed=false;voiceWasConnected=false;soundEvents.ConfirmVoiceLeft();cloudChannelId=null;StopVoice();voiceCommunity=null;RenderChannels();UpdateStatus();await voiceTransitions.WaitAsync();
        try{if(rtc is not null)await rtc.LeaveAsync();if(cloud is not null&&leavingCommunity is not null)await cloud.PresenceAsync(leavingCommunity.Id,profile,null);}
        finally{voiceTransitions.Release();}
    }
    private void CancelReconnect()=>Interlocked.Exchange(ref reconnect,null)?.Cancel();
    private void BeginReconnect(string communityId,Channel target)
    {
        CancelReconnect();var lifetime=new CancellationTokenSource();reconnect=lifetime;
        Run(async()=>
        {
            try
            {
                foreach(int delay in new[]{1,2,4,8,15})
                {
                    if(closing||voiceCommunity?.Id!=communityId||lifetime.IsCancellationRequested)return;
                    voiceIntent.Begin(target.Id);voiceChannelId=target.Id;RenderChannels();UpdateStatus();
                    await Task.Delay(TimeSpan.FromSeconds(delay),lifetime.Token);
                    try {await JoinVoice(target,true,lifetime.Token);return;}
                    catch(OperationCanceledException)when(lifetime.IsCancellationRequested){return;}
                    catch(Exception ex){DiagnosticLog.Write("voice-reconnect",ex);}
                }
                Notify("Не удалось восстановить голос после пяти попыток. Проверь сервер и подключись к каналу вручную.",true);
            }
            catch(OperationCanceledException)when(lifetime.IsCancellationRequested){}
            finally
            {
                if(ReferenceEquals(reconnect,lifetime))
                {
                    reconnect=null;
                    if(rtc?.Joined!=true){cloudChannelId=null;StopVoice();RenderChannels();UpdateStatus();}
                }
                lifetime.Dispose();
            }
        });
    }
    public void Quit(){exitRequested=true;Close();}
    private void RestoreFromTray(){Show();WindowState=WindowState.Normal;Activate();foreach(var viewer in viewers.Values)viewer.Show();}
    private async Task SignOut()
    {
        SessionVault.Clear(store.RootDirectory);
        try{await Disconnect();if(cloud is not null)await cloud.LogoutAsync();}finally{exitRequested=true;Close();}
    }
    private void StopVoice() { if(recorder.IsRecording&&recorder.UsesSharedSource)Run(RecordVoice);soundboardPopup?.Close();voiceIntent.Leave(); StopSharing(); voice.Stop(); sharePlayback.Stop(); foreach (var id in remoteStreams.Keys.ToList()) StopVideoPeer(id); videoFrames.Clear(); voiceChannelId = null; foreach (var v in viewers.Values.ToList()) v.Close(); viewers.Clear(); remoteScreens.Clear(); dismissedScreens.Clear(); UpdateStatus(); }
    private async Task Disconnect()
    {
        await LeaveVoice();cloudPresence=Array.Empty<PeerPresence>();Interlocked.Increment(ref sessionEpoch);latestFrames.Clear();presence=Array.Empty<PeerPresence>();RenderMembers();RenderChannels();UpdateStatus();
    }
    private async Task ToggleMic() { muted = !muted;new VoicePreferences(muted,deafened).Save(store.RootDirectory);voice.Muted = muted; RenderChannels(); UpdateStatus(); if (rtc is not null) await rtc.SetMutedAsync(muted, deafened); soundEvents.ObserveLocalAudio(muted,deafened); UpdateStatus(); }
    private async Task ToggleDeafen() { deafened = !deafened;new VoicePreferences(muted,deafened).Save(store.RootDirectory);voice.Deafened = deafened; sharePlayback.Deafened = deafened; RenderChannels(); UpdateStatus(); if (rtc is not null) await rtc.SetMutedAsync(muted, deafened); if (deafened) { sounds.StopNotifications();sounds.StopFile(); VoiceMessagePlayer.PauseActive(); } soundEvents.ObserveLocalAudio(muted,deafened); UpdateStatus(); }
    private async Task UpdateGame()
    {
        var game = GamePresenceService.DetectGame(); if (game == profile.Game) return; profile = profile with { Game = game }; store.SaveProfile(profile); if(cloud is not null)await cloud.SaveProfileAsync(profile); RenderMembers();
    }
    private async Task SendMessage()
    {
        if (recorder.IsRecording) { Notify("Сначала заверши запись голосового сообщения.", true); return; }
        if (sendingMessage || selectedChannel is null || (string.IsNullOrWhiteSpace(composer.Text) && pendingAttachment is null)) return;
        sendingMessage = true; sendButton.ToolTip = "Отправка…"; sendButton.IsEnabled = false;
        var channel = selectedChannel; var communityId = community?.Id; var text = composer.Text; var attachment = pendingAttachment;
        try
        {
            var catalog = communityId is null ? new List<MediaAsset>() : store.GetAssets(communityId, "emoji");
            var used = new Dictionary<string,string>();
            foreach (Match match in Regex.Matches(text, @":[\p{L}\p{N}_-]{1,40}:"))
            {
                var token = match.Value;
                var asset = draftEmojis.GetValueOrDefault(token) ?? catalog.FirstOrDefault(a => EmojiToken(a) == token);
                if (asset is null) continue; used[token] = asset.Id;
                if (cloud is not null && !publishedEmojis.Contains(asset.Id))
                {
                    await CommunityAssets.UploadEmojiAsync(cloud, asset);
                    publishedEmojis.Add(asset.Id);
                }
            }
            if (attachment is not null && store.GetAsset(attachment) is { } attached)
            {
                if (cloud is not null && !publishedFiles.Contains(attached.Id)) { await CloudFiles.UploadAsync(cloud, attached); publishedFiles.Add(attached.Id); }
            }
            var message = new ChatMessage(Guid.NewGuid().ToString("N"), channel.Id, profile.Id, profile.Username, text, DateTimeOffset.UtcNow, attachment, used.Count == 0 ? null : used);
            if (cloud is not null && communityId is not null) await cloud.SendMessageAsync(communityId, message);
            store.SaveMessage(message); if (selectedChannel?.Id == channel.Id) AddMessage(message);
            if (composer.Text == text) composer.Clear();
            if (pendingAttachment == attachment) pendingAttachment = null;
            draftEmojis.Clear(); UpdateAttachmentPreview();  Buttons.Flash(sendButton, "check");
        }
        finally { sendingMessage = false; sendButton.ToolTip="Отправить сообщение";UpdateComposerState(); if (Keyboard.FocusedElement == sendButton) composer.Focus(); }
    }
    private static string EmojiToken(MediaAsset a)
    {
        var name = new string(Path.GetFileNameWithoutExtension(a.Name).Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_').Take(32).ToArray());
        return ":" + (name.Length == 0 ? "emoji" : name) + ":";
    }
    private SelectableChatText MessageText(ChatMessage message, FrameworkElement scope)
    {
        var text=new SelectableChatText();chatTextSelection.Register(text);
        var tokens=message.Emojis?.Keys;double size=EmojiContent.EmojiSize(message.Text,tokens);
        text.Document.LineHeight=double.NaN;
        foreach(var segment in EmojiContent.Split(message.Text,tokens))
        {
            var asset=segment.IsCustom&&message.Emojis?.TryGetValue(segment.Value,out var id)==true?store.GetAsset(id):null;
            if(asset?.Kind=="emoji"&&asset.CommunityId==community?.Id)
            {
                try{var image=AvatarCatalog.Image(asset.LocalPath,size,mode:GifPlaybackMode.Hover,hoverScope:scope);image.Stretch=Stretch.Uniform;text.AppendEmoji(image,segment.Value);continue;}
                catch(Exception error)when(error is IOException or NotSupportedException or ArgumentException){DiagnosticLog.Write("chat-emoji",error);}
            }
            text.Paragraph.Inlines.Add(new Run(segment.Value){FontSize=segment.IsEmoji&&!segment.IsCustom?size:Ui.Body});
        }
        return text;
    }
    private void UpdateAttachmentPreview()
    {
        attachmentPreview.Children.Clear(); UpdateRecordingControls(); if (pendingAttachment is null) return;
        var asset = store.GetAsset(pendingAttachment); if (asset is null) return;
        var row = new DockPanel { Margin = Ui.Insets(0, Ui.Xs, 0, Ui.S) };
        var remove = Tool("x", "Убрать вложение", () => { pendingAttachment = null; UpdateAttachmentPreview(); }); DockPanel.SetDock(remove, Dock.Right); row.Children.Add(remove);
        row.Children.Add(new TextBlock { Text = $"{asset.Name} · {asset.Size / 1024} КБ", VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis }); attachmentPreview.Children.Add(row);
        if(asset.Kind=="voice"||new[]{".wav",".mp3"}.Contains(Path.GetExtension(asset.Name).ToLowerInvariant()))attachmentPreview.Children.Add(new VoiceMessagePlayer(asset.LocalPath,e=>Notify(e,true),()=>!deafened));
        else if(AttachmentCard.IsImage(asset.Name)||AttachmentCard.IsVideo(asset.Name))
            attachmentPreview.Children.Add(new AttachmentCard(this,asset,()=>AttachmentCard.Save(this,asset),()=>OpenAsset(asset),error=>Notify(error,true)){MaxHeight=Ui.Metric("Height.MediaPreview"),MaxWidth=Ui.Metric("Width.MediaPreview")});
    }
    private void AddMessage(ChatMessage m)
    {
        if (!renderedMessages.Add(m.Id)) return;
        bool grouped = lastRenderedMessage is { } previous && previous.AuthorId == m.AuthorId &&
            m.SentAt >= previous.SentAt && m.SentAt - previous.SentAt < TimeSpan.FromMinutes(5);
        lastRenderedMessage = m;
        messages.Add(m.Id, () => MessageRow(m, grouped), !loadingHistory);
        while (messages.Count > 200) messages.RemoveFirst();
        messages.ScrollToEnd();
    }
    private FrameworkElement MessageRow(ChatMessage m, bool grouped)
    {
        var layout = new Grid { Margin = Ui.Insets(Ui.L, grouped ? Ui.Xs : Ui.L, Ui.L, 0), Background = Brushes.Transparent };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Ui.Metric("Size.Avatar") + Ui.M) });
        layout.ColumnDefinitions.Add(new ColumnDefinition());
        if (!grouped)
        {
            var author = m.AuthorId == profile.Id ? profile : community is null ? null : store.GetMembers(community.Id).FirstOrDefault(member => member.Id == m.AuthorId);
            var picture=CreateMessageAvatar(m.AuthorId,author,layout);
            picture.VerticalAlignment = VerticalAlignment.Top; picture.HorizontalAlignment = HorizontalAlignment.Left; layout.Children.Add(picture);
        }
        var row = new StackPanel(); Grid.SetColumn(row, 1); layout.Children.Add(row);
        if (!grouped)
        {
            var meta = new StackPanel { Orientation = Orientation.Horizontal, Margin = Ui.Insets(0, 0, 0, Ui.Xs) };
            var name = Ui.Label(m.AuthorName); name.FontWeight = FontWeights.SemiBold; meta.Children.Add(name);
            var timestamp = Ui.Label(m.SentAt.LocalDateTime.ToString("dd.MM HH:mm"), Ui.Caption, true); timestamp.Margin = Ui.Insets(Ui.S, 0, 0, 0); meta.Children.Add(timestamp); row.Children.Add(meta);
        }
        if (!string.IsNullOrEmpty(m.Text)) row.Children.Add(MessageText(m, layout));
        if (m.AttachmentId is not null)
        {
            var asset = store.GetAsset(m.AttachmentId); if (asset?.CommunityId != community?.Id) asset = null;
            if (asset is not null)
            {
                if (asset.Kind == "emoji")
                { try { row.Children.Add(AvatarCatalog.Image(asset.LocalPath, Ui.Body, mode: GifPlaybackMode.Hover, hoverScope: layout)); } catch { } }
                else if (asset.Kind == "voice") row.Children.Add(new VoiceMessagePlayer(asset.LocalPath, e => Notify(e, true), () => !deafened));
                else row.Children.Add(new AttachmentCard(this,asset,()=>AttachmentCard.Save(this,asset),()=>OpenAsset(asset),error=>Notify(error,true)));
            }
            else row.Children.Add(Ui.Label("Вложение недоступно на этом компьютере", Ui.Caption, true));
        }
        return layout;
    }
    private void OpenAsset(MediaAsset asset)
    {
        if(asset.CommunityId!=community?.Id)throw new InvalidOperationException("Вложение принадлежит другому сообществу.");
        if(AttachmentCard.IsImage(asset.Name)||AttachmentCard.IsVideo(asset.Name))MediaViewer.Show(this,asset,error=>Notify(error,true),()=>!deafened);
        else AttachmentCard.Save(this,asset);
    }
    private async Task<MediaAsset?> ImportAndSend(string path, string kind)
    {
        if (community is null) return null; var asset = store.ImportAsset(community.Id, path, kind);
        IsEnabled = false;
        try { await BroadcastAsset(asset); return asset; } finally { if (!closing) IsEnabled = true; }
    }
    private async Task AttachFile()
    {
        if (selectedChannel is null) return; var pick = new Microsoft.Win32.OpenFileDialog { Title = "Прикрепить медиа или файл · до 16 МБ" }; if (pick.ShowDialog(this) != true) return;
        await Task.CompletedTask; var a = community is null ? null : store.ImportAsset(community.Id, pick.FileName, "attachment"); if (a is not null) { pendingAttachment = a.Id; UpdateAttachmentPreview(); Notify($"Прикреплён {a.Name}. Нажми «Отправить».");  }
    }
    private async Task RecordVoice()
    {
        if (selectedChannel is null) return;
        if (!recorder.IsRecording && recordedPath is null)
        {
            var path = Path.Combine(store.RootDirectory, $"recording-{Guid.NewGuid():N}.wav"); recorder.Start(path,rtc?.Joined==true?voice:null,voice.Devices.InputId); ((ToggleIconButton)recordButton).IsChecked = true; UpdateRecordingControls();
        }
        else
        {
            sendingRecording = true;
            try { string path = recordedPath ?? await recorder.StopAsync(); recordedPath = null; ((ToggleIconButton)recordButton).IsChecked = false;
                StageRecording(path);

            } finally { sendingRecording = false; UpdateRecordingControls(); }
        }
    }
    private void StageRecording(string path)
    {
        try { if (community is not null) { var asset = store.ImportAsset(community.Id, path, "voice"); pendingAttachment = asset.Id; } }
        finally { File.Delete(path); recordedPath = null; ((ToggleIconButton)recordButton).IsChecked = false; UpdateAttachmentPreview(); UpdateRecordingControls(); }
    }
    private async Task CancelRecording()
    {
        if (!recorder.IsRecording && recordedPath is null) { if (pendingAttachment is not null && store.GetAsset(pendingAttachment)?.Kind == "voice") { pendingAttachment = null; UpdateAttachmentPreview(); UpdateRecordingControls(); } return; }
        sendingRecording = true;
        try { var path = recordedPath ?? await recorder.StopAsync(); recordedPath = null; File.Delete(path); ((ToggleIconButton)recordButton).IsChecked = false; Notify("Запись удалена.");  }
        finally { sendingRecording = false; UpdateRecordingControls(); }
    }
    private async Task Receive(WireMessage m)
    {
        bool reliable = m.Type is "asset" or "chat" or "sound";
        if (reliable) await inbound.WaitAsync();
        try { if (!closing) await ReceiveCore(m); } finally { if (reliable) inbound.Release(); }
    }
    private async Task ReceiveCore(WireMessage m)
    {
        if (m.SenderId == profile.Id) return;
        switch (m.Type)
        {
            case "chat":
                var chat = JsonSerializer.Deserialize<ChatMessage>(m.Payload); if (chat is null || chat.AuthorId != m.SenderId || community is null || !store.GetChannels(community.Id).Any(c => c.Id == chat.ChannelId)) return;
                store.SaveMessage(chat); if (selectedChannel?.Id == chat.ChannelId) AddMessage(chat); soundEvents.ObserveMessages((community?.Id??"")+"/"+chat.ChannelId,new[]{chat}); break;
            case "audio": if (voiceChannelId == m.ChannelId) voice.Receive(m.SenderId, m.Payload, m.MissingAudioFrames); break;
            case "screen": if (voiceChannelId == m.ChannelId) ShowFrame(m.SenderId, m.Payload); break;
            case "screen-start":
                if (voiceChannelId != m.ChannelId || m.Payload.Length > 2048) return;
                var streamInfo = JsonSerializer.Deserialize<ScreenStreamInfo>(m.Payload); if (streamInfo?.IsValid != true || streamInfo.SessionId.Length > 64) return;
                if (!remoteStreams.TryGetValue(m.SenderId, out var oldInfo) || oldInfo != streamInfo) { soundEvents.ObserveStreamStarted(VoiceRoomKey,m.SenderId,streamInfo.SessionId,streamInfo.StartedAtUnixMs<voiceJoinedAt);StopVideoPeer(m.SenderId);remoteStreams[m.SenderId]=streamInfo;RenderChannels();UpdateStatus(); }
                else if(viewers.ContainsKey(m.SenderId))AnnounceView(m.SenderId,true,true);
                break;
            case "video-report":
                if (sharing && voiceChannelId == m.ChannelId && m.Payload.Length <= 512 && JsonSerializer.Deserialize<VideoNetworkReport>(m.Payload) is { } report) screen.ObserveNetwork(m.SenderId, report);
                break;
            case "screen-keyframe":
                if (!sharing || voiceChannelId != m.ChannelId || m.Payload.Length > 512) return;
                var request = JsonSerializer.Deserialize<ScreenCommand>(m.Payload);
                if (request?.TargetId == profile.Id && request.SessionId == screen.CurrentInfo?.SessionId && screenKeyRequests.TryEnter()) screen.RequestKeyFrame();
                break;
            case "screen-state": HandleStreamState(m); break;
            case "screen-stop": StopVideoPeer(m.SenderId,true);remoteScreens.Remove(m.SenderId);dismissedScreens.Remove(m.SenderId);RenderChannels();UpdateStatus();break;
            case "asset":
                if (voiceCommunity is null || voiceChannelId != m.ChannelId) return; var communityId = voiceCommunity.Id; var incoming = JsonSerializer.Deserialize<AssetTransfer>(m.Payload); if (incoming is null || incoming.Data.Length > 16 * 1024 * 1024 || incoming.Name.Length > 200 || incoming.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || incoming.Name != Path.GetFileName(incoming.Name) || incoming.Name is "." or "..") return;
                using (var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256))
                {
                    hash.AppendData(Encoding.UTF8.GetBytes(communityId + "\0" + incoming.Kind + "\0")); hash.AppendData(incoming.Data);
                    if (!string.Equals(Convert.ToHexString(hash.GetHashAndReset()), incoming.Id, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Неверный идентификатор вложения.");
                }
                var dir = Path.Combine(store.RootDirectory, "incoming", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir); var temporary = Path.Combine(dir, "payload.tmp");
                try { await File.WriteAllBytesAsync(temporary, incoming.Data); var a = store.ImportAsset(communityId, temporary, incoming.Kind, incoming.Name); if (a.Id != incoming.Id) Notify("Вложение отклонено: неверный идентификатор.", true);
                    else if (selectedChannel is not null && store.GetMessages(selectedChannel.Id).Any(msg => msg.AttachmentId == a.Id || msg.Emojis?.ContainsValue(a.Id) == true))
                    { var history = store.GetMessages(selectedChannel.Id); LoadHistory(history); } } finally { Directory.Delete(dir, true); } break;
            case "sound":
                if (deafened || voiceChannelId != m.ChannelId) return; var sound = voiceCommunity is null ? null : await EnsureSoundAsync(voiceCommunity.Id,Encoding.UTF8.GetString(m.Payload)); if (sound?.Kind == "sound" && sound.CommunityId == voiceCommunity?.Id) sounds.PlayFile(sound.LocalPath,sound.Volume); break;
        }
    }
    private void SendRealtime(string type, byte[] payload) { if (voiceChannelId is null || rtc?.Joined != true) return; _ = SendRealtimeSafe(type, voiceChannelId, payload); }
    private async Task SendRealtimeSafe(string type, string channel, byte[] data) { try { await SendPacket(new WireMessage(type, profile.Id, channel, data)); } catch { /* Disconnect/presence events surface connection failure. */ } }
    private async Task EmojiPanel()
    {
        if(community is not{} target)return;
        if(emojiPicker?.IsOpen==true){emojiPicker.Close();return;}
        Dictionary<string,AssetTransfer>? remote=null;
        if(cloud is not null){remote=await cloud.GetAsync<Dictionary<string,AssetTransfer>>($"communities/{target.Id}/emojis")??new();await FetchEmojiAssets(remote.Keys,target.Id);}
        if(community?.Id!=target.Id||closing)return;
        var assets=store.GetAssets(target.Id,"emoji").Where(a=>remote is null||remote.ContainsKey(a.Id)).Select(a=>remote is not null&&remote.TryGetValue(a.Id,out var fresh)?a with{Name=fresh.Name}:a).ToDictionary(a=>a.Id);
        var items=new[]{"😀","😃","😄","😁","😆","😅","😂","🙂","😉","😊","😍","🥰","😎","🤔","😢","😭","😡","😱","👍","👎","👏","🙌","🙏","💪","❤️","💜","🔥","🎉","✨","✅","❌","👀","💀","🎮","🚀","💯","🇰🇿","🇰🇷"}.Select(value=>new EmojiPickerItem(value,value)).ToList();
        items.AddRange(assets.Values.Select(a=>new EmojiPickerItem(EmojiToken(a),a.Name,a.LocalPath)));
        emojiPicker=EmojiPicker.Show(emojiButton,items,item=>
        {
            if(community?.Id!=target.Id)return;
            var token=item.Token;
            var asset=assets.Values.FirstOrDefault(a=>a.LocalPath==item.ImagePath);
            if(asset is not null){if(draftEmojis.TryGetValue(token,out var previous)&&previous.Id!=asset.Id)token=token[..^1]+"_"+asset.Id[..6]+":";draftEmojis[token]=asset;}
            var caret=composer.SelectionStart;composer.SelectedText=token;composer.CaretIndex=caret+token.Length;composer.Focus();
        },store.CanManageCommunity(target.Id,profile.Id)?()=>OpenCommunitySettings(target):null);
    }
    private Task SendPacket(WireMessage message) => rtc?.Joined == true ? rtc.SendAsync(message) : Task.CompletedTask;
    private async Task BroadcastAsset(MediaAsset asset)
    {
        if(cloud is null)return;
        if(asset.Kind=="emoji")await CommunityAssets.UploadEmojiAsync(cloud,asset);
        else if(asset.Kind is "attachment" or "voice")await CloudFiles.UploadAsync(cloud,asset);
        else if(asset.Kind=="sound")await cloud.TryCreateAsync($"communities/{asset.CommunityId}/sounds/{asset.Id}",new AssetTransfer(asset.Id,asset.Name,asset.Kind,await File.ReadAllBytesAsync(asset.LocalPath)));
    }

    private void EditAudioDevices()
    {
        var dialog = new FormDialog(this, "Устройства связи", 600);
        dialog.Note("Устройства можно менять во время разговора. По умолчанию используются мультимедийные устройства Windows. Эхоподавление зависит от драйвера; отдельные AEC и автоусиление пока не добавлены.");
        var suppression = dialog.Check("Шумоподавление", voice.NoiseSuppression);
        suppression.ToolTip = "Убирает фоновый шум (вентилятор, клавиатура). Может слегка менять тембр голоса. Не влияет на звук демонстрации.";
        void UpdateSuppressionAvailability()
        {
            suppression.IsEnabled = voice.NoiseSuppressionAvailable;
            suppression.Content = voice.NoiseSuppressionAvailable ? "Шумоподавление" : "Шумоподавление · недоступно";
        }
        void AvailabilityChanged() => Dispatcher.BeginInvoke(UpdateSuppressionAvailability);
        void SaveSuppression()
        {
            var settings = AudioDeviceSettings.Load(store.RootDirectory) with { NoiseSuppression = suppression.IsChecked == true };
            try { settings.Save(store.RootDirectory); voice.NoiseSuppression = settings.NoiseSuppression; }
            catch (Exception ex)
            {
                suppression.IsChecked = voice.NoiseSuppression;
                Notify("Не удалось сохранить настройку шумоподавления: " + ex.Message, true);
            }
        }
        UpdateSuppressionAvailability();
        suppression.Checked += (_, _) => { if (suppression.IsChecked != voice.NoiseSuppression) SaveSuppression(); };
        suppression.Unchecked += (_, _) => { if (suppression.IsChecked != voice.NoiseSuppression) SaveSuppression(); };
        ComboBox DeviceList(string label, bool capture, string? selected)
        {
            dialog.Note(label);
            var devices = AudioDeviceSettings.List(capture);
            var box = new ComboBox { ItemsSource = devices, DisplayMemberPath = "Name", SelectedIndex = Math.Max(0, devices.ToList().FindIndex(device => device.Id == selected)), Margin = Ui.Insets(0, Ui.Xs, 0, Ui.M) };
            dialog.Add(box); return box;
        }
        var microphone = DeviceList("Микрофон", true, voice.Devices.InputId);
        var headphones = DeviceList("Голос участников", false, voice.Devices.OutputId);
        dialog.Note("Настройка выхода относится к голосовому разговору. Мультимедиа чата и звук демонстрации используют системный выход Windows.");
        dialog.Accept("Применить");
        bool? accepted;
        voice.NoiseSuppressionAvailabilityChanged += AvailabilityChanged;
        try { accepted = dialog.ShowDialog(); }
        finally { voice.NoiseSuppressionAvailabilityChanged -= AvailabilityChanged; }
        if (accepted != true) return;
        var settings = new AudioDeviceSettings(((AudioDeviceChoice)microphone.SelectedItem).Id, ((AudioDeviceChoice)headphones.SelectedItem).Id, voice.NoiseSuppression);
        settings.Save(store.RootDirectory);
        Run(async () => { await ApplyAudio(settings); Notify("Аудиоустройства сохранены. Голосовой канал остаётся подключённым."); });
    }
    private void EditVoiceServer()
    {
        var dialog=new FormDialog(this,"Сервер Wisp");
        dialog.Note("Аккаунт, чат, файлы, присутствие и сигналинг обслуживает твой сервер с PostgreSQL.");
        dialog.Note("Адрес: "+(cloud?.Config.Url??"не подключён"));dialog.Note("SFU · WebRTC · DTLS/SRTP · без Google STUN и P2P-сигналинга");
        dialog.Note("Чтобы сменить сервер, полностью выйди из Wisp и запусти Wisp.exe --setup.");dialog.Accept("Закрыть");dialog.ShowDialog();
    }
    private async Task PublishProfile()
    {
        if(cloud is not null)
        {
            if(profile.AvatarPath is{} avatar&&File.Exists(avatar))
            {
                var bytes=await File.ReadAllBytesAsync(avatar);var revision=AvatarCatalog.Revision(avatar);
                await cloud.PutAsync($"profiles/{profile.Id}/Images/avatar",new AssetTransfer(revision,Path.GetFileName(avatar),"avatar",bytes));profile=profile with{AvatarRevision=revision};
            }
            else if(profile.AvatarRevision is null) await cloud.DeleteAsync($"profiles/{profile.Id}/Images/avatar");
            if(profile.BannerPath is{} banner&&File.Exists(banner))
            {
                var bytes=await File.ReadAllBytesAsync(banner);var revision=AvatarCatalog.Revision(banner);
                await cloud.PutAsync($"profiles/{profile.Id}/Images/banner",new AssetTransfer(revision,Path.GetFileName(banner),"banner",bytes));profile=profile with{BannerAssetId=revision};
            }
            else if(profile.BannerAssetId is null) await cloud.DeleteAsync($"profiles/{profile.Id}/Images/banner");
            await cloud.SaveProfileAsync(profile);
        }
        store.SaveProfile(profile);
    }
    private void ViewProfile(UserProfile p)
    {
        var body=new ProfileView(p,avatars.Resolve(p.Id==profile.Id?profile:p,store),p.BannerPath,p.Id==profile.Id?EditProfile:null);
        ModalHost.Open(this,p.Username,new ScrollViewer{Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled},ProfileLayout.CardWidth,new ModalOptions(Headerless:true));
    }
    private static BitmapImage LoadImage(string path, int width) { using var stream = File.OpenRead(path); var b = new BitmapImage(); b.BeginInit(); b.CacheOption = BitmapCacheOption.OnLoad; b.DecodePixelWidth = width; b.StreamSource = stream; b.EndInit(); b.Freeze(); return b; }
    private async Task RefreshCloud()
    {
        if (cloud is null || cloudPolling || closing) return;
        cloudPolling = true; var id = community?.Id; var channelId = selectedChannel?.Id;
        try
        {
            var pendingCreates=creatingCommunities.ToHashSet();
            var access=await cloud.GetAsync<Dictionary<string,bool>>($"users/{profile.Id}/communities")??new();
            if (savedRefreshToken != cloud.RefreshToken) { SessionVault.Save(store.RootDirectory, cloud.RefreshToken!); savedRefreshToken = cloud.RefreshToken; }
            foreach(var old in MyCommunities().Where(c=>!access.ContainsKey(c.Id)&&!pendingCreates.Contains(c.Id)&&!creatingCommunities.Contains(c.Id)).ToArray())
            {
                store.RemoveCommunityMembership(old.Id,profile.Id);
                if(voiceCommunity?.Id==old.Id){CancelReconnect();activeVoiceFailed=false;cloudChannelId=null;StopVoice();voiceCommunity=null;if(rtc is not null)await rtc.LeaveAsync();}
                if(community?.Id==old.Id){await CancelRecording();ShowHome();id=null;channelId=null;}
                RenderServers();Notify("Доступ к серверу «"+old.Name+"» прекращён.");
            }
            if(voiceCommunity is{} activeVoice)
            {
                await cloud.PresenceAsync(activeVoice.Id,profile,rtc?.Joined==true?voiceChannelId:null);
                if(activeVoice.Id!=id){var activeSnapshot=await cloud.GetAsync<CommunitySnapshot>($"communities/{activeVoice.Id}/snapshot");if(activeSnapshot is not null){voiceCommunity=activeSnapshot.Community;store.SaveSnapshot(activeSnapshot);foreach(var member in activeSnapshot.Members)await avatars.FetchAsync(cloud,member);await LeaveRemovedVoiceChannel(activeSnapshot);}}
            }
            if(id is null)return;
            if(id!=voiceCommunity?.Id)await cloud.PresenceAsync(id,profile,null);
            var snapshot = await cloud.GetAsync<CommunitySnapshot>($"communities/{id}/snapshot");
            if(snapshot is not null)
            {
                store.SaveSnapshot(snapshot);
                if(community?.Id==id)
                {
                    bool changed=community!=snapshot.Community;community=snapshot.Community;serverTitle.Text=community.Name;
                    if(selectedChannel is{} selected&&!snapshot.Channels.Any(c=>c.Id==selected.Id))await CancelRecording();
                    RefreshSelectedChannel();channelId=selectedChannel?.Id;
                    await RefreshCommunityIcon(community);if(changed)RenderServers();
                }
                if(voiceCommunity?.Id==id)voiceCommunity=snapshot.Community;
                await LeaveRemovedVoiceChannel(snapshot);
            }
            var online = await cloud.GetAsync<Dictionary<string, ServerPresence>>($"communities/{id}/presence") ?? new();
            var roomMetadata = await cloud.GetAsync<Dictionary<string, JsonElement>>($"communities/{id}/rtcRooms") ?? new();
            var now = cloud.ServerNow().ToUnixTimeMilliseconds();
            if (community?.Id != id || closing) return;
            cloudVoiceRooms = roomMetadata; voiceRoomsCommunityId = id;
            cloudPresence = online.Values.Where(p => now - p.SeenAt < 45000 && now >= p.SeenAt - 10000).Select(p => new PeerPresence(p.Profile, p.ChannelId, false, false)).ToList();
            foreach (var user in snapshot?.Members ?? cloudPresence.Select(p=>p.Profile).ToList())
            {
                await avatars.FetchAsync(cloud,user);
                var banner=await avatars.FetchBannerAsync(cloud,user);
                if(user.Id==profile.Id&&!File.Exists(profile.BannerPath))profile=profile with{BannerPath=banner};
                store.UpsertMember(id,user.Id==profile.Id?profile:user with{BannerPath=banner});
            }
            var refreshed=store.GetMembers(id).ToDictionary(p=>p.Id);
            cloudPresence=cloudPresence.Select(p=>p with{Profile=refreshed.GetValueOrDefault(p.Profile.Id,p.Profile)}).ToList();
            if (community?.Id != id || closing) return;
            RenderMembers(); RenderChannels(); UpdateStatus();
            if (channelId is not null)
            {
                var history = await cloud.MessagesAsync(id, channelId);soundEvents.ObserveMessages(id+"/"+channelId,history);
                foreach (var message in history.OrderBy(m => m.SentAt)) {
                    if (message.AttachmentId is not null && Regex.IsMatch(message.AttachmentId, "^[a-f0-9]{64}$") && store.GetAsset(message.AttachmentId) is null && !unavailableFiles.Contains(message.AttachmentId))
                    { if (await CloudFiles.DownloadAsync(cloud, store, id, message.AuthorId, message.AttachmentId) is null) unavailableFiles.Add(message.AttachmentId); }
                    await FetchEmojis(message, id); store.SaveMessage(message); if (selectedChannel?.Id == channelId) AddMessage(message); }
            }
        }
        finally { cloudPolling = false; }
    }
    private Task FetchEmojis(ChatMessage message,string communityId)=>FetchEmojiAssets(message.Emojis?.Values??Enumerable.Empty<string>(),communityId);
    private async Task FetchEmojiAssets(IEnumerable<string> assetIds,string communityId)
    {
        if(cloud is null)return;
        foreach (var id in assetIds.Distinct().Take(100))
        {
            if (string.IsNullOrWhiteSpace(id) || !Regex.IsMatch(id, "^[a-f0-9]{64}$") || store.GetAsset(id) is not null) continue;
            var asset = await cloud.GetAsync<AssetTransfer>($"communities/{communityId}/emojis/{id}");
            if (asset is null || asset.Data is null || asset.Kind != "emoji" || asset.Data.Length > 256 * 1024 || asset.Name != Path.GetFileName(asset.Name) || asset.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || asset.Name is "." or ".." || asset.Name.Length > 200) continue;
            using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            hash.AppendData(Encoding.UTF8.GetBytes(communityId + "\0emoji\0")); hash.AppendData(asset.Data);
            if (Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() != id) continue;
            var directory = Path.Combine(store.RootDirectory, "incoming", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory); var path = Path.Combine(directory, "emoji.payload");
            try { await File.WriteAllBytesAsync(path, asset.Data); AvatarCatalog.Validate(path); store.ImportAsset(communityId, path, "emoji", asset.Name); }
            finally { Directory.Delete(directory, true); }
        }
    }
    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (closing) return;
        if (!exitRequested) { e.Cancel = true; foreach (var v in viewers.Values) v.Hide(); Hide(); return; }
        e.Cancel = true; closing = true; CancelReconnect();activityTimer.Stop(); cloudTimer.Stop(); receiveLifetime.Cancel(); incoming.Writer.TryComplete(); latestFrames.Clear();
        try { if (recorder.IsRecording) { var path = await recorder.StopAsync(); File.Delete(path); } if (recordedPath is not null) File.Delete(recordedPath); recorder.Dispose(); if(cloud is not null)foreach(var id in new[]{community?.Id,voiceCommunity?.Id}.OfType<string>().Distinct()){try{await cloud.DeleteAsync($"communities/{id}/presence/{profile.Id}");}catch{}} recordingWaveform.Dispose();soundboardPopup?.Close();StopVoice(); if (rtc is not null) await rtc.DisposeAsync(); tray.Visible = false; trayIndicator?.Dispose();tray.Dispose();VoiceMessagePlayer.StopAll(); voice.Dispose(); screen.Dispose(); sounds.Dispose(); shareAudio.Dispose(); sharePlayback.Dispose(); }
        catch { } finally { _ = Dispatcher.BeginInvoke(new Action(Close)); }
    }
}
