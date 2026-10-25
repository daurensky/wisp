using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
        var thread = new Thread(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "wisp-theme-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            App? app = null;
            Window? derived = null;
            AuthWindow? auth = null;
            try
            {
                app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.InitializeComponent();
                // An old version's marker must not override the new default.
                File.WriteAllText(Path.Combine(root, "light-theme"), "1");
                ThemeManager.Initialize(root);
                Assert.False(ThemeManager.IsLight);

                using var store = new LocalStore(root);
                derived = new DerivedWindow();
                auth = new AuthWindow(store);
                AssertWindowColors(derived, Colors.Black, Color.FromRgb(237, 237, 242));
                AssertWindowColors(auth, Colors.Black, Color.FromRgb(237, 237, 242));
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The Windows theme check did not finish.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void AssertWindowColors(Window window, Color background, Color foreground)
    {
        Assert.Equal(background, BrushColor(window.Background));
        Assert.Equal(foreground, BrushColor(window.Foreground));
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
