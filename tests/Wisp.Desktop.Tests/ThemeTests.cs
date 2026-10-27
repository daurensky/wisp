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
                SaveSnapshot(auth, "auth-light");

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
            Assert.Contains(Descendants(main).OfType<TextBlock>(), text => text.Inlines.OfType<InlineUIContainer>().Any());
            using (var exclusive = new FileStream(emoji.LocalPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Assert.True(exclusive.Length > 0);
            Assert.Equal(Visibility.Collapsed, Descendants(main).OfType<Button>().Single(button => System.Windows.Automation.AutomationProperties.GetName(button) == "Отменить запись").Visibility);
            phase("responsive layout"); main.Width = Ui.Metric("Width.WindowMinimum"); PumpLayout(main); SaveSnapshot(main, "main-compact-dark");
            var lists = VisualDescendants(main).OfType<VirtualRows>().ToArray();
            Assert.Equal(4, lists.Length); Assert.Equal(3, lists.Count(list => list.IsVisible));
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
