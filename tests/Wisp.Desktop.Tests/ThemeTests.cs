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
            var windowCount = Application.Current.Windows.Count;
            var notices = new NotificationArea(); notices.Add("Тест", true);
            phase("notification overlay"); var overlay = ModalHost.Open(main, "Уведомления", notices);
            Assert.Equal(windowCount, Application.Current.Windows.Count); overlay.Close();
            ModalHost.Open(main, "Уведомления", notices).Close(); // Reopening must detach from the old parent.
            phase("form overlay"); var dialog = new FormDialog(main, "Профиль"); dialog.Field("Имя", "UiTester"); dialog.Accept();
            main.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => { Assert.Equal(windowCount, Application.Current.Windows.Count); dialog.DialogResult = true; }));
            Assert.True(dialog.ShowDialog());
            phase("send on Enter"); var composer = Assert.Single(Descendants(main).OfType<TextBox>(), box => box.AcceptsReturn);
            composer.Text = "hello :loop: world";
            var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(main), 0, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            composer.RaiseEvent(key);
            Assert.True(key.Handled); Assert.Empty(composer.Text);
            var message = Assert.Single(store.GetMessages(channel.Id));
            Assert.Equal(emoji.Id, message.Emojis![":loop:"]);
            Assert.Contains(Descendants(main).OfType<TextBlock>(), text => text.Inlines.OfType<InlineUIContainer>().Any());
            using (var exclusive = new FileStream(emoji.LocalPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Assert.True(exclusive.Length > 0);
            Assert.Equal(Visibility.Collapsed, Descendants(main).OfType<Button>().Single(button => Equals(button.Content, "Отменить запись")).Visibility);
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
        foreach (var child in LogicalTreeHelper.GetChildren(element).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private sealed class DerivedWindow : ThemedWindow { }
}
