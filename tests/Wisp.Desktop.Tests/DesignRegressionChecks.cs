using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Wisp.Core;
using Wisp.Desktop;
using Wisp.Desktop.Media;
using XamlAnimatedGif;
using Xunit;

namespace Wisp.Desktop.Tests;

// Runs inside ThemeTests' single WPF Application, so the checks exercise the
// same resource inheritance and detached popup trees as the real application.
internal static class DesignRegressionChecks
{
    internal static void Check(Window owner, string root)
    {
        Assert.Equal(TextRenderingMode.Grayscale, TextOptions.GetTextRenderingMode(owner));
        Assert.Equal(TextFormattingMode.Display, TextOptions.GetTextFormattingMode(owner));
        Assert.True(Typography.HasOnest);
        var detached = new Button { Content = "Popup action" };
        Ui.Style(detached, "Button.Secondary"); detached.ApplyTemplate();
        var section = new TextBlock { Text = "Popup section" }; Ui.Style(section, "Text.Section");
        Assert.Equal(TextRenderingMode.Grayscale, TextOptions.GetTextRenderingMode(detached));
        Assert.Equal(TextRenderingMode.Grayscale, TextOptions.GetTextRenderingMode(section));
        using var voice = new VoiceService();
        var gif = Path.Combine(root, "design-selected-cover.gif");
        WriteGif(gif);
        var profile = new UserProfile("design-remote", "Remote user", AvatarCoverFrame: 1);
        var participant = new VoiceParticipantRow(new PeerPresence(profile, "voice", false, false), gif, voice, false);
        var rail = new ServerRailItem("Wisp Community", null, true, () => { });
        var text = new TextBlock { Text = "Onest — Grayscale" };
        var input = new TextBox { Text = "Сообщение" };
        Ui.Style(input, "Input.Composer");
        var rich = new RichTextBox();
        var button = new Button { Content = "Профиль" };
        var always = AvatarCatalog.Image(gif, 32, mode: GifPlaybackMode.Always, coverFrame: 1);
        var content = new StackPanel { Children = { text, input, rich, button, rail, participant, always } };
        var probe = new ThemedWindow { Title = "Design regression", Width = 440, Height = 560, Content = content };
        ContextMenu? menu = null;
        ToolTip? tip = null;
        try
        {
            probe.Show(); Pump(probe);
            foreach (var element in new DependencyObject[] { probe, text, input, rich, button })
            {
                Assert.Equal(TextRenderingMode.Grayscale, TextOptions.GetTextRenderingMode(element));
                Assert.Equal(TextFormattingMode.Display, TextOptions.GetTextFormattingMode(element));
            }
            Assert.Equal((Brush)Application.Current.FindResource("InputFocusSoft"), InputChrome.GetFocusBrush(input));
            Assert.Equal(288d, Ui.Metric("Width.Channels"));
            Assert.Equal(0, rail.Button.BorderThickness.Left);
            Assert.Equal(new Thickness(0), rail.Button.Margin);
            var pill = Assert.IsType<Border>(rail.FindName("ActivePill"));
            Assert.Equal(Ui.Metric("Height.ServerPillActive"), pill.Height);
            double width = rail.Button.ActualWidth, height = rail.Button.ActualHeight;
            rail.IsSelected = false; Pump(probe);
            Assert.Equal(rail.IsMouseOver ? Ui.Metric("Height.ServerPillHover") : 0, pill.Height);
            Assert.Equal(width, rail.Button.ActualWidth); Assert.Equal(height, rail.Button.ActualHeight);
            var placeholder = Assert.IsType<Border>(rail.Button.Content);
            Assert.Equal(placeholder.Width / 2, placeholder.CornerRadius.TopLeft);
            Assert.Equal("WI", Assert.IsType<TextBlock>(placeholder.Child).Text);
            Assert.Equal((Brush)Application.Current.FindResource("ServerPlaceholderBackground"), placeholder.Background);

            // A selected paused GIF cover must be the composited chosen frame,
            // both initially and after a hover playback reset.
            var avatar = Assert.IsType<Image>(participant.Avatar);
            PumpUntil(probe, () => avatar.Source is BitmapSource bitmap && Pixel(bitmap) == Colors.Blue);
            GifPlayback.Reset(avatar);
            Assert.Equal(Colors.Blue, Pixel(Assert.IsAssignableFrom<BitmapSource>(avatar.Source)));
            Assert.Equal(Ui.L, participant.Margin.Left);
            CheckInactiveCover(probe, always, gif);

            // The new no-gutter template must still present interactive headers.
            menu = Assert.IsType<ContextMenu>(participant.ContextMenu);
            menu.PlacementTarget = participant; menu.IsOpen = true; Pump(probe);
            Assert.Equal(TextRenderingMode.Grayscale, TextOptions.GetTextRenderingMode(menu));
            var slider = Assert.Single(Descendants(menu).OfType<Slider>());
            Assert.True(slider.ActualWidth >= 160);
            slider.Value = 31;
            Assert.Equal(.31f, voice.GetPeerVolume(profile.Id));
            Assert.True(menu.IsOpen);
            var interactiveHeader = Assert.IsType<StackPanel>(Assert.IsType<MenuItem>(menu.Items[1]).Header);
            Assert.InRange(interactiveHeader.TranslatePoint(new Point(), menu).X, Ui.S, Ui.Xxl);
            menu.IsOpen = false;
            tip = new ToolTip { PlacementTarget = button, Content = "Подсказка", IsOpen = true };
            Pump(probe);
            Assert.Equal(TextRenderingMode.Grayscale, TextOptions.GetTextRenderingMode(tip));
        }
        finally
        {
            if (menu is not null) menu.IsOpen = false;
            if (tip is not null) tip.IsOpen = false;
            probe.Close();
        }
    }

    private static void CheckInactiveCover(Window probe, Image image, string gif)
    {
        bool reduced = Motion.Reduce;
        var focus = new ThemedWindow { Title = "Focus transfer", Width = 180, Height = 100 };
        try
        {
            PumpUntil(probe, () => image.Source is BitmapSource bitmap && Pixel(bitmap) == Colors.Blue);
            Motion.SetReduced(false); Pump(probe);
            if (Motion.Allowed(image))
                PumpUntil(probe, () => AnimationBehavior.GetAnimator(image) is not null);
            var animator = AnimationBehavior.GetAnimator(image);
            if (animator is not null && Motion.Allowed(image))
                PumpUntil(probe, () => animator.CurrentFrameIndex == 1);
            var animatedSource = image.Source;
            animator?.Pause(); Pump(probe);
            int? position = animator?.CurrentFrameIndex;
            // Deliberately leave a different displayed frame: losing focus must
            // restore the selected blue cover without rewinding its animator.
            image.Source = new ImageEditingDocument(gif).GetFrame(0, 32);
            Assert.Equal(Colors.Red, Pixel(Assert.IsAssignableFrom<BitmapSource>(image.Source)));
            focus.Show(); focus.Activate(); Pump(probe);
            Assert.False(probe.IsActive);
            Assert.Equal(Colors.Blue, Pixel(Assert.IsAssignableFrom<BitmapSource>(image.Source)));
            if (animator is not null)
            {
                Assert.True(animator.IsPaused);
                Assert.Equal(position, animator.CurrentFrameIndex);
            }
            focus.Close(); probe.Activate(); Pump(probe);
            if (animator is not null && Motion.Allowed(image))
            {
                Assert.False(animator.IsPaused);
                Assert.Same(animatedSource, image.Source);
            }
        }
        finally
        {
            focus.Close(); Motion.SetReduced(reduced); Pump(probe);
        }
    }

    private static void WriteGif(string path)
    {
        var encoder = new GifBitmapEncoder();
        foreach (var color in new[] { Colors.Red, Colors.Blue })
        {
            byte[] pixels = Enumerable.Range(0, 4).SelectMany(_ => new[] { color.B, color.G, color.R, color.A }).ToArray();
            var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
            var metadata = new BitmapMetadata("gif"); metadata.SetQuery("/grctlext/Delay", (ushort)10);
            metadata.SetQuery("/grctlext/Disposal", (byte)1);
            encoder.Frames.Add(BitmapFrame.Create(bitmap, null, metadata, null));
        }
        using var output = File.Create(path); encoder.Save(output);
    }

    private static Color Pixel(BitmapSource source)
    {
        var bitmap = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var pixel = new byte[4]; bitmap.CopyPixels(new Int32Rect(0, 0, 1, 1), pixel, 4, 0);
        return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static void Pump(Window window)
    {
        window.UpdateLayout(); window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout();
    }

    private static void PumpUntil(Window window, Func<bool> ready)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (!ready() && timer.Elapsed < TimeSpan.FromSeconds(4))
        {
            Pump(window); Thread.Sleep(5);
        }
        Assert.True(ready(), "The selected composited GIF cover did not appear.");
    }
}
