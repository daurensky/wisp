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
                AssertWindowColors(derived, Color.FromRgb(16, 16, 16), Color.FromRgb(244, 244, 245));
                AssertWindowColors(auth, Color.FromRgb(16, 16, 16), Color.FromRgb(244, 244, 245));
                CheckChatAndOverlays(store, root, value => phase = value);
                var password = Assert.Single(Descendants(auth).OfType<PasswordBox>());
                Assert.Equal(Color.FromRgb(244, 244, 245), BrushColor(password.Foreground));
                Assert.Equal(Color.FromRgb(36, 36, 36), BrushColor(password.Background));

                ThemeManager.Toggle();
                Assert.True(ThemeManager.IsLight);
                Assert.Equal("light", File.ReadAllText(Path.Combine(root, "theme.txt")).Trim());
                AssertWindowColors(derived, Color.FromRgb(247, 247, 247), Color.FromRgb(24, 24, 27));
                AssertWindowColors(auth, Color.FromRgb(247, 247, 247), Color.FromRgb(24, 24, 27));
                Assert.Equal(Color.FromRgb(24, 24, 27), BrushColor(password.Foreground));
                auth.Show(); PumpLayout(auth); SaveSnapshot(auth, "auth-light"); auth.Hide();
                phase="server setup and server account screens";CheckServerAccountScreens(store,root);

                var fresh = Path.Combine(root, "fresh");
                Directory.CreateDirectory(fresh);
                ThemeManager.Initialize(fresh);
                Assert.False(ThemeManager.IsLight);
                AssertWindowColors(derived, Color.FromRgb(16, 16, 16), Color.FromRgb(244, 244, 245));
                ThemeManager.Initialize(root);
                Assert.True(ThemeManager.IsLight);
                AssertWindowColors(auth, Color.FromRgb(247, 247, 247), Color.FromRgb(24, 24, 27));

                File.WriteAllText(Path.Combine(root, "theme.txt"), "invalid-theme");
                ThemeManager.Initialize(root);
                Assert.False(ThemeManager.IsLight);
                AssertWindowColors(auth, Color.FromRgb(16, 16, 16), Color.FromRgb(244, 244, 245));
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(45)), "The Windows UI check did not finish at: " + phase);
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void CheckServerAccountScreens(LocalStore store,string root)
    {
        bool wasLight=ThemeManager.IsLight;
        if(wasLight)ThemeManager.Toggle();
        try
        {
            var config=new ServerConfig("https://example.com:8443",new string('a',64));
            using var backend=new ServerBackend(config);
            var login=new AuthWindow(store,backend);
            try
            {
                login.Show();PumpLayout(login);
                Assert.Contains(Descendants(login).OfType<TextBox>(),b=>System.Windows.Automation.AutomationProperties.GetName(b)=="Email");
                var username=Assert.Single(Descendants(login).OfType<TextBox>(),b=>System.Windows.Automation.AutomationProperties.GetName(b)=="Имя пользователя при регистрации");
                void CheckSubmitContrast()
                {
                    var submit=Assert.Single(Descendants(login).OfType<Button>(),b=>b.IsDefault);
                    var label=Assert.Single(Descendants(submit).OfType<TextBlock>());
                    Assert.Equal(BrushColor((Brush)Application.Current.FindResource("OnAccent")),BrushColor(label.Foreground));
                }
                CheckSubmitContrast();
                Assert.Equal(Visibility.Collapsed,username.Visibility);SaveSnapshot(login,"auth-server-dark");
                var mode=Assert.Single(Descendants(login).OfType<Button>(),b=>System.Windows.Automation.AutomationProperties.GetName(b)=="Нет аккаунта? Зарегистрироваться");
                mode.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));PumpLayout(login);
                Assert.Equal(Visibility.Visible,username.Visibility);Assert.Contains("Регистрация",login.Title);CheckSubmitContrast();SaveSnapshot(login,"signup-server-dark");
            }
            finally{login.Close();}
            var setup=new ServerSetupWindow(Path.Combine(root,"setup-preview"),config);
            try
            {
                setup.Show();PumpLayout(setup);
                var pin=Assert.Single(Descendants(setup).OfType<TextBox>(),b=>System.Windows.Automation.AutomationProperties.GetName(b)=="Публичный SHA-256 сертификата");
                Assert.Equal(config.CertificateSha256,pin.Text);SaveSnapshot(setup,"server-setup-dark");
                pin.Text="invalid";
                var next=Assert.Single(Descendants(setup).OfType<Button>(),b=>b.IsDefault);
                next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));PumpLayout(setup);
                Assert.Null(setup.Config);Assert.Contains(Descendants(setup).OfType<TextBlock>(),t=>t.IsVisible&&t.Text.Contains("64")&&BrushColor(t.Foreground)==BrushColor((Brush)Application.Current.FindResource("Danger")));
                Assert.False(File.Exists(Path.Combine(root,"setup-preview","server.json")));
            }
            finally{setup.Close();}
        }
        finally{if(ThemeManager.IsLight!=wasLight)ThemeManager.Toggle();}
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
        Assert.Equal(Color.FromRgb(36, 36, 36), BrushColor(tip.Background));
        Assert.Equal(Color.FromRgb(244, 244, 245), BrushColor(tip.Foreground));
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
        VoiceMessagePlayer player;
        using (var exclusive = new FileStream(wave, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            player = new VoiceMessagePlayer(wave, errors.Add);
            // Constructing an off-screen chat row must not open or decode its attachment.
            Assert.Empty(errors);
        }
        using (player)
        {
            var probe = new Window { Content = player, Width = 400, Height = 100, ShowInTaskbar = false };
            try
            {
                probe.Show();
                var slider = Assert.Single(Descendants(player).OfType<Slider>());
                PumpUntil(probe, () => slider.Maximum == 2, "Voice duration should arrive with the asynchronous waveform.");
                slider.Value = 1;
                Assert.Contains(Descendants(player).OfType<TextBlock>(), text => text.Text == "0:01 / 0:02");
                Assert.Empty(errors);
            }
            finally { probe.Close(); }
        }
        var longWave = Path.Combine(root, "long-voice.wav");
        const int longSeconds = 601, longBytes = longSeconds * 24000 * 2;
        using (var writer = new BinaryWriter(File.Create(longWave)))
        {
            writer.Write("RIFF"u8); writer.Write(longBytes + 36); writer.Write("WAVEfmt "u8); writer.Write(16);
            writer.Write((short)1); writer.Write((short)1); writer.Write(24000); writer.Write(48000);
            writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(longBytes);
            writer.BaseStream.SetLength(longBytes + 44);
        }
        using (var longPlayer = new VoiceMessagePlayer(longWave, errors.Add))
        {
            var probe = new Window { Content = longPlayer, Width = 400, Height = 100, ShowInTaskbar = false };
            try
            {
                probe.Show();
                var slider = Assert.Single(Descendants(longPlayer).OfType<Slider>());
                PumpUntil(probe, () => slider.Maximum == longSeconds, "Long recordings should retain duration and seeking when waveform analysis is rejected.");
                var waveView = Assert.Single(Descendants(longPlayer).OfType<WaveformView>());
                Assert.Null(waveView.Peaks); Assert.StartsWith("Волна недоступна:", Assert.IsType<string>(waveView.ToolTip));
                slider.Value = 600;
                Assert.Contains(Descendants(longPlayer).OfType<TextBlock>(), text => text.Text == "10:00 / 10:01");
                Assert.Empty(errors);
            }
            finally { probe.Close(); }
        }
        var profile = store.Register("UiTester", "correct horse battery");
        var community = store.CreateCommunity("UI checks", profile.Id);
        var channel = store.GetChannels(community.Id).First(c => !c.IsVoice);
        var emoji = store.ImportAsset(community.Id, gif, "emoji");
        phase("main construction");
        var main = new MainWindow(store, profile);
        try
        {
            phase("main layout"); main.Show(); PumpLayout(main);
            const System.Reflection.BindingFlags privateFlags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            Assert.Null(typeof(MainWindow).GetField("community",privateFlags)!.GetValue(main));
            Assert.Null(typeof(MainWindow).GetField("selectedChannel",privateFlags)!.GetValue(main));
            Assert.True(Assert.IsType<ScrollViewer>(typeof(MainWindow).GetField("homeWorkspace",privateFlags)!.GetValue(main)).IsVisible);
            CheckHomeAscii(main);
            SaveSnapshot(main, "startup-home-dark");
            typeof(MainWindow).GetMethod("SetCommunity",privateFlags)!.Invoke(main,new object[]{community});PumpLayout(main);
            Assert.Equal(288d,Ui.Metric("Width.Channels"));
            phase("Grayscale, chosen GIF cover and popup controls");DesignRegressionChecks.Check(main,root);
            var uiFailures=new List<Exception>();
            void CheckUi(string name,Action check)
            {
                phase(name);
                try{check();}
                catch(Exception error){UiCheckEvidence.Write("failed_"+name,error.ToString());uiFailures.Add(new InvalidOperationException(name,error));}
            }
            CheckUi("stable keyed server rail and rounded square artwork",()=>RailStabilityChecks.Check(main,store,profile,community));
            CheckUi("channel hover path, gear input and contiguous row highlight",()=>ChannelRowChecks.Check(main));
            CheckUi("focus-visible input modality, window reactivation and popup dismissal",()=>FocusVisibleChecks.Check(main));
            CheckUi("opaque settings transitions, inset and header",()=>ModalLifecycleChecks.Check(main,profile,name=>SaveSnapshot(main,name)));
            CheckUi("20:7 profile artwork, top aligned bio and preserved status",()=>ProfileLayoutChecks.Check(main,profile,gif));
            if(uiFailures.Count>0)throw new AggregateException("Native UI regression checks failed",uiFailures);
            phase("server settings roles, stale replies and real sound gain");SettingsIntegrationChecks.Check(main,store,profile,community);
            phase("stream audience, gain and cleanup");StreamIntegrationChecks.Check(main,store,profile,community);
            phase("native image editor save, cover and cancel");CheckImageEditor(main,gif);
            phase("live recording waveform from PCM");CheckRecordingWaveform(main,root);
            CheckComposerState(main,channel);CheckVoiceActionLayout(main,false);
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
            CheckSourcePicker(main);
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
            Assert.Equal("hello :loop: world", Clipboard.GetText());
            Assert.Equal(22d, Assert.IsType<Image>(body.Paragraph.Inlines.OfType<InlineUIContainer>().Single().Child).Width);
            Assert.Equal(position, body.TranslatePoint(new Point(0, 0), main));
            var chatEmoji = Assert.IsType<Image>(body.Paragraph.Inlines.OfType<InlineUIContainer>().Single().Child);
            Assert.Null(AnimationBehavior.GetSourceStream(chatEmoji));var firstFrame=chatEmoji.Source;
            chatEmoji.Source=null;GifPlayback.Reset(chatEmoji);Assert.Same(firstFrame,chatEmoji.Source);

            Assert.Equal(GifPlaybackMode.Hover, GifPlayback.GetMode(chatEmoji));
            Assert.NotNull(GifPlayback.GetScope(chatEmoji));
            phase("existing chat avatar refresh");CheckChatAvatarRefresh(main,body,gif);
            phase("connection status, brand and smooth scrolling"); CheckDesktopPolish(main, community.Id, store.GetChannels(community.Id).First(c => c.IsVoice).Id);
            phase("navigation during call and redesigned screens");CheckNavigationAndScreens(main,store,profile,community,gif);
            main.Activate();PumpLayout(main);
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
            Assert.Equal(GifPlaybackMode.Hover, GifPlayback.GetMode(participant.Avatar));
            Assert.Contains(Descendants(mutedParticipant).OfType<Icon>(), icon => icon.Kind == "mic-off");
            Assert.Contains(Descendants(mutedParticipant).OfType<Icon>(), icon => icon.Kind == "headphone-off");
            localVoice.Dispose();
            phase("optimistic voice entry and cancellation");
            var voiceChannel = store.GetChannels(community.Id).First(c => c.IsVoice);
            // Keep admission pending while actual dispatcher/rendering work runs.
            // This local fixture has no backend, so allowing admission to finish
            // would correctly reject the join before the optimistic UI is checked.
            var transitionGate = (SemaphoreSlim)typeof(MainWindow).GetField("voiceTransitions", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(main)!;
            Assert.True(transitionGate.Wait(0));
            bool gateHeld = true;
            try
            {
                var joinTask = (Task)typeof(MainWindow).GetMethod("JoinVoice", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(main, new object[] { voiceChannel, false, CancellationToken.None })!;
                Assert.False(joinTask.IsCompleted);
                CheckVoiceActionLayout(main,true);
                Assert.Contains(Descendants(main).OfType<TextBlock>(), text => text.Text == "SFU · Подключение");
                main.UpdateLayout();
                Assert.Contains(Descendants(main).OfType<VoiceParticipantRow>(), row => System.Windows.Automation.AutomationProperties.GetName(row).StartsWith(profile.Username));
                SaveSnapshot(main, "voice-connecting");
                var leaveTask = (Task)typeof(MainWindow).GetMethod("LeaveVoice", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(main, null)!;
                transitionGate.Release(); gateHeld = false;
                PumpUntil(main, () => joinTask.IsCompleted && leaveTask.IsCompleted, "pending voice entry cancellation");
                Assert.True(joinTask.IsCompletedSuccessfully); Assert.True(leaveTask.IsCompletedSuccessfully);
                CheckVoiceActionLayout(main,false);
                Assert.DoesNotContain(Descendants(main).OfType<TextBlock>(), text => text.Text == "SFU · Подключение");
            }
            finally { if (gateHeld) transitionGate.Release(); }
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

    private static void CheckHomeAscii(MainWindow main)
    {
        const System.Reflection.BindingFlags flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
        var home=Assert.IsType<ScrollViewer>(typeof(MainWindow).GetField("homeWorkspace",flags)!.GetValue(main));
        double width=main.Width;bool light=ThemeManager.IsLight;
        try
        {
            foreach(bool theme in new[]{false,true})
            {
                if(ThemeManager.IsLight!=theme)ThemeManager.Toggle();
                foreach(var size in new[]{("minimum",Ui.Metric("Width.WindowMinimum")),("normal",Ui.Metric("Width.Window"))})
                {
                    main.Width=size.Item2;PumpLayout(main);
                    var ascii=Assert.Single(Descendants(home).OfType<TextBlock>(),text=>text.Text==BrandAscii.Art);
                    Assert.True(ascii.IsVisible);Assert.True(ascii.ActualWidth>0);Assert.True(ascii.ActualHeight>0);
                    Assert.Equal(TextWrapping.NoWrap,ascii.TextWrapping);
                    Assert.Contains("Consolas",ascii.FontFamily.Source,StringComparison.OrdinalIgnoreCase);
                    var typeface=new Typeface(ascii.FontFamily,ascii.FontStyle,ascii.FontWeight,ascii.FontStretch);
                    double TextWidth(string value)=>new FormattedText(value,System.Globalization.CultureInfo.InvariantCulture,FlowDirection.LeftToRight,typeface,ascii.FontSize,ascii.Foreground,VisualTreeHelper.GetDpi(ascii).PixelsPerDip).Width;
                    Assert.InRange(TextWidth("iiii")-TextWidth("WWWW"),-.01,.01);
                    Assert.DoesNotContain("WISP",ascii.Text,StringComparison.OrdinalIgnoreCase);
                    Assert.All(ascii.Text,letter=>Assert.InRange((int)letter,0,127));
                    var visible=ascii.TransformToAncestor(home).TransformBounds(new Rect(new Size(ascii.ActualWidth,ascii.ActualHeight)));
                    Assert.InRange(visible.Left,-1,home.ActualWidth+1);Assert.InRange(visible.Top,-1,home.ActualHeight+1);
                    Assert.True(visible.Right<=home.ActualWidth+1);Assert.True(visible.Bottom<=home.ActualHeight+1);
                    var actions=Descendants(home).OfType<Button>().ToArray();
                    Assert.Contains(actions,button=>System.Windows.Automation.AutomationProperties.GetName(button)=="Создать сервер"&&button.IsEnabled);
                    Assert.Contains(actions,button=>System.Windows.Automation.AutomationProperties.GetName(button)=="Присоединиться"&&button.IsEnabled);
                    SaveSnapshot(main,"home-ascii-"+(theme?"light":"dark")+"-"+size.Item1);
                }
            }
        }
        finally{main.Width=width;if(ThemeManager.IsLight!=light)ThemeManager.Toggle();PumpLayout(main);}
    }

    private static void CheckChatAvatarRefresh(MainWindow main,SelectableChatText body,string gif)
    {
        const System.Reflection.BindingFlags flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
        var profileField=typeof(MainWindow).GetField("profile",flags)!;
        var original=Assert.IsType<UserProfile>(profileField.GetValue(main));
        var messages=Assert.IsType<VirtualRows>(typeof(MainWindow).GetField("messages",flags)!.GetValue(main));
        var rows=messages.Items.Cast<UiRow>().ToArray();
        var host=Assert.Single(Descendants(messages).OfType<ContentControl>(),control=>control.Name=="ChatAvatar");
        var originalImage=host.Content;
        var scroll=Assert.Single(Descendants(messages).OfType<ScrollViewer>(),viewer=>ReferenceEquals(viewer.TemplatedParent,messages));
        double offset=scroll.VerticalOffset;string selected=body.SelectedPlainText();
        var position=body.TranslatePoint(new Point(),main);
        bool reduced=Motion.Reduce;
        try
        {
            Motion.SetReduced(true);
            profileField.SetValue(main,original with{AvatarPath=gif,AvatarRevision=AvatarCatalog.Revision(gif),AvatarCoverFrame=1});
            typeof(MainWindow).GetMethod("RenderMembers",flags)!.Invoke(main,null);PumpLayout(main);
            Assert.Same(host,Assert.Single(Descendants(messages).OfType<ContentControl>(),control=>control.Name=="ChatAvatar"));
            var image=Assert.IsType<Image>(host.Content);Assert.NotSame(originalImage,image);
            Assert.Equal(GifPlaybackMode.Always,GifPlayback.GetMode(image));
            var expected=Wisp.Desktop.Media.ImageEditingService.LoadCover(gif,1,80);
            byte[] Pixels(System.Windows.Media.Imaging.BitmapSource source)
            {
                var bitmap=new System.Windows.Media.Imaging.FormatConvertedBitmap(source,PixelFormats.Bgra32,null,0);
                var bytes=new byte[bitmap.PixelWidth*bitmap.PixelHeight*4];bitmap.CopyPixels(bytes,bitmap.PixelWidth*4,0);return bytes;
            }
            var expectedPixels=Pixels(expected);
            PumpUntil(main,()=>image.Source is System.Windows.Media.Imaging.BitmapSource bitmap&&Pixels(bitmap).SequenceEqual(expectedPixels),"updated chat avatar chosen GIF cover");
            Assert.Equal(rows.Length,messages.Items.Count);
            for(int index=0;index<rows.Length;index++)Assert.Same(rows[index],messages.Items[index]);
            Assert.Same(body,Assert.Single(Descendants(messages).OfType<SelectableChatText>()));
            Assert.Equal(selected,body.SelectedPlainText());Assert.Equal(offset,scroll.VerticalOffset);
            Assert.Equal(position,body.TranslatePoint(new Point(),main));
            SaveSnapshot(main,"chat-avatar-updated-dark");
        }
        finally
        {
            profileField.SetValue(main,original);typeof(MainWindow).GetMethod("RenderMembers",flags)!.Invoke(main,null);
            Motion.SetReduced(reduced);PumpLayout(main);
        }
    }

    private static void CheckImageEditor(MainWindow main,string gif)
    {
        var original=File.ReadAllBytes(gif);
        int windowCount=Application.Current.Windows.Count;
        var accepted=ImageEditor.ShowAsync(main,gif,ImageEditKind.UserAvatar);
        var editor=Assert.Single(Descendants(main).OfType<ImageEditor>());
        PumpUntil(main,()=>editor.ViewModel.CanEdit,"image editor decoded GIF");
        Assert.Equal(windowCount,Application.Current.Windows.Count);Assert.True(editor.ViewModel.Animated);
        Assert.Equal(1,editor.ViewModel.MaximumFrame);Assert.Equal(0,editor.ViewModel.CoverFrame);
        var initialCrop=editor.SourceCrop;Assert.True(initialCrop.Width>0);Assert.InRange(initialCrop.Width/initialCrop.Height,.999,1.001);
        var zoom=Assert.Single(Descendants(editor).OfType<Slider>(),slider=>Equals(slider.ToolTip,"Масштаб изображения"));
        zoom.Value=2;PumpLayout(main);Assert.Equal(2,editor.ViewModel.Zoom);Assert.True(editor.SourceCrop.Width<initialCrop.Width);
        var viewport=Assert.IsType<Canvas>(editor.FindName("Viewport"));
        var wheel=new MouseWheelEventArgs(Mouse.PrimaryDevice,Environment.TickCount,120){RoutedEvent=Mouse.MouseWheelEvent};
        viewport.RaiseEvent(wheel);PumpLayout(main);Assert.True(wheel.Handled);Assert.True(editor.ViewModel.Zoom>2);
        Assert.Single(Descendants(editor).OfType<Button>(),button=>Equals(button.Content,"Сбросить")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpLayout(main);Assert.Equal(1,editor.ViewModel.Zoom);Assert.Equal(initialCrop,editor.SourceCrop);
        var initialFrame=editor.ViewModel.Source;
        var timeline=Assert.Single(Descendants(editor).OfType<Slider>(),slider=>Equals(slider.ToolTip,"Кадр статичной обложки"));timeline.Value=1;
        PumpUntil(main,()=>editor.ViewModel.CoverFrame==1&&!ReferenceEquals(initialFrame,editor.ViewModel.Source),"image editor chosen cover");
        SaveSnapshot(main,"image-editor-gif-dark");
        Assert.Single(Descendants(editor).OfType<Button>(),button=>Equals(button.ToolTip,"Сохранить обрезку")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(main,()=>accepted.IsCompleted,"image editor animated save");
        var result=Assert.IsType<EditedImage>(accepted.GetAwaiter().GetResult());
        string draftPath=result.Path;string draftDirectory=Path.GetDirectoryName(draftPath)!;
        try
        {
            Assert.Equal(1,result.CoverFrame);Assert.Equal(2,result.FrameCount);Assert.Equal(result.Width,result.Height);
            Assert.True(new FileInfo(result.Path).Length<=Wisp.Desktop.Media.ImageEditingService.MaximumBytes);
            var edited=new Wisp.Desktop.Media.ImageEditingDocument(result.Path);Assert.True(edited.Animated);Assert.Equal(2,edited.FrameCount);
            Assert.Equal(new Wisp.Desktop.Media.ImageEditingDocument(gif).Delays.ToArray(),edited.Delays.ToArray());
            Assert.Equal(original,File.ReadAllBytes(gif));
        }
        finally{result.Dispose();}
        Assert.False(File.Exists(draftPath));Assert.False(Directory.Exists(draftDirectory));
        var cancelled=ImageEditor.ShowAsync(main,gif,ImageEditKind.ProfileBanner,initialCoverFrame:1);
        var bannerEditor=Assert.Single(Descendants(main).OfType<ImageEditor>());
        PumpUntil(main,()=>bannerEditor.ViewModel.CanEdit,"banner editor decoded GIF");
        Assert.Equal(1,bannerEditor.ViewModel.CoverFrame);
        Assert.InRange(bannerEditor.SourceCrop.Width/bannerEditor.SourceCrop.Height,20d/7-.001,20d/7+.001);
        bannerEditor.ViewModel.Zoom=2;PumpLayout(main);SaveSnapshot(main,"image-editor-banner-dark");
        Assert.Single(Descendants(bannerEditor).OfType<Button>(),button=>Equals(button.ToolTip,"Отмена · Esc")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(main,()=>cancelled.IsCompleted,"image editor cancel");
        Assert.Null(cancelled.GetAwaiter().GetResult());Assert.Equal(original,File.ReadAllBytes(gif));
        Assert.DoesNotContain(Descendants(main),child=>child is ImageEditor);
    }

    private static void CheckRecordingWaveform(MainWindow main,string root)
    {
        string path=Path.Combine(root,"waveform-recording.wav"),retry=Path.Combine(root,"waveform-recording-retry.wav");
        using var recorder=new Wisp.Desktop.Media.VoiceRecorder();
        using var view=new RecordingWaveformView();view.Bind(recorder);
        var handle=ModalHost.Open(main,"Запись голоса",view,740);PumpLayout(main);
        var bars=Assert.IsType<WaveformBars>(view.FindName("Bars"));
        var elapsed=Assert.IsType<TextBlock>(view.FindName("Elapsed"));
        byte[] Pcm(float amplitude)
        {
            var block=new byte[4800*2];
            for(int i=0;i<4800;i++)
            {
                short value=(short)(Math.Sin(2*Math.PI*440*i/48000)*amplitude*32767);
                block[i*2]=(byte)value;block[i*2+1]=(byte)(value>>8);
            }
            return block;
        }
        try
        {
            recorder.StartExternal(path);
            for(int i=0;i<20;i++)recorder.AppendPcm(Pcm(.1f+(i%9)*.1f));
            PumpUntil(main,()=>bars.Peaks?.Count==20&&elapsed.Text=="0:02","recorded PCM waveform and elapsed time");
            Assert.True(recorder.IsRecording);Assert.True(bars.ActualWidth>200);
            Assert.True(bars.Peaks!.Max()>.85f);Assert.True(bars.Peaks!.Min()<.15f);
            SaveSnapshot(main,"recording-live-waveform-dark");
            recorder.AppendPcm(Pcm(.5f));
            PumpUntil(main,()=>bars.Peaks?.Count==21,"live waveform update");
            var stopping=recorder.StopAsync();PumpUntil(main,()=>stopping.IsCompleted,"waveform recording stop");
            Assert.Equal(path,stopping.GetAwaiter().GetResult());Assert.False(recorder.IsRecording);
            view.Reset();Assert.Empty(bars.Peaks!);Assert.Equal("0:00",elapsed.Text);
            recorder.StartExternal(retry);recorder.AppendPcm(Pcm(.25f));
            PumpUntil(main,()=>bars.Peaks?.Count==1,"waveform resets between recordings");
            Assert.InRange(bars.Peaks![0],.24f,.26f);
            stopping=recorder.StopAsync();PumpUntil(main,()=>stopping.IsCompleted,"waveform retry stop");stopping.GetAwaiter().GetResult();
        }
        finally
        {
            if(recorder.IsRecording){var stopping=recorder.StopAsync();PumpUntil(main,()=>stopping.IsCompleted,"waveform cleanup");stopping.GetAwaiter().GetResult();}
            handle.Close();PumpLayout(main);File.Delete(path);File.Delete(retry);
        }
    }

    private static void CheckVoiceActionLayout(MainWindow main,bool connected)
    {
        const System.Reflection.BindingFlags flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
        var block=Assert.IsType<Border>(typeof(MainWindow).GetField("voiceActionBlock",flags)!.GetValue(main));
        PumpLayout(main);
        Assert.Equal(connected?Visibility.Visible:Visibility.Collapsed,block.Visibility);
        Assert.True(Assert.IsType<ToggleIconButton>(typeof(MainWindow).GetField("micButton",flags)!.GetValue(main)).IsVisible);
        Assert.True(Assert.IsType<ToggleIconButton>(typeof(MainWindow).GetField("deafenButton",flags)!.GetValue(main)).IsVisible);
        if(!connected){Assert.Equal(0,block.DesiredSize.Height);return;}
        var grid=Assert.Single(Descendants(block).OfType<System.Windows.Controls.Primitives.UniformGrid>(),element=>element.Name=="VoiceActionsGrid");
        Assert.Equal(4,grid.Columns);Assert.Equal(1,grid.Rows);
        var buttons=grid.Children.OfType<Button>().ToArray();Assert.Equal(4,buttons.Length);
        var dpi=VisualTreeHelper.GetDpi(grid);double pixelX=1/dpi.DpiScaleX,pixelY=1/dpi.DpiScaleY;
        double width=buttons[0].ActualWidth,height=buttons[0].ActualHeight;
        Assert.True(width>24);Assert.True(height>24);
        var positions=new List<Rect>();
        foreach(var button in buttons)
        {
            Assert.Equal(new Thickness(Ui.Xs,0,Ui.Xs,0),button.Margin);
            Assert.InRange(button.ActualWidth,width-pixelX,width+pixelX);Assert.InRange(button.ActualHeight,height-pixelY,height+pixelY);
            var content=Assert.IsAssignableFrom<FrameworkElement>(button.Content);
            var iconCenter=content.TranslatePoint(new Point(content.ActualWidth/2,content.ActualHeight/2),button);
            Assert.InRange(iconCenter.X,button.ActualWidth/2-1,button.ActualWidth/2+1);
            Assert.InRange(iconCenter.Y,button.ActualHeight/2-1,button.ActualHeight/2+1);
            positions.Add(new Rect(button.TranslatePoint(new Point(),grid),new Size(button.ActualWidth,button.ActualHeight)));
        }
        Assert.InRange(positions[0].Left-(grid.ActualWidth-positions[^1].Right),-pixelX,pixelX);
        double gap=positions[1].Left-positions[0].Right;
        // UniformGrid gives equal logical cells. Layout rounding can allocate a
        // leftover physical pixel to a gap at widths not divisible by four.
        for(int index=2;index<positions.Count;index++)Assert.InRange(positions[index].Left-positions[index-1].Right,gap-pixelX-.01,gap+pixelX+.01);
    }

    private static void CheckComposerState(MainWindow main,Channel channel)
    {
        const System.Reflection.BindingFlags flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
        var composer=Assert.IsType<TextBox>(typeof(MainWindow).GetField("composer",flags)!.GetValue(main));
        var send=Assert.IsType<Button>(typeof(MainWindow).GetField("sendButton",flags)!.GetValue(main));
        var view=Assert.IsType<MessageComposerView>(typeof(MainWindow).GetField("messageComposer",flags)!.GetValue(main));
        Assert.Equal("Написать "+channel.Name,view.Placeholder);Assert.False(send.IsEnabled);
        composer.Text=" \r\n\t";PumpLayout(main);Assert.False(send.IsEnabled);
        composer.Text="draft";PumpLayout(main);Assert.True(send.IsEnabled);
        composer.Clear();PumpLayout(main);Assert.False(send.IsEnabled);
        Assert.DoesNotContain(Descendants(main).OfType<TextBlock>(),text=>text.Text.Contains("Enter —")||text.Text.Contains("Shift+Enter"));
        var actions=Assert.IsType<StackPanel>(send.Parent);
        var recording=Assert.IsType<ToggleIconButton>(typeof(MainWindow).GetField("recordButton",flags)!.GetValue(main));
        Assert.True(actions.Children.IndexOf(recording)<actions.Children.IndexOf(send));
    }

    private static void CheckGlobalMini(MainWindow main,ScreenViewer video,Grid center,UserProfile profile)
    {
        var surface=Assert.Single(center.Children.OfType<Border>());
        video.SetVideoAspect(1920,1080);video.ToggleMini();PumpLayout(main);
        Assert.True(video.IsMini);Assert.Empty(center.Children.OfType<Border>());
        var overlay=ModalHost.Surface(main);Assert.Contains(surface,overlay.Children.OfType<Border>());
        Assert.Equal(1000,Panel.GetZIndex(surface));
        Assert.InRange(surface.Width/surface.Height,16d/9-.01,16d/9+.01);
        var modalBody=new TextBlock{Text="Settings remain behind the mini preview"};
        var modal=ModalHost.Open(main,"Поверх настроек",modalBody,options:new ModalOptions(FullWindow:true));PumpLayout(main);
        Assert.True(Panel.GetZIndex(surface)>Panel.GetZIndex((UIElement)overlay.Children[overlay.Children.Count-1]));
        Assert.True(surface.IsVisible);SaveSnapshot(main,"mini-above-settings-dark");
        modal.Close();PumpUntil(main,()=>!Descendants(main).Contains(modalBody),"mini overlay modal close");
        video.DockMini(MiniDockPosition.TopRight,false);PumpLayout(main);
        Assert.Equal(MiniDockPosition.TopRight,video.MiniPosition);Assert.InRange(surface.Margin.Top,0,Ui.L+1);
        video.DockMini(MiniDockPosition.BottomRight,false);PumpLayout(main);
        Assert.Equal(MiniDockPosition.BottomRight,video.MiniPosition);
        Assert.True(surface.Margin.Top+surface.Height<=overlay.ActualHeight+1);
        video.Hide();Assert.False(video.IsVisible);video.Show();PumpLayout(main);
        Assert.True(video.IsMini);Assert.True(video.IsVisible);Assert.Same(overlay,surface.Parent);
        double oldWidth=main.Width;main.Width=main.MinWidth;PumpLayout(main);
        Assert.True(surface.Margin.Left+surface.Width<=overlay.ActualWidth+1);
        Assert.True(surface.Margin.Top+surface.Height<=overlay.ActualHeight+1);
        main.Width=oldWidth;PumpLayout(main);
        float changed=-1;video.VolumeChanged+=gain=>changed=gain;
        var slider=Assert.Single(Descendants(surface).OfType<Slider>());slider.Value=25;
        Assert.Equal(.25f,video.Volume);Assert.Equal(.25f,changed);
        Button Find(string label)=>Assert.Single(Descendants(surface).OfType<Button>(),button=>System.Windows.Automation.AutomationProperties.GetName(button)==label);
        Find("Выключить звук демонстрации").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Assert.Equal(0,video.Volume);
        Find("Включить звук демонстрации").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Assert.Equal(.25f,video.Volume);
        using(var voice=new Wisp.Desktop.Media.VoiceService())
        {
            var watchers=new[]{new PeerPresence(profile,"voice",false,false),new PeerPresence(new UserProfile("watcher","Actual viewer"),"voice",true,false)};
            video.UpdateViewers(watchers,_=>null,voice,profile.Id);PumpLayout(main);
            Assert.Contains(Descendants(surface).OfType<Border>(),border=>Equals(border.ToolTip,"Actual viewer"));
            video.UpdateViewers(Array.Empty<PeerPresence>(),_=>null,voice,profile.Id);PumpLayout(main);
            Assert.DoesNotContain(Descendants(surface).OfType<Border>(),border=>Equals(border.ToolTip,"Actual viewer"));
        }
        video.ToggleMini();PumpLayout(main);Assert.False(video.IsMini);Assert.Contains(surface,center.Children.OfType<Border>());
        bool stopOwn=false;
        var own=new ScreenViewer(main,"My demo",center,ownStream:true);own.StopRequested+=()=>stopOwn=true;own.ToggleMini();PumpLayout(main);
        var ownSurface=Assert.Single(overlay.Children.OfType<Border>());
        Assert.Single(Descendants(ownSurface).OfType<Button>(),button=>System.Windows.Automation.AutomationProperties.GetName(button)=="Завершить демонстрацию").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(stopOwn);own.Close();Assert.DoesNotContain(ownSurface,overlay.Children.OfType<Border>());
        bool remoteStop=false;
        var remote=new ScreenViewer(main,"Remote demo",center);remote.StopRequested+=()=>remoteStop=true;remote.ToggleMini();PumpLayout(main);
        var remoteSurface=Assert.Single(overlay.Children.OfType<Border>());
        Assert.Single(Descendants(remoteSurface).OfType<Button>(),button=>System.Windows.Automation.AutomationProperties.GetName(button)=="Закрыть просмотр").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(remote.IsClosed);Assert.False(remoteStop);Assert.DoesNotContain(remoteSurface,overlay.Children.OfType<Border>());
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
        PumpUntil(main,()=>leaving.IsCompleted,"voice disconnection completion");Assert.True(leaving.IsCompletedSuccessfully);
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
        void CheckLocalPresence(string? expectedChannel)
        {
            var visible=(IReadOnlyList<PeerPresence>)type.GetProperty("DisplayPresence",flags)!.GetValue(main)!;
            var local=Assert.Single(visible,person=>person.Profile.Id==profile.Id);
            Assert.Equal(new PeerPresence((UserProfile)Get("profile")!,expectedChannel,(bool)Get("muted")!||(bool)Get("deafened")!,(bool)Get("deafened")!),local);
        }
        var originalRoom=Get("rtc");var originalVoice=Get("voiceCommunity");var originalChannel=Get("voiceChannelId");
        var originalSharing=Get("sharing");var originalFailed=Get("activeVoiceFailed");var originalSelected=Get("selectedChannel");
        var voiceChannel=store.GetChannels(original.Id).First(c=>c.IsVoice);
        var info=new SfuConnectionInfo("wss://89.126.195.20:8443/rtc",ServerConnectionState.Connected,28,DateTimeOffset.UtcNow,"host/host");
        var room=new TestVoiceRoom(info,voiceChannel.Id)
        {
            Participants=new[]{new RtcSeat(profile.Id,new string('a',32),DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())}
        };
        var mic=Assert.IsType<ToggleIconButton>(Get("micButton"));var deafen=Assert.IsType<ToggleIconButton>(Get("deafenButton"));
        Assert.True(mic.IsVisible);Assert.True(deafen.IsVisible);Assert.Null(Get("voiceChannelId"));
        ((Task)type.GetMethod("ToggleMic",flags)!.Invoke(main,null)!).GetAwaiter().GetResult();
        Assert.True(mic.IsChecked);Assert.True(VoicePreferences.Load(store.RootDirectory).Muted);
        ((Task)type.GetMethod("ToggleDeafen",flags)!.Invoke(main,null)!).GetAwaiter().GetResult();
        Assert.True(deafen.IsChecked);Assert.True(VoicePreferences.Load(store.RootDirectory).Deafened);
        ((Task)type.GetMethod("ToggleDeafen",flags)!.Invoke(main,null)!).GetAwaiter().GetResult();
        ((Task)type.GetMethod("ToggleMic",flags)!.Invoke(main,null)!).GetAwaiter().GetResult();
        var second=store.CreateCommunity("Other community",profile.Id);
        var secondText=store.GetChannels(second.Id).First(c=>!c.IsVoice);
        try
        {
            Set("rtc",room);Set("voiceCommunity",original);Set("voiceChannelId",voiceChannel.Id);Set("activeVoiceFailed",false);Set("sharing",true);
            Invoke("UpdateStatus");Invoke("RenderServers");PumpLayout(main);CheckVoiceActionLayout(main,true);
            CheckLocalPresence(voiceChannel.Id);
            Invoke("CaptureError", "stale capture failure");
            Set("shareGeneration", (int)Get("shareGeneration")! + 1);
            PumpLayout(main);
            Assert.True((bool)Get("sharing")!);
            var originalSource=(Wisp.Desktop.Media.ScreenShareService)Get("screen")!;
            var originalGeneration=Get("shareGeneration");
            var rail=(VirtualRows)Get("servers")!;
            var otherButton=Assert.Single(Descendants(rail).OfType<Button>(),b=>System.Windows.Automation.AutomationProperties.GetName(b)==second.Name);
            otherButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));PumpLayout(main);
            Assert.Equal(second.Id,Assert.IsType<Community>(Get("community")).Id);
            Assert.Same(room,Get("rtc"));Assert.Same(original,Get("voiceCommunity"));Assert.Equal(voiceChannel.Id,Get("voiceChannelId"));
            Assert.True(Assert.IsType<bool>(Get("sharing")));Assert.Equal(originalGeneration,Get("shareGeneration"));Assert.Same(originalSource,Get("screen"));
            Assert.Equal(0,room.EnterCalls);Assert.Equal(0,room.LeaveCalls);
            var oldDeafened=Get("deafened");Set("deafened",true);
            try{CheckLocalPresence(null);}finally{Set("deafened",oldDeafened);}
            Invoke("SelectChannel",secondText);PumpLayout(main);
            Assert.Equal(secondText.Id,Assert.IsType<Channel>(Get("selectedChannel")).Id);
            Assert.Equal(voiceChannel.Id,Get("voiceChannelId"));Assert.Equal(0,room.LeaveCalls);Assert.Equal(0,room.EnterCalls);
            var status=(TextBlock)Get("connection")!;
            Assert.Contains(original.Name,status.Text);Assert.Contains(voiceChannel.Name,status.Text);Assert.DoesNotContain(second.Name,status.Text);
            var memberPanel=(Border)Get("memberPanel")!;
            var memberColumn=(ColumnDefinition)Get("memberColumn")!;
            var rootPanel=Assert.IsType<Grid>(Get("streamWorkspace") is Grid stream?stream.Parent:null);
            var layout=Assert.IsType<Grid>(rootPanel.Parent);
            Assert.Equal(4,layout.ColumnDefinitions.Count);
            Assert.DoesNotContain(Descendants(main).OfType<TextBox>(),box=>System.Windows.Automation.AutomationProperties.GetName(box)=="Поиск участников");
            SaveSnapshot(main,"community-during-call-dark");
            foreach(double width in new[]{1360d,1100d})
            {Invoke("UpdateResponsivePanels",width);main.UpdateLayout();Assert.True(memberPanel.IsVisible);Assert.Equal(220d,memberColumn.Width.Value);}
            Invoke("UpdateResponsivePanels",1040d);main.UpdateLayout();Assert.False(memberPanel.IsVisible);Assert.Equal(0d,memberColumn.Width.Value);
            Invoke("UpdateResponsivePanels",new object[]{null!});main.UpdateLayout();
            var chat=(VirtualRows)Get("messages")!;var before=chat.Items.Cast<UiRow>().ToArray();
            var selectedBefore=Get("selectedChannel");Invoke("SetCommunity",second);PumpLayout(main);
            Assert.Same(selectedBefore,Get("selectedChannel"));Assert.Equal(before,chat.Items.Cast<UiRow>().ToArray());Assert.Equal(new Thickness(0),chat.Margin);
            var center=Assert.IsType<Grid>(Get("streamWorkspace"));
            var video=new ScreenViewer(main,"Central demo",center);PumpLayout(main);
            Assert.False(video.IsMini);Assert.True(video.IsLoading);Assert.Single(center.Children.Cast<Border>());
            Assert.Equal(2,Grid.GetColumn(rootPanel));Assert.True(((VirtualRows)Get("servers")!).IsVisible);
            video.Present(new byte[16],2,2);Assert.False(video.IsLoading);
            CheckGlobalMini(main,video,center,profile);
            video.SetUnavailable();SaveSnapshot(main,"central-stream-dark");video.Close();Assert.Empty(center.Children.Cast<UIElement>());
            main.Width=Ui.Metric("Width.Window");Invoke("SetCommunity",original);Invoke("SelectChannel",voiceChannel);PumpLayout(main);
            CheckLocalPresence(voiceChannel.Id);
            var channelRows=(VirtualRows)Get("channels")!;
            var voiceButton=Assert.Single(Descendants(channelRows).OfType<Button>(),b=>System.Windows.Automation.AutomationProperties.GetName(b)==voiceChannel.Name);
            Assert.NotEqual(BrushColor((Brush)Application.Current.FindResource("Selected")),BrushColor(voiceButton.Background));
            var voiceRow=Assert.Single(channelRows.Items.Cast<UiRow>(),row=>row.Key==voiceChannel.Id);
            var voiceContainer=Assert.IsType<ListBoxItem>(channelRows.ItemContainerGenerator.ContainerFromItem(voiceRow));
            voiceContainer.IsSelected=true;voiceContainer.ApplyTemplate();main.UpdateLayout();
            Assert.Equal(Colors.Transparent,BrushColor(Assert.IsType<Border>(voiceContainer.Template.FindName("Item",voiceContainer)).Background));
            voiceContainer.IsSelected=false;
            Invoke("ShowHome");PumpLayout(main);
            CheckLocalPresence(null);
            Assert.True(((ScrollViewer)Get("homeWorkspace")!).IsVisible);Assert.Equal(Visibility.Collapsed,((DockPanel)Get("chatWorkspace")!).Visibility);
            Assert.False(Assert.IsType<Button>(Get("inviteButton")).IsEnabled);
            Assert.Equal(Visibility.Collapsed,Assert.IsType<Button>(Get("serverSettingsButton")).Visibility);
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
            var profileScroll=new ScrollViewer{Content=profileView,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
            var profileHandle=ModalHost.Open(main,"",profileScroll,ProfileLayout.CardWidth,new ModalOptions(Headerless:true));PumpLayout(main);
            var profileCard=Assert.IsType<Border>(LogicalTreeHelper.GetParent(Assert.IsType<DockPanel>(LogicalTreeHelper.GetParent(profileScroll))));
            Assert.DoesNotContain(Descendants(profileCard).OfType<Button>(),button=>System.Windows.Automation.AutomationProperties.GetName(button)=="Закрыть · Esc");
            Assert.DoesNotContain(Descendants(profileCard).OfType<Image>(),image=>ReferenceEquals(image.Source,Brand.Logo));
            Assert.Contains(Descendants(profileView).OfType<TextBlock>(),t=>t.Text==sample.About);
            Assert.Contains(Descendants(profileView).OfType<TextBlock>(),t=>t.Text.Contains("Dota 2"));
            var animated=Descendants(profileView).OfType<Image>().ToArray();
            Assert.Equal(2,animated.Length);Assert.All(animated,i=>Assert.Equal(GifPlaybackMode.Always,GifPlayback.GetMode(i)));
            Assert.Contains(animated,image=>image.Width==Ui.Metric("Size.ProfileAvatar")*2);
            var bannerFrame=Assert.Single(Descendants(profileView).OfType<AspectRatioFrame>());
            Assert.InRange(bannerFrame.ActualWidth,399.99,400.01);Assert.InRange(bannerFrame.ActualHeight,139.99,140.01);
            Assert.InRange(bannerFrame.ActualWidth/bannerFrame.ActualHeight,20d/7-.01,20d/7+.01);
            Assert.All(animated,i=>Assert.Equal(RepeatBehavior.Forever,AnimationBehavior.GetRepeatBehavior(i)));
            SaveSnapshot(main,"profile-redesign-dark");
            ThemeManager.Toggle();PumpLayout(main);
            Assert.InRange(bannerFrame.ActualWidth,399.99,400.01);Assert.InRange(bannerFrame.ActualHeight,139.99,140.01);
            SaveSnapshot(main,"profile-redesign-light");ThemeManager.Toggle();PumpLayout(main);
            var escape=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(main),Environment.TickCount,Key.Escape){RoutedEvent=Keyboard.PreviewKeyDownEvent};
            profileView.RaiseEvent(escape);Assert.True(escape.Handled);PumpUntil(main,()=>!Descendants(main).Contains(profileView),"headerless profile Escape close");
            CheckProfileEditing(main,sample,gif);
            CheckExactTextSelection(main);
            Wisp.Desktop.Media.AudioDeviceSettings? applied=null;bool? notify=null;float volume=0;
            var settings=new SettingsView(new SettingsContext
            {
                Profile=sample,AvatarPath=gif,BannerPath=gif,Audio=new(),NoiseSuppressionAvailable=true,
                ApplyAudio=value=>{applied=value;return Task.CompletedTask;},EditProfile=()=>{},ShowNotifications=()=>{},
                InputDevices=()=>new[]{new Wisp.Desktop.Media.AudioDeviceChoice(null,"Default input"),new("input-device","Test microphone")},
                OutputDevices=()=>new[]{new Wisp.Desktop.Media.AudioDeviceChoice(null,"Default output"),new("output-device","Test headphones")},
                ApplyNotifications=(enabled,level)=>{notify=enabled;volume=level;}
            });
            var handle=ModalHost.Open(main,"Настройки",settings,options:new ModalOptions(FullWindow:true));PumpLayout(main);
            Assert.Equal(new[]{"Account","Appearance","Audio","Notifications"},settings.Shell.Sections.Select(section=>section.Key));
            Assert.Equal(new[]{"Аккаунт и профиль","Внешний вид","Голос и звук","Уведомления"},settings.Shell.Sections.Select(section=>section.Label));
            Assert.Equal(SettingsSection.Account,settings.SelectedSection);Assert.Equal("Account",settings.Shell.SelectedKey);
            Assert.DoesNotContain(Descendants(settings).OfType<Image>(),image=>ReferenceEquals(image.Source,Brand.Logo));
            var navigation=Assert.Single(Descendants(settings).OfType<ListBox>());
            navigation.SelectedIndex=1;PumpLayout(main);Assert.Equal(SettingsSection.Appearance,settings.SelectedSection);
            var fullCard=Assert.IsType<Border>(LogicalTreeHelper.GetParent(Assert.IsType<DockPanel>(LogicalTreeHelper.GetParent(settings))));
            Assert.Equal(Ui.Insets(Ui.L*3),fullCard.Margin);Assert.Equal(HorizontalAlignment.Stretch,fullCard.HorizontalAlignment);
            Assert.InRange(fullCard.ActualWidth,ModalHost.Surface(main).ActualWidth-Ui.L*6-1,ModalHost.Surface(main).ActualWidth-Ui.L*6+1);
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
            Assert.Empty(Descendants(settings).OfType<Slider>());
            var enabled=Assert.Single(Descendants(settings).OfType<CheckBox>());enabled.IsChecked=false;
            Assert.False(notify);Assert.Equal(.8f,volume);
            SaveSnapshot(main,"settings-notifications-dark");
            Assert.DoesNotContain(settings.Shell.Sections,section=>section.Label is "Подключение" or "Демонстрация экрана");
            handle.Close();PumpUntil(main,()=>!Descendants(main).Contains(settings),"settings close");
        }
        finally {if(ThemeManager.IsLight)ThemeManager.Toggle();Motion.SetReduced(wasReduced);}
    }

    private static void CheckProfileEditing(MainWindow main,UserProfile profile,string gif)
    {
        UserProfile? saved=null;
        var editor=new ProfileEditor(profile,gif,gif,value=>{saved=value;return Task.FromResult(value);});
        var handle=ModalHost.Open(main,"Аккаунт и профиль",editor,ProfileLayout.CardWidth);PumpLayout(main);
        Button FindProfileButton(string label)=>Assert.Single(Descendants(editor).OfType<Button>(),b=>System.Windows.Automation.AutomationProperties.GetName(b)==label);
        void Click(string label){FindProfileButton(label).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));PumpLayout(main);}
        try
        {
            Assert.False(editor.IsEditing);
            Assert.DoesNotContain(Descendants(editor).OfType<Button>(),b=>System.Windows.Automation.AutomationProperties.GetName(b) is "Изменить аватар" or "Изменить баннер");
            Click("Редактировать профиль");Assert.True(editor.IsEditing);
            Assert.NotNull(FindProfileButton("Изменить аватар"));Assert.NotNull(FindProfileButton("Изменить баннер"));
            var name=Assert.Single(Descendants(editor).OfType<TextBox>(),b=>System.Windows.Automation.AutomationProperties.GetName(b)=="Имя пользователя");
            name.Text="CancelledName";Click("Удалить баннер");Click("Отмена");
            Assert.False(editor.IsEditing);Assert.Null(saved);
            Click("Редактировать профиль");
            name=Assert.Single(Descendants(editor).OfType<TextBox>(),b=>System.Windows.Automation.AutomationProperties.GetName(b)=="Имя пользователя");
            Assert.Equal(profile.Username,name.Text);Assert.Equal(2,Descendants(editor).OfType<Image>().Count());
            name.Text="UpdatedName";
            var about=Assert.Single(Descendants(editor).OfType<TextBox>(),b=>System.Windows.Automation.AutomationProperties.GetName(b)=="О себе");
            Assert.Equal(VerticalAlignment.Top,about.VerticalContentAlignment);Assert.Equal(HorizontalAlignment.Left,about.HorizontalContentAlignment);
            Assert.True(about.AcceptsReturn);Assert.Equal(ScrollBarVisibility.Auto,about.VerticalScrollBarVisibility);
            Assert.Empty(Descendants(editor).OfType<ComboBox>());
            about.Text="Line one\nLine two";
            Click("Удалить аватар");Click("Сохранить");
            Assert.False(editor.IsEditing);Assert.NotNull(saved);Assert.Equal("UpdatedName",saved.Username);Assert.Equal("Line one\nLine two",saved.About);
            Assert.Null(saved.AvatarPath);Assert.Equal(gif,saved.BannerPath);Assert.Equal(profile.Status,saved.Status);
            Assert.Throws<ArgumentException>(()=>ProfileEditor.ValidateDraft("x","",null,null));
            Assert.Throws<ArgumentException>(()=>ProfileEditor.ValidateDraft("ValidName",new string('x',2001),null,null));
            ProfileEditor.ValidateImage(gif);
            SaveSnapshot(main,"profile-editor-dark");
        }
        finally{handle.Close();PumpLayout(main);}
    }

    private static void CheckSourcePicker(MainWindow main)
    {
        var sources=new List<Wisp.Desktop.Media.CaptureSource>{new("window:1","Application A"),new("monitor:1","Screen 1")};
        using var picker=new ScreenSourcePicker(()=>sources.ToArray(),_=>
        {var bitmap=System.Windows.Media.Imaging.BitmapSource.Create(2,2,96,96,PixelFormats.Bgr32,null,new byte[16],8);bitmap.Freeze();return bitmap;});
        var handle=ModalHost.Open(main,"Демонстрация экрана",picker,720);PumpLayout(main);
        try
        {
            Button Find(string label)=>Assert.Single(Descendants(picker).OfType<Button>(),b=>System.Windows.Automation.AutomationProperties.GetName(b)==label);
            PumpUntil(main,()=>Descendants(picker).OfType<Image>().Any(i=>i.Source is not null),"source thumbnails");
            Find("Источник · Application A").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Assert.Equal("window:1",picker.SelectedSource?.Id);
            Assert.DoesNotContain(Descendants(picker).OfType<CheckBox>(),_=>true);
            SaveSnapshot(main,"source-picker-dark");
            Find("Источники · Экран").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(main,()=>Descendants(picker).OfType<Button>().Any(b=>System.Windows.Automation.AutomationProperties.GetName(b)=="Источник · Screen 1"),"screen tab");
            Find("Источник · Screen 1").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Assert.Equal("monitor:1",picker.SelectedSource?.Id);
            sources.Clear();var refreshing=picker.RefreshAsync();PumpUntil(main,()=>refreshing.IsCompleted&&picker.SelectedSource is null,"closed source removal");
        }
        finally{handle.Close();}
        CheckShareDialog(main);
    }

    private static void CheckShareDialog(MainWindow main)
    {
        Wisp.Desktop.Media.CaptureSource[] sources=[new("window:1","Editor"),new("window:2","Game"),new("window:3","Browser"),new("monitor:2","Second screen")];
        using var dialog=new ScreenShareDialog(()=>sources,_=>
        {
            var bitmap=System.Windows.Media.Imaging.BitmapSource.Create(16,9,96,96,PixelFormats.Bgr32,null,new byte[16*9*4],16*4);bitmap.Freeze();return bitmap;
        }){Height=500};
        var handle=ModalHost.Open(main,"Демонстрация экрана",dialog,Ui.Metric("Width.ShareDialog"));PumpLayout(main);
        try
        {
            PumpUntil(main,()=>Descendants(dialog).OfType<Image>().Count(image=>image.Source is not null)==3,"full share source previews");
            var launch=Assert.Single(Descendants(dialog).OfType<Button>(),button=>System.Windows.Automation.AutomationProperties.GetName(button)=="Запустить");
            Assert.False(launch.IsEnabled);Assert.Equal(3,Descendants(dialog).OfType<ComboBox>().Count());
            Assert.DoesNotContain(Descendants(dialog).OfType<Button>(),button=>Equals(button.Content,"Отмена"));
            var source=Assert.Single(Descendants(dialog).OfType<Button>(),button=>System.Windows.Automation.AutomationProperties.GetName(button)=="Источник · Game");
            source.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));PumpLayout(main);Assert.True(launch.IsEnabled);
            var fps=Assert.Single(Descendants(dialog).OfType<ComboBox>(),box=>System.Windows.Automation.AutomationProperties.GetName(box)=="Частота кадров демонстрации");fps.SelectedIndex=2;
            Assert.Equal(60,dialog.ViewModel.Selection()!.FramesPerSecond);
            SaveSnapshot(main,"share-dialog-dark");
            launch.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(new ScreenShareSelection(sources[1],1080,60,4_000_000),dialog.Result);
        }
        finally{handle.Close();PumpLayout(main);}
    }

    private static void CheckExactTextSelection(MainWindow main)
    {
        const string original="First sentence.\r\nSecond line with space \r\n";
        var selectable=new SelectableChatText();var run=new Run(original);selectable.Paragraph.Inlines.Add(run);
        var handle=ModalHost.Open(main,"Выделение текста",selectable,440);PumpLayout(main);
        try
        {
            selectable.SelectAll();Assert.Equal(original,selectable.SelectedPlainText());
            ApplicationCommands.Copy.Execute(null,selectable);Assert.Equal(original,Clipboard.GetText());
            var start=run.ContentStart;
            selectable.Selection.Select(start,start.GetPositionAtOffset("First sentence.".Length));
            Assert.Equal("First sentence.",selectable.SelectedPlainText());
            var pair=new StackPanel();var first=new SelectableChatText();var second=new SelectableChatText();
            first.Paragraph.Inlines.Add(new Run("One sentence."));second.Paragraph.Inlines.Add(new Run("Two\nlines."));pair.Children.Add(first);pair.Children.Add(second);
            var groupedSelection=new ChatTextSelection(pair);groupedSelection.Register(first);groupedSelection.Register(second);
            var pairHandle=ModalHost.Open(main,"Несколько сообщений",pair,440);PumpLayout(main);
            try
            {
                groupedSelection.SelectBetween(first,first.Paragraph.ContentStart,second,second.Paragraph.ContentEnd);
                Assert.Equal("One sentence.\nTwo\nlines.",groupedSelection.SelectedPlainText());
                ApplicationCommands.Copy.Execute(null,first);Assert.Equal("One sentence.\nTwo\nlines.",Clipboard.GetText());
            }
            finally{pairHandle.Close();}

        }
        finally{handle.Close();PumpLayout(main);}
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
