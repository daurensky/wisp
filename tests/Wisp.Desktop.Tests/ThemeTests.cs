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
                Assert.True(Typography.HasOnest);Assert.Equal("Onest",Typography.FamilyName);

                using var store = new LocalStore(root);
                SessionVault.Save(root, "test-session-secret");
                Assert.Equal("test-session-secret", SessionVault.Read(root));
                Assert.DoesNotContain("test-session-secret", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(root, "session.bin"))));
                SessionVault.Clear(root);
                Assert.Null(SessionVault.Read(root));
                derived = new DerivedWindow();
                auth = new AuthWindow(store);
                AssertWindowColors(derived, Color.FromRgb(16, 19, 25), Color.FromRgb(242, 243, 247));
                AssertWindowColors(auth, Color.FromRgb(16, 19, 25), Color.FromRgb(242, 243, 247));
                CheckChatAndOverlays(store, root, value => phase = value);
                var password = Assert.Single(Descendants(auth).OfType<PasswordBox>());
                Assert.Equal(Color.FromRgb(242, 243, 247), BrushColor(password.Foreground));
                Assert.Equal(Color.FromRgb(32, 39, 53), BrushColor(password.Background));

                ThemeManager.Toggle();
                Assert.True(ThemeManager.IsLight);
                Assert.Equal("light", File.ReadAllText(Path.Combine(root, "theme.txt")).Trim());
                AssertWindowColors(derived, Color.FromRgb(245, 246, 250), Color.FromRgb(23, 32, 51));
                AssertWindowColors(auth, Color.FromRgb(245, 246, 250), Color.FromRgb(23, 32, 51));
                Assert.Equal(Color.FromRgb(23, 32, 51), BrushColor(password.Foreground));
                auth.Show(); PumpLayout(auth); SaveSnapshot(auth, "auth-light"); auth.Hide();

                var fresh = Path.Combine(root, "fresh");
                Directory.CreateDirectory(fresh);
                ThemeManager.Initialize(fresh);
                Assert.False(ThemeManager.IsLight);
                AssertWindowColors(derived, Color.FromRgb(16, 19, 25), Color.FromRgb(242, 243, 247));
                ThemeManager.Initialize(root);
                Assert.True(ThemeManager.IsLight);
                AssertWindowColors(auth, Color.FromRgb(245, 246, 250), Color.FromRgb(23, 32, 51));

                File.WriteAllText(Path.Combine(root, "theme.txt"), "invalid-theme");
                ThemeManager.Initialize(root);
                Assert.False(ThemeManager.IsLight);
                AssertWindowColors(auth, Color.FromRgb(16, 19, 25), Color.FromRgb(242, 243, 247));
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
        Assert.Equal(Color.FromRgb(32, 39, 53), BrushColor(tip.Background));
        Assert.Equal(Color.FromRgb(242, 243, 247), BrushColor(tip.Foreground));
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
            CheckCaptionButtons(main);
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
            phase("navigation during call and redesigned screens");CheckNavigationAndScreens(main,store,profile,community,gif);
            var inputButton = Descendants(main).OfType<Button>().First(button => Equals(System.Windows.Automation.AutomationProperties.GetName(button), "Настройки Wisp"));
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
            Assert.Contains(Descendants(main).OfType<TextBlock>(), text => text.Text == "SFU · Подключение");
            main.UpdateLayout();
            Assert.Contains(Descendants(main).OfType<VoiceParticipantRow>(), row => System.Windows.Automation.AutomationProperties.GetName(row).StartsWith(profile.Username));
            SaveSnapshot(main, "voice-connecting");
            var leaveTask = (Task)typeof(MainWindow).GetMethod("LeaveVoice", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(main, null)!;
            PumpLayout(main);
            Assert.True(joinTask.IsCompletedSuccessfully); Assert.True(leaveTask.IsCompletedSuccessfully);
            Assert.DoesNotContain(Descendants(main).OfType<TextBlock>(), text => text.Text == "SFU · Подключение");
            using (var exclusive = new FileStream(emoji.LocalPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Assert.True(exclusive.Length > 0);
            Assert.Equal(Visibility.Collapsed, Descendants(main).OfType<Button>().Single(button => System.Windows.Automation.AutomationProperties.GetName(button) == "Отменить запись").Visibility);
            phase("responsive layout"); main.Width = Ui.Metric("Width.WindowMinimum"); PumpLayout(main); SaveSnapshot(main, "main-compact-dark");
            var lists = VisualDescendants(main).OfType<VirtualRows>().ToArray();
            Assert.Equal(5, lists.Length); Assert.Equal(3, lists.Count(list => list.IsVisible));
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
        var communityField=typeof(MainWindow).GetField("voiceCommunity",flags)!;
        var failedField=typeof(MainWindow).GetField("activeVoiceFailed",flags)!;
        var oldRoom = roomField.GetValue(main); var oldChannel = channelField.GetValue(main);var oldVoiceCommunity=communityField.GetValue(main);var oldFailed=failedField.GetValue(main);
        var info = new SfuConnectionInfo("wss://89.126.195.20:8443/rtc", ServerConnectionState.Connected, 42, DateTimeOffset.UtcNow, "host/host");
        var room = new TestVoiceRoom(info, channelId);
        try
        {
            roomField.SetValue(main, room); channelField.SetValue(main, channelId);communityField.SetValue(main,typeof(MainWindow).GetField("community",flags)!.GetValue(main));failedField.SetValue(main,false);
            var text = (TextBlock)typeof(MainWindow).GetField("connection", flags)!.GetValue(main)!;
            void Update(double? ping, ServerConnectionState state, string brush)
            {
                room.ConnectionInfo = info with { PingMilliseconds = ping, State = state };
                typeof(MainWindow).GetMethod("UpdateStatus", flags)!.Invoke(main, null);
                Assert.Equal(BrushColor((Brush)Application.Current.FindResource(brush)), BrushColor(text.Foreground));
            }
            Update(42, ServerConnectionState.Connected, "Success"); Assert.Contains("SFU · Подключено · 42 мс", text.Text);
            Update(150, ServerConnectionState.Connected, "Warning");
            Update(250, ServerConnectionState.Connected, "Warning");
            Update(null, ServerConnectionState.Unreachable, "Warning"); Assert.Contains("пинг —", text.Text);
            Update(null, ServerConnectionState.Disconnected, "TextSecondary");Assert.Equal("Не подключён",text.Text);
            failedField.SetValue(main,true);Update(null, ServerConnectionState.Disconnected, "Danger"); Assert.Contains("потеряно", text.Text);
            failedField.SetValue(main,false);
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
        finally { roomField.SetValue(main, oldRoom); channelField.SetValue(main, oldChannel);communityField.SetValue(main,oldVoiceCommunity);failedField.SetValue(main,oldFailed);typeof(MainWindow).GetMethod("UpdateStatus", flags)!.Invoke(main, null); }
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
        communityField.SetValue(main,typeof(MainWindow).GetField("community",flags)!.GetValue(main));
        typeof(MainWindow).GetMethod("BeginReconnect",flags)!.Invoke(main,new object[]{communityId,target});
        var intent=(VoiceJoinIntent)typeof(MainWindow).GetField("voiceIntent",flags)!.GetValue(main)!;
        Assert.True(intent.Connecting);
        var leaving=(Task)typeof(MainWindow).GetMethod("LeaveVoice",flags)!.Invoke(main,null)!;
        PumpLayout(main);Assert.True(leaving.IsCompletedSuccessfully);
        Assert.False(intent.Connecting);Assert.Null(intent.ChannelId);
        Assert.Null(typeof(MainWindow).GetField("reconnect",flags)!.GetValue(main));
        var status=(TextBlock)typeof(MainWindow).GetField("connection",flags)!.GetValue(main)!;
        Assert.Equal("Не подключён",status.Text);Assert.Equal(BrushColor((Brush)Application.Current.FindResource("TextSecondary")),BrushColor(status.Foreground));
    }
    private static void CheckCaptionButtons(MainWindow main)
    {
        var buttons=VisualDescendants(main).OfType<Button>().Where(b=>b.Name is "PART_Minimize" or "PART_Maximize" or "PART_Close").ToArray();
        Assert.Equal(3,buttons.Length);
        foreach(var button in buttons)
        {
            button.ApplyTemplate();
            var surface=Assert.IsType<Border>(button.Template.FindName("CaptionSurface",button));
            Assert.Equal(new CornerRadius(0),surface.CornerRadius);
            Assert.True(System.Windows.Shell.WindowChrome.GetIsHitTestVisibleInChrome(button));
            Assert.Equal(Ui.Metric("Size.TitleBar"),button.Height);
        }
        var maximize=buttons.Single(b=>b.Name=="PART_Maximize");
        Assert.Equal("square",Assert.IsType<Icon>(maximize.Content).Kind);
        var size=new Size(main.Width,main.Height);
        maximize.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(main,()=>main.WindowState==WindowState.Maximized,"maximize caption button");
        Assert.Equal("square",Assert.IsType<Icon>(maximize.Content).Kind);
        Assert.Equal("Восстановить",maximize.ToolTip);
        maximize.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(main,()=>main.WindowState==WindowState.Normal,"restore caption button");
        Assert.Equal("square",Assert.IsType<Icon>(maximize.Content).Kind);
        Assert.Equal("Развернуть",maximize.ToolTip);
        Assert.InRange(main.Width,size.Width-1,size.Width+1);Assert.InRange(main.Height,size.Height-1,size.Height+1);
    }

    private static void CheckNavigationAndScreens(MainWindow main,LocalStore store,UserProfile profile,Community original,string gif)
    {
        const System.Reflection.BindingFlags flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
        var type=typeof(MainWindow);
        object? Get(string name)=>type.GetField(name,flags)!.GetValue(main);
        void Set(string name,object? value)=>type.GetField(name,flags)!.SetValue(main,value);
        void Invoke(string name,params object[] arguments)=>type.GetMethod(name,flags)!.Invoke(main,arguments);
        var originalRoom=Get("rtc");var originalVoice=Get("voiceCommunity");var originalChannel=Get("voiceChannelId");
        var originalSharing=Get("sharing");var originalFailed=Get("activeVoiceFailed");var originalSelected=Get("selectedChannel");
        var voiceChannel=store.GetChannels(original.Id).First(c=>c.IsVoice);
        var info=new SfuConnectionInfo("wss://89.126.195.20:8443/rtc",ServerConnectionState.Connected,28,DateTimeOffset.UtcNow,"host/host");
        var room=new TestVoiceRoom(info,voiceChannel.Id)
        {
            Participants=new[]{new RtcSeat(profile.Id,new string('a',32),DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())}
        };
        var second=store.CreateCommunity("Other community",profile.Id);
        var secondText=store.GetChannels(second.Id).First(c=>!c.IsVoice);
        try
        {
            Set("rtc",room);Set("voiceCommunity",original);Set("voiceChannelId",voiceChannel.Id);Set("activeVoiceFailed",false);Set("sharing",true);
            Invoke("UpdateStatus");Invoke("RenderServers");PumpLayout(main);
            var originalSource=(Wisp.Desktop.Media.ScreenShareService)Get("screen")!;
            var originalGeneration=Get("shareGeneration");
            var rail=(VirtualRows)Get("servers")!;
            var otherButton=Assert.Single(Descendants(rail).OfType<Button>(),b=>System.Windows.Automation.AutomationProperties.GetName(b)==second.Name);
            otherButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));PumpLayout(main);
            Assert.Equal(second.Id,Assert.IsType<Community>(Get("community")).Id);
            Assert.Same(room,Get("rtc"));Assert.Same(original,Get("voiceCommunity"));Assert.Equal(voiceChannel.Id,Get("voiceChannelId"));
            Assert.True(Assert.IsType<bool>(Get("sharing")));Assert.Equal(originalGeneration,Get("shareGeneration"));Assert.Same(originalSource,Get("screen"));
            Assert.Equal(0,room.EnterCalls);Assert.Equal(0,room.LeaveCalls);
            Invoke("SelectChannel",secondText);PumpLayout(main);
            Assert.Equal(secondText.Id,Assert.IsType<Channel>(Get("selectedChannel")).Id);
            Assert.Equal(voiceChannel.Id,Get("voiceChannelId"));Assert.Equal(0,room.LeaveCalls);Assert.Equal(0,room.EnterCalls);
            var status=(TextBlock)Get("connection")!;
            Assert.Contains(original.Name,status.Text);Assert.Contains(voiceChannel.Name,status.Text);Assert.DoesNotContain(second.Name,status.Text);
            var callPanel=(Border)Get("callPanel")!;
            // The Windows runner may physically clamp restored windows to its
            // display. First verify the actual viewport, then exercise both
            // breakpoints explicitly without assuming a particular monitor.
            var memberPanel=(Border)Get("memberPanel")!;
            var callColumn=(ColumnDefinition)Get("callColumn")!;
            var memberColumn=(ColumnDefinition)Get("memberColumn")!;
            Assert.Equal(main.ActualWidth>=Ui.Metric("Width.CallBreakpoint"),callPanel.IsVisible);
            Assert.Contains(Descendants(callPanel).OfType<TextBlock>(),t=>t.Text==original.Name);
            SaveSnapshot(main,"community-during-call-dark");
            Invoke("UpdateResponsivePanels",1360d);main.UpdateLayout();
            Assert.True(callPanel.IsVisible);Assert.True(memberPanel.IsVisible);
            Assert.Equal(260d,callColumn.Width.Value);Assert.Equal(220d,memberColumn.Width.Value);
            Invoke("UpdateResponsivePanels",1100d);main.UpdateLayout();
            Assert.False(callPanel.IsVisible);Assert.True(memberPanel.IsVisible);
            Assert.Equal(0d,callColumn.Width.Value);Assert.Equal(220d,memberColumn.Width.Value);
            Invoke("UpdateResponsivePanels",1040d);main.UpdateLayout();
            Assert.False(callPanel.IsVisible);Assert.False(memberPanel.IsVisible);
            Assert.Equal(0d,callColumn.Width.Value);Assert.Equal(0d,memberColumn.Width.Value);
            Invoke("UpdateResponsivePanels",new object[]{null!});main.UpdateLayout();
            main.Width=1100;PumpLayout(main);Assert.False(callPanel.IsVisible);
            Invoke("OpenCallPanel");PumpLayout(main);
            Assert.Contains(Descendants(main).OfType<TextBlock>(),t=>t.Text==original.Name);
            SaveSnapshot(main,"call-modal-dark");
            var closeCall=Assert.Single(Descendants(main).OfType<Button>(),b=>System.Windows.Automation.AutomationProperties.GetName(b)=="Закрыть · Esc");
            closeCall.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));PumpLayout(main);
            main.Width=Ui.Metric("Width.Window");Invoke("SetCommunity",original);Invoke("SelectChannel",voiceChannel);PumpLayout(main);
            var channelRows=(VirtualRows)Get("channels")!;
            var voiceButton=Assert.Single(Descendants(channelRows).OfType<Button>(),b=>System.Windows.Automation.AutomationProperties.GetName(b)==voiceChannel.Name);
            Assert.NotEqual(BrushColor((Brush)Application.Current.FindResource("Selected")),BrushColor(voiceButton.Background));
            var voiceRow=Assert.Single(channelRows.Items.Cast<UiRow>(),row=>row.Key==voiceChannel.Id);
            var voiceContainer=Assert.IsType<ListBoxItem>(channelRows.ItemContainerGenerator.ContainerFromItem(voiceRow));
            voiceContainer.IsSelected=true;voiceContainer.ApplyTemplate();main.UpdateLayout();
            Assert.Equal(Colors.Transparent,BrushColor(Assert.IsType<Border>(voiceContainer.Template.FindName("Item",voiceContainer)).Background));
            voiceContainer.IsSelected=false;
            Invoke("ShowHome");PumpLayout(main);
            Assert.True(((ScrollViewer)Get("homeWorkspace")!).IsVisible);Assert.Equal(Visibility.Collapsed,((DockPanel)Get("chatWorkspace")!).Visibility);
            Assert.False(Assert.IsType<Button>(Get("inviteButton")).IsEnabled);
            Assert.False(Assert.IsType<Button>(Get("createChannelButton")).IsEnabled);
            var globalActions=Descendants(main).OfType<Button>().Where(button=>System.Windows.Automation.AutomationProperties.GetName(button) is "Создать сообщество" or "Присоединиться по приглашению").ToArray();
            Assert.NotEmpty(globalActions);Assert.All(globalActions,button=>Assert.True(button.IsEnabled));
            Assert.Equal(voiceChannel.Id,Get("voiceChannelId"));Assert.Equal(0,room.LeaveCalls);Assert.True((bool)Get("sharing")!);
            SaveSnapshot(main,"home-dark");
            CheckSettingsAndProfile(main,profile,gif);
        }
        finally
        {
            Set("sharing",originalSharing);Set("rtc",originalRoom);Set("voiceCommunity",originalVoice);Set("voiceChannelId",originalChannel);Set("activeVoiceFailed",originalFailed);
            main.Width=Ui.Metric("Width.Window");Invoke("SetCommunity",original);
            if(originalSelected is Channel selected)Invoke("SelectChannel",selected);
            Invoke("UpdateStatus");PumpLayout(main);
        }
    }

    private static void CheckSettingsAndProfile(MainWindow main,UserProfile profile,string gif)
    {
        var wasReduced=Motion.Reduce;
        try
        {
            Motion.SetReduced(false);
            var sample=profile with{About="Native Windows client",Game="Dota 2",AvatarPath=gif,BannerPath=gif};
            var profileView=new ProfileView(sample,gif,gif);
            var profileHandle=ModalHost.Open(main,"Профиль пользователя",profileView,480);PumpLayout(main);
            Assert.Contains(Descendants(profileView).OfType<TextBlock>(),t=>t.Text==sample.About);
            Assert.Contains(Descendants(profileView).OfType<TextBlock>(),t=>t.Text.Contains("Dota 2"));
            var animated=Descendants(profileView).OfType<Image>().Where(i=>AnimationBehavior.GetSourceStream(i)is not null).ToArray();
            Assert.Equal(2,animated.Length);Assert.All(animated,i=>Assert.Equal(GifPlaybackMode.Always,GifPlayback.GetMode(i)));
            Assert.All(animated,i=>Assert.Equal(RepeatBehavior.Forever,AnimationBehavior.GetRepeatBehavior(i)));
            SaveSnapshot(main,"profile-redesign-dark");profileHandle.Close();PumpLayout(main);
            Wisp.Desktop.Media.AudioDeviceSettings? applied=null;bool? notify=null;float volume=0;
            var settings=new SettingsView(new SettingsContext
            {
                Profile=sample,AvatarPath=gif,BannerPath=gif,Audio=new(),NoiseSuppressionAvailable=true,
                ApplyAudio=value=>{applied=value;return Task.CompletedTask;},EditProfile=()=>{},ShowNotifications=()=>{},ConnectionDetails=()=>{},ShareScreen=()=>{},
                InputDevices=()=>new[]{new Wisp.Desktop.Media.AudioDeviceChoice(null,"Default input"),new("input-device","Test microphone")},
                OutputDevices=()=>new[]{new Wisp.Desktop.Media.AudioDeviceChoice(null,"Default output"),new("output-device","Test headphones")},
                ApplyNotifications=(enabled,level)=>{notify=enabled;volume=level;},ServerAddress="https://example.com:8443",ConnectionStatus=()=>"Не подключён"
            });
            var handle=ModalHost.Open(main,"Настройки",settings,880);PumpLayout(main);
            settings.SelectSection(SettingsSection.Appearance);PumpLayout(main);
            var themes=Assert.Single(Descendants(settings).OfType<ComboBox>());
            themes.SelectedIndex=1;Assert.True(ThemeManager.IsLight);
            themes.SelectedIndex=0;Assert.False(ThemeManager.IsLight);
            var reduce=Assert.Single(Descendants(settings).OfType<CheckBox>());
            reduce.IsChecked=true;Assert.True(Motion.Reduce);
            settings.SelectSection(SettingsSection.Audio);PumpLayout(main);
            var devices=Descendants(settings).OfType<ComboBox>().ToArray();Assert.Equal(2,devices.Length);
            devices[0].SelectedIndex=1;devices[1].SelectedIndex=1;PumpLayout(main);
            Assert.Contains(VisualDescendants(devices[0]).OfType<TextBlock>(),t=>t.Text=="Test microphone");
            Assert.Contains(VisualDescendants(devices[1]).OfType<TextBlock>(),t=>t.Text=="Test headphones");
            Assert.DoesNotContain(VisualDescendants(settings).OfType<TextBlock>(),t=>t.Text.Contains("AudioDeviceChoice"));
            var suppress=Assert.Single(Descendants(settings).OfType<CheckBox>());suppress.IsChecked=false;
            var apply=Assert.Single(Descendants(settings).OfType<Button>(),b=>System.Windows.Automation.AutomationProperties.GetName(b)=="Применить");
            apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));PumpLayout(main);
            Assert.NotNull(applied);Assert.Equal("input-device",applied.InputId);Assert.Equal("output-device",applied.OutputId);Assert.False(applied.NoiseSuppression);
            SaveSnapshot(main,"settings-audio-dark");
            settings.SelectSection(SettingsSection.Notifications);PumpLayout(main);
            var slider=Assert.Single(Descendants(settings).OfType<Slider>());slider.Value=25;
            slider.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left){RoutedEvent=Mouse.PreviewMouseUpEvent});
            var enabled=Assert.Single(Descendants(settings).OfType<CheckBox>());enabled.IsChecked=false;
            Assert.False(notify);Assert.Equal(.25f,volume);
            SaveSnapshot(main,"settings-notifications-dark");
            settings.SelectSection(SettingsSection.Connection);PumpLayout(main);
            Assert.Contains(Descendants(settings).OfType<TextBlock>(),t=>t.Text=="https://example.com:8443");
            handle.Close();PumpLayout(main);
        }
        finally {if(ThemeManager.IsLight)ThemeManager.Toggle();Motion.SetReduced(wasReduced);}
    }

    private static void PumpUntil(Window window,Func<bool> condition,string scenario)
    {
        if(condition()){PumpLayout(window);return;}
        var frame=new DispatcherFrame();var deadline=DateTime.UtcNow.AddSeconds(2);
        var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(10)};
        timer.Tick+=(_,_)=>{if(condition()||DateTime.UtcNow>=deadline){timer.Stop();frame.Continue=false;}};
        timer.Start();Dispatcher.PushFrame(frame);PumpLayout(window);Assert.True(condition(),scenario);
    }
    private sealed class TestVoiceRoom(SfuConnectionInfo info, string channel) : IVoiceRoom
    {
        public SfuConnectionInfo ConnectionInfo { get; set; } = info;
        public bool Joined => ConnectionInfo.State is ServerConnectionState.Connected or ServerConnectionState.Unreachable;
        public string? ChannelId => Joined ? channel : null;
        public string TransportName => "WebRTC · SFU";
        public IReadOnlyList<RtcSeat> Participants {get;set;} = Array.Empty<RtcSeat>();
        public int EnterCalls {get;private set;}
        public int LeaveCalls {get;private set;}
        public int ConnectedCount => 0;
        public IReadOnlyList<string> ConnectedPeerIds => Array.Empty<string>();
        public IReadOnlyDictionary<string,string> ConnectionPaths => new Dictionary<string,string>();
        public event Action<WireMessage>? MessageReceived { add {} remove {} }
        public event Action<string>? Error { add {} remove {} }
        public event Action? Changed { add {} remove {} }
        public Task EnterAsync(string communityId,string channelId,bool mute,bool deafen,CancellationToken cancellation=default){EnterCalls++;return Task.CompletedTask;}
        public Task SetMutedAsync(bool mute,bool deafen)=>Task.CompletedTask;
        public Task SendAsync(WireMessage message)=>Task.CompletedTask;
        public Task LeaveAsync(){LeaveCalls++;return Task.CompletedTask;}
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
