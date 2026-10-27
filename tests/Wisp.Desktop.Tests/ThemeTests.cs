using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Documents;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using XamlAnimatedGif;
using Wisp.Core;
using Wisp.Desktop;
using Xunit;

namespace Wisp.Desktop.Tests;

public sealed class ThemeTests
{
    [Fact]
    public void DerivedWindowsStartDarkAndFollowPersistedThemeChanges()
    {
        Exception? failure = null;
        string phase = "initialization";
        var thread = new Thread(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "wisp-theme-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Application? app = null;
            Window? derived = null;
            AuthWindow? auth = null;
            try
            {
                app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Wisp;component/Theme.xaml") });
                // An old version's marker must not override the new default.
                File.WriteAllText(Path.Combine(root, "light-theme"), "1");
                ThemeManager.Initialize(root);
                Motion.SetReduced(true);
                Assert.False(ThemeManager.IsLight);

                using var store = new LocalStore(root);
                SessionVault.Save(root, "test-session-secret");
                Assert.Equal("test-session-secret", SessionVault.Read(root));
                Assert.DoesNotContain("test-session-secret", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(root, "session.bin"))));
                SessionVault.Clear(root);
                Assert.Null(SessionVault.Read(root));
                derived = new DerivedWindow();
                auth = new AuthWindow(store);
                AssertWindowColors(derived, Colors.Black, Color.FromRgb(237, 237, 242));
                AssertWindowColors(auth, Colors.Black, Color.FromRgb(237, 237, 242));
                CheckChatAndOverlays(store, root, value => phase = value);
                var password = Assert.Single(Descendants(auth).OfType<PasswordBox>());
                Assert.Equal(Color.FromRgb(237, 237, 242), BrushColor(password.Foreground));
                Assert.Equal(Color.FromRgb(28, 29, 35), BrushColor(password.Background));

                ThemeManager.Toggle();
                Assert.True(ThemeManager.IsLight);
                Assert.Equal("light", File.ReadAllText(Path.Combine(root, "theme.txt")).Trim());
                AssertWindowColors(derived, Colors.White, Color.FromRgb(24, 25, 35));
                AssertWindowColors(auth, Colors.White, Color.FromRgb(24, 25, 35));
                Assert.Equal(Color.FromRgb(24, 25, 35), BrushColor(password.Foreground));
                auth.Show(); PumpLayout(auth); SaveSnapshot(auth, "auth-light"); auth.Hide();

                var fresh = Path.Combine(root, "fresh");
                Directory.CreateDirectory(fresh);
                ThemeManager.Initialize(fresh);
                Assert.False(ThemeManager.IsLight);
                AssertWindowColors(derived, Colors.Black, Color.FromRgb(237, 237, 242));
                ThemeManager.Initialize(root);
                Assert.True(ThemeManager.IsLight);
                AssertWindowColors(auth, Colors.White, Color.FromRgb(24, 25, 35));

                File.WriteAllText(Path.Combine(root, "theme.txt"), "invalid-theme");
                ThemeManager.Initialize(root);
                Assert.False(ThemeManager.IsLight);
                AssertWindowColors(auth, Colors.Black, Color.FromRgb(237, 237, 242));
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                try
                {
                    derived?.Close();
                    auth?.Close();
                    app?.Shutdown();
                    if (Directory.Exists(root)) Directory.Delete(root, true);
                }
                catch (Exception ex) { failure ??= ex; }
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The Windows UI check did not finish at: " + phase);
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void AssertWindowColors(Window window, Color background, Color foreground)
    {
        Assert.Equal(background, BrushColor(window.Background));
        Assert.Equal(foreground, BrushColor(window.Foreground));
    }

    private static void CheckChatAndOverlays(LocalStore store, string root, Action<string> phase)
    {
        phase("tooltip and avatar");
        Assert.True(ComposerInput.ShouldSend(Key.Enter, ModifierKeys.None));
        Assert.False(ComposerInput.ShouldSend(Key.Enter, ModifierKeys.Shift));
        var tip = new ToolTip { Content = "Подсказка" };
        Assert.Equal(Color.FromRgb(28, 29, 35), BrushColor(tip.Background));
        Assert.Equal(Color.FromRgb(237, 237, 242), BrushColor(tip.Foreground));
        var avatar = Assert.IsType<Image>(AvatarCatalog.Circle(null, 36));
        var frozen = new ScaleTransform(1, 1); frozen.Freeze();
        var frozenProbe = new Border { RenderTransform = frozen };
        Motion.Stop(frozenProbe);
        Assert.True(frozen.IsFrozen);
        var iconResources = Application.Current.Resources.MergedDictionaries[0].MergedDictionaries.Single(dictionary => dictionary.Source.OriginalString.EndsWith("Icons.xaml"));
        foreach (var key in iconResources.Keys.Cast<string>())
        {
            var geometry = Assert.IsAssignableFrom<Geometry>(Application.Current.FindResource(key));
            Assert.True(geometry.IsFrozen);
            Assert.InRange(geometry.Bounds.Left, -.01, 24.01); Assert.InRange(geometry.Bounds.Top, -.01, 24.01);
            Assert.InRange(geometry.Bounds.Right, -.01, 24.01); Assert.InRange(geometry.Bounds.Bottom, -.01, 24.01);
        }
        Assert.IsType<EllipseGeometry>(avatar.Clip); Assert.NotNull(avatar.Source);
        var gif = Path.Combine(root, "loop.gif");
        File.WriteAllBytes(gif, Convert.FromBase64String("R0lGODlhAgACAIEAAP8AAAAAAAAAAAAAACH/C05FVFNDQVBFMi4wAwEBAAAh+QQACAAAACwAAAAAAgACAAAIBgABCAQQEAAh+QQBCAABACwAAAAAAgACAIEAAP8AAAAAAAAAAAAIBgABCAQQEAA7"));
        AvatarCatalog.Validate(gif);
        Assert.Equal(RepeatBehavior.Forever, AnimationBehavior.GetRepeatBehavior(AvatarCatalog.Image(gif, 36)));
        var wave = Path.Combine(root, "voice.wav");
        using (var writer = new BinaryWriter(File.Create(wave)))
        {
            writer.Write("RIFF"u8); writer.Write(96036); writer.Write("WAVEfmt "u8); writer.Write(16);
            writer.Write((short)1); writer.Write((short)1); writer.Write(24000); writer.Write(48000);
            writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(96000); writer.Write(new byte[96000]);
        }
        var errors = new List<string>();
        phase("voice seek");
        using (var player = new VoiceMessagePlayer(wave, errors.Add))
        {
            var slider = Assert.Single(Descendants(player).OfType<Slider>());
            Assert.Equal(2, slider.Maximum); slider.Value = 1;
            Assert.Contains(Descendants(player).OfType<TextBlock>(), text => text.Text == "0:01 / 0:02");
            Assert.Empty(errors);
        }
        var profile = store.Register("UiTester", "correct horse battery");
        var community = store.CreateCommunity("UI checks", profile.Id);
        var channel = store.GetChannels(community.Id).First(c => !c.IsVoice);
        var emoji = store.ImportAsset(community.Id, gif, "emoji");
        phase("main construction");
        var main = new MainWindow(store, profile);
        try
        {
            phase("main layout"); main.Show(); main.UpdateLayout();
            SaveSnapshot(main, "main-dark");
            Assert.NotNull(System.Windows.Shell.WindowChrome.GetWindowChrome(main));
            Assert.Contains(VisualDescendants(main).OfType<Button>(), button => button.Name == "PART_Close");
            phase("recycling");
            var virtualRows = new VirtualRows { Width = 240, Height = 160 };
            int created = 0;
            for (int i = 0; i < 1000; i++) virtualRows.Add(i.ToString(), () => { created++; return new TextBlock { Text = "Virtualized row", Height = Ui.RowHeight }; });
            var recycleOverlay = ModalHost.Open(main, "Виртуализация", virtualRows, 400); main.UpdateLayout();
            Assert.InRange(created, 1, 50);
            recycleOverlay.Close();
            Assert.True(VirtualizingPanel.GetIsVirtualizing(virtualRows));
            var toggle = new ToggleIconButton { IconOff = "mic", IconOn = "mic-off", ToolTipOff = "Выключить микрофон", ToolTipOn = "Включить микрофон" };
            toggle.IsChecked = true;
            Assert.Equal("Включить микрофон", System.Windows.Automation.AutomationProperties.GetName(toggle));
            var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(toggle);
            var provider = Assert.IsAssignableFrom<System.Windows.Automation.Provider.IToggleProvider>(peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Toggle));
            Assert.Equal(System.Windows.Automation.ToggleState.On, provider.ToggleState);
            toggle.Click += (_, _) => toggle.IsChecked = !toggle.IsChecked;
            provider.Toggle();
            Assert.Equal(System.Windows.Automation.ToggleState.Off, provider.ToggleState);
            Assert.Equal(TimeSpan.Zero, Motion.Duration(main).TimeSpan);
            var windowCount = Application.Current.Windows.Count;
            var notices = new NotificationArea(); Assert.True(notices.Add("Тест", true));
            for (int i = 0; i < 500; i++) Assert.False(notices.Add("Тест", true));
            Assert.Equal(1, notices.Count);
            phase("notification overlay"); var overlay = ModalHost.Open(main, "Уведомления", notices);
            Assert.Equal(windowCount, Application.Current.Windows.Count); overlay.Close();
            ModalHost.Open(main, "Уведомления", notices).Close(); // Reopening must detach from the old parent.
            phase("screen viewer");
            var videoView = new ScreenViewer(main, "Demo"); Assert.False(videoView.IsMini); Assert.Equal(windowCount, Application.Current.Windows.Count);
            videoView.ToggleMini(); Assert.True(videoView.IsMini); videoView.ToggleMini(); Assert.False(videoView.IsMini);
            videoView.ToggleFullscreen(); Assert.True(videoView.IsFullscreen); videoView.ToggleFullscreen(); Assert.False(videoView.IsFullscreen); videoView.Close();
            phase("form overlay"); var dialog = new FormDialog(main, "Профиль"); dialog.Field("Имя", "UiTester"); dialog.Accept();
            main.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                Assert.Equal(windowCount, Application.Current.Windows.Count);
                SaveSnapshot(main, "profile-modal-dark");
                Assert.Single(VisualDescendants(main).OfType<TextBlock>(), text => text.Text == "Профиль");
                dialog.DialogResult = true;
            }));
            Assert.True(dialog.ShowDialog());
            phase("live microphone setting");
            main.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                var suppression = Assert.Single(Descendants(main).OfType<CheckBox>(), box =>
                    box.Content?.ToString()?.StartsWith("Шумоподавление") == true);
                Assert.True(suppression.IsChecked);
                if (suppression.IsEnabled)
                {
                    suppression.IsChecked = false;
                    Assert.False(Wisp.Desktop.Media.AudioDeviceSettings.Load(root).NoiseSuppression);
                    suppression.IsChecked = true;
                    Assert.True(Wisp.Desktop.Media.AudioDeviceSettings.Load(root).NoiseSuppression);
                }
                var audioDialog = Assert.Single(Descendants(main).OfType<FormDialog>());
                audioDialog.Close();
            }));
            typeof(MainWindow).GetMethod("EditAudioDevices", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(main, null);
            Assert.Equal(windowCount, Application.Current.Windows.Count);
            phase("send on Enter"); var composer = Assert.Single(Descendants(main).OfType<TextBox>(), box => box.AcceptsReturn);
            composer.Text = "hello :loop: world";
            var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(main), 0, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            composer.RaiseEvent(key);
            Assert.True(key.Handled); Assert.Empty(composer.Text);
            PumpLayout(main);
            var message = Assert.Single(store.GetMessages(channel.Id));
            Assert.Equal(emoji.Id, message.Emojis![":loop:"]);
            var body = Assert.Single(Descendants(main).OfType<SelectableChatText>());
            Assert.Contains(body.Paragraph.Inlines, inline => inline is InlineUIContainer);
            Assert.True(body.IsReadOnly);
            var position = body.TranslatePoint(new Point(0, 0), main);
            body.Focus(); body.SelectAll(); PumpLayout(main);
            Assert.Contains("hello", body.Selection.Text); Assert.Contains("world", body.Selection.Text);
            ApplicationCommands.Copy.Execute(null, body);
            Assert.Contains("hello", Clipboard.GetText());
            Assert.Equal(position, body.TranslatePoint(new Point(0, 0), main));
            var chatEmoji = Assert.IsType<Image>(body.Paragraph.Inlines.OfType<InlineUIContainer>().Single().Child);
            Assert.Equal(GifPlaybackMode.Hover, GifPlayback.GetMode(chatEmoji));
            Assert.NotNull(GifPlayback.GetScope(chatEmoji));
            phase("connection status, brand and smooth scrolling"); CheckDesktopPolish(main, community.Id, store.GetChannels(community.Id).First(c => c.IsVoice).Id);
            var inputButton = Descendants(main).OfType<Button>().First(button => Equals(System.Windows.Automation.AutomationProperties.GetName(button), "Профиль и настройки"));
            inputButton.Focus(); PointerFocus.SetKeyboardFocus(inputButton, true); main.UpdateLayout();
            Assert.Equal(1, ((Border)inputButton.Template.FindName("FocusRing", inputButton)).Opacity);
            inputButton.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
            main.UpdateLayout(); Assert.Equal(0, ((Border)inputButton.Template.FindName("FocusRing", inputButton)).Opacity);
            var localVoice = new Wisp.Desktop.Media.VoiceService();
            var participant = new VoiceParticipantRow(new PeerPresence(profile, "voice", false, false), gif, localVoice, true);
            participant.IsSpeaking = true; Assert.True(GifPlayback.GetSpeaking(participant.Avatar));
            participant.IsSpeaking = false; Assert.False(GifPlayback.GetSpeaking(participant.Avatar));
            var mutedParticipant = new VoiceParticipantRow(new PeerPresence(profile, "voice", true, true), gif, localVoice, true);
            mutedParticipant.IsSpeaking = true; Assert.False(GifPlayback.GetSpeaking(mutedParticipant.Avatar));
            Assert.Equal(GifPlaybackMode.Speaking, GifPlayback.GetMode(participant.Avatar));
            Assert.Contains(Descendants(mutedParticipant).OfType<Icon>(), icon => icon.Kind == "mic-off");
            Assert.Contains(Descendants(mutedParticipant).OfType<Icon>(), icon => icon.Kind == "headphone-off");
            localVoice.Dispose();
            phase("optimistic voice entry and cancellation");
            var voiceChannel = store.GetChannels(community.Id).First(c => c.IsVoice);
            var joinTask = (Task)typeof(MainWindow).GetMethod("JoinVoice", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(main, new object[] { voiceChannel, false, CancellationToken.None })!;
            Assert.False(joinTask.IsCompleted);
            Assert.Contains(Descendants(main).OfType<TextBlock>(), text => text.Text == "Соединение");
            main.UpdateLayout();
            Assert.Contains(Descendants(main).OfType<VoiceParticipantRow>(), row => System.Windows.Automation.AutomationProperties.GetName(row).StartsWith(profile.Username));
            SaveSnapshot(main, "voice-connecting");
            var leaveTask = (Task)typeof(MainWindow).GetMethod("LeaveVoice", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(main, null)!;
            PumpLayout(main);
            Assert.True(joinTask.IsCompletedSuccessfully); Assert.True(leaveTask.IsCompletedSuccessfully);
            Assert.DoesNotContain(Descendants(main).OfType<TextBlock>(), text => text.Text == "Соединение");
            using (var exclusive = new FileStream(emoji.LocalPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Assert.True(exclusive.Length > 0);
            Assert.Equal(Visibility.Collapsed, Descendants(main).OfType<Button>().Single(button => System.Windows.Automation.AutomationProperties.GetName(button) == "Отменить запись").Visibility);
            phase("responsive layout"); main.Width = Ui.Metric("Width.WindowMinimum"); PumpLayout(main); SaveSnapshot(main, "main-compact-dark");
            var lists = VisualDescendants(main).OfType<VirtualRows>().ToArray();
            Assert.Equal(4, lists.Length); Assert.Equal(3, lists.Count(list => list.IsVisible));
            Assert.All(lists, list => Assert.Equal(Colors.Transparent, BrushColor(list.Background)));
            main.Width = Ui.Metric("Width.Window"); ThemeManager.Toggle(); PumpLayout(main); SaveSnapshot(main, "main-light"); ThemeManager.Toggle();
        }
        finally
        {
            phase("shutdown"); main.Quit();
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            var deadline = DateTime.UtcNow.AddSeconds(5);
            timer.Tick += (_, _) => { if (!main.IsVisible || DateTime.UtcNow >= deadline) { timer.Stop(); frame.Continue = false; } };
            timer.Start(); Dispatcher.PushFrame(frame);
            Assert.False(main.IsVisible, "Quit must finish after the initial Closing callback returns.");
        }
    }

    private static void CheckDesktopPolish(MainWindow main, string communityId, string channelId)
    {
        Assert.NotNull(main.Icon);
        Assert.Contains(VisualDescendants(main).OfType<Image>(), image => ReferenceEquals(image.Source, Brand.Logo));
        using (var icon = Brand.TrayIcon()) Assert.True(icon.Width >= 16 && icon.Handle != IntPtr.Zero);
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var roomField = typeof(MainWindow).GetField("rtc", flags)!;
        var channelField = typeof(MainWindow).GetField("voiceChannelId", flags)!;
        var oldRoom = roomField.GetValue(main); var oldChannel = channelField.GetValue(main);
        var info = new SfuConnectionInfo("wss://89.126.195.20:8443/rtc", ServerConnectionState.Connected, 42, DateTimeOffset.UtcNow, "host/host");
        var room = new TestVoiceRoom(info, channelId);
        try
        {
            roomField.SetValue(main, room); channelField.SetValue(main, channelId);
            var text = (TextBlock)typeof(MainWindow).GetField("connection", flags)!.GetValue(main)!;
            void Update(double? ping, ServerConnectionState state, string brush)
            {
                room.ConnectionInfo = info with { PingMilliseconds = ping, State = state };
                typeof(MainWindow).GetMethod("UpdateStatus", flags)!.Invoke(main, null);
                Assert.Equal(BrushColor((Brush)Application.Current.FindResource(brush)), BrushColor(text.Foreground));
            }
            Update(42, ServerConnectionState.Connected, "Success"); Assert.Contains("Подключён · SFU · 42 мс", text.Text);
            Update(150, ServerConnectionState.Connected, "Warning");
            Update(250, ServerConnectionState.Connected, "Danger");
            Update(null, ServerConnectionState.Unreachable, "Warning"); Assert.Contains("пинг —", text.Text);
            Update(null, ServerConnectionState.Disconnected, "Danger"); Assert.Contains("потеряно", text.Text);
            Update(42, ServerConnectionState.Connected, "Success");
            main.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                var details = Descendants(main).OfType<FormDialog>().Single();
                Assert.Equal("Подключение", details.Title);
                Assert.Contains(Descendants(details).OfType<TextBlock>(), label => label.Text.Contains("89.126.195.20"));
                Assert.Contains(Descendants(details).OfType<TextBlock>(), label => label.Text.Contains("RTT"));
                details.DialogResult = true;
            }));
            typeof(MainWindow).GetMethod("ConnectionDetails", flags)!.Invoke(main, null);
            SaveSnapshot(main, "sfu-connected");
        }
        finally { roomField.SetValue(main, oldRoom); channelField.SetValue(main, oldChannel); typeof(MainWindow).GetMethod("UpdateStatus", flags)!.Invoke(main, null); }
        var messages = (VirtualRows)typeof(MainWindow).GetField("messages", flags)!.GetValue(main)!;
        var item = Assert.IsType<ListBoxItem>(messages.ItemContainerGenerator.ContainerFromIndex(0));
        item.IsSelected = true; item.ApplyTemplate();
        Assert.Equal(Colors.Transparent, BrushColor(((Border)item.Template.FindName("Item", item)).Background));
        item.IsSelected = false;
        var rail=(VirtualRows)typeof(MainWindow).GetField("servers",flags)!.GetValue(main)!;
        var railItem=Assert.IsType<ListBoxItem>(rail.ItemContainerGenerator.ContainerFromIndex(0));railItem.ApplyTemplate();
        Assert.Equal(Colors.Transparent,BrushColor(((Border)railItem.Template.FindName("Item",railItem)).Background));
        double height=railItem.ActualHeight;
        railItem.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice,Environment.TickCount){RoutedEvent=Mouse.MouseEnterEvent});main.UpdateLayout();
        Assert.Equal(height,railItem.ActualHeight);
        using(var audio=new Wisp.Desktop.Media.VoiceService())
        {
            bool saved=false;
            var remote=new VoiceParticipantRow(new PeerPresence(new UserProfile("remote","RemoteUser"),channelId,false,false),null,audio,false,null,()=>saved=true);
            var userPopup=ModalHost.Open(main,"Участник",remote,400);PumpLayout(main);
            remote.ContextMenu.PlacementTarget=remote;remote.ContextMenu.IsOpen=true;PumpLayout(main);
            var panel=Assert.IsType<StackPanel>(Assert.IsType<MenuItem>(remote.ContextMenu.Items[1]).Header);
            var slider=Assert.Single(panel.Children.OfType<Slider>());slider.Value=25;Assert.Equal(.25f,audio.GetPeerVolume("remote"));
            remote.ContextMenu.IsOpen=false;PumpLayout(main);Assert.True(saved);userPopup.Close();
        }
        var content = new StackPanel(); for (int i=0;i<200;i++) content.Children.Add(new TextBlock { Text="Scroll row " + i, Height=24 });
        var view = new ScrollViewer { Content=content, Height=180, VerticalScrollBarVisibility=ScrollBarVisibility.Auto };
        var overlay = ModalHost.Open(main, "Прокрутка", view, 420); PumpLayout(main);
        Assert.True(SmoothScroll.GetEnabled(view));
        Motion.SetReduced(false);
        var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent=Mouse.PreviewMouseWheelEvent };
        view.RaiseEvent(wheel); Assert.True(wheel.Handled);
        main.UpdateLayout(); PumpLayout(main);
        Assert.InRange(view.VerticalOffset, 1, 200); // movement, bounded independently of Windows' wheel-line preference
        Motion.SetReduced(true); overlay.Close();
        var target=new Channel(channelId,communityId,"Reconnect test",true);
        typeof(MainWindow).GetMethod("BeginReconnect",flags)!.Invoke(main,new object[]{communityId,target});
        var intent=(VoiceJoinIntent)typeof(MainWindow).GetField("voiceIntent",flags)!.GetValue(main)!;
        Assert.True(intent.Connecting);
        var leaving=(Task)typeof(MainWindow).GetMethod("LeaveVoice",flags)!.Invoke(main,null)!;
        PumpLayout(main);Assert.True(leaving.IsCompletedSuccessfully);
        Assert.False(intent.Connecting);Assert.Null(intent.ChannelId);
        Assert.Null(typeof(MainWindow).GetField("reconnect",flags)!.GetValue(main));
    }
    private sealed class TestVoiceRoom(SfuConnectionInfo info, string channel) : IVoiceRoom
    {
        public SfuConnectionInfo ConnectionInfo { get; set; } = info;
        public bool Joined => ConnectionInfo.State is ServerConnectionState.Connected or ServerConnectionState.Unreachable;
        public string? ChannelId => Joined ? channel : null;
        public string TransportName => "WebRTC · SFU";
        public IReadOnlyList<RtcSeat> Participants => Array.Empty<RtcSeat>();
        public int ConnectedCount => 0;
        public IReadOnlyList<string> ConnectedPeerIds => Array.Empty<string>();
        public IReadOnlyDictionary<string,string> ConnectionPaths => new Dictionary<string,string>();
        public event Action<WireMessage>? MessageReceived { add {} remove {} }
        public event Action<string>? Error { add {} remove {} }
        public event Action? Changed { add {} remove {} }
        public Task EnterAsync(string communityId,string channelId,bool mute,bool deafen,CancellationToken cancellation=default)=>Task.CompletedTask;
        public Task SetMutedAsync(bool mute,bool deafen)=>Task.CompletedTask;
        public Task SendAsync(WireMessage message)=>Task.CompletedTask;
        public Task LeaveAsync()=>Task.CompletedTask;
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    private static Color BrushColor(Brush brush) => Assert.IsType<SolidColorBrush>(brush).Color;

    private static IEnumerable<DependencyObject> Descendants(DependencyObject element)
    {
        var seen = new HashSet<DependencyObject>();
        var queue = new Queue<DependencyObject>(); queue.Enqueue(element);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!seen.Add(current)) continue;
            if (current != element) yield return current;
            foreach (var child in LogicalTreeHelper.GetChildren(current).OfType<DependencyObject>()) queue.Enqueue(child);
            if (current is Visual or System.Windows.Media.Media3D.Visual3D)
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(current); i++) queue.Enqueue(VisualTreeHelper.GetChild(current, i));
        }
    }
    private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject element) => Descendants(element);
    private static void PumpLayout(Window window)
    {
        window.UpdateLayout();
        var frame = new DispatcherFrame();
        window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame); window.UpdateLayout();
    }
    private static void SaveSnapshot(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") is not { Length: > 0 } workspace) return;
        window.UpdateLayout();
        var target = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        target.Render(window);
        var directory = Path.Combine(workspace, "artifacts", "test-results", "ui-snapshots"); Directory.CreateDirectory(directory);
        using var output = File.Create(Path.Combine(directory, name + ".png"));
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(target)); encoder.Save(output);
    }

    private sealed class DerivedWindow : ThemedWindow { }
}
