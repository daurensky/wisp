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

public sealed class MainWindow : ThemedWindow
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
    private DateTimeOffset lastErrorCue = DateTimeOffset.MinValue;
    private readonly FirebaseBackend? cloud;
    private readonly FileIceServerProvider iceServers;
    private string lastConnectionPaths = "";
    private readonly DispatcherTimer cloudTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private IReadOnlyList<PeerPresence> cloudPresence = Array.Empty<PeerPresence>();
    private string? cloudChannelId;
    private bool cloudPolling;
    private string? savedRefreshToken;
    private readonly HashSet<string> renderedMessages = new();
    private IReadOnlyList<PeerPresence> DisplayPresence => presence.Concat(cloudPresence).GroupBy(p => p.Profile.Id).Select(g => g.First()).ToArray();
    private UserProfile profile;
    private readonly PeerNode node;
    private readonly RtcMesh? rtc;
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
    private readonly StackPanel servers = new(), channels = new(), members = new(), messages = new();
    private readonly TextBlock serverTitle = new() { FontSize = 18, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock channelTitle = new() { FontSize = 18, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock connection = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock userName = new() { FontWeight = FontWeights.SemiBold };
    private readonly TextBlock notice = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(18, 8, 18, 8) };
    private readonly TextBox composer = new() { AcceptsReturn = true, MaxHeight = 110, MinHeight = 44, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxLength = 8000 };
    private readonly ScrollViewer messageScroll;
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
    private Channel? selectedChannel;
    private string? voiceChannelId, sessionCommunityId, pendingAttachment;
    private string? currentInvite;
    private bool muted, deafened, sharing, closing;
    private string? recordedPath;
    private bool sendingRecording;
    private IReadOnlyList<PeerPresence> presence = Array.Empty<PeerPresence>();

    public MainWindow(LocalStore store, UserProfile profile, FirebaseBackend? cloud = null)
    {
        if (profile.AvatarRevision is null && profile.AvatarPath is not null && File.Exists(profile.AvatarPath)) profile = profile with { AvatarRevision = AvatarCatalog.Revision(profile.AvatarPath) };
        this.store = store; avatars = new AvatarCatalog(store.RootDirectory, profile.Id); this.profile = profile; this.cloud = cloud; savedRefreshToken = cloud?.RefreshToken; node = new PeerNode(profile, store.RootDirectory);
        iceServers = new FileIceServerProvider(store.RootDirectory);
        rtc = cloud is null ? null : new RtcMesh(cloud, iceServers);
        Title = "Wisp"; Width = 1360; Height = 860; MinWidth = 1060; MinHeight = 650; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var root = new Grid(); root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) }); root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) }); root.ColumnDefinitions.Add(new ColumnDefinition()); root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) }); Content = root;
        var rail = new DockPanel { Margin = new Thickness(8, 14, 8, 8) }; root.Children.Add(rail);
        var logo = new TextBlock { Text = "w", FontSize = 35, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 18) }; DockPanel.SetDock(logo, Dock.Top); rail.Children.Add(logo);
        var railActions = new StackPanel(); DockPanel.SetDock(railActions, Dock.Bottom); rail.Children.Add(railActions);
        railActions.Children.Add(Btn("+", CreateCommunity, "Создать сообщество")); railActions.Children.Add(Btn("↗", JoinCommunity, "Присоединиться по приглашению"));
        railActions.Children.Add(Btn("◐", ToggleTheme, "Светлая / тёмная тема")); rail.Children.Add(new ScrollViewer { Content = servers, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var side = new DockPanel(); side.SetResourceReference(Panel.BackgroundProperty, "Panel"); Grid.SetColumn(side, 1); root.Children.Add(side);
        var heading = new StackPanel { Margin = new Thickness(16, 18, 12, 14) }; heading.Children.Add(serverTitle);
        heading.Children.Add(new TextBlock { Text = "ТВОЁ ПРОСТРАНСТВО", FontSize = 10, Opacity = .5, Margin = new Thickness(0, 6, 0, 14) });
        var actions = new WrapPanel(); actions.Children.Add(Btn("Пригласить", HostCommunity)); actions.Children.Add(Btn("+ Канал", CreateChannel)); heading.Children.Add(actions); DockPanel.SetDock(heading, Dock.Top); side.Children.Add(heading);
        var self = new StackPanel { Margin = new Thickness(12) }; self.Children.Add(connection); self.Children.Add(userName);
        var controls = new WrapPanel(); micButton = Btn("Микр.", ToggleMic, "Выключить / включить микрофон"); deafenButton = Btn("Звук", ToggleDeafen, "Выключить наушники и микрофон"); controls.Children.Add(micButton); controls.Children.Add(deafenButton); controls.Children.Add(Btn("⚙", EditProfile, "Профиль и настройки")); self.Children.Add(controls); leaveButton = Btn("Выйти из канала", LeaveVoice); self.Children.Add(leaveButton); DockPanel.SetDock(self, Dock.Bottom); side.Children.Add(self); side.Children.Add(new ScrollViewer { Content = channels, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var center = new DockPanel(); Grid.SetColumn(center, 2); root.Children.Add(center);
        var top = new DockPanel { Margin = new Thickness(22, 18, 18, 12) }; top.Children.Add(channelTitle);
        var toolbar = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right }; toolbar.Children.Add(Btn("Эмодзи", EmojiPanel)); toolbar.Children.Add(Btn("Звуки", SoundPanel)); shareButton = Btn("Демонстрация", ShareScreen); toolbar.Children.Add(shareButton); toolbar.Children.Add(Btn("Смотреть", WatchScreens));
        notificationButton = Btn("Уведомления", ShowNotifications); toolbar.Children.Add(notificationButton);
        notifications.Changed += count => notificationButton.Content = count == 0 ? "Уведомления" : $"Уведомления ({count})";
        top.Children.Add(toolbar); DockPanel.SetDock(top, Dock.Top); center.Children.Add(top);
        notice.Visibility = Visibility.Collapsed; DockPanel.SetDock(notice, Dock.Top); center.Children.Add(notice);
        var input = new StackPanel { Margin = new Thickness(18, 6, 18, 18) }; input.Children.Add(attachmentPreview); var composeRow = new Grid(); composeRow.ColumnDefinitions.Add(new ColumnDefinition()); composeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); composeRow.Children.Add(composer); sendButton = Btn("Отправить", SendMessage); Grid.SetColumn(sendButton, 1); composeRow.Children.Add(sendButton); input.Children.Add(composeRow);
        var attach = new WrapPanel(); attach.Children.Add(Btn("+ Файл", AttachFile)); recordButton = Btn("● Голосовое", RecordVoice); attach.Children.Add(recordButton); cancelRecordButton = Btn("Отменить запись", CancelRecording); cancelRecordButton.Visibility = Visibility.Collapsed; attach.Children.Add(cancelRecordButton); attach.Children.Add(new TextBlock { Text = "Enter — отправить · Shift+Enter — новая строка", FontSize = 10, Opacity = .5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) }); input.Children.Add(attach); DockPanel.SetDock(input, Dock.Bottom); center.Children.Add(input);
        messageScroll = new ScrollViewer { Content = messages, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0) }; messages.Margin = new Thickness(22, 0, 16, 0); center.Children.Add(messageScroll);
        var right = new DockPanel { Margin = new Thickness(0) }; members.Margin = new Thickness(12, 0, 8, 12); right.SetResourceReference(Panel.BackgroundProperty, "Panel"); Grid.SetColumn(right, 3); root.Children.Add(right);
        var memberHeader = new TextBlock { Text = "УЧАСТНИКИ", FontSize = 11, Opacity = .6, Margin = new Thickness(16, 22, 0, 14) }; DockPanel.SetDock(memberHeader, Dock.Top); right.Children.Add(memberHeader); right.Children.Add(new ScrollViewer { Content = members, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        composer.PreviewKeyDown += (_, e) => { if (ComposerInput.ShouldSend(e.Key, Keyboard.Modifiers)) { e.Handled = true; Run(SendMessage); } };
        _ = Task.Run(ProcessIncoming);
        node.MessageReceived += EnqueueIncoming;
        if (rtc is not null)
        {
            rtc.MessageReceived += EnqueueIncoming;
            rtc.Error += message => Dispatcher.BeginInvoke(() => { if (!closing) Notify(message, true); });
            rtc.Changed += () => Dispatcher.BeginInvoke(() =>
            {
                if (closing) return;
                if (!rtc.Joined) { cloudChannelId = null; if (voiceChannelId is not null) StopVoice(); }
                var active = rtc.ConnectedPeerIds; voice.SynchronizePeers(active);
                var paths = string.Join(", ", rtc.ConnectionPaths.Values.OrderBy(path => path));
                if (paths.Length > 0 && paths != lastConnectionPaths) Notify("Путь прямого соединения: " + paths);
                lastConnectionPaths = paths;
                foreach (var id in remoteStreams.Keys.Where(id => !active.Contains(id)).ToList()) StopVideoPeer(id);
                var peerKey = string.Join("|", active.OrderBy(id => id));
                if (sharing && peerKey != sharedPeers && screen.CurrentInfo is { } info) { sharedPeers = peerKey; Run(() => SendPacket(new WireMessage("screen-start", profile.Id, voiceChannelId, JsonSerializer.SerializeToUtf8Bytes(info)))); }
                foreach (var id in remoteScreens.Keys.Where(id => !active.Contains(id)).ToList()) { remoteScreens.Remove(id); dismissedScreens.Remove(id); }
                foreach (var id in viewers.Keys.Where(id => !active.Contains(id)).ToList()) if (viewers.Remove(id, out var gone)) gone.Close();
                UpdateStatus();
            });
        }
        node.PresenceChanged += p => Dispatcher.BeginInvoke(() =>
        {
            if (closing || rtc is not null) return; var hasNew = p.Any(x => x.Profile.Id != profile.Id && !presence.Any(old => old.Profile.Id == x.Profile.Id)); presence = p;
            RenderMembers(); RenderChannels(); if (voiceChannelId is not null && !p.Any(x => x.Profile.Id == profile.Id && x.VoiceChannelId == voiceChannelId)) StopVoice();
            foreach (var v in viewers.Keys.Where(id => !p.Any(x => x.Profile.Id == id && x.VoiceChannelId == voiceChannelId)).ToList()) if (viewers.Remove(v, out var gone)) gone.Close();
            voice.SynchronizePeers(p.Where(x => x.VoiceChannelId == voiceChannelId && x.Profile.Id != profile.Id).Select(x => x.Profile.Id));
            UpdateStatus(); if (hasNew) Run(PublishProfile);
        });
        node.SnapshotReceived += s => Dispatcher.BeginInvoke(() =>
        {
            if (closing) return;
            try { store.SaveSnapshot(s); sessionCommunityId = s.Community.Id; community = s.Community; serverTitle.Text = community.Name; RenderServers(); RenderChannels(); var firstChannel = store.GetChannels(community.Id).FirstOrDefault(); if (selectedChannel?.CommunityId != community.Id && firstChannel is not null) SelectChannel(firstChannel); RenderMembers(); }
            catch (Exception ex) { Notify(ex.Message, true); }
        });
        node.Error += e => Dispatcher.BeginInvoke(() => Notify(e, true));
        _ = voice.ConfigureDevicesAsync(AudioDeviceSettings.Load(store.RootDirectory));
        voice.PacketReady += p => SendRealtime("audio", p); voice.Error += e => Dispatcher.BeginInvoke(() => Notify(e, true));
        screen.FrameReady += p => SendRealtime("screen-h264", p);
        screen.Status += text => Dispatcher.BeginInvoke(() => { if (!closing && sharing) Notify(text); });
        screen.PauseChanged += paused => { if (screen.CurrentInfo is { } info) SendRealtime("screen-state", JsonSerializer.SerializeToUtf8Bytes(new ScreenCommand(profile.Id, info.SessionId, paused))); };
        shareAudio.PacketReady += p => { if (!deafened) SendRealtime("screen-audio", p); };
        shareAudio.Error += e => Dispatcher.BeginInvoke(() => Notify(e, true));
        sharePlayback.Error += e => Dispatcher.BeginInvoke(() => Notify(e, true)); screen.Error += e => Dispatcher.BeginInvoke(() => { StopSharing(); Notify(e, true); });
        recorder.Completed += path => Dispatcher.BeginInvoke(() => { if (closing || sendingRecording || !File.Exists(path)) return; Run(() => { recordedPath = path; StageRecording(path); Notify("Запись завершена. Можно прослушать и отправить."); return Task.CompletedTask; }); });
        activityTimer.Tick += (_, _) => Run(UpdateGame); activityTimer.Start();
        tray = new System.Windows.Forms.NotifyIcon { Icon = (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone(), Text = "Wisp", Visible = true };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Открыть Wisp", null, (_, _) => Dispatcher.BeginInvoke(RestoreFromTray));
        menu.Items.Add("Выйти из аккаунта", null, (_, _) => Dispatcher.BeginInvoke(() => Run(SignOut)));
        menu.Items.Add("Закрыть Wisp", null, (_, _) => Dispatcher.BeginInvoke(Quit));
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(RestoreFromTray);
        cloudTimer.Tick += (_, _) => Run(RefreshCloud); if (cloud is not null) cloudTimer.Start();
        Closing += OnClosing;
        Loaded += (_, _) => { if (cloud is not null && this.profile.AvatarPath is not null) Run(PublishProfile); };
        userName.Text = profile.Username; RenderServers();
        var first = MyCommunities().FirstOrDefault(); if (first is not null) SetCommunity(first); else { serverTitle.Text = "Добро пожаловать"; channelTitle.Text = "Место для своих"; Notify("Создай сообщество кнопкой + слева или присоединись по приглашению ↗."); }
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

    private Button Btn(string text, Action action, string? tip = null) { var b = new Button { Content = text, ToolTip = tip ?? text }; b.Click += (_, _) => { try { action(); } catch (Exception ex) { Notify(ex.Message, true); } }; return b; }
    private Button Btn(string text, Func<Task> action, string? tip = null) => Btn(text, () => Run(action), tip);
    private async void Run(Func<Task> action) { if (closing) return; try { await action(); } catch (Exception ex) { if (!closing) Notify(ex.Message, true); } }
    private void Notify(string text, bool error = false) { var added = notifications.Add(text, error); if (error && added && DateTimeOffset.UtcNow - lastErrorCue >= TimeSpan.FromSeconds(15)) { lastErrorCue = DateTimeOffset.UtcNow; sounds.Play("error"); } }
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
        servers.Children.Clear(); foreach (var c in MyCommunities())
        {
            var b = Btn(c.Name[..Math.Min(2, c.Name.Length)].ToUpperInvariant(), async () => { if (community?.Id != c.Id) await Disconnect(); SetCommunity(c); }, c.Name);
            b.Height = 48; if (community?.Id == c.Id) b.SetResourceReference(Control.BackgroundProperty, "Accent"); servers.Children.Add(b);
        }
    }
    private void SetCommunity(Community c) { EnsureNoRecording(); community = c; serverTitle.Text = c.Name; RenderServers(); RenderChannels(); var first = store.GetChannels(c.Id).FirstOrDefault(); if (first is not null) SelectChannel(first); RenderMembers(); UpdateStatus(); }
    private void EnsureNoRecording() { if (recorder.IsRecording || recordedPath is not null) throw new InvalidOperationException("Сначала отправь голосовую запись."); }
    private void RenderChannels()
    {
        channels.Children.Clear(); if (community is null) return;
        foreach (bool voiceKind in new[] { false, true })
        {
            channels.Children.Add(new TextBlock { Text = voiceKind ? "ГОЛОСОВЫЕ КАНАЛЫ · ДО 5" : "ТЕКСТОВЫЕ КАНАЛЫ", FontSize = 10, Opacity = .55, Margin = new Thickness(16, 18, 0, 8) });
            foreach (var c in store.GetChannels(community.Id).Where(x => x.IsVoice == voiceKind))
            {
                var count = DisplayPresence.Count(p => p.VoiceChannelId == c.Id);
                var b = Btn(voiceKind ? $"{(voiceChannelId == c.Id ? "✓" : "♫")}  {c.Name}  {count}/5" : $"#  {c.Name}", async () => { if (c.IsVoice) await JoinVoice(c); else SelectChannel(c); });
                b.HorizontalContentAlignment = HorizontalAlignment.Left;
                if (voiceKind)
                {
                    var row = new DockPanel(); var chat = Btn("Чат", () => SelectChannel(c), "Открыть текстовый чат голосового канала");
                    chat.Padding = new Thickness(6, 8, 6, 8); DockPanel.SetDock(chat, Dock.Right); row.Children.Add(chat); row.Children.Add(b); channels.Children.Add(row);
                }
                else channels.Children.Add(b);
                if (voiceKind) foreach (var p in DisplayPresence.Where(p => p.VoiceChannelId == c.Id)) channels.Children.Add(new TextBlock { Text = $"    ● {p.Profile.Username}{(p.Deafened ? " · без звука" : p.Muted ? " · микр. выкл." : "")}", FontSize = 12, Margin = new Thickness(18, 2, 0, 4), Opacity = .7 });
            }
        }
    }
    private void SelectChannel(Channel c) { if (recorder.IsRecording || recordedPath is not null) throw new InvalidOperationException("Сначала отправь голосовую запись."); selectedChannel = c; channelTitle.Text = $"{(c.IsVoice ? "♫" : "#")} {c.Name}"; pendingAttachment = null; draftEmojis.Clear(); UpdateAttachmentPreview(); renderedMessages.Clear(); messages.Children.Clear(); foreach (var m in store.GetMessages(c.Id)) AddMessage(m); RenderChannels(); Notify(c.IsVoice ? "Голосовой канал · максимум 5 участников. Для связи нужна активная P2P-сессия." : "Сообщения и вложения сохраняются локально. Подключённые участники получают новые сообщения."); }
    private void RenderMembers()
    {
        if (community is null) { members.Children.Clear(); return; }
        var saved = store.GetMembers(community.Id).ToDictionary(x => x.Id); saved[profile.Id] = profile;
        foreach (var p in DisplayPresence) saved[p.Profile.Id] = p.Profile;
        saved[profile.Id] = profile;
        var ordered = saved.Values.OrderByDescending(x => x.Id == profile.Id || DisplayPresence.Any(p => p.Profile.Id == x.Id)).ThenBy(x => x.Username).ToList();
        var key = string.Join("|", ordered.Select(p => $"{p.Id}:{p.Username}:{p.Game}:{p.About}:{p.BannerAssetId}:{p.BannerPath}:{p.AvatarRevision}:{avatars.Resolve(p,store)}:{DisplayPresence.Any(s => s.Profile.Id == p.Id)}"));
        if (key == memberRenderKey) return; memberRenderKey = key; members.Children.Clear();
        foreach (var p in ordered)
        {
            var online = p.Id == profile.Id || DisplayPresence.Any(s => s.Profile.Id == p.Id);
            var info = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };
            info.Children.Add(new TextBlock { Text = p.Username, Opacity = online ? 1 : .45, TextTrimming = TextTrimming.CharacterEllipsis });
            var game = p.Id == profile.Id ? profile.Game : DisplayPresence.FirstOrDefault(s => s.Profile.Id == p.Id)?.Profile.Game;
            info.Children.Add(new TextBlock { Text = game is null ? online ? "● В сети" : "○ Не в сети" : $"Играет в {game}", FontSize = 10, Opacity = .6, Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
            var row = new DockPanel(); var avatar = avatars.Resolve(p, store); var picture = AvatarCatalog.Circle(avatar, 36); DockPanel.SetDock(picture, Dock.Left); row.Children.Add(picture); row.Children.Add(info);
            var button = Btn("", () => ViewProfile(p)); button.Content = row; button.HorizontalContentAlignment = HorizontalAlignment.Stretch; button.Padding = new Thickness(6, 8, 6, 8); members.Children.Add(button);
        }
    }
    private void UpdateStatus() { var inVoice = voiceChannelId is not null;
        leaveButton.Visibility = inVoice || cloudChannelId is not null ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in new[] { micButton, deafenButton, shareButton }) button.Visibility = inVoice ? Visibility.Visible : Visibility.Collapsed;
        connection.Text = voiceChannelId is not null ? "● В канале: " + (community is null ? "" : store.GetChannels(community.Id).FirstOrDefault(c => c.Id == voiceChannelId)?.Name) + (rtc is null ? "" : $" · WebRTC: {rtc.ConnectedCount} соединений") : cloudChannelId is not null ? "● В канале · голос ещё не подключён" : cloud is not null ? "● Firebase подключён" : node.IsConnected ? "● P2P-сессия подключена" : "○ Локальный режим"; userName.Text = profile.Username; micButton.Content = muted || deafened ? "Микр. ×" : "Микр. ✓"; deafenButton.Content = deafened ? "Звук ×" : "Звук ✓"; }
    private void CreateCommunity()
    {
        var d = new FormDialog(this, "Создать сообщество"); var name = d.Field("Название"); d.Accept("Создать"); if (d.ShowDialog() != true) return;
        if (node.IsConnected || rtc?.Joined == true) { Notify("Сначала выйди из голосового канала.", true); return; } SetCommunity(store.CreateCommunity(name.Text, profile.Id));
        if (cloud is not null) Run(async () => { await cloud.CreateCommunityAsync(new CommunitySnapshot(community!, store.GetChannels(community!.Id), new List<UserProfile> { profile })); await RefreshCloud(); });
        sounds.Play("connect");
    }
    private async Task HostCommunity()
    {
        if (community is null) return;
        if (cloud is not null) { var inviteDialog = new FormDialog(this, "Пригласить в сообщество"); var code = "wisp-cloud:" + community.Id; inviteDialog.Field("Приглашение", code); inviteDialog.Action("Скопировать", () => Clipboard.SetText(code)); inviteDialog.Accept("Готово"); inviteDialog.ShowDialog(); return; }
        if (node.IsConnected) { if (node.IsHost && currentInvite is not null) { ShowInvite(currentInvite); return; } throw new InvalidOperationException("Сначала отключись от текущей сессии."); }
        var d = new FormDialog(this, "Открыть P2P-сессию"); var address = d.Field("Твой доступный IP или DNS (необязательно)");
        d.Note("В одной сети адрес обычно определяется автоматически. Через интернет нужен проброс TCP 45832 на этот компьютер или общая VPN-сеть. Разреши Wisp в брандмауэре Windows. Не отправляй приглашение посторонним."); d.Accept("Открыть сессию"); if (d.ShowDialog() != true) return;
        await node.UpdateProfileAsync(profile);
        currentInvite = await node.HostAsync(new CommunitySnapshot(community, store.GetChannels(community.Id), store.GetMembers(community.Id)), advertisedHost: string.IsNullOrWhiteSpace(address.Text) ? null : address.Text.Trim()); sessionCommunityId = community.Id; sounds.Play("connect"); UpdateStatus(); ShowInvite(currentInvite);
    }
    private void ShowInvite(string invite) { var d = new FormDialog(this, "Пригласить друзей"); d.Field("Личное приглашение", invite, true); d.Note("Сессия работает, пока открыт Wisp на этом компьютере. Приглашение даёт доступ к сообществу."); d.Action("Скопировать", () => { Clipboard.SetText(invite); sounds.Play("message"); }); d.Accept("Готово"); d.ShowDialog(); }
    private async Task JoinCommunity()
    {
        EnsureNoRecording();
        var d = new FormDialog(this, "Присоединиться"); var invite = d.Field(cloud is null ? "Приглашение wisp://" : "Приглашение wisp-cloud:", "", true); d.Note(cloud is null ? "Для голоса нужен прямой доступ к компьютеру друга." : "Приглашение даёт доступ к сообществу и чату. Голос использует прямой WebRTC с настроенными STUN-серверами. В некоторых сетях прямое соединение невозможно."); d.Accept("Подключиться"); if (d.ShowDialog() != true) return;
        IsEnabled = false;
        try { await Disconnect();
            if (cloud is not null && invite.Text.Trim().StartsWith("wisp-cloud:"))
            {
                var id = invite.Text.Trim()[11..]; await cloud.JoinCommunityAsync(id);
                var snapshot = await cloud.GetAsync<CommunitySnapshot>($"communities/{id}/snapshot") ?? throw new InvalidOperationException("Сообщество не найдено.");
                store.SaveSnapshot(snapshot); store.UpsertMember(snapshot.Community.Id, profile); SetCommunity(snapshot.Community); await RefreshCloud(); return;
            }
            if (cloud is not null) throw new ArgumentException("Используй приглашение wisp-cloud: для сообщества Firebase.");
            await node.JoinAsync(invite.Text.Trim()); sounds.Play("connect"); UpdateStatus(); await Dispatcher.InvokeAsync(() => { }); await PublishProfile(); }
        finally { if (!closing) IsEnabled = true; }
    }
    private void CreateChannel()
    {
        if (community is null) return; if (community.OwnerId != profile.Id) throw new InvalidOperationException("Создавать каналы может владелец сообщества.");
        if (node.IsConnected) throw new InvalidOperationException("Для изменения каналов останови сессию, затем открой её снова с новым приглашением.");
        var d = new FormDialog(this, "Новый канал"); var name = d.Field("Название"); var isVoice = d.Check("Голосовой канал · максимум 5 человек"); d.Accept("Создать"); if (d.ShowDialog() == true) { var c = store.CreateChannel(community.Id, name.Text, isVoice.IsChecked == true); SelectChannel(c); if (cloud is not null) Run(() => cloud.CreateCommunityAsync(new CommunitySnapshot(community, store.GetChannels(community.Id), store.GetMembers(community.Id)))); sounds.Play("message"); }
    }
    private async Task JoinVoice(Channel c)
    {
        if (rtc is not null && cloud is not null && community is not null)
        {
            if (voiceChannelId == c.Id && rtc.Joined) return;
            EnsureNoRecording(); StopVoice();
            try
            {
                await rtc.EnterAsync(community.Id, c.Id, muted, deafened);
                voice.Muted = muted; voice.Deafened = deafened; voice.Start();
                voiceChannelId = c.Id; cloudChannelId = c.Id;
                await cloud.PresenceAsync(community.Id, profile, c.Id);
                await RefreshCloud(); RenderChannels(); UpdateStatus(); sounds.Play("connect");
                Notify("WebRTC · STUN. Соединение с участниками устанавливается напрямую.");
            }
            catch { StopVoice(); cloudChannelId = null; await rtc.LeaveAsync(); throw; }
            return;
        }
        if (!node.IsConnected)
        {
            if (community is null || community.OwnerId != profile.Id) throw new InvalidOperationException("Подключись к сообществу по приглашению владельца.");
            currentInvite = await node.HostAsync(new CommunitySnapshot(community, store.GetChannels(community.Id), store.GetMembers(community.Id)));
            sessionCommunityId = community.Id;
            if (cloud is not null) await cloud.PutAsync($"communities/{community.Id}/endpoint", currentInvite);
        }
        if (c.Id == voiceChannelId) return;
        if (recorder.IsRecording) throw new InvalidOperationException("Сначала закончи голосовую запись.");
        if (await node.SetVoiceChannelAsync(c.Id, muted, deafened))
        {
            try { StopSharing(); voice.Stop(); voice.Start(); voice.Muted = muted; voice.Deafened = deafened; voiceChannelId = c.Id; presence = node.Presence; sounds.Play("connect"); RenderChannels(); RenderMembers(); UpdateStatus(); }
            catch { StopVoice(); await node.SetVoiceChannelAsync(null, muted, deafened); throw; }
        }
        else { cloudChannelId = null; if (cloud is not null && community is not null) await cloud.PresenceAsync(community.Id, profile, null); UpdateStatus(); Notify("Канал заполнен: максимум 5 человек.", true); }
    }
    private async Task LeaveVoice() { if (rtc is not null) await rtc.LeaveAsync(); cloudChannelId = null; cloudPresence = cloudPresence.Where(p => p.Profile.Id != profile.Id).ToList(); if (cloud is not null && community is not null) await cloud.PresenceAsync(community.Id, profile, null); if (node.IsConnected) await node.SetVoiceChannelAsync(null, muted, deafened); StopVoice(); presence = node.Presence; RenderChannels(); sounds.Play("disconnect"); }
    public void Quit() { exitRequested = true; Close(); }
    private void RestoreFromTray() { Show(); WindowState = WindowState.Normal; Activate(); foreach (var v in viewers.Values) v.Show(); }
    private async Task SignOut()
    {
        var saved = SessionVault.Read(store.RootDirectory);
        if (saved is not null && !saved.StartsWith("firebase:")) store.RevokeSession(saved);
        SessionVault.Clear(store.RootDirectory);
        try { await Disconnect(); } finally { exitRequested = true; Close(); }
    }
    private void StopVoice() { StopSharing(); voice.Stop(); sharePlayback.Stop(); foreach (var id in remoteStreams.Keys.ToList()) StopVideoPeer(id); videoFrames.Clear(); voiceChannelId = null; foreach (var v in viewers.Values.ToList()) v.Close(); viewers.Clear(); remoteScreens.Clear(); dismissedScreens.Clear(); UpdateStatus(); }
    private async Task Disconnect() { if (rtc is not null) await rtc.LeaveAsync(); cloudChannelId = null; cloudPresence = Array.Empty<PeerPresence>(); if (cloud is not null && community is not null) await cloud.PresenceAsync(community.Id, profile, null); Interlocked.Increment(ref sessionEpoch); latestFrames.Clear(); StopVoice(); await node.DisconnectAsync(); presence = Array.Empty<PeerPresence>(); sessionCommunityId = null; currentInvite = null; RenderMembers(); RenderChannels(); UpdateStatus(); sounds.Play("disconnect"); }
    private async Task ToggleMic() { muted = !muted; voice.Muted = muted; if (rtc is not null) await rtc.SetMutedAsync(muted, deafened); if (node.IsConnected) await node.SetVoiceChannelAsync(voiceChannelId, muted, deafened); sounds.Play(muted ? "mute" : "unmute"); UpdateStatus(); }
    private async Task ToggleDeafen() { deafened = !deafened; voice.Deafened = deafened; sharePlayback.Deafened = deafened; if (rtc is not null) await rtc.SetMutedAsync(muted, deafened); if (deafened) { sounds.StopFile(); VoiceMessagePlayer.PauseActive(); } if (node.IsConnected) await node.SetVoiceChannelAsync(voiceChannelId, muted, deafened); sounds.Play(deafened ? "deafen" : "undeafen"); UpdateStatus(); }
    private async Task UpdateGame()
    {
        var game = GamePresenceService.DetectGame(); if (game == profile.Game) return; profile = profile with { Game = game }; store.SaveProfile(profile); await node.UpdateProfileAsync(profile); RenderMembers();
    }
    private async Task SendMessage()
    {
        if (recorder.IsRecording) { Notify("Сначала заверши запись голосового сообщения.", true); return; }
        if (sendingMessage || selectedChannel is null || (string.IsNullOrWhiteSpace(composer.Text) && pendingAttachment is null)) return;
        sendingMessage = true; sendButton.Content = "Отправка…"; sendButton.IsEnabled = false;
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
            if (node.IsConnected) await node.SendAsync(new WireMessage("chat", profile.Id, channel.Id, JsonSerializer.SerializeToUtf8Bytes(message)));
            store.SaveMessage(message); if (selectedChannel?.Id == channel.Id) AddMessage(message);
            if (composer.Text == text) composer.Clear();
            if (pendingAttachment == attachment) pendingAttachment = null;
            draftEmojis.Clear(); UpdateAttachmentPreview(); sounds.Play("message");
        }
        finally { sendingMessage = false; sendButton.Content = "Отправить"; sendButton.IsEnabled = true; if (Keyboard.FocusedElement == sendButton) composer.Focus(); }
    }
    private static string EmojiToken(MediaAsset a)
    {
        var name = new string(Path.GetFileNameWithoutExtension(a.Name).Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_').Take(32).ToArray());
        return ":" + (name.Length == 0 ? "emoji" : name) + ":";
    }
    private TextBlock MessageText(ChatMessage message)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0) };
        int offset = 0;
        foreach (Match match in Regex.Matches(message.Text, @":[\p{L}\p{N}_-]{1,40}:"))
        {
            if (match.Index > offset) text.Inlines.Add(new Run(message.Text[offset..match.Index]));
            var asset = message.Emojis?.TryGetValue(match.Value, out var id) == true ? store.GetAsset(id) : null;
            if (asset?.Kind == "emoji" && asset.CommunityId == community?.Id)
            {
                try { var image = AvatarCatalog.Image(asset.LocalPath, text.FontSize + 2); image.Stretch = Stretch.Uniform; text.Inlines.Add(new InlineUIContainer(image) { BaselineAlignment = BaselineAlignment.Center }); }
                catch { text.Inlines.Add(new Run(match.Value)); }
            }
            else text.Inlines.Add(new Run(match.Value));
            offset = match.Index + match.Length;
        }
        if (offset < message.Text.Length) text.Inlines.Add(new Run(message.Text[offset..]));
        return text;
    }
    private void UpdateAttachmentPreview()
    {
        attachmentPreview.Children.Clear(); UpdateRecordingControls(); if (pendingAttachment is null) return;
        var asset = store.GetAsset(pendingAttachment); if (asset is null) return;
        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 6) };
        var remove = Btn("✕", () => { pendingAttachment = null; UpdateAttachmentPreview(); }, "Убрать вложение"); DockPanel.SetDock(remove, Dock.Right); row.Children.Add(remove);
        row.Children.Add(new TextBlock { Text = $"{asset.Name} · {asset.Size / 1024} КБ", VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis }); attachmentPreview.Children.Add(row);
        if (new[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp" }.Contains(Path.GetExtension(asset.Name).ToLowerInvariant()))
        { try { attachmentPreview.Children.Add(new Image { Source = LoadImage(asset.LocalPath, 320), Height = 110, MaxWidth = 260, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left }); } catch { } }
        else if (asset.Kind == "voice" || new[] { ".wav", ".mp3" }.Contains(Path.GetExtension(asset.Name).ToLowerInvariant())) attachmentPreview.Children.Add(new VoiceMessagePlayer(asset.LocalPath, e => Notify(e, true), () => !deafened));
    }
    private void AddMessage(ChatMessage m)
    {
        if (!renderedMessages.Add(m.Id)) return;
        var row = new StackPanel { Margin = new Thickness(0, 8, 0, 12) }; var meta = new TextBlock { Text = $"{m.AuthorName}   {m.SentAt.LocalDateTime:dd.MM HH:mm}", FontWeight = FontWeights.SemiBold, FontSize = 12 }; meta.SetResourceReference(TextBlock.ForegroundProperty, "Accent"); row.Children.Add(meta);
        if (!string.IsNullOrEmpty(m.Text)) row.Children.Add(MessageText(m));
        if (m.AttachmentId is not null)
        {
            var asset = store.GetAsset(m.AttachmentId); if (asset?.CommunityId != community?.Id) asset = null;
            if (asset is not null)
            {
                if (asset.Kind == "emoji")
                { try { row.Children.Add(AvatarCatalog.Image(asset.LocalPath, 16)); } catch { } }
                else if (new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp" }.Contains(Path.GetExtension(asset.Name).ToLowerInvariant()))
                {
                    try { row.Children.Add(new Image { Source = LoadImage(asset.LocalPath, 600), MaxHeight = 260, MaxWidth = 500, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) }); } catch { }
                }
                if (asset.Kind == "voice") row.Children.Add(new VoiceMessagePlayer(asset.LocalPath, e => Notify(e, true), () => !deafened));
                else if (asset.Kind != "emoji") row.Children.Add(Btn($"↓ {asset.Name} · {asset.Size / 1024} КБ", () => OpenAsset(asset)));
            }
            else row.Children.Add(new TextBlock { Text = "Вложение недоступно на этом компьютере", FontSize = 11, Opacity = .5 });
        }
        messages.Children.Add(row); while (messages.Children.Count > 200) messages.Children.RemoveAt(0); messageScroll.ScrollToEnd();
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
        try { if (node.IsConnected || rtc?.Joined == true) await BroadcastAsset(asset); return asset; } finally { if (!closing) IsEnabled = true; }
    }
    private async Task AttachFile()
    {
        if (selectedChannel is null) return; var pick = new Microsoft.Win32.OpenFileDialog { Title = "Прикрепить медиа или файл · до 16 МБ" }; if (pick.ShowDialog(this) != true) return;
        await Task.CompletedTask; var a = community is null ? null : store.ImportAsset(community.Id, pick.FileName, "attachment"); if (a is not null) { pendingAttachment = a.Id; UpdateAttachmentPreview(); Notify($"Прикреплён {a.Name}. Нажми «Отправить»."); sounds.Play("message"); }
    }
    private async Task RecordVoice()
    {
        if (selectedChannel is null) return;
        if (!recorder.IsRecording && recordedPath is null)
        {
            if (voiceChannelId is not null) throw new InvalidOperationException("Для голосового сообщения сначала отключись от звонка.");
            var path = Path.Combine(store.RootDirectory, $"recording-{Guid.NewGuid():N}.wav"); recorder.Start(path); recordButton.Content = "■ Завершить запись"; UpdateRecordingControls(); Notify("Запись микрофона · максимум 60 секунд. Нажми ещё раз, чтобы прослушать перед отправкой."); sounds.Play("connect");
        }
        else
        {
            sendingRecording = true;
            try { string path = recordedPath ?? await recorder.StopAsync(); recordedPath = null; recordButton.Content = "● Голосовое";
                StageRecording(path);

            } finally { sendingRecording = false; UpdateRecordingControls(); }
        }
    }
    private void StageRecording(string path)
    {
        try { if (community is not null) { var asset = store.ImportAsset(community.Id, path, "voice"); pendingAttachment = asset.Id; } }
        finally { File.Delete(path); recordedPath = null; recordButton.Content = "● Голосовое"; UpdateAttachmentPreview(); UpdateRecordingControls(); }
    }
    private async Task CancelRecording()
    {
        if (!recorder.IsRecording && recordedPath is null) { if (pendingAttachment is not null && store.GetAsset(pendingAttachment)?.Kind == "voice") { pendingAttachment = null; UpdateAttachmentPreview(); UpdateRecordingControls(); } return; }
        sendingRecording = true;
        try { var path = recordedPath ?? await recorder.StopAsync(); recordedPath = null; File.Delete(path); recordButton.Content = "● Голосовое"; Notify("Запись удалена."); sounds.Play("disconnect"); }
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
                store.SaveMessage(chat); if (selectedChannel?.Id == chat.ChannelId) AddMessage(chat); if (!deafened) sounds.Play("message"); break;
            case "audio": if (voiceChannelId == m.ChannelId) voice.Receive(m.SenderId, m.Payload, m.MissingAudioFrames); break;
            case "screen": if (voiceChannelId == m.ChannelId) ShowFrame(m.SenderId, m.Payload); break;
            case "screen-start":
                if (voiceChannelId != m.ChannelId || m.Payload.Length > 2048) return;
                var streamInfo = JsonSerializer.Deserialize<ScreenStreamInfo>(m.Payload); if (streamInfo?.IsValid != true || streamInfo.SessionId.Length > 64) return;
                if (!remoteStreams.TryGetValue(m.SenderId, out var oldInfo) || oldInfo != streamInfo) { StopVideoPeer(m.SenderId); remoteStreams[m.SenderId] = streamInfo; OpenVideoPeer(m.SenderId); } break;
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
            case "screen-stop": StopVideoPeer(m.SenderId); remoteScreens.Remove(m.SenderId); dismissedScreens.Remove(m.SenderId); break;
            case "asset":
                if (community is null) return; var communityId = community.Id; var incoming = JsonSerializer.Deserialize<AssetTransfer>(m.Payload); if (incoming is null || incoming.Data.Length > 16 * 1024 * 1024 || incoming.Name.Length > 200 || incoming.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || incoming.Name != Path.GetFileName(incoming.Name) || incoming.Name is "." or "..") return;
                using (var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256))
                {
                    hash.AppendData(Encoding.UTF8.GetBytes(communityId + "\0" + incoming.Kind + "\0")); hash.AppendData(incoming.Data);
                    if (!string.Equals(Convert.ToHexString(hash.GetHashAndReset()), incoming.Id, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Неверный идентификатор вложения.");
                }
                var dir = Path.Combine(store.RootDirectory, "incoming", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir); var temporary = Path.Combine(dir, "payload.tmp");
                try { await File.WriteAllBytesAsync(temporary, incoming.Data); var a = store.ImportAsset(communityId, temporary, incoming.Kind, incoming.Name); if (a.Id != incoming.Id) Notify("Вложение отклонено: неверный идентификатор.", true);
                    else if (selectedChannel is not null && store.GetMessages(selectedChannel.Id).Any(msg => msg.AttachmentId == a.Id || msg.Emojis?.ContainsValue(a.Id) == true))
                    { var history = store.GetMessages(selectedChannel.Id); messages.Children.Clear(); renderedMessages.Clear(); foreach (var msg in history) AddMessage(msg); } } finally { Directory.Delete(dir, true); } break;
            case "sound":
                if (deafened || voiceChannelId != m.ChannelId) return; var sound = store.GetAsset(Encoding.UTF8.GetString(m.Payload)); if (sound?.Kind == "sound" && sound.CommunityId == community?.Id) sounds.PlayFile(sound.LocalPath); break;
        }
    }
    private void SendRealtime(string type, byte[] payload) { if (voiceChannelId is null || (!node.IsConnected && rtc?.Joined != true)) return; _ = SendRealtimeSafe(type, voiceChannelId, payload); }
    private async Task SendRealtimeSafe(string type, string channel, byte[] data) { try { await SendPacket(new WireMessage(type, profile.Id, channel, data)); } catch { /* Disconnect/presence events surface connection failure. */ } }
    private async Task ShareScreen()
    {
        if (sharing) { StopSharing(); return; } if (voiceChannelId is null) throw new InvalidOperationException("Сначала подключись к голосовому каналу.");
        var d = new FormDialog(this, "Демонстрация экрана", 600);
        d.Note("Выбери окно приложения или монитор. 1080p/60 FPS требует больше ресурсов и скорости сети; при перегрузке кадры пропускаются, чтобы не накапливать задержку.");
        ComboBox Choice(string label, string[] values, int selected) { d.Note(label); var box = new ComboBox { ItemsSource = values, SelectedIndex = selected, Margin = new Thickness(0, 4, 0, 8) }; d.Add(box); return box; }
        var resolution = Choice("Разрешение", new[] { "720p", "1080p" }, 1);
        var framerate = Choice("Частота кадров", new[] { "15 FPS", "30 FPS", "60 FPS" }, 1);
        var bitrate = Choice("Битрейт потока · общий исходящий = значение × число зрителей", new[] { "2 Мбит/с", "4 Мбит/с", "6 Мбит/с", "8 Мбит/с" }, 1);
        var audio = d.Check("Передавать звук приложения / экрана (Windows 11)", ShareAudioCapture.Supported);
        d.Note("При выборе приложения передаётся его звук. Для монитора передаётся системный звук с исключением Wisp, чтобы разговор не возвращался эхом.");
        CaptureSource? selected = null; foreach (var source in ScreenShareService.GetSources()) d.Action(source.Name, () => { selected = source; d.DialogResult = true; });
        if (d.ShowDialog() != true || selected is null) return;
        var info = ScreenShareService.Describe(selected, resolution.SelectedIndex == 0 ? 720 : 1080, new[] { 15, 30, 60 }[framerate.SelectedIndex], new[] { 2000000, 4000000, 6000000, 8000000 }[bitrate.SelectedIndex]) with { SessionId = Guid.NewGuid().ToString("N") };
        await SendPacket(new WireMessage("screen-start", profile.Id, voiceChannelId, JsonSerializer.SerializeToUtf8Bytes(info)));
        screen.Start(selected, info); sharing = true; sharedPeers = "";
        notice.Text = $"Идёт демонстрация: {selected.Name} · {info.Width}×{info.Height} · {info.FramesPerSecond} FPS"; notice.Visibility = Visibility.Visible; shareButton.Content = "Остановить показ"; sounds.Play("share");
        if (audio.IsChecked == true) { try { await shareAudio.StartAsync(selected); } catch (Exception ex) { Notify(ex.Message, true); } }
        Notify($"Демонстрация начата: {selected.Name} · H.264 · {info.FramesPerSecond} FPS");
    }
    private void StopSharing()
    {
        notice.Text = ""; notice.Visibility = Visibility.Collapsed; shareAudio.Stop(); screen.Stop(); sharedPeers = "";
        if (!sharing) return; sharing = false; shareButton.Content = "Демонстрация"; SendRealtime("screen-stop", Array.Empty<byte>()); sounds.Play("share"); Notify("Демонстрация остановлена.");
    }
    private ScreenViewer CreateViewer(string peerId)
    {
        if (viewers.TryGetValue(peerId, out var existing)) return existing;
        foreach (var other in viewers.Values.Where(v => v.IsFullscreen).ToList()) other.ToggleFullscreen();
        var name = DisplayPresence.FirstOrDefault(x => x.Profile.Id == peerId)?.Profile.Username ?? "Участник";
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
        foreach (var id in ids) d.Action(DisplayPresence.FirstOrDefault(p => p.Profile.Id == id)?.Profile.Username ?? "Участник", () => { d.Close(); dismissedScreens.Remove(id); if (remoteStreams.ContainsKey(id)) { if (viewers.TryGetValue(id, out var viewer)) { foreach (var other in viewers.Values.Where(v => v != viewer && v.IsFullscreen).ToList()) other.ToggleFullscreen(); if (viewer.IsMini) viewer.ToggleMini(); viewer.Show(); } else OpenVideoPeer(id); } else ShowFrame(id, remoteScreens[id]); });
        d.ShowDialog();
    }
    private void EmojiPanel()
    {
        if (community is null) return; var d = new FormDialog(this, "Эмодзи сообщества"); d.Note("Эмодзи добавится в текст сообщения на месте курсора.");
        foreach (var a in store.GetAssets(community.Id, "emoji"))
        {
            var button = d.Action(EmojiToken(a), () =>
            {
                var token = EmojiToken(a);
                if (draftEmojis.TryGetValue(token, out var previous) && previous.Id != a.Id) token = token[..^1] + "_" + a.Id[..6] + ":";
                draftEmojis[token] = a; d.Close(); var caret = composer.CaretIndex; composer.Text = composer.Text.Insert(caret, token); composer.CaretIndex = caret + token.Length; composer.Focus();
            });
            var label = new StackPanel { Orientation = Orientation.Horizontal };
            try { label.Children.Add(AvatarCatalog.Image(a.LocalPath, 18)); } catch { }
            label.Children.Add(new TextBlock { Text = "  " + EmojiToken(a) }); button.Content = label;
        }
        d.Action("+ Загрузить эмодзи", () => { var f = new Microsoft.Win32.OpenFileDialog { Filter = "Изображения|*.png;*.jpg;*.jpeg;*.gif", Title = "Эмодзи · до 256 КБ" }; if (f.ShowDialog(this) != true) return; if (new FileInfo(f.FileName).Length > 256 * 1024) throw new InvalidOperationException("Эмодзи должен быть меньше 256 КБ."); d.Close(); Run(async () => { await ImportAndSend(f.FileName, "emoji"); sounds.Play("message"); }); }); d.ShowDialog();
    }
    private Task SendPacket(WireMessage message) => rtc?.Joined == true ? rtc.SendAsync(message) : node.IsConnected ? node.SendAsync(message) : Task.CompletedTask;
    private async Task BroadcastAsset(MediaAsset a) { if (node.IsConnected || rtc?.Joined == true) await SendPacket(new WireMessage("asset", profile.Id, null, JsonSerializer.SerializeToUtf8Bytes(new AssetTransfer(a.Id, a.Name, a.Kind, await File.ReadAllBytesAsync(a.LocalPath))))); }
    private void SoundPanel()
    {
        if (community is null) return; var d = new FormDialog(this, "Звуковая панель"); d.Note("Звуки слышат участники твоего голосового канала. WAV или MP3, максимум 2 МБ. Вне звонка доступно локальное прослушивание.");
        foreach (var a in store.GetAssets(community.Id, "sound")) d.Action($"▶ {a.Name}", () => Run(async () => { if (deafened) return; await BroadcastAsset(a); sounds.PlayFile(a.LocalPath); if (voiceChannelId is not null) await SendPacket(new WireMessage("sound", profile.Id, voiceChannelId, Encoding.UTF8.GetBytes(a.Id))); }));
        d.Action("+ Загрузить звук", () => { var f = new Microsoft.Win32.OpenFileDialog { Filter = "Аудио|*.wav;*.mp3", Title = "Звук · до 2 МБ" }; if (f.ShowDialog(this) != true) return; if (new FileInfo(f.FileName).Length > 2 * 1024 * 1024) throw new InvalidOperationException("Звук должен быть меньше 2 МБ."); d.Close(); Run(async () => { await ImportAndSend(f.FileName, "sound"); sounds.Play("message"); }); }); d.ShowDialog();
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
            if (isAvatar) avatar = destination; else banner = destination; sounds.Play("message");
        }
        d.Action("Звук · устройства", () => { d.Close(); EditAudioDevices(); });
        d.Action("Сеть · STUN", () => { d.Close(); EditNetworkSettings(); });
        d.Action("Выбрать аватар", () => Pick(true)); d.Action("Выбрать баннер", () => Pick(false)); d.Action("Удалить аватар и баннер", () => { avatar = null; banner = null; });
        d.Note("Аватар и баннер: до 4 МБ каждый. Изменения отправляются участникам активной сессии."); d.Accept();
        if (d.ShowDialog() != true) return; var proposed = profile with { Username = name.Text.Trim(), About = about.Text.Trim(), AvatarPath = avatar, BannerPath = banner, AvatarAssetId = null, BannerAssetId = null, AvatarRevision = avatar is null ? null : AvatarCatalog.Revision(avatar) }; store.SaveProfile(proposed); profile = proposed; if (community is not null) store.UpsertMember(community.Id, profile); Run(async () => { await PublishProfile(); RenderMembers(); UpdateStatus(); sounds.Play("message"); });
    }
    private void EditAudioDevices()
    {
        var dialog = new FormDialog(this, "Устройства связи", 600);
        dialog.Note("Устройства можно менять во время разговора. По умолчанию используются устройства связи Windows. Категория Communications запрашивает обработку голоса у системы; AEC/шумоподавление зависят от драйвера.");
        ComboBox DeviceList(string label, bool capture, string? selected)
        {
            dialog.Note(label);
            var devices = AudioDeviceSettings.List(capture);
            var box = new ComboBox { ItemsSource = devices, DisplayMemberPath = "Name", SelectedIndex = Math.Max(0, devices.ToList().FindIndex(device => device.Id == selected)), Margin = new Thickness(0, 4, 0, 12) };
            dialog.Add(box); return box;
        }
        var microphone = DeviceList("Микрофон", true, voice.Devices.InputId);
        var headphones = DeviceList("Голос участников", false, voice.Devices.OutputId);
        dialog.Note("Настройка выхода относится к голосовому разговору. Мультимедиа чата и звук демонстрации используют системный выход Windows.");
        dialog.Accept("Применить");
        if (dialog.ShowDialog() != true) return;
        var settings = new AudioDeviceSettings(((AudioDeviceChoice)microphone.SelectedItem).Id, ((AudioDeviceChoice)headphones.SelectedItem).Id);
        settings.Save(store.RootDirectory);
        Run(async () => { await voice.ConfigureDevicesAsync(settings); Notify("Аудиоустройства сохранены. Голосовой канал остаётся подключённым."); });
    }
    private void EditNetworkSettings()
    {
        var dialog = new FormDialog(this, "Прямое соединение · STUN", 600);
        string[] current;
        try { current = iceServers.Load().StunUrls; }
        catch (Exception ex) { Notify(ex.Message, true); current = FileIceServerProvider.Defaults; }
        dialog.Note("Один STUN URL на строку. Пустой список оставляет только локальные ICE-кандидаты. Адреса STUN и SDP раскрывают IP участников; содержимое разговора защищено DTLS/SRTP.");
        var urls = dialog.Field("STUN-серверы", string.Join(Environment.NewLine, current), true);
        dialog.Action("Сохранить", () =>
        {
            iceServers.Save(urls.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
            dialog.DialogResult = true;
        });
        if (dialog.ShowDialog() == true)
            Notify("STUN-настройки сохранены. Они будут применены при следующем входе или восстановлении пары.");
    }
    private async Task PublishProfile()
    {
        if (closing) return;
        if (cloud is not null && profile.AvatarPath is not null && profile.AvatarRevision is not null && await cloud.GetAsync<string>($"profiles/{profile.Id}/Images/avatar/Id") != profile.AvatarRevision)
            await cloud.PutAsync($"profiles/{profile.Id}/Images/avatar", new AssetTransfer(profile.AvatarRevision, Path.GetFileName(profile.AvatarPath), "avatar", await File.ReadAllBytesAsync(profile.AvatarPath)));
        if ((node.IsConnected || rtc?.Joined == true) && community is not null)
        {
            var capturedProfile = profile;
            var avatar = capturedProfile.AvatarPath is not null && File.Exists(capturedProfile.AvatarPath) ? await ImportAndSend(capturedProfile.AvatarPath, "avatar") : null;
            var banner = capturedProfile.BannerPath is not null && File.Exists(capturedProfile.BannerPath) ? await ImportAndSend(capturedProfile.BannerPath, "banner") : null;
            if (profile.Id == capturedProfile.Id && profile.AvatarPath == capturedProfile.AvatarPath && profile.BannerPath == capturedProfile.BannerPath) profile = profile with { AvatarAssetId = avatar?.Id, BannerAssetId = banner?.Id };
        }
        store.SaveProfile(profile); await node.UpdateProfileAsync(profile);
        if (cloud is not null) await cloud.SaveProfileAsync(profile);
    }
    private void ViewProfile(UserProfile p)
    {
        var d = new FormDialog(this, p.Username);
        string? banner = p.Id == profile.Id ? profile.BannerPath : p.BannerAssetId is not null ? store.GetAsset(p.BannerAssetId)?.LocalPath : null;
        string? avatar = avatars.Resolve(p.Id == profile.Id ? profile : p, store);
        if (banner is not null && File.Exists(banner)) { try { d.Add(new Image { Source = LoadImage(banner, 800), Height = 130, Stretch = Stretch.UniformToFill }); } catch { } }
        d.Add(AvatarCatalog.Circle(avatar, 80));
        d.Note(p.Game is null ? "Участник сообщества" : $"Играет в {p.Game}"); d.Note(p.About.Length == 0 ? "Пока ничего о себе не рассказал." : p.About);
        if (p.Id == profile.Id) d.Action("Редактировать профиль", () => { d.Close(); EditProfile(); });
        d.Note($"Идентификатор: {p.Id}"); d.Accept("Закрыть"); d.ShowDialog();
    }
    private static BitmapImage LoadImage(string path, int width) { using var stream = File.OpenRead(path); var b = new BitmapImage(); b.BeginInit(); b.CacheOption = BitmapCacheOption.OnLoad; b.DecodePixelWidth = width; b.StreamSource = stream; b.EndInit(); b.Freeze(); return b; }
    private async Task RefreshCloud()
    {
        if (cloud is null || community is null || cloudPolling || closing) return;
        cloudPolling = true; var id = community.Id; var channelId = selectedChannel?.Id;
        try
        {
            await cloud.PresenceAsync(id, profile, voiceChannelId ?? cloudChannelId);
            if (savedRefreshToken != cloud.RefreshToken) { SessionVault.Save(store.RootDirectory, "firebase:" + cloud.RefreshToken); savedRefreshToken = cloud.RefreshToken; }
            var snapshot = await cloud.GetAsync<CommunitySnapshot>($"communities/{id}/snapshot");
            if (snapshot is not null) store.SaveSnapshot(snapshot);
            var online = await cloud.GetAsync<Dictionary<string, FirebasePresence>>($"communities/{id}/presence") ?? new();
            var now = cloud.ServerNow().ToUnixTimeMilliseconds();
            if (community?.Id != id || closing) return;
            cloudPresence = online.Values.Where(p => now - p.SeenAt < 45000 && now >= p.SeenAt - 10000).Select(p => new PeerPresence(p.Profile, p.ChannelId, false, false)).ToList();
            foreach (var user in cloudPresence.Select(p => p.Profile)) { await avatars.FetchAsync(cloud, user); store.UpsertMember(id, user.Id == profile.Id ? profile : user); }
            RenderMembers(); RenderChannels(); UpdateStatus();
            if (channelId is not null)
            {
                var history = await cloud.MessagesAsync(id, channelId);
                foreach (var message in history.OrderBy(m => m.SentAt)) {
                    if (message.AttachmentId is not null && Regex.IsMatch(message.AttachmentId, "^[a-f0-9]{64}$") && store.GetAsset(message.AttachmentId) is null && !unavailableFiles.Contains(message.AttachmentId))
                    { if (await CloudFiles.DownloadAsync(cloud, store, id, message.AuthorId, message.AttachmentId) is null) unavailableFiles.Add(message.AttachmentId); }
                    await FetchEmojis(message, id); store.SaveMessage(message); if (selectedChannel?.Id == channelId) AddMessage(message); }
            }
        }
        finally { cloudPolling = false; }
    }
    private async Task FetchEmojis(ChatMessage message, string communityId)
    {
        if (cloud is null || message.Emojis is null) return;
        foreach (var id in message.Emojis.Values.Distinct().Take(64))
        {
            if (string.IsNullOrWhiteSpace(id) || !Regex.IsMatch(id, "^[a-f0-9]{64}$") || store.GetAsset(id) is not null) continue;
            var asset = await cloud.GetAsync<AssetTransfer>($"communities/{communityId}/emojis/{id}");
            if (asset is null && message.AuthorId == cloud.Profile?.Id)
                asset = await cloud.GetAsync<AssetTransfer>($"profiles/{message.AuthorId}/Images/{id}");
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
        sounds.Play("message");
    }
    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (closing) return;
        if (!exitRequested) { e.Cancel = true; foreach (var v in viewers.Values) v.Hide(); Hide(); return; }
        e.Cancel = true; closing = true; activityTimer.Stop(); cloudTimer.Stop(); receiveLifetime.Cancel(); incoming.Writer.TryComplete(); latestFrames.Clear();
        try { if (recorder.IsRecording) { var path = await recorder.StopAsync(); File.Delete(path); } if (recordedPath is not null) File.Delete(recordedPath); recorder.Dispose(); if (cloud is not null && community is not null) { try { await cloud.DeleteAsync($"communities/{community.Id}/presence/{profile.Id}"); if (node.IsHost) await cloud.DeleteAsync($"communities/{community.Id}/endpoint"); } catch { } } StopVoice(); if (rtc is not null) await rtc.DisposeAsync(); await node.DisposeAsync(); tray.Visible = false; tray.Icon?.Dispose(); tray.Dispose(); voice.Dispose(); screen.Dispose(); sounds.Dispose(); shareAudio.Dispose(); sharePlayback.Dispose(); }
        catch { } finally { _ = Dispatcher.BeginInvoke(new Action(Close)); }
    }
}
