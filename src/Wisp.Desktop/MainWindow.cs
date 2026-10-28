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
    private readonly Button notificationButton, cancelRecordButton;
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
    private sealed record ScreenCommand(string TargetId, string SessionId, bool Paused = false);
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
    private readonly Button micButton, deafenButton, recordButton, shareButton, leaveButton;
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
    private IReadOnlyList<PeerPresence> presence = Array.Empty<PeerPresence>();

    public MainWindow(LocalStore store, UserProfile profile, ServerBackend? cloud = null)
    {
        if (profile.AvatarRevision is null && profile.AvatarPath is not null && File.Exists(profile.AvatarPath)) profile = profile with { AvatarRevision = AvatarCatalog.Revision(profile.AvatarPath) };
        this.store = store; avatars = new AvatarCatalog(store.RootDirectory, profile.Id); this.profile = profile; this.cloud = cloud; savedRefreshToken = cloud?.RefreshToken;
        rtc = cloud is null ? null : new SfuRoom(cloud,cloud.Config.VoiceSettings());
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
        callColumn=new ColumnDefinition{Width=new GridLength(0)};root.ColumnDefinitions.Add(callColumn);Content = root;

        var rail = new DockPanel { Margin = Ui.Insets(Ui.S, Ui.M) };
        var home = Brand.Image(Ui.Metric("Size.NavigationIcon")); home.ToolTip = "Wisp"; home.HorizontalAlignment = HorizontalAlignment.Center;
        home.Width = home.Height = Ui.Metric("Size.NavigationIcon"); home.Margin = Ui.Insets(0, 0, 0, Ui.M);
        var homeButton=Btn("Главная Wisp",ShowHome,"Главная Wisp");homeButton.Content=home;Ui.Style(homeButton,"Button.Ghost");DockPanel.SetDock(homeButton,Dock.Top);rail.Children.Add(homeButton);
        var railActions = new StackPanel(); DockPanel.SetDock(railActions, Dock.Bottom); rail.Children.Add(railActions);
        railActions.Children.Add(Tool("plus", "Создать сообщество", CreateCommunity));
        railActions.Children.Add(Tool("arrow-up-right", "Присоединиться по приглашению", JoinCommunity));
        var themeToggle = new ToggleIconButton { IconOn = "sun", IconOff = "moon", ToolTipOn = "Включить тёмную тему", ToolTipOff = "Включить светлую тему", IsChecked = ThemeManager.IsLight };
        themeToggle.Click += (_, _) => { ToggleTheme(); themeToggle.IsChecked = ThemeManager.IsLight; };
        railActions.Children.Add(themeToggle); rail.Children.Add(servers); root.Children.Add(Ui.Panel(rail, "RailSurface"));

        var side = new DockPanel();
        var heading = new DockPanel { Height = Ui.Metric("Size.SectionHeader"), Margin = Ui.Insets(Ui.M, 0) };
        var channelActions = new StackPanel { Orientation = Orientation.Horizontal };
        channelActions.Children.Add(Tool("users", "Пригласить в сообщество", HostCommunity));
        channelActions.Children.Add(Tool("plus", "Создать канал", CreateChannel));
        DockPanel.SetDock(channelActions, Dock.Right); heading.Children.Add(channelActions);
        serverTitle.TextTrimming = TextTrimming.CharacterEllipsis; serverTitle.VerticalAlignment = VerticalAlignment.Center;
        heading.Children.Add(serverTitle); DockPanel.SetDock(heading, Dock.Top); side.Children.Add(heading);
        var self = new StackPanel { Margin = Ui.Insets(Ui.M) };
        Ui.Secondary(connection); connection.Margin = Ui.Insets(0);
        Ui.Style(connectionButton, "Button.Ghost"); connectionButton.Padding = Ui.Insets(Ui.Xs); connectionButton.Margin = Ui.Insets(0, 0, 0, Ui.Xs);
        connectionButton.HorizontalContentAlignment = HorizontalAlignment.Left; connectionButton.Content = connection;
        Ui.Name(connectionButton, "Информация о подключении"); connectionButton.ToolTip = "Подключение, сервер и пинг";
        connectionButton.Click += (_, _) => ConnectionDetails();
        messages.SetResourceReference(ItemsControl.ItemContainerStyleProperty, "ListItem.Chat");
        servers.SetResourceReference(ItemsControl.ItemContainerStyleProperty, "ListItem.Chat");
        self.Children.Add(connectionButton);var identity=new DockPanel();identityAvatar=Btn("Мой профиль",()=>ViewProfile(this.profile));identityAvatar.Padding=Ui.Insets(0);identityAvatar.Margin=Ui.Insets(0,0,Ui.S,0);identityAvatar.Width=identityAvatar.Height=36;Ui.Style(identityAvatar,"Button.Ghost");DockPanel.SetDock(identityAvatar,Dock.Left);identity.Children.Add(identityAvatar);userName.VerticalAlignment=VerticalAlignment.Center;identity.Children.Add(userName);self.Children.Add(identity);
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        micButton = VoiceToggle("mic", "mic-off", "Выключить микрофон", "Включить микрофон", ToggleMic);
        deafenButton = VoiceToggle("headphones", "headphone-off", "Выключить наушники и микрофон", "Включить наушники", ToggleDeafen);
        controls.Children.Add(micButton); controls.Children.Add(deafenButton);
        controls.Children.Add(Tool("settings", "Настройки Wisp", OpenSettings)); self.Children.Add(controls);
        leaveButton = Btn("Выйти из канала", LeaveVoice); Ui.Style(leaveButton, "Button.Danger"); leaveButton.Margin = Ui.Insets(0, Ui.S, 0, 0); self.Children.Add(leaveButton);
        var selfPanel = Ui.Panel(self, "SurfaceElevated", false); DockPanel.SetDock(selfPanel, Dock.Bottom); side.Children.Add(selfPanel);
        side.Children.Add(channels); var sidebar = Ui.Panel(side,"ChannelSurface"); Grid.SetColumn(sidebar, 1); root.Children.Add(sidebar);

        var center=new DockPanel();chatWorkspace=center;center.SetResourceReference(Panel.BackgroundProperty,"ChatSurface");
        var workspace=new Grid();workspace.Children.Add(center);homeWorkspace=new ScrollViewer{VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Visibility=Visibility.Collapsed};workspace.Children.Add(homeWorkspace);Grid.SetColumn(workspace,2);root.Children.Add(workspace);
        var top = new DockPanel { Height = Ui.Metric("Size.SectionHeader"), Margin = Ui.Insets(Ui.L, 0) };
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        toolbar.Children.Add(Tool("volume-2", "Звуковая панель", SoundPanel));
        shareButton = VoiceToggle("screen-share", "screen-share-off", "Начать демонстрацию", "Остановить демонстрацию", ShareScreen);
        ((ToggleIconButton)shareButton).SetResourceReference(ToggleIconButton.CheckedBrushProperty, "Accent"); toolbar.Children.Add(shareButton);
        toolbar.Children.Add(Tool("monitor", "Смотреть демонстрации", WatchScreens));
        toolbar.Children.Add(Tool("users","Текущий звонок",OpenCallPanel));
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
        var input = new StackPanel { Margin = Ui.Insets(Ui.L, Ui.S, Ui.L, Ui.L) }; input.Children.Add(attachmentPreview);
        var compose = new Grid(); compose.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); compose.ColumnDefinitions.Add(new ColumnDefinition()); compose.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var inputTools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
        inputTools.Children.Add(Tool("paperclip", "Прикрепить файл", AttachFile)); inputTools.Children.Add(Tool("face-slightly-smiling", "Добавить эмодзи", EmojiPanel)); compose.Children.Add(inputTools);
        composer.Margin = Ui.Insets(Ui.S, 0); composer.Padding = Ui.Insets(Ui.M, Ui.S); Ui.Name(composer, "Сообщение · Enter — отправить, Shift+Enter — новая строка"); Grid.SetColumn(composer, 1); compose.Children.Add(composer);
        sendButton = Tool("send", "Отправить сообщение · Enter", SendMessage); Ui.Style(sendButton, "Button.Primary"); sendButton.Width = Ui.ControlHeight; sendButton.Padding = Ui.Insets(0); sendButton.VerticalAlignment = VerticalAlignment.Bottom; Grid.SetColumn(sendButton, 2); compose.Children.Add(sendButton); input.Children.Add(compose);
        var attach = new DockPanel { Margin = Ui.Insets(0, Ui.S, 0, 0) };
        recordButton = VoiceToggle("mic", "square", "Записать голосовое", "Завершить запись", RecordVoice); DockPanel.SetDock(recordButton, Dock.Left); attach.Children.Add(recordButton);
        cancelRecordButton = Tool("x", "Отменить запись", CancelRecording); cancelRecordButton.Visibility = Visibility.Collapsed; DockPanel.SetDock(cancelRecordButton, Dock.Left); attach.Children.Add(cancelRecordButton);
        attach.Children.Add(Ui.Label("Enter — отправить · Shift+Enter — новая строка", Ui.Caption, true)); input.Children.Add(attach);
        DockPanel.SetDock(input, Dock.Bottom); center.Children.Add(input); messages.Margin = Ui.Insets(Ui.L, 0); center.Children.Add(messages);

        var right = new DockPanel();var search=new TextBox{Margin=Ui.Insets(Ui.S),ToolTip="Поиск участников"};Ui.Name(search,"Поиск участников");search.TextChanged+=(_,_)=>{memberSearch=search.Text;memberRenderKey="";RenderMembers();};DockPanel.SetDock(search,Dock.Top);right.Children.Add(search);var memberHeader = Ui.Label("УЧАСТНИКИ", Ui.Caption, true); memberHeader.FontWeight = FontWeights.SemiBold;
        memberHeader.Height = Ui.Metric("Size.SectionHeader"); memberHeader.Margin = Ui.Insets(Ui.L, 0); DockPanel.SetDock(memberHeader, Dock.Top); right.Children.Add(memberHeader); right.Children.Add(members);
        memberPanel = Ui.Panel(right,"AsideSurface"); Grid.SetColumn(memberPanel, 3); root.Children.Add(memberPanel);
        callPanel=BuildCallPanel();Grid.SetColumn(callPanel,4);root.Children.Add(callPanel);
        SizeChanged += (_, _) => UpdateResponsivePanels();
        composer.PreviewKeyDown += (_, e) => { if (ComposerInput.ShouldSend(e.Key, Keyboard.Modifiers)) { e.Handled = true; Run(SendMessage); } };
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
                var active = rtc.ConnectedPeerIds; voice.SynchronizePeers(active);
                if(rtc.Joined&&!voiceIntent.Connecting)soundEvents.ObserveVoiceRoster(VoiceRoomKey,rtc.Participants);
                var paths = string.Join(", ", rtc.ConnectionPaths.Values.OrderBy(path => path));
                if (paths.Length > 0 && paths != lastConnectionPaths) Notify("Путь соединения: " + paths);
                lastConnectionPaths = paths;
                foreach (var id in remoteStreams.Keys.Where(id => !active.Contains(id)).ToList()) StopVideoPeer(id);
                var peerKey = string.Join("|", active.OrderBy(id => id));
                if (sharing && peerKey != sharedPeers && screen.CurrentInfo is { } info) { sharedPeers = peerKey; Run(() => SendPacket(new WireMessage("screen-start", profile.Id, voiceChannelId, JsonSerializer.SerializeToUtf8Bytes(info)))); }
                foreach (var id in remoteScreens.Keys.Where(id => !active.Contains(id)).ToList()) { remoteScreens.Remove(id); dismissedScreens.Remove(id); }
                foreach (var id in viewers.Keys.Where(id => !active.Contains(id)).ToList()) if (viewers.Remove(id, out var gone)) gone.Close();
                RenderChannels();RenderCallPanel(); UpdateStatus();
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
        screen.Status += text => Dispatcher.BeginInvoke(() => { if (!closing && sharing) Notify(text); });
        screen.PauseChanged += paused => { if (screen.CurrentInfo is { } info) SendRealtime("screen-state", JsonSerializer.SerializeToUtf8Bytes(new ScreenCommand(profile.Id, info.SessionId, paused))); };
        shareAudio.PacketReady += p => { if (!deafened) SendRealtime("screen-audio", p); };
        shareAudio.Error += e => Dispatcher.BeginInvoke(() => Notify(e, true));
        sharePlayback.Error += e => Dispatcher.BeginInvoke(() => Notify(e, true)); screen.Error += e => Dispatcher.BeginInvoke(() => { StopSharing(); Notify(e, true); });
        recorder.Completed += path => Dispatcher.BeginInvoke(() => { if (closing || sendingRecording || !File.Exists(path)) return; Run(() => { recordedPath = path; StageRecording(path); Notify("Запись завершена. Можно прослушать и отправить."); return Task.CompletedTask; }); });
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
        var first = MyCommunities().FirstOrDefault(); if (first is not null) SetCommunity(first); else ShowHome();
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
    private void UpdateRecordingControls() => cancelRecordButton.Visibility = recorder.IsRecording || recordedPath is not null || (pendingAttachment is not null && store.GetAsset(pendingAttachment)?.Kind == "voice") ? Visibility.Visible : Visibility.Collapsed;
    private List<Community> MyCommunities() => cloud is null ? store.GetCommunities() : store.GetCommunities().Where(c => store.GetMembers(c.Id).Any(m => m.Id == profile.Id)).ToList();
    private void RenderServers()
    {
        servers.Clear();
        foreach (var c in MyCommunities()) servers.Add(c.Id, () =>
        {
            var button = Btn(c.Name, () => SetCommunity(c), c.Name);
            button.Content = Ui.Label(c.Name[..Math.Min(2, c.Name.Length)].ToUpperInvariant(), Ui.Subtitle);
            button.BorderThickness=new Thickness(community?.Id==c.Id?2:0);button.SetResourceReference(Control.BorderBrushProperty,"Accent");
            button.Width = button.Height = Ui.Metric("Size.RailItem"); button.Margin = Ui.Insets(0, 0, 0, Ui.S); Ui.Style(button, "Button.Ghost");
            if (community?.Id == c.Id) button.SetResourceReference(Control.BackgroundProperty, "Selected");
            return button;
        });
    }
    private void SetCommunity(Community c) { EnsureNoRecording();if(community?.Id!=c.Id){cloudPresence=Array.Empty<PeerPresence>();presence=Array.Empty<PeerPresence>();cloudVoiceRooms=new();voiceRoomsCommunityId=null;memberRenderKey="";channelRenderKey="";} community = c;chatWorkspace.Visibility=Visibility.Visible;homeWorkspace.Visibility=Visibility.Collapsed;serverTitle.Text = c.Name; RenderServers(); RenderChannels(); var first = store.GetChannels(c.Id).FirstOrDefault(); if (first is not null) SelectChannel(first); RenderMembers(); UpdateStatus(); }
    private void EnsureNoRecording() { if (recorder.IsRecording || recordedPath is not null) throw new InvalidOperationException("Сначала отправь голосовую запись."); }
    private void RenderChannels()
    {
        var visiblePresence = DisplayPresence;
        var nextKey = (community?.Id ?? "") + ":" + selectedChannel?.Id + ":" + voiceChannelId + ":" + voiceIntent.Connecting + ":" +
            string.Join("|", community is null ? Array.Empty<string>() : store.GetChannels(community.Id).Select(c => $"{c.Id}:{c.Name}:{c.IsVoice}")) + ":" +
            string.Join("|", visiblePresence.OrderBy(p => p.Profile.Id).Select(p => $"{p.Profile.Id}:{p.Profile.Username}:{p.Profile.AvatarRevision}:{avatars.Resolve(p.Profile, store)}:{p.VoiceChannelId}:{p.Muted}:{p.Deafened}"));
        if (nextKey == channelRenderKey) return;
        channelRenderKey = nextKey; channels.Clear(); if (community is null) return;
        foreach (bool voiceKind in new[] { false, true })
        {
            channels.Add(voiceKind ? "voice-heading" : "text-heading", () => { var heading = Ui.Label(voiceKind ? "ГОЛОСОВЫЕ КАНАЛЫ" : "ТЕКСТОВЫЕ КАНАЛЫ"); Ui.Style(heading, "Text.Section"); return heading; });
            foreach (var c in store.GetChannels(community.Id).Where(x => x.IsVoice == voiceKind))
            {
                int count = DisplayPresence.Count(p => p.VoiceChannelId == c.Id);
                channels.Add(c.Id, () =>
                {
                    var button = Btn(c.Name, async () => { if (c.IsVoice) await JoinVoice(c); else SelectChannel(c); });
                    Ui.Style(button, "Button.Row");
                    var row = new DockPanel();
                    if (voiceKind)
                    {
                        var chat = Tool("message-square", "Открыть текстовый чат голосового канала", () => SelectChannel(c)); DockPanel.SetDock(chat, Dock.Right); row.Children.Add(chat);
                    }
                    var label = new Grid(); label.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); label.ColumnDefinitions.Add(new ColumnDefinition()); label.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    label.Children.Add(new Icon { Kind = voiceKind ? "volume-2" : "hash", Margin = Ui.Insets(0, 0, Ui.S, 0), VerticalAlignment = VerticalAlignment.Center });
                    var name = Ui.Label(c.Name); Grid.SetColumn(name, 1); label.Children.Add(name);
                    if (voiceKind) { var capacity = Ui.Label($"{count}", Ui.Caption, true); capacity.Margin = Ui.Insets(Ui.S, 0, 0, 0); Grid.SetColumn(capacity, 2); label.Children.Add(capacity); }
                    button.Content = label;
                    if (!voiceKind && selectedChannel?.Id == c.Id) button.SetResourceReference(Control.BackgroundProperty, "Selected");
                    row.Children.Add(button); return row;
                });
                if (voiceKind) foreach (var p in visiblePresence.Where(p => p.VoiceChannelId == c.Id))
                    channels.Add(c.Id + ":" + p.Profile.Id, () => new VoiceParticipantRow(p, avatars.Resolve(p.Profile, store), voice, p.Profile.Id == profile.Id, () => ViewProfile(p.Profile), () => Run(() => { voice.SavePeerVolumes(); return Task.CompletedTask; })));
            }
        }
    }
    private void ClearMessages() { messages.Clear(); renderedMessages.Clear(); lastRenderedMessage = null; }
    private void LoadHistory(IEnumerable<ChatMessage> history)
    {
        ClearMessages(); loadingHistory = true;
        try { foreach (var message in history) AddMessage(message); } finally { loadingHistory = false; }
    }
    private void SelectChannel(Channel c)
    {
        if (recorder.IsRecording || recordedPath is not null) throw new InvalidOperationException("Сначала отправь голосовую запись.");
        selectedChannel = c; channelTitle.Text = c.Name; channelTitle.ToolTip = c.Name;
        pendingAttachment = null; draftEmojis.Clear(); UpdateAttachmentPreview(); LoadHistory(store.GetMessages(c.Id)); RenderChannels();
    }
    private void RenderMembers()
    {
        if (community is null) { members.Clear(); return; }
        var saved = store.GetMembers(community.Id).ToDictionary(x => x.Id); saved[profile.Id] = profile;
        foreach (var p in DisplayPresence) saved[p.Profile.Id] = p.Profile;
        saved[profile.Id] = profile;
        var ordered = saved.Values.Where(p=>p.Username.Contains(memberSearch,StringComparison.OrdinalIgnoreCase)||p.Game?.Contains(memberSearch,StringComparison.OrdinalIgnoreCase)==true).OrderByDescending(x => x.Id == profile.Id || DisplayPresence.Any(p => p.Profile.Id == x.Id)).ThenBy(x => x.Username).ToList();
        var key = string.Join("|", ordered.Select(p => $"{p.Id}:{p.Username}:{p.Game}:{p.About}:{p.BannerAssetId}:{p.BannerPath}:{p.AvatarRevision}:{avatars.Resolve(p,store)}:{DisplayPresence.Any(s => s.Profile.Id == p.Id)}"));
        if (key == memberRenderKey) return;
        var oldKeys = members.Keys.ToHashSet(); memberRenderKey = key; members.Clear();
        foreach (var p in ordered)
        {
            bool online = p.Id == profile.Id || DisplayPresence.Any(s => s.Profile.Id == p.Id);
            string? game = p.Id == profile.Id ? profile.Game : DisplayPresence.FirstOrDefault(s => s.Profile.Id == p.Id)?.Profile.Game;
            members.Add(p.Id, () =>
            {
                var row = new DockPanel();
                var picture = AvatarCatalog.Circle(avatars.Resolve(p, store), Ui.Metric("Size.Avatar"), GifPlaybackMode.Hover); picture.Margin = Ui.Insets(0, 0, Ui.S, 0); DockPanel.SetDock(picture, Dock.Left); row.Children.Add(picture);
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
        leaveButton.Visibility = inVoice || cloudChannelId is not null ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in new[] { micButton, deafenButton, shareButton }) button.Visibility = inVoice ? Visibility.Visible : Visibility.Collapsed;
        connection.Text = voiceIntent.Connecting ? "Соединение" : voiceChannelId is not null ? "В канале: " + (community is null ? "" : store.GetChannels((voiceCommunity??community).Id).FirstOrDefault(c => c.Id == voiceChannelId)?.Name) + (rtc is null ? "" : $" · {rtc.TransportName}: {rtc.ConnectedCount} соединений") : cloudChannelId is not null ? "В канале · голос ещё не подключён" : cloud is not null ? "Сервер подключён" : "Локальный режим";
        UpdateConnectionStatus();
        userName.Text = profile.Username;var avatarPath=avatars.Resolve(profile,store);var avatarKey=profile.AvatarRevision+":"+avatarPath;if(identityAvatar is not null&&avatarKey!=identityAvatarKey){identityAvatarKey=avatarKey;identityAvatar.Content=AvatarCatalog.Circle(avatarPath,32,GifPlaybackMode.Hover);}
        ((ToggleIconButton)micButton).IsChecked = muted || deafened;
        ((ToggleIconButton)deafenButton).IsChecked = deafened;
        ((ToggleIconButton)shareButton).IsChecked = sharing;
        shareButton.IsEnabled = inVoice && !voiceIntent.Connecting;
        UpdateTray();RenderCallPanel();UpdateResponsivePanels();
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
        var d = new FormDialog(this, "Создать сообщество"); var name = d.Field("Название"); d.Accept("Создать"); if (d.ShowDialog() != true) return;
        SetCommunity(store.CreateCommunity(name.Text, profile.Id));
        if (cloud is not null) Run(async () => { await cloud.CreateCommunityAsync(new CommunitySnapshot(community!, store.GetChannels(community!.Id), new List<UserProfile> { profile })); await RefreshCloud(); });

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
    private void CreateChannel()
    {
        if (community is null) return; if (community.OwnerId != profile.Id) throw new InvalidOperationException("Создавать каналы может владелец сообщества.");
        var d = new FormDialog(this, "Новый канал"); var name = d.Field("Название"); var isVoice = d.Check("Голосовой канал"); d.Accept("Создать"); if (d.ShowDialog() == true) { var c = store.CreateChannel(community.Id, name.Text, isVoice.IsChecked == true); SelectChannel(c); if (cloud is not null) Run(() => cloud.CreateCommunityAsync(new CommunitySnapshot(community, store.GetChannels(community.Id), store.GetMembers(community.Id))));  }
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
    private void StopVoice() { voiceIntent.Leave(); StopSharing(); voice.Stop(); sharePlayback.Stop(); foreach (var id in remoteStreams.Keys.ToList()) StopVideoPeer(id); videoFrames.Clear(); voiceChannelId = null; foreach (var v in viewers.Values.ToList()) v.Close(); viewers.Clear(); remoteScreens.Clear(); dismissedScreens.Clear(); UpdateStatus(); }
    private async Task Disconnect()
    {
        await LeaveVoice();cloudPresence=Array.Empty<PeerPresence>();Interlocked.Increment(ref sessionEpoch);latestFrames.Clear();presence=Array.Empty<PeerPresence>();RenderMembers();RenderChannels();UpdateStatus();
    }
    private async Task ToggleMic() { muted = !muted; voice.Muted = muted; RenderChannels(); UpdateStatus(); if (rtc is not null) await rtc.SetMutedAsync(muted, deafened); soundEvents.ObserveLocalAudio(muted,deafened); UpdateStatus(); }
    private async Task ToggleDeafen() { deafened = !deafened; voice.Deafened = deafened; sharePlayback.Deafened = deafened; RenderChannels(); UpdateStatus(); if (rtc is not null) await rtc.SetMutedAsync(muted, deafened); if (deafened) { sounds.StopNotifications();sounds.StopFile(); VoiceMessagePlayer.PauseActive(); } soundEvents.ObserveLocalAudio(muted,deafened); UpdateStatus(); }
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
                await BroadcastAsset(asset);
            }
            if (attachment is not null && store.GetAsset(attachment) is { } attached)
            {
                if (cloud is not null && !publishedFiles.Contains(attached.Id)) { await CloudFiles.UploadAsync(cloud, attached); publishedFiles.Add(attached.Id); }
                await BroadcastAsset(attached);
            }
            var message = new ChatMessage(Guid.NewGuid().ToString("N"), channel.Id, profile.Id, profile.Username, text.Trim(), DateTimeOffset.UtcNow, attachment, used.Count == 0 ? null : used);
            if (cloud is not null && communityId is not null) await cloud.SendMessageAsync(communityId, message);
            store.SaveMessage(message); if (selectedChannel?.Id == channel.Id) AddMessage(message);
            if (composer.Text == text) composer.Clear();
            if (pendingAttachment == attachment) pendingAttachment = null;
            draftEmojis.Clear(); UpdateAttachmentPreview();  Buttons.Flash(sendButton, "check");
        }
        finally { sendingMessage = false; sendButton.ToolTip = "Отправить сообщение · Enter"; sendButton.IsEnabled = true; if (Keyboard.FocusedElement == sendButton) composer.Focus(); }
    }
    private static string EmojiToken(MediaAsset a)
    {
        var name = new string(Path.GetFileNameWithoutExtension(a.Name).Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_').Take(32).ToArray());
        return ":" + (name.Length == 0 ? "emoji" : name) + ":";
    }
    private SelectableChatText MessageText(ChatMessage message, FrameworkElement scope)
    {
        var text = new SelectableChatText();
        int offset = 0;
        foreach (Match match in Regex.Matches(message.Text, @":[\p{L}\p{N}_-]{1,40}:"))
        {
            if (match.Index > offset) text.Paragraph.Inlines.Add(new Run(message.Text[offset..match.Index]));
            var asset = message.Emojis?.TryGetValue(match.Value, out var id) == true ? store.GetAsset(id) : null;
            if (asset?.Kind == "emoji" && asset.CommunityId == community?.Id)
            {
                try { var image = AvatarCatalog.Image(asset.LocalPath, Ui.Body, mode: GifPlaybackMode.Hover, hoverScope: scope); image.Stretch = Stretch.Uniform; text.Paragraph.Inlines.Add(new InlineUIContainer(image) { BaselineAlignment = BaselineAlignment.Center }); }
                catch { text.Paragraph.Inlines.Add(new Run(match.Value)); }
            }
            else text.Paragraph.Inlines.Add(new Run(match.Value));
            offset = match.Index + match.Length;
        }
        if (offset < message.Text.Length) text.Paragraph.Inlines.Add(new Run(message.Text[offset..]));
        return text;
    }
    private void UpdateAttachmentPreview()
    {
        attachmentPreview.Children.Clear(); UpdateRecordingControls(); if (pendingAttachment is null) return;
        var asset = store.GetAsset(pendingAttachment); if (asset is null) return;
        var row = new DockPanel { Margin = Ui.Insets(0, Ui.Xs, 0, Ui.S) };
        var remove = Tool("x", "Убрать вложение", () => { pendingAttachment = null; UpdateAttachmentPreview(); }); DockPanel.SetDock(remove, Dock.Right); row.Children.Add(remove);
        row.Children.Add(new TextBlock { Text = $"{asset.Name} · {asset.Size / 1024} КБ", VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis }); attachmentPreview.Children.Add(row);
        if (new[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp" }.Contains(Path.GetExtension(asset.Name).ToLowerInvariant()))
        { try { attachmentPreview.Children.Add(new Image { Source = LoadImage(asset.LocalPath, 320), Height = Ui.Metric("Height.MediaPreview"), MaxWidth = Ui.Metric("Width.MediaPreview"), Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left }); } catch { } }
        else if (asset.Kind == "voice" || new[] { ".wav", ".mp3" }.Contains(Path.GetExtension(asset.Name).ToLowerInvariant())) attachmentPreview.Children.Add(new VoiceMessagePlayer(asset.LocalPath, e => Notify(e, true), () => !deafened));
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
        var layout = new Grid { Margin = Ui.Insets(0, grouped ? Ui.Xs : Ui.L, Ui.S, 0), Background = Brushes.Transparent };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Ui.Metric("Size.Avatar") + Ui.M) });
        layout.ColumnDefinitions.Add(new ColumnDefinition());
        if (!grouped)
        {
            var author = m.AuthorId == profile.Id ? profile : community is null ? null : store.GetMembers(community.Id).FirstOrDefault(member => member.Id == m.AuthorId);
            var picture = AvatarCatalog.Circle(author is null ? null : avatars.Resolve(author, store), Ui.Metric("Size.Avatar"), GifPlaybackMode.Hover, layout);
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
                else if (new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp" }.Contains(Path.GetExtension(asset.Name).ToLowerInvariant()))
                {
                    try { row.Children.Add(MessageImage(asset.LocalPath, layout)); } catch { }
                }
                if (asset.Kind == "voice") row.Children.Add(new VoiceMessagePlayer(asset.LocalPath, e => Notify(e, true), () => !deafened));
                else if (asset.Kind != "emoji") row.Children.Add(Btn($"{asset.Name} · {asset.Size / 1024} КБ", () => OpenAsset(asset)));
            }
            else row.Children.Add(Ui.Label("Вложение недоступно на этом компьютере", Ui.Caption, true));
        }
        return layout;
    }
    private static Image MessageImage(string path, FrameworkElement scope)
    {
        var image = AvatarCatalog.Image(path, Ui.Metric("Width.Media"), mode: GifPlaybackMode.Hover, hoverScope: scope);
        image.Width = double.NaN; image.Height = double.NaN; image.Stretch = Stretch.Uniform;
        image.MaxHeight = Ui.Metric("Height.Media"); image.MaxWidth = Ui.Metric("Width.Media");
        image.HorizontalAlignment = HorizontalAlignment.Left; image.Margin = Ui.Insets(0, Ui.S, 0, 0); return image;
    }
    private void OpenAsset(MediaAsset asset)
    {
        if (asset.CommunityId != community?.Id) throw new InvalidOperationException("Вложение принадлежит другому сообществу.");
        if (asset.Kind == "voice" || asset.Kind == "sound") { if (deafened) return; sounds.PlayFile(asset.LocalPath); return; }
        var save = new Microsoft.Win32.SaveFileDialog { FileName = asset.Name, Title = "Сохранить вложение" }; if (save.ShowDialog(this) == true && Path.GetFullPath(save.FileName) != Path.GetFullPath(asset.LocalPath)) File.Copy(asset.LocalPath, save.FileName, true);
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
            if (voiceChannelId is not null) throw new InvalidOperationException("Для голосового сообщения сначала отключись от звонка.");
            var path = Path.Combine(store.RootDirectory, $"recording-{Guid.NewGuid():N}.wav"); recorder.Start(path); ((ToggleIconButton)recordButton).IsChecked = true; UpdateRecordingControls(); Notify("Запись микрофона · максимум 60 секунд. Нажми ещё раз, чтобы прослушать перед отправкой.");
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
                if (!remoteStreams.TryGetValue(m.SenderId, out var oldInfo) || oldInfo != streamInfo) { soundEvents.ObserveStreamStarted(VoiceRoomKey,m.SenderId,streamInfo.SessionId,streamInfo.StartedAtUnixMs<voiceJoinedAt);StopVideoPeer(m.SenderId); remoteStreams[m.SenderId] = streamInfo; OpenVideoPeer(m.SenderId);RenderCallPanel(); } break;
            case "video-report":
                if (sharing && voiceChannelId == m.ChannelId && m.Payload.Length <= 512 && JsonSerializer.Deserialize<VideoNetworkReport>(m.Payload) is { } report) screen.ObserveNetwork(m.SenderId, report);
                break;
            case "screen-keyframe":
                if (!sharing || voiceChannelId != m.ChannelId || m.Payload.Length > 512) return;
                var request = JsonSerializer.Deserialize<ScreenCommand>(m.Payload);
                if (request?.TargetId == profile.Id && request.SessionId == screen.CurrentInfo?.SessionId && screenKeyRequests.TryEnter()) screen.RequestKeyFrame();
                break;
            case "screen-state":
                if (voiceChannelId != m.ChannelId || m.Payload.Length > 512) return;
                var state = JsonSerializer.Deserialize<ScreenCommand>(m.Payload);
                if (state?.TargetId == m.SenderId && remoteStreams.TryGetValue(m.SenderId, out var sourceInfo) && state.SessionId == sourceInfo.SessionId && viewers.TryGetValue(m.SenderId, out var sourceView)) sourceView.SetPaused(state.Paused);
                break;
            case "screen-stop": StopVideoPeer(m.SenderId); remoteScreens.Remove(m.SenderId); dismissedScreens.Remove(m.SenderId);RenderCallPanel(); break;
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
                if (deafened || voiceChannelId != m.ChannelId) return; var sound = voiceCommunity is null ? null : await EnsureSoundAsync(voiceCommunity.Id,Encoding.UTF8.GetString(m.Payload)); if (sound?.Kind == "sound" && sound.CommunityId == voiceCommunity?.Id) sounds.PlayFile(sound.LocalPath); break;
        }
    }
    private void SendRealtime(string type, byte[] payload) { if (voiceChannelId is null || rtc?.Joined != true) return; _ = SendRealtimeSafe(type, voiceChannelId, payload); }
    private async Task SendRealtimeSafe(string type, string channel, byte[] data) { try { await SendPacket(new WireMessage(type, profile.Id, channel, data)); } catch { /* Disconnect/presence events surface connection failure. */ } }
    private async Task ShareScreen()
    {
        if(rtc?.Joined!=true||voiceIntent.Connecting)throw new InvalidOperationException("Для демонстрации сначала дождись подключения к голосовому каналу.");
        if (sharing) { StopSharing(); return; } if (voiceChannelId is null) throw new InvalidOperationException("Сначала подключись к голосовому каналу.");
        long shareRequest = Interlocked.Increment(ref shareGeneration);
        var d = new FormDialog(this, "Демонстрация экрана", 600);
        d.Note("Выбери окно приложения или монитор. 1080p/60 FPS требует больше ресурсов и скорости сети; при перегрузке кадры пропускаются, чтобы не накапливать задержку.");
        ComboBox Choice(string label, string[] values, int selected) { d.Note(label); var box = new ComboBox { ItemsSource = values, SelectedIndex = selected, Margin = Ui.Insets(0, Ui.Xs, 0, Ui.S) }; d.Add(box); return box; }
        var resolution = Choice("Разрешение", new[] { "720p", "1080p" }, 1);
        var framerate = Choice("Частота кадров", new[] { "15 FPS", "30 FPS", "60 FPS" }, 1);
        var bitrate = Choice("Битрейт потока · общий исходящий = значение × число зрителей", new[] { "2 Мбит/с", "4 Мбит/с", "6 Мбит/с", "8 Мбит/с" }, 1);
        var audio = d.Check("Передавать звук приложения / экрана (Windows 11)", ShareAudioCapture.Supported);
        d.Note("При выборе приложения передаётся его звук. Для монитора передаётся системный звук с исключением Wisp, чтобы разговор не возвращался эхом.");
        d.Note("Окно или монитор");
        var sources = ScreenShareService.GetSources().ToList();
        var sourcePicker = new ComboBox { ItemsSource = sources, DisplayMemberPath = "Name", SelectedIndex = sources.Count > 0 ? 0 : -1 };
        Ui.Name(sourcePicker, "Окно или монитор"); d.Add(sourcePicker);
        var borderless = d.Check("Захват без жёлтой рамки", true);
        d.Note("Windows может запросить разрешение убрать рамку. Если оно недоступно, используется совместимый захват без рамки: перекрытые окна некоторых приложений могут отображаться иначе, нагрузка может быть выше.");
        d.Accept("Начать демонстрацию");
        if (d.ShowDialog() != true || sourcePicker.SelectedItem is not CaptureSource selected) return;
        string? sharingChannel = voiceChannelId;
        bool withoutBorder = borderless.IsChecked == true;
        bool borderlessGranted = withoutBorder && await BorderlessCaptureAccess.RequestAsync();
        if (closing || sharingChannel is null || voiceChannelId != sharingChannel || shareRequest != Volatile.Read(ref shareGeneration)) return;
        var info = ScreenShareService.Describe(selected, resolution.SelectedIndex == 0 ? 720 : 1080, new[] { 15, 30, 60 }[framerate.SelectedIndex], new[] { 2000000, 4000000, 6000000, 8000000 }[bitrate.SelectedIndex]) with { SessionId = Guid.NewGuid().ToString("N"), StartedAtUnixMs=(cloud?.ServerNow()??DateTimeOffset.UtcNow).ToUnixTimeMilliseconds() };
        await SendPacket(new WireMessage("screen-start", profile.Id, voiceChannelId, JsonSerializer.SerializeToUtf8Bytes(info)));
        if (closing || voiceChannelId != sharingChannel || shareRequest != Volatile.Read(ref shareGeneration)) return;
        screen.Start(selected, info, withoutBorder, borderlessGranted);soundEvents.ObserveStreamStarted(VoiceRoomKey,profile.Id,info.SessionId); sharing = true; sharedPeers = "";
        notice.Text = $"Идёт демонстрация: {selected.Name} · {info.Width}×{info.Height} · {info.FramesPerSecond} FPS"; notice.Visibility = Visibility.Visible; ((ToggleIconButton)shareButton).IsChecked = true;
        if (audio.IsChecked == true) { try { await shareAudio.StartAsync(selected); } catch (Exception ex) { Notify(ex.Message, true); } }
        Notify($"Демонстрация начата: {selected.Name} · H.264 · {info.FramesPerSecond} FPS");UpdateStatus();
    }
    private void StopSharing()
    {
        Interlocked.Increment(ref shareGeneration);
        notice.Text = ""; notice.Visibility = Visibility.Collapsed; shareAudio.Stop(); screen.Stop(); sharedPeers = "";
        if (!sharing) return; sharing = false; ((ToggleIconButton)shareButton).IsChecked = false; SendRealtime("screen-stop", Array.Empty<byte>());  Notify("Демонстрация остановлена.");RenderCallPanel();
    }
    private ScreenViewer CreateViewer(string peerId)
    {
        if (viewers.TryGetValue(peerId, out var existing)) return existing;
        foreach (var other in viewers.Values.Where(v => v.IsFullscreen).ToList()) other.ToggleFullscreen();
        var name = VoicePresence.FirstOrDefault(x => x.Profile.Id == peerId)?.Profile.Username ?? "Участник";
        var viewer = new ScreenViewer(this, $"{name} · Демонстрация"); viewers[peerId] = viewer; sharePlayback.SetMuted(peerId, false);
        viewer.MuteChanged += value => sharePlayback.SetMuted(peerId, value);
        viewer.Closed += () => { viewers.Remove(peerId); dismissedScreens.Add(peerId); if (videoReceivers.TryRemove(peerId, out var receiver)) receiver.Dispose(); videoFrames.TryRemove(peerId, out _); sharePlayback.SetMuted(peerId, true); };
        return viewer;
    }
    private void OpenVideoPeer(string peerId)
    {
        if (dismissedScreens.Contains(peerId) || !remoteStreams.TryGetValue(peerId, out var info) || videoReceivers.ContainsKey(peerId)) return;
        CreateViewer(peerId);
        var receiver = new ScreenVideoReceiver(info, pixels =>
        {
            if (closing || !videoReceivers.TryGetValue(peerId, out var current) || current.Info != info) return;
            videoFrames[peerId] = (info, pixels);
            if (Interlocked.Exchange(ref videoScheduled, 1) != 0) return;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                Interlocked.Exchange(ref videoScheduled, 0);
                foreach (var id in videoFrames.Keys) if (videoFrames.TryRemove(id, out var frame) && viewers.TryGetValue(id, out var view)) view.Present(frame.Pixels, frame.Info.Width, frame.Info.Height);
            }));
        }, error => Dispatcher.BeginInvoke(() => { if (!closing) Notify(error, true); }),
            () => SendRealtime("screen-keyframe", JsonSerializer.SerializeToUtf8Bytes(new ScreenCommand(peerId, info.SessionId))));
        if (!videoReceivers.TryAdd(peerId, receiver)) receiver.Dispose();
    }
    private void StopVideoPeer(string peerId)
    {
        if (videoReceivers.TryRemove(peerId, out var receiver)) receiver.Dispose(); videoFrames.TryRemove(peerId, out _);
        if (viewers.Remove(peerId, out var viewer)) viewer.Close(); sharePlayback.Remove(peerId); remoteStreams.Remove(peerId); dismissedScreens.Remove(peerId);
    }
    private void ShowFrame(string peerId, byte[] jpeg)
    {
        if (jpeg.Length > 2 * 1024 * 1024) return; remoteScreens[peerId] = jpeg; if (dismissedScreens.Contains(peerId)) return;
        try { using var stream = new MemoryStream(jpeg); var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.DecodePixelWidth = 1920; image.StreamSource = stream; image.EndInit(); image.Freeze(); CreateViewer(peerId).Target.Source = image; } catch { }
    }
    private void WatchScreens()
    {
        var d = new FormDialog(this, "Демонстрации участников"); var ids = remoteStreams.Keys.Concat(remoteScreens.Keys).Distinct().ToList();
        if (ids.Count == 0) d.Note("Сейчас никто не демонстрирует экран.");
        foreach (var id in ids) d.Action(VoicePresence.FirstOrDefault(p => p.Profile.Id == id)?.Profile.Username ?? "Участник", () => { d.Close(); dismissedScreens.Remove(id); if (remoteStreams.ContainsKey(id)) { if (viewers.TryGetValue(id, out var viewer)) { foreach (var other in viewers.Values.Where(v => v != viewer && v.IsFullscreen).ToList()) other.ToggleFullscreen(); if (viewer.IsMini) viewer.ToggleMini(); viewer.Show(); } else OpenVideoPeer(id); } else ShowFrame(id, remoteScreens[id]); });
        d.Accept("Закрыть"); d.ShowDialog();
    }
    private async Task EmojiPanel()
    {
        if (community is null) return;
        if(cloud is not null){var emojiIndex=await cloud.GetAsync<Dictionary<string,AssetTransfer>>($"communities/{community.Id}/emojis")??new();await FetchEmojiAssets(emojiIndex.Keys,community.Id);}
        var d = new FormDialog(this, "Эмодзи сообщества"); d.Note("Выбери эмодзи — он добавится в сообщение на месте курсора.");
        var catalog = store.GetAssets(community.Id, "emoji");
        var list = new VirtualRows { Height = Math.Min(Ui.Metric("Height.SourceList"), Ui.RowHeight * catalog.Count) };
        foreach (var a in catalog) list.Add(a.Id, () =>
        {
            var button = Btn(EmojiToken(a), () =>
            {
                var token = EmojiToken(a);
                if (draftEmojis.TryGetValue(token, out var previous) && previous.Id != a.Id) token = token[..^1] + "_" + a.Id[..6] + ":";
                draftEmojis[token] = a; d.Close(); var caret = composer.CaretIndex; composer.Text = composer.Text.Insert(caret, token); composer.CaretIndex = caret + token.Length; composer.Focus();
            });
            Ui.Style(button, "Button.Row");
            var label = new DockPanel();
            try { var image = AvatarCatalog.Image(a.LocalPath, Ui.Metric("Size.InlineIcon")); image.Margin = Ui.Insets(0, 0, Ui.S, 0); label.Children.Add(image); } catch { }
            label.Children.Add(Ui.Label(EmojiToken(a))); button.Content = label; return button;
        });
        if (catalog.Count > 0) d.Add(list); else d.Note("В сообществе пока нет своих эмодзи.");
        d.FooterAction("Загрузить эмодзи", () =>
        {
            var f = new Microsoft.Win32.OpenFileDialog { Filter = "Изображения|*.png;*.jpg;*.jpeg;*.gif", Title = "Эмодзи · до 256 КБ" };
            if (f.ShowDialog(this) != true) return;
            if (new FileInfo(f.FileName).Length > 256 * 1024) throw new InvalidOperationException("Эмодзи должен быть меньше 256 КБ.");
            d.Close(); Run(async () => { await ImportAndSend(f.FileName, "emoji");  });
        });
        d.Accept("Закрыть"); d.ShowDialog();
    }
    private Task SendPacket(WireMessage message) => rtc?.Joined == true ? rtc.SendAsync(message) : Task.CompletedTask;
    private async Task BroadcastAsset(MediaAsset asset)
    {
        if(cloud is null)return;
        if(asset.Kind=="emoji")await CommunityAssets.UploadEmojiAsync(cloud,asset);
        else if(asset.Kind is "attachment" or "voice")await CloudFiles.UploadAsync(cloud,asset);
        else if(asset.Kind=="sound")await cloud.TryCreateAsync($"communities/{asset.CommunityId}/sounds/{asset.Id}",new AssetTransfer(asset.Id,asset.Name,asset.Kind,await File.ReadAllBytesAsync(asset.LocalPath)));
    }

    private async Task SoundPanel()
    {
        if(voiceCommunity is null&&community is null)return;string cid=(voiceCommunity??community)!.Id;
        var remote=cloud is null?new Dictionary<string,AssetTransfer>():await cloud.GetAsync<Dictionary<string,AssetTransfer>>($"communities/{cid}/sounds")??new();
        var catalog=store.GetAssets(cid,"sound").ToDictionary(a=>a.Id,a=>a.Name);
        foreach(var pair in remote)catalog[pair.Key]=pair.Value.Name;
        var dialog=new FormDialog(this,"Звуковая панель");dialog.Note("Звуки хранятся на сервере сообщества. WAV/MP3 до 2 МБ.");
        foreach(var pair in catalog)
        {
            string assetId=pair.Key;
            dialog.Action(pair.Value,()=>Run(async()=>{if(deafened)return;var asset=await EnsureSoundAsync(cid,assetId);if(asset is null)return;sounds.PlayFile(asset.LocalPath);if(voiceChannelId is not null)await SendPacket(new WireMessage("sound",profile.Id,voiceChannelId,Encoding.UTF8.GetBytes(asset.Id)));}));
        }
        if(catalog.Count==0)dialog.Note("В сообществе пока нет звуков.");
        dialog.FooterAction("Загрузить звук",()=>
        {
            var pick=new Microsoft.Win32.OpenFileDialog{Filter="Аудио|*.wav;*.mp3"};if(pick.ShowDialog(this)!=true)return;
            if(new FileInfo(pick.FileName).Length>2*1024*1024)throw new InvalidOperationException("Звук должен быть меньше 2 МБ.");
            dialog.Close();Run(async()=>{await ImportAndSend(pick.FileName,"sound");});
        });
        dialog.Accept("Закрыть");dialog.ShowDialog();
    }
    private async Task<MediaAsset?> EnsureSoundAsync(string cid,string id)
    {
        var existing=store.GetAsset(id);if(existing is not null)return existing;
        if(cloud is null||!Regex.IsMatch(id,"^[a-f0-9]{64}$"))return null;
        var asset=await cloud.GetAsync<AssetTransfer>($"communities/{cid}/sounds/{id}");
        if(asset?.Data is null||asset.Kind!="sound"||asset.Data.Length>2*1024*1024||asset.Name!=Path.GetFileName(asset.Name))return null;
        var directory=Path.Combine(store.RootDirectory,"incoming",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);var path=Path.Combine(directory,"sound.tmp");
        try{await File.WriteAllBytesAsync(path,asset.Data);var imported=store.ImportAsset(cid,path,"sound",asset.Name);return imported.Id==id?imported:null;}finally{Directory.Delete(directory,true);}
    }
    private void EditProfile()
    {
        var d = new FormDialog(this, "Мой профиль"); var name = d.Field("Имя пользователя", profile.Username); var about = d.Field("О себе", profile.About, true); var avatar = avatars.Resolve(profile, store); var banner = profile.BannerPath ?? (profile.BannerAssetId is null ? null : store.GetAsset(profile.BannerAssetId)?.LocalPath);
        void Pick(bool isAvatar)
        {
            var f = new Microsoft.Win32.OpenFileDialog { Filter = "Изображения|*.png;*.jpg;*.jpeg;*.gif" }; if (f.ShowDialog(this) != true) return;
            if (new FileInfo(f.FileName).Length > 4 * 1024 * 1024) throw new InvalidOperationException("Изображение профиля должно быть меньше 4 МБ.");
            AvatarCatalog.Validate(f.FileName); var directory = Path.Combine(store.RootDirectory, "profile-images"); Directory.CreateDirectory(directory);
            var destination = Path.Combine(directory, Guid.NewGuid().ToString("N") + Path.GetExtension(f.FileName)); File.Copy(f.FileName, destination);
            if (isAvatar) avatar = destination; else banner = destination;
        }
        d.Action("Выбрать аватар", () => Pick(true)); d.Action("Выбрать баннер", () => Pick(false)); d.Action("Удалить аватар и баннер", () => { avatar = null; banner = null; });
        d.Note("Аватар и баннер: до 4 МБ каждый. Изменения сохраняются на сервере."); d.Accept();
        if (d.ShowDialog() != true) return; var proposed = profile with { Username = name.Text.Trim(), About = about.Text.Trim(), AvatarPath = avatar, BannerPath = banner, AvatarAssetId = null, BannerAssetId = null, AvatarRevision = avatar is null ? null : AvatarCatalog.Revision(avatar) }; store.SaveProfile(proposed); profile = proposed; if (community is not null) store.UpsertMember(community.Id, profile); Run(async () => { await PublishProfile(); RenderMembers(); UpdateStatus();  });
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
        ModalHost.Open(this,p.Username,body,480);
    }
    private static BitmapImage LoadImage(string path, int width) { using var stream = File.OpenRead(path); var b = new BitmapImage(); b.BeginInit(); b.CacheOption = BitmapCacheOption.OnLoad; b.DecodePixelWidth = width; b.StreamSource = stream; b.EndInit(); b.Freeze(); return b; }
    private async Task RefreshCloud()
    {
        if (cloud is null || cloudPolling || closing) return;
        cloudPolling = true; var id = community?.Id; var channelId = selectedChannel?.Id;
        try
        {
            if(voiceCommunity is{} activeVoice)
            {
                await cloud.PresenceAsync(activeVoice.Id,profile,rtc?.Joined==true?voiceChannelId:null);
                if(activeVoice.Id!=id){var activeSnapshot=await cloud.GetAsync<CommunitySnapshot>($"communities/{activeVoice.Id}/snapshot");if(activeSnapshot is not null){store.SaveSnapshot(activeSnapshot);foreach(var member in activeSnapshot.Members)await avatars.FetchAsync(cloud,member);}}
            }
            if(id is null)return;
            if(id!=voiceCommunity?.Id)await cloud.PresenceAsync(id,profile,null);
            if (savedRefreshToken != cloud.RefreshToken) { SessionVault.Save(store.RootDirectory, cloud.RefreshToken!); savedRefreshToken = cloud.RefreshToken; }
            var snapshot = await cloud.GetAsync<CommunitySnapshot>($"communities/{id}/snapshot");
            if (snapshot is not null) store.SaveSnapshot(snapshot);
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
    private void ToggleTheme()
    {
        ThemeManager.Toggle();

    }
    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (closing) return;
        if (!exitRequested) { e.Cancel = true; foreach (var v in viewers.Values) v.Hide(); Hide(); return; }
        e.Cancel = true; closing = true; CancelReconnect();activityTimer.Stop(); cloudTimer.Stop(); receiveLifetime.Cancel(); incoming.Writer.TryComplete(); latestFrames.Clear();
        try { if (recorder.IsRecording) { var path = await recorder.StopAsync(); File.Delete(path); } if (recordedPath is not null) File.Delete(recordedPath); recorder.Dispose(); if(cloud is not null)foreach(var id in new[]{community?.Id,voiceCommunity?.Id}.OfType<string>().Distinct()){try{await cloud.DeleteAsync($"communities/{id}/presence/{profile.Id}");}catch{}} StopVoice(); if (rtc is not null) await rtc.DisposeAsync(); tray.Visible = false; trayIndicator?.Dispose();tray.Dispose();VoiceMessagePlayer.StopAll(); voice.Dispose(); screen.Dispose(); sounds.Dispose(); shareAudio.Dispose(); sharePlayback.Dispose(); }
        catch { } finally { _ = Dispatcher.BeginInvoke(new Action(Close)); }
    }
}
