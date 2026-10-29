using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Wisp.Core;
using Wisp.Desktop;
using Xunit;

namespace Wisp.Desktop.Tests;

/// <summary>Runs in ThemeTests' existing application/STA and exercises the real server rail.</summary>
internal static class RailStabilityChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Check(Window owner, LocalStore store, UserProfile profile, Community community)
    {
        var main = Assert.IsType<MainWindow>(owner);
        var rows = Assert.IsType<VirtualRows>(typeof(MainWindow).GetField("servers", Private)!.GetValue(main));
        void Refresh() => typeof(MainWindow).GetMethod("RenderServers", Private)!.Invoke(main, null);
        Refresh(); Pump(owner);
        var data = Assert.Single(rows.Items.Cast<UiRow>(), item => item.Key == community.Id);
        var rail = Assert.Single(Descendants(rows).OfType<ServerRailItem>(), item => Equals(item.Button.ToolTip, community.Name));
        var pill = Assert.IsType<Border>(rail.FindName("ActivePill"));
        var artwork = Assert.IsType<Border>(rail.Button.Content);
        Assert.True(rail.IsSelected);
        Assert.Equal(40, rail.Button.Width); Assert.Equal(40, rail.Button.Height);
        Assert.Equal(Ui.Radius("Radius.ServerAvatar"), artwork.CornerRadius);
        Assert.Equal(8, artwork.CornerRadius.TopLeft);
        var pillBounds = pill.TransformToAncestor(rail).TransformBounds(new Rect(pill.RenderSize));
        var artBounds = rail.Button.TransformToAncestor(rail).TransformBounds(new Rect(rail.Button.RenderSize));
        Assert.True(artBounds.Left - pillBounds.Right >= Ui.Metric("Width.ServerPillGap"));
        Assert.InRange(artBounds.Right, 0, rail.ActualWidth + .01);

        bool reduced = Motion.Reduce;
        try
        {
            Motion.SetReduced(false); owner.Activate(); Pump(owner);
            Settle(owner, pill);
            Assert.Equal(Ui.Metric("Height.ServerPillActive"), pill.Height);
            Assert.False(pill.HasAnimatedProperties);
            // Moving between the icon/row and away must not start a new clock
            // when the selected target remains exactly the same.
            for (int index = 0; index < 3; index++)
            {
                rail.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent });
                rail.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseLeaveEvent });
                Refresh(); Pump(owner);
                Assert.Same(data, Assert.Single(rows.Items.Cast<UiRow>(), item => item.Key == community.Id));
                Assert.Same(rail, Assert.Single(Descendants(rows).OfType<ServerRailItem>(), item => Equals(item.Button.ToolTip, community.Name)));
                Assert.Same(artwork, rail.Button.Content);
                Assert.False(pill.HasAnimatedProperties);
                Assert.Equal(Ui.Metric("Height.ServerPillActive"), pill.Height);
            }
            // Actual detach/reload reproduces the old Loaded-reset path without
            // modifying selected state or discarding the server row itself.
            var presenter = Assert.IsAssignableFrom<ContentControl>(rail.Parent);
            presenter.Content = null; Pump(owner);
            Assert.Equal(Ui.Metric("Height.ServerPillActive"), pill.Height);
            presenter.Content = rail; Pump(owner);
            Assert.Equal(Ui.Metric("Height.ServerPillActive"), pill.Height);
            Assert.False(pill.HasAnimatedProperties);

            var shell = new SettingsShell();
            shell.Sections.Add(new("one", "Первая секция", "settings"));
            shell.Sections.Add(new("two", "Вторая секция", "users"));
            shell.SectionSelected += key => shell.PageContent = new TextBlock { Text = key };
            shell.Select("one"); shell.PageContent = new TextBlock { Text = "one" };
            var modal = ModalHost.Open(owner, "Настройки сервера", shell, options: new ModalOptions(FullWindow: true));
            try
            {
                Pump(owner);
                var navigation = Assert.Single(Descendants(shell).OfType<ListBox>());
                for (int index = 0; index < 4; index++)
                {
                    navigation.SelectedIndex = index % 2; Pump(owner);
                    Assert.Same(data, Assert.Single(rows.Items.Cast<UiRow>(), item => item.Key == community.Id));
                    Assert.Same(rail, Assert.Single(Descendants(rows).OfType<ServerRailItem>(), item => Equals(item.Button.ToolTip, community.Name)));
                    Assert.Equal(Ui.Metric("Height.ServerPillActive"), pill.Height);
                    Assert.False(pill.HasAnimatedProperties);
                }
            }
            finally { modal.Close(); Wait(owner, () => !Descendants(owner).Contains(shell), "Rail settings modal did not close."); }
        }
        finally { Motion.SetReduced(reduced); }

        CheckReconciliation(owner);
        CheckArtwork(store.RootDirectory);
    }

    private static void CheckArtwork(string root)
    {
        var pixels = new byte[8 * 4 * 4];
        for (int y = 0; y < 4; y++) for (int x = 0; x < 8; x++)
        {
            int offset = (y * 8 + x) * 4;
            pixels[offset + (x is >= 2 and <= 5 ? 1 : 2)] = 255; pixels[offset + 3] = 255;
        }
        var bitmap = BitmapSource.Create(8, 4, 96, 96, PixelFormats.Bgra32, null, pixels, 32); bitmap.Freeze();
        foreach (var extension in new[] { ".png", ".gif" })
        {
            string path = Path.Combine(root, "rounded-server-art" + extension);
            try
            {
                BitmapEncoder encoder = extension == ".png" ? new PngBitmapEncoder() : new GifBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var output = File.Create(path)) encoder.Save(output);
                var image = Assert.IsType<Image>(ServerRailItem.CreateAvatar("Uploaded", path, 40));
                Assert.Equal(Stretch.UniformToFill, image.Stretch);
                var clip = Assert.IsType<RectangleGeometry>(image.Clip);
                Assert.Equal(new Rect(0, 0, 40, 40), clip.Rect); Assert.Equal(8, clip.RadiusX); Assert.Equal(8, clip.RadiusY);
                image.Measure(new Size(40, 40)); image.Arrange(new Rect(0, 0, 40, 40)); image.UpdateLayout();
                var rendered = new RenderTargetBitmap(40, 40, 96, 96, PixelFormats.Pbgra32); rendered.Render(image);
                var outputPixels = new byte[40 * 40 * 4]; rendered.CopyPixels(outputPixels, 160, 0);
                Assert.Equal(0, outputPixels[3]);
                int center = (20 * 40 + 20) * 4; Assert.Equal(255, outputPixels[center + 3]);
                Assert.True(outputPixels[center + 1] > outputPixels[center + 2]);
            }
            finally { File.Delete(path); }
        }
    }

    private static void CheckReconciliation(Window owner)
    {
        var list = new VirtualRows { Width = 260, Height = 180 };
        int created = 0;
        UiRow Mutable(string text) => new("stable", () => { created++; return new TextBlock { Text = text }; }, Update: element => ((TextBlock)element).Text = text);
        list.Reconcile([Mutable("before"), new("immutable", () => new TextBlock { Text = "first" })]);
        var modal = ModalHost.Open(owner, "Keyed rail rows", list, 360);
        try
        {
            Pump(owner);
            var stableData = list.Items.Cast<UiRow>().First();
            var stable = Assert.Single(Descendants(list).OfType<TextBlock>(), block => block.Text == "before");
            var oldImmutable = Assert.Single(Descendants(list).OfType<TextBlock>(), block => block.Text == "first");
            list.Reconcile([Mutable("after"), new("immutable", () => new TextBlock { Text = "second" })]); Pump(owner);
            Assert.Same(stableData, list.Items.Cast<UiRow>().First());
            Assert.Same(stable, Assert.Single(Descendants(list).OfType<TextBlock>(), block => block.Text == "after"));
            Assert.Equal(1, created);
            Assert.DoesNotContain(Descendants(list), element => ReferenceEquals(element, oldImmutable));
            Assert.Contains(Descendants(list).OfType<TextBlock>(), block => block.Text == "second");
            Assert.Throws<ArgumentException>(() => list.Reconcile([Mutable("x"), Mutable("y")]));
            Assert.Equal(2, list.Count);
            list.Reconcile([new("immutable", () => new TextBlock { Text = "moved" }), Mutable("last")]); Pump(owner);
            Assert.Equal(new[] { "immutable", "stable" }, list.Keys);
            Assert.Same(stableData, list.Items.Cast<UiRow>().Last());
        }
        finally { modal.Close(); Wait(owner, () => !Descendants(owner).Contains(list), "Keyed rows modal did not close."); }
    }

    private static void Settle(Window owner, Border pill) => Wait(owner, () => !pill.HasAnimatedProperties, "Server pill animation did not finish.");
    private static void Wait(Window window, Func<bool> ready, string message)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (!ready() && timer.Elapsed < TimeSpan.FromSeconds(3)) { Pump(window); Thread.Sleep(5); }
        Assert.True(ready(), message);
    }
    private static void Pump(Window window)
    {
        window.UpdateLayout(); window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout();
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
