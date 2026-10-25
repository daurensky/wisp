using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Wisp.Core;
using Wisp.Desktop.Media;
using Button = System.Windows.Controls.Button;
using Image = System.Windows.Controls.Image;

namespace Wisp.Desktop;

public sealed class MainWindow : Window
{
    private readonly LocalStore store;
    private UserProfile profile;
    private readonly PeerNode node;
    private readonly VoiceService voice = new();
    private readonly VoiceRecorder recorder = new();
    private readonly ScreenShareService screen = new();
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
    private readonly Button micButton, deafenButton, recordButton, shareButton;
    private readonly Dictionary<string, (Window Window, Image Image)> viewers = new();
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
    private bool muted, deafened, sharing, closing, light;
    private string? recordedPath;
    private bool sendingRecording;
    private IReadOnlyList<PeerPresence> presence = Array.Empty<PeerPresence>();

    public MainWindow(LocalStore store, UserProfile profile)
    {
        this.store = store; this.profile = profile; node = new PeerNode(profile, store.RootDirectory);
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
        var controls = new WrapPanel(); micButton = Btn("Микр.", ToggleMic, "Выключить / включить микрофон"); deafenButton = Btn("Звук", ToggleDeafen, "Выключить наушники и микрофон"); controls.Children.Add(micButton); controls.Children.Add(deafenButton); controls.Children.Add(Btn("⚙", EditProfile, "Профиль и настройки")); self.Children.Add(controls); self.Children.Add(Btn("Отключиться", Disconnect)); DockPanel.SetDock(self, Dock.Bottom); side.Children.Add(self); side.Children.Add(new ScrollViewer { Content = channels, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var center = new DockPanel(); Grid.SetColumn(center, 2); root.Children.Add(center);
        var top = new DockPanel { Margin = new Thickness(22, 18, 18, 12) }; top.Children.Add(channelTitle);
        var toolbar = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right }; toolbar.Children.Add(Btn("Эмодзи", EmojiPanel)); toolbar.Children.Add(Btn("Звуки", SoundPanel)); shareButton = Btn("Демонстрация", ShareScreen); toolbar.Children.Add(shareButton); top.Children.Add(toolbar); DockPanel.SetDock(top, Dock.Top); center.Children.Add(top);
        DockPanel.SetDock(notice, Dock.Top); center.Children.Add(notice);
        var input = new StackPanel { Margin = new Thickness(18, 6, 18, 18) }; var composeRow = new Grid(); composeRow.ColumnDefinitions.Add(new ColumnDefinition()); composeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); composeRow.Children.Add(composer); var send = Btn("Отправить", SendMessage); Grid.SetColumn(send, 1); composeRow.Children.Add(send); input.Children.Add(composeRow);
        var attach = new WrapPanel(); attach.Children.Add(Btn("+ Файл", AttachFile)); recordButton = Btn("● Голосовое", RecordVoice); attach.Children.Add(recordButton); attach.Children.Add(Btn("Отменить запись", CancelRecording)); attach.Children.Add(new TextBlock { Text = "Enter — отправить · Shift+Enter — новая строка", FontSize = 10, Opacity = .5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) }); input.Children.Add(attach); DockPanel.SetDock(input, Dock.Bottom); center.Children.Add(input);
        messageScroll = new ScrollViewer { Content = messages, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(22, 0, 22, 0) }; center.Children.Add(messageScroll);
        var right = new DockPanel { Margin = new Thickness(16, 22, 12, 12) }; right.SetResourceReference(Panel.BackgroundProperty, "Panel"); Grid.SetColumn(right, 3); root.Children.Add(right);
        var memberHeader = new TextBlock { Text = "УЧАСТНИКИ", FontSize = 11, Opacity = .6, Margin = new Thickness(8, 0, 0, 14) }; DockPanel.SetDock(memberHeader, Dock.Top); right.Children.Add(memberHeader); right.Children.Add(new ScrollViewer { Content = members, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        composer.KeyDown += (_, e) => { if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { e.Handled = true; Run(SendMessage); } };
        _ = Task.Run(ProcessIncoming);
        node.MessageReceived += EnqueueIncoming;
        node.PresenceChanged += p => Dispatcher.BeginInvoke(() =>
        {
            if (closing) return; var hasNew = p.Any(x => x.Profile.Id != profile.Id && !presence.Any(old => old.Profile.Id == x.Profile.Id)); presence = p;
            RenderMembers(); RenderChannels(); if (voiceChannelId is not null && !p.Any(x => x.Profile.Id == profile.Id && x.VoiceChannelId == voiceChannelId)) StopVoice();
            foreach (var v in viewers.Keys.Where(id => !p.Any(x => x.Profile.Id == id && x.VoiceChannelId == voiceChannelId)).ToList()) if (viewers.Remove(v, out var gone)) gone.Window.Close();
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
        voice.PacketReady += p => SendRealtime("audio", p); voice.Error += e => Dispatcher.BeginInvoke(() => Notify(e, true));
        screen.FrameReady += p => SendRealtime("screen", p); screen.Error += e => Dispatcher.BeginInvoke(() => { StopSharing(); Notify(e, true); });
        recorder.Completed += path => Dispatcher.BeginInvoke(() => { if (closing || sendingRecording || !File.Exists(path)) return; recordedPath = path; recordButton.Content = "↑ Отправить запись"; Notify("Запись завершена. Нажми «Отправить запись»."); });
        activityTimer.Tick += (_, _) => Run(UpdateGame); activityTimer.Start();
        Closing += OnClosing;
        userName.Text = profile.Username; RenderServers();
        var first = store.GetCommunities().FirstOrDefault(); if (first is not null) SetCommunity(first); else { serverTitle.Text = "Добро пожаловать"; channelTitle.Text = "Место для своих"; Notify("Создай сообщество кнопкой + слева или присоединись по приглашению ↗."); }
        UpdateStatus();
        try { if (File.Exists(Path.Combine(store.RootDirectory, "light-theme"))) ToggleTheme(); } catch { }
    }

    private void EnqueueIncoming(WireMessage m)
    {
        if (closing) return;
        if (m.Type == "audio") { if (voiceChannelId == m.ChannelId) voice.Receive(m.SenderId, m.Payload); return; }
        if (m.Type == "screen")
        {
            if (voiceChannelId != m.ChannelId || m.Payload.Length > 2 * 1024 * 1024) return;
            latestFrames[m.SenderId] = m;
            if (Interlocked.Exchange(ref frameScheduled, 1) == 0) Dispatcher.BeginInvoke(() =>
            {
                Interlocked.Exchange(ref frameScheduled, 0);
                foreach (var id in latestFrames.Keys) if (latestFrames.TryRemove(id, out var frame) && !closing && frame.ChannelId == voiceChannelId) ShowFrame(id, frame.Payload);
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
    private void Notify(string text, bool error = false) { notice.Text = text; notice.Foreground = error ? Brushes.Salmon : (Brush)FindResource("Muted"); if (error) sounds.Play("error"); }
    private void RenderServers()
    {
        servers.Children.Clear(); foreach (var c in store.GetCommunities())
        {
            var b = Btn(c.Name[..Math.Min(2, c.Name.Length)].ToUpperInvariant(), async () => { if (node.IsConnected && c.Id != sessionCommunityId) await Disconnect(); SetCommunity(c); }, c.Name);
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
                var count = presence.Count(p => p.VoiceChannelId == c.Id);
                var b = Btn(voiceKind ? $"♫  {c.Name}     {count}/5" : $"#  {c.Name}", async () => { SelectChannel(c); if (c.IsVoice) await JoinVoice(c); });
                b.HorizontalContentAlignment = HorizontalAlignment.Left; if (c.Id == selectedChannel?.Id) b.SetResourceReference(Control.BackgroundProperty, "Card"); channels.Children.Add(b);
                if (voiceKind) foreach (var p in presence.Where(p => p.VoiceChannelId == c.Id)) channels.Children.Add(new TextBlock { Text = $"    ● {p.Profile.Username}{(p.Deafened ? " · без звука" : p.Muted ? " · микр. выкл." : "")}", FontSize = 12, Margin = new Thickness(18, 2, 0, 4), Opacity = .7 });
            }
        }
    }
    private void SelectChannel(Channel c) { if (recorder.IsRecording || recordedPath is not null) throw new InvalidOperationException("Сначала отправь голосовую запись."); selectedChannel = c; channelTitle.Text = $"{(c.IsVoice ? "♫" : "#")} {c.Name}"; pendingAttachment = null; messages.Children.Clear(); foreach (var m in store.GetMessages(c.Id)) AddMessage(m); RenderChannels(); Notify(c.IsVoice ? "Голосовой канал · максимум 5 участников. Для связи нужна активная P2P-сессия." : "Сообщения и вложения сохраняются локально. Подключённые участники получают новые сообщения."); }
    private void RenderMembers()
    {
        members.Children.Clear(); if (community is null) return;
        var saved = store.GetMembers(community.Id).ToDictionary(x => x.Id); saved[profile.Id] = profile;
        foreach (var p in presence) saved[p.Profile.Id] = p.Profile;
        foreach (var p in saved.Values.OrderByDescending(x => x.Id == profile.Id || presence.Any(s => s.Profile.Id == x.Id)).ThenBy(x => x.Username))
        {
            var online = p.Id == profile.Id || presence.Any(s => s.Profile.Id == p.Id); var display = new StackPanel(); display.Children.Add(new TextBlock { Text = $"{(online ? "●" : "○")}  {p.Username}", Opacity = online ? 1 : .45 });
            var game = presence.FirstOrDefault(x => x.Profile.Id == p.Id)?.Profile.Game ?? (p.Id == profile.Id ? profile.Game : null); display.Children.Add(new TextBlock { Text = game is null ? online ? "В сети" : "Не в сети" : $"Играет в {game}", FontSize = 10, Opacity = .5, Margin = new Thickness(18, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
            var b = Btn("", () => ViewProfile(p)); b.Content = display; b.HorizontalContentAlignment = HorizontalAlignment.Stretch; members.Children.Add(b);
        }
    }
    private void UpdateStatus() { connection.Text = voiceChannelId is not null ? "● Голос подключён · P2P / Opus" : node.IsConnected ? "● P2P-сессия подключена" : "○ Локальный режим"; userName.Text = profile.Username; micButton.Content = muted || deafened ? "Микр. ×" : "Микр. ✓"; deafenButton.Content = deafened ? "Звук ×" : "Звук ✓"; }
    private void CreateCommunity()
    {
        var d = new FormDialog(this, "Создать сообщество"); var name = d.Field("Название"); d.Accept("Создать"); if (d.ShowDialog() != true) return;
        if (node.IsConnected) { Notify("Сначала отключись от текущей сессии.", true); return; } SetCommunity(store.CreateCommunity(name.Text, profile.Id)); sounds.Play("connect");
    }
    private async Task HostCommunity()
    {
        if (community is null) return;
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
        var d = new FormDialog(this, "Присоединиться"); var invite = d.Field("Приглашение wisp://", "", true); d.Note("Нужен прямой доступ к компьютеру друга: локальная сеть, VPN или открытый порт. Отпечаток сертификата проверяется автоматически."); d.Accept("Подключиться"); if (d.ShowDialog() != true) return;
        IsEnabled = false;
        try { await Disconnect(); await node.JoinAsync(invite.Text.Trim()); sounds.Play("connect"); UpdateStatus(); await Dispatcher.InvokeAsync(() => { }); await PublishProfile(); }
        finally { if (!closing) IsEnabled = true; }
    }
    private void CreateChannel()
    {
        if (community is null) return; if (community.OwnerId != profile.Id) throw new InvalidOperationException("Создавать каналы может владелец сообщества.");
        if (node.IsConnected) throw new InvalidOperationException("Для изменения каналов останови сессию, затем открой её снова с новым приглашением.");
        var d = new FormDialog(this, "Новый канал"); var name = d.Field("Название"); var isVoice = d.Check("Голосовой канал · максимум 5 человек"); d.Accept("Создать"); if (d.ShowDialog() == true) { var c = store.CreateChannel(community.Id, name.Text, isVoice.IsChecked == true); SelectChannel(c); sounds.Play("message"); }
    }
    private async Task JoinVoice(Channel c)
    {
        if (!node.IsConnected) throw new InvalidOperationException("Открой сессию кнопкой «Пригласить» или подключись к другу.");
        if (c.Id == voiceChannelId) return;
        if (recorder.IsRecording) throw new InvalidOperationException("Сначала закончи голосовую запись.");
        if (await node.SetVoiceChannelAsync(c.Id, muted, deafened))
        {
            try { StopSharing(); voice.Stop(); voice.Start(); voice.Muted = muted; voice.Deafened = deafened; voiceChannelId = c.Id; sounds.Play("connect"); UpdateStatus(); }
            catch { StopVoice(); await node.SetVoiceChannelAsync(null, muted, deafened); throw; }
        }
        else Notify("Канал заполнен: максимум 5 человек.", true);
    }
    private void StopVoice() { StopSharing(); voice.Stop(); voiceChannelId = null; foreach (var v in viewers.Values.ToList()) v.Window.Close(); viewers.Clear(); UpdateStatus(); }
    private async Task Disconnect() { Interlocked.Increment(ref sessionEpoch); latestFrames.Clear(); StopVoice(); await node.DisconnectAsync(); presence = Array.Empty<PeerPresence>(); sessionCommunityId = null; currentInvite = null; RenderMembers(); RenderChannels(); UpdateStatus(); sounds.Play("disconnect"); }
    private async Task ToggleMic() { muted = !muted; voice.Muted = muted; if (node.IsConnected) await node.SetVoiceChannelAsync(voiceChannelId, muted, deafened); sounds.Play(muted ? "mute" : "unmute"); UpdateStatus(); }
    private async Task ToggleDeafen() { deafened = !deafened; voice.Deafened = deafened; if (deafened) sounds.StopFile(); if (node.IsConnected) await node.SetVoiceChannelAsync(voiceChannelId, muted, deafened); sounds.Play(deafened ? "deafen" : "undeafen"); UpdateStatus(); }
    private async Task UpdateGame()
    {
        var game = GamePresenceService.DetectGame(); if (game == profile.Game) return; profile = profile with { Game = game }; store.SaveProfile(profile); await node.UpdateProfileAsync(profile); RenderMembers();
    }
    private async Task SendMessage()
    {
        if (selectedChannel is null || (string.IsNullOrWhiteSpace(composer.Text) && pendingAttachment is null)) return;
        var m = new ChatMessage(Guid.NewGuid().ToString("N"), selectedChannel.Id, profile.Id, profile.Username, composer.Text.Trim(), DateTimeOffset.UtcNow, pendingAttachment);
        if (node.IsConnected) await node.SendAsync(new WireMessage("chat", profile.Id, selectedChannel.Id, JsonSerializer.SerializeToUtf8Bytes(m)));
        store.SaveMessage(m); AddMessage(m); composer.Clear(); pendingAttachment = null; sounds.Play("message"); Notify(node.IsConnected ? "Отправлено участникам сессии." : "Сохранено локально. Для отправки друзьям нужна P2P-сессия.");
    }
    private void AddMessage(ChatMessage m)
    {
        var row = new StackPanel { Margin = new Thickness(0, 8, 0, 12) }; var meta = new TextBlock { Text = $"{m.AuthorName}   {m.SentAt.LocalDateTime:dd.MM HH:mm}", FontWeight = FontWeights.SemiBold, FontSize = 12 }; meta.SetResourceReference(TextBlock.ForegroundProperty, "Accent"); row.Children.Add(meta);
        if (!string.IsNullOrEmpty(m.Text)) row.Children.Add(new TextBlock { Text = m.Text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0) });
        if (m.AttachmentId is not null)
        {
            var asset = store.GetAsset(m.AttachmentId); if (asset?.CommunityId != community?.Id) asset = null;
            if (asset is not null)
            {
                if (asset.Kind == "emoji" || new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp" }.Contains(Path.GetExtension(asset.Name).ToLowerInvariant()))
                {
                    try { row.Children.Add(new Image { Source = LoadImage(asset.LocalPath, 600), MaxHeight = asset.Kind == "emoji" ? 72 : 260, MaxWidth = 500, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) }); } catch { }
                }
                row.Children.Add(Btn(asset.Kind == "voice" ? "▶ Голосовое сообщение" : $"↓ {asset.Name} · {asset.Size / 1024} КБ", () => OpenAsset(asset)));
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
        try { if (node.IsConnected) await BroadcastAsset(asset); return asset; } finally { if (!closing) IsEnabled = true; }
    }
    private async Task AttachFile()
    {
        if (selectedChannel is null) return; var pick = new Microsoft.Win32.OpenFileDialog { Title = "Прикрепить медиа или файл · до 16 МБ" }; if (pick.ShowDialog(this) != true) return;
        var a = await ImportAndSend(pick.FileName, "attachment"); if (a is not null) { pendingAttachment = a.Id; Notify($"Прикреплён {a.Name}. Нажми «Отправить»."); sounds.Play("message"); }
    }
    private async Task RecordVoice()
    {
        if (selectedChannel is null) return;
        if (!recorder.IsRecording && recordedPath is null)
        {
            if (voiceChannelId is not null) throw new InvalidOperationException("Для голосового сообщения сначала отключись от звонка.");
            var path = Path.Combine(store.RootDirectory, $"recording-{Guid.NewGuid():N}.wav"); recorder.Start(path); recordButton.Content = "■ Отправить запись"; Notify("Запись микрофона · максимум 60 секунд. Нажми ещё раз, чтобы отправить."); sounds.Play("connect");
        }
        else
        {
            sendingRecording = true;
            try { string path = recordedPath ?? await recorder.StopAsync(); recordedPath = null; recordButton.Content = "● Голосовое";
                try { var a = await ImportAndSend(path, "voice"); if (a is not null) { pendingAttachment = a.Id; await SendMessage(); } } finally { File.Delete(path); }
            } finally { sendingRecording = false; }
        }
    }
    private async Task CancelRecording()
    {
        if (!recorder.IsRecording && recordedPath is null) return;
        sendingRecording = true;
        try { var path = recordedPath ?? await recorder.StopAsync(); recordedPath = null; File.Delete(path); recordButton.Content = "● Голосовое"; Notify("Запись удалена."); sounds.Play("disconnect"); }
        finally { sendingRecording = false; }
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
            case "audio": if (voiceChannelId == m.ChannelId) voice.Receive(m.SenderId, m.Payload); break;
            case "screen": if (voiceChannelId == m.ChannelId) ShowFrame(m.SenderId, m.Payload); break;
            case "screen-stop": if (viewers.Remove(m.SenderId, out var viewer)) viewer.Window.Close(); break;
            case "asset":
                if (community is null) return; var communityId = community.Id; var incoming = JsonSerializer.Deserialize<AssetTransfer>(m.Payload); if (incoming is null || incoming.Data.Length > 16 * 1024 * 1024 || incoming.Name.Length > 200 || incoming.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || incoming.Name != Path.GetFileName(incoming.Name) || incoming.Name is "." or "..") return;
                using (var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256))
                {
                    hash.AppendData(Encoding.UTF8.GetBytes(communityId + "\0" + incoming.Kind + "\0")); hash.AppendData(incoming.Data);
                    if (!string.Equals(Convert.ToHexString(hash.GetHashAndReset()), incoming.Id, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Неверный идентификатор вложения.");
                }
                var dir = Path.Combine(store.RootDirectory, "incoming", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir); var temporary = Path.Combine(dir, incoming.Name);
                try { await File.WriteAllBytesAsync(temporary, incoming.Data); var a = store.ImportAsset(communityId, temporary, incoming.Kind); if (a.Id != incoming.Id) Notify("Вложение отклонено: неверный идентификатор.", true); } finally { Directory.Delete(dir, true); } break;
            case "sound":
                if (deafened || voiceChannelId != m.ChannelId) return; var sound = store.GetAsset(Encoding.UTF8.GetString(m.Payload)); if (sound?.Kind == "sound" && sound.CommunityId == community?.Id) sounds.PlayFile(sound.LocalPath); break;
        }
    }
    private void SendRealtime(string type, byte[] payload) { if (voiceChannelId is null || !node.IsConnected) return; _ = SendRealtimeSafe(type, voiceChannelId, payload); }
    private async Task SendRealtimeSafe(string type, string channel, byte[] data) { try { await node.SendAsync(new WireMessage(type, profile.Id, channel, data)); } catch { /* Disconnect/presence events surface connection failure. */ } }
    private void ShareScreen()
    {
        if (sharing) { StopSharing(); return; } if (voiceChannelId is null) throw new InvalidOperationException("Сначала подключись к голосовому каналу.");
        var d = new FormDialog(this, "Демонстрация экрана"); d.Note("Выбери отдельный монитор или окно приложения. До 1280×720, 5 кадров/с для снижения нагрузки. Захватывается изображение без системного звука.");
        CaptureSource? selected = null; foreach (var source in ScreenShareService.GetSources()) d.Action(source.Name, () => { selected = source; d.DialogResult = true; });
        if (d.ShowDialog() != true || selected is null) return; screen.Start(selected); sharing = true; shareButton.Content = "Остановить показ"; sounds.Play("share"); Notify($"Идёт демонстрация: {selected.Name}");
    }
    private void StopSharing() { if (!sharing) return; screen.Stop(); sharing = false; shareButton.Content = "Демонстрация"; SendRealtime("screen-stop", Array.Empty<byte>()); sounds.Play("share"); }
    private void ShowFrame(string peerId, byte[] jpeg)
    {
        if (jpeg.Length > 2 * 1024 * 1024) return;
        try
        {
            using var stream = new MemoryStream(jpeg); var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.DecodePixelWidth = 1280; image.StreamSource = stream; image.EndInit(); image.Freeze();
            if (!viewers.TryGetValue(peerId, out var view))
            {
                var target = new Image { Stretch = Stretch.Uniform }; var name = presence.FirstOrDefault(x => x.Profile.Id == peerId)?.Profile.Username ?? "Участник"; var window = new Window { Owner = this, Title = $"{name} · Демонстрация", Width = 1000, Height = 640, Content = target }; view = (window, target); viewers[peerId] = view; window.Closed += (_, _) => viewers.Remove(peerId); window.Show();
            }
            view.Image.Source = image;
        }
        catch { /* A malformed or incomplete image is discarded. */ }
    }
    private void EmojiPanel()
    {
        if (community is null) return; var d = new FormDialog(this, "Эмодзи сообщества"); d.Note("Выбери эмодзи — он будет отправлен в текущий текстовый чат.");
        foreach (var a in store.GetAssets(community.Id, "emoji")) d.Action($":{Path.GetFileNameWithoutExtension(a.Name)}:", () => { pendingAttachment = a.Id; d.Close(); Run(async () => { await BroadcastAsset(a); await SendMessage(); }); });
        d.Action("+ Загрузить эмодзи", () => { var f = new Microsoft.Win32.OpenFileDialog { Filter = "Изображения|*.png;*.jpg;*.jpeg;*.gif", Title = "Эмодзи · до 256 КБ" }; if (f.ShowDialog(d) != true) return; if (new FileInfo(f.FileName).Length > 256 * 1024) throw new InvalidOperationException("Эмодзи должен быть меньше 256 КБ."); d.Close(); Run(async () => { await ImportAndSend(f.FileName, "emoji"); sounds.Play("message"); }); }); d.ShowDialog();
    }
    private async Task BroadcastAsset(MediaAsset a) { if (node.IsConnected) await node.SendAsync(new WireMessage("asset", profile.Id, null, JsonSerializer.SerializeToUtf8Bytes(new AssetTransfer(a.Id, a.Name, a.Kind, await File.ReadAllBytesAsync(a.LocalPath))))); }
    private void SoundPanel()
    {
        if (community is null) return; var d = new FormDialog(this, "Звуковая панель"); d.Note("Звуки слышат участники твоего голосового канала. WAV или MP3, максимум 2 МБ. Вне звонка доступно локальное прослушивание.");
        foreach (var a in store.GetAssets(community.Id, "sound")) d.Action($"▶ {a.Name}", () => Run(async () => { if (deafened) return; await BroadcastAsset(a); sounds.PlayFile(a.LocalPath); if (voiceChannelId is not null) await node.SendAsync(new WireMessage("sound", profile.Id, voiceChannelId, Encoding.UTF8.GetBytes(a.Id))); }));
        d.Action("+ Загрузить звук", () => { var f = new Microsoft.Win32.OpenFileDialog { Filter = "Аудио|*.wav;*.mp3", Title = "Звук · до 2 МБ" }; if (f.ShowDialog(d) != true) return; if (new FileInfo(f.FileName).Length > 2 * 1024 * 1024) throw new InvalidOperationException("Звук должен быть меньше 2 МБ."); d.Close(); Run(async () => { await ImportAndSend(f.FileName, "sound"); sounds.Play("message"); }); }); d.ShowDialog();
    }
    private void EditProfile()
    {
        var d = new FormDialog(this, "Мой профиль"); var name = d.Field("Имя пользователя", profile.Username); var about = d.Field("О себе", profile.About, true); var avatar = profile.AvatarPath; var banner = profile.BannerPath;
        void Pick(bool isAvatar)
        {
            var f = new Microsoft.Win32.OpenFileDialog { Filter = "Изображения|*.png;*.jpg;*.jpeg;*.gif" }; if (f.ShowDialog(d) != true) return;
            if (new FileInfo(f.FileName).Length > 4 * 1024 * 1024) throw new InvalidOperationException("Изображение профиля должно быть меньше 4 МБ.");
            _ = LoadImage(f.FileName, 1280); var directory = Path.Combine(store.RootDirectory, "profile-images"); Directory.CreateDirectory(directory);
            var destination = Path.Combine(directory, Guid.NewGuid().ToString("N") + Path.GetExtension(f.FileName)); File.Copy(f.FileName, destination);
            if (isAvatar) avatar = destination; else banner = destination; sounds.Play("message");
        }
        d.Action("Выбрать аватар", () => Pick(true)); d.Action("Выбрать баннер", () => Pick(false)); d.Action("Удалить аватар и баннер", () => { avatar = null; banner = null; });
        d.Note("Аватар и баннер: до 4 МБ каждый. Изменения отправляются участникам активной сессии."); d.Accept();
        if (d.ShowDialog() != true) return; var proposed = profile with { Username = name.Text.Trim(), About = about.Text.Trim(), AvatarPath = avatar, BannerPath = banner, AvatarAssetId = null, BannerAssetId = null }; store.SaveProfile(proposed); profile = proposed; if (community is not null) store.UpsertMember(community.Id, profile); Run(async () => { await PublishProfile(); RenderMembers(); UpdateStatus(); sounds.Play("message"); });
    }
    private async Task PublishProfile()
    {
        if (closing) return;
        if (node.IsConnected && community is not null)
        {
            var capturedProfile = profile;
            var avatar = capturedProfile.AvatarPath is not null && File.Exists(capturedProfile.AvatarPath) ? await ImportAndSend(capturedProfile.AvatarPath, "avatar") : null;
            var banner = capturedProfile.BannerPath is not null && File.Exists(capturedProfile.BannerPath) ? await ImportAndSend(capturedProfile.BannerPath, "banner") : null;
            if (profile.Id == capturedProfile.Id && profile.AvatarPath == capturedProfile.AvatarPath && profile.BannerPath == capturedProfile.BannerPath) profile = profile with { AvatarAssetId = avatar?.Id, BannerAssetId = banner?.Id };
        }
        await node.UpdateProfileAsync(profile);
    }
    private void ViewProfile(UserProfile p)
    {
        var d = new FormDialog(this, p.Username);
        string? banner = p.Id == profile.Id ? profile.BannerPath : p.BannerAssetId is not null ? store.GetAsset(p.BannerAssetId)?.LocalPath : null;
        string? avatar = p.Id == profile.Id ? profile.AvatarPath : p.AvatarAssetId is not null ? store.GetAsset(p.AvatarAssetId)?.LocalPath : null;
        if (banner is not null && File.Exists(banner)) { try { d.Add(new Image { Source = LoadImage(banner, 800), Height = 130, Stretch = Stretch.UniformToFill }); } catch { } }
        if (avatar is not null && File.Exists(avatar)) { try { d.Add(new Image { Source = LoadImage(avatar, 160), Height = 80, Width = 80, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 12), Stretch = Stretch.UniformToFill }); } catch { } }
        d.Note(p.Game is null ? "Участник сообщества" : $"Играет в {p.Game}"); d.Note(p.About.Length == 0 ? "Пока ничего о себе не рассказал." : p.About);
        if (p.Id == profile.Id) d.Action("Редактировать профиль", () => { d.Close(); EditProfile(); });
        d.Note($"Идентификатор: {p.Id}"); d.Accept("Закрыть"); d.ShowDialog();
    }
    private static BitmapImage LoadImage(string path, int width) { using var stream = File.OpenRead(path); var b = new BitmapImage(); b.BeginInit(); b.CacheOption = BitmapCacheOption.OnLoad; b.DecodePixelWidth = width; b.StreamSource = stream; b.EndInit(); b.Freeze(); return b; }
    private void ToggleTheme()
    {
        light = !light; var colors = light ? new[] { "#FFFFFF", "#F1F2F6", "#E2E3EC", "#181923", "#626577", "#6350DA" } : new[] { "#000000", "#101114", "#1C1D23", "#EDEDF2", "#9899AA", "#8B78FF" }; var keys = new[] { "Bg", "Panel", "Card", "Fg", "Muted", "Accent" };
        for (int i = 0; i < keys.Length; i++) System.Windows.Application.Current.Resources[keys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
        var setting = Path.Combine(store.RootDirectory, "light-theme"); if (light) File.WriteAllText(setting, "1"); else File.Delete(setting); sounds.Play("message");
    }
    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (closing) return; e.Cancel = true; closing = true; activityTimer.Stop(); receiveLifetime.Cancel(); incoming.Writer.TryComplete(); latestFrames.Clear();
        try { if (recorder.IsRecording) { var path = await recorder.StopAsync(); File.Delete(path); } if (recordedPath is not null) File.Delete(recordedPath); recorder.Dispose(); StopVoice(); await node.DisposeAsync(); voice.Dispose(); screen.Dispose(); sounds.Dispose(); }
        catch { } finally { Close(); }
    }
}
