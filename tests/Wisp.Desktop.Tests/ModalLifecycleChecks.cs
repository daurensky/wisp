using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Wisp.Core;
using Wisp.Desktop;
using Wisp.Desktop.Media;
using Xunit;

namespace Wisp.Desktop.Tests;

/// <summary>Runs in the existing real WPF STA/Application fixture.</summary>
internal static class ModalLifecycleChecks
{
    internal static bool LegacyTransparencyReproduced { get; private set; }
    internal static void Check(Window owner, UserProfile profile, Action<string>? snapshot = null)
    {
        bool light = ThemeManager.IsLight, reduced = Motion.Reduce;
        var previous = (owner.Width, owner.Height);
        var errors = new List<string>();
        var layer = ModalHost.Surface(owner);
        // Deliberately contrast the page behind the modal. A transparent opening,
        // page replacement or closing frame would show this instead of Surface.
        var underlay = new Border { IsHitTestVisible = false };
        layer.Children.Add(underlay);
        int captured = 0;
        try
        {
            foreach (bool requestedLight in new[] { false, true })
            {
                if (ThemeManager.IsLight != requestedLight) ThemeManager.Toggle();
                underlay.Background = requestedLight ? Brushes.Black : Brushes.White;
                if (!requestedLight)
                {
                    owner.Activate(); Pump(owner); Motion.SetReduced(false);
                    VerifyLegacyOpacityLeak(owner, layer);
                }
                foreach (var size in new[] { new Size(800, 560), new Size(1040, 780) })
                {
                    owner.Width = size.Width; owner.Height = size.Height;
                    owner.Activate(); Pump(owner);
                    Motion.SetReduced(false);
                    var view = new SettingsView(new SettingsContext
                    {
                        Profile = profile, Audio = new(), ApplyAudio = _ => Task.CompletedTask,
                        EditProfile = () => { }, ShowNotifications = () => { }, ReportError = errors.Add,
                        InputDevices = () => [new AudioDeviceChoice(null, "Default input")],
                        OutputDevices = () => [new AudioDeviceChoice(null, "Default output")]
                    });
                    var modal = ModalHost.Open(owner, "Настройки", view, options: new ModalOptions(FullWindow: true));
                    var shade = Assert.IsType<Grid>(layer.Children[layer.Children.Count - 1]);
                    var card = Assert.Single(shade.Children.OfType<Border>(), border => border.Name == "ModalCard");
                    void Sample()
                    {
                        if (!ReferenceEquals(shade.Parent, layer) || !ReferenceEquals(card.Parent, shade) || card.ActualWidth <= 24 || card.ActualHeight <= 24) return;
                        Assert.Equal(1, shade.Opacity); Assert.Equal(1, card.Opacity);
                        var expected = Assert.IsType<SolidColorBrush>(Application.Current.FindResource("Surface")).Color;
                        var point = card.TranslatePoint(new Point(card.ActualWidth - 12, card.ActualHeight - 12), layer);
                        Assert.Equal(expected, Pixel(layer, point)); captured++;
                    }
                    try
                    {
                        Pump(owner); Sample(); CollectFrames(owner, Sample, 230);
                        var margin = new Thickness(48);
                        Assert.Equal(margin, card.Margin);
                        Assert.Equal((Thickness)Application.Current.FindResource("Margin.SettingsModal"), card.Margin);
                        var origin = card.TranslatePoint(new Point(), layer);
                        Assert.InRange(origin.X, 47, 49); Assert.InRange(origin.Y, 47, 49);
                        Assert.InRange(card.ActualWidth, layer.ActualWidth - 97, layer.ActualWidth - 95);
                        Assert.InRange(card.ActualHeight, layer.ActualHeight - 97, layer.ActualHeight - 95);
                        var header = Assert.Single(Descendants(card).OfType<Border>(), border => border.Name == "ModalHeader");
                        Assert.Equal(new Thickness(0, 0, 0, Ui.Metric("Size.Hairline")), header.BorderThickness);
                        Assert.InRange(header.ActualWidth, card.ActualWidth - 3, card.ActualWidth - 1);
                        Assert.Same(Application.Current.FindResource("Border"), header.BorderBrush);
                        var navigation = Assert.IsType<ListBox>(view.Shell.FindName("Navigation"));
                        for (int index = 0; index < navigation.Items.Count; index++)
                        {
                            navigation.SelectedIndex = index; owner.UpdateLayout(); Sample();
                            Assert.Equal(1, Assert.IsType<ContentControl>(typeof(SettingsView).GetField("page", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(view)).Opacity);
                            var item = Assert.IsType<ListBoxItem>(navigation.ItemContainerGenerator.ContainerFromIndex(index));
                            Assert.Equal(Cursors.Hand, item.Cursor);
                            CollectFrames(owner, Sample, 40);
                        }
                        Assert.NotEqual(Cursors.Hand, navigation.Cursor);
                        if (size.Width == 1040) snapshot?.Invoke(requestedLight ? "settings-modal-light" : "settings-modal-dark");
                    }
                    finally
                    {
                        modal.Close(); CollectFrames(owner, Sample, 150); Pump(owner);
                        Assert.DoesNotContain(shade, layer.Children.Cast<UIElement>());
                    }
                    VerifyServerShell(owner, profile, errors, snapshot, requestedLight);
                }
            }
            Assert.Empty(errors); Assert.True(captured >= 16, "The frame check did not inspect the running WPF transitions.");
            UiCheckEvidence.Write("modal_opaque_composition_samples",captured);
            UiCheckEvidence.Write("modal_settings_both_themes_sizes_open_switch_close",true);
        }
        finally
        {
            layer.Children.Remove(underlay);
            owner.Width = previous.Width; owner.Height = previous.Height;
            if (ThemeManager.IsLight != light) ThemeManager.Toggle();
            Motion.SetReduced(reduced); Pump(owner);
        }
    }

    private static void VerifyServerShell(Window owner, UserProfile profile, List<string> errors, Action<string>? snapshot, bool light)
    {
        var community = new Community("modal-check", "Modal checks", profile.Id);
        var state = new CommunitySnapshot(community, [], [profile], new() { [profile.Id] = "owner" });
        using var view = new CommunitySettingsView(new CommunitySettingsContext
        {
            UserId = profile.Id, Snapshot = state, Reload = () => Task.FromResult(state),
            Apply = _ => Task.CompletedTask, Icon = c => ServerRailItem.CreateAvatar(c.Name, null, 64),
            SaveIcon = _ => Task.CompletedTask, Assets = _ => Task.FromResult<IReadOnlyList<MediaAsset>>([]),
            Upload = (_, _, _, _) => Task.CompletedTask, Preview = (_, _) => { }, Confirm = _ => true,
            ReportError = errors.Add
        });
        var modal = ModalHost.Open(owner, "Настройки сервера", view, options: new ModalOptions(FullWindow: true));
        var layer = ModalHost.Surface(owner); var shade = Assert.IsType<Grid>(layer.Children[layer.Children.Count - 1]);
        var card = Assert.Single(shade.Children.OfType<Border>(), border => border.Name == "ModalCard");
        void Sample()
        {
            if (!ReferenceEquals(shade.Parent, layer) || card.ActualWidth <= 24 || card.ActualHeight <= 24) return;
            Assert.Equal(1, shade.Opacity); Assert.Equal(1, card.Opacity);
            var expected = Assert.IsType<SolidColorBrush>(Application.Current.FindResource("Surface")).Color;
            var point = card.TranslatePoint(new Point(card.ActualWidth - 12, card.ActualHeight - 12), layer);
            Assert.Equal(expected, Pixel(layer, point));
        }
        try
        {
            Pump(owner); Sample(); CollectFrames(owner, Sample, 230); Assert.Equal(new Thickness(48), card.Margin);
            var navigation = Assert.IsType<ListBox>(view.Shell.FindName("Navigation"));
            for (int index = 0; index < navigation.Items.Count; index++)
            {
                navigation.SelectedIndex = index; Pump(owner);
                var item = Assert.IsType<ListBoxItem>(navigation.ItemContainerGenerator.ContainerFromIndex(index));
                Assert.Equal(Cursors.Hand, item.Cursor); Assert.Equal(1, card.Opacity); Sample(); CollectFrames(owner, Sample, 40);
            }
            snapshot?.Invoke(light ? "server-modal-light" : "server-modal-dark");
        }
        finally { modal.Close(); CollectFrames(owner, Sample, 150); Pump(owner); }
    }

    private static void VerifyLegacyOpacityLeak(Window owner, Grid layer)
    {
        var oldShade = new Grid(); oldShade.SetResourceReference(Panel.BackgroundProperty, "Overlay");
        var oldCard = new Border { Width = 400, Height = 200, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        oldCard.SetResourceReference(Border.BackgroundProperty, "Surface"); oldShade.Children.Add(oldCard); layer.Children.Add(oldShade);
        try
        {
            owner.UpdateLayout();
            // Counterfactual of the old ModalHost: these are its actual two
            // Motion calls, with an explicitly white document behind the card.
            // The brush is already correct; parent opacity alone leaks white.
            bool animated = Motion.Allowed(oldShade);
            Motion.Enter(oldCard); Motion.Fade(oldShade, 0, 1, "Normal");
            if (animated)
            {
                var point = oldCard.TranslatePoint(new Point(200, 100), layer);
                Assert.Equal(Colors.White, Pixel(layer, point));
                Assert.True(oldShade.Opacity < 1); Assert.True(oldCard.Opacity < 1);
                LegacyTransparencyReproduced = true;
            }
            UiCheckEvidence.Write("legacy_modal_opacity_white_frame_reproduced",LegacyTransparencyReproduced);
            UiCheckEvidence.Write("legacy_modal_animation_allowed",animated);
        }
        finally { Motion.Stop(oldCard); Motion.Stop(oldShade); layer.Children.Remove(oldShade); }
    }

    private static Color Pixel(FrameworkElement visual, Point point)
    {
        var sample = new DrawingVisual();
        using (var drawing = sample.RenderOpen())
        {
            drawing.PushTransform(new TranslateTransform(-Math.Floor(point.X), -Math.Floor(point.Y)));
            drawing.DrawRectangle(new VisualBrush(visual), null, new Rect(0, 0, visual.ActualWidth, visual.ActualHeight));
            drawing.Pop();
        }
        var bitmap = new RenderTargetBitmap(1, 1, 96, 96, PixelFormats.Pbgra32); bitmap.Render(sample);
        var pixel = new byte[4]; bitmap.CopyPixels(pixel, 4, 0);
        return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        var seen = new HashSet<DependencyObject>();
        return Walk(parent);
        IEnumerable<DependencyObject> Walk(DependencyObject node)
        {
            foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
            {
                if (!seen.Add(child)) continue;
                yield return child; foreach (var nested in Walk(child)) yield return nested;
            }
            if (node is not Visual) yield break;
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
            {
                var child = VisualTreeHelper.GetChild(node, index);
                if (!seen.Add(child)) continue;
                yield return child; foreach (var nested in Walk(child)) yield return nested;
            }
        }
    }

    private static void Pump(Window owner)
    {
        owner.UpdateLayout();
        owner.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static void CollectFrames(Window owner, Action inspect, int milliseconds)
    {
        var frame = new DispatcherFrame(); var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        Exception? failure = null;
        EventHandler rendered = (_, _) => { if (failure is not null) return; try { inspect(); } catch (Exception error) { failure = error; frame.Continue = false; } };
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) => { owner.InvalidateVisual(); if (DateTime.UtcNow >= until) { timer.Stop(); frame.Continue = false; } };
        CompositionTarget.Rendering += rendered;
        try { timer.Start(); Dispatcher.PushFrame(frame); if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw(); }
        finally { timer.Stop(); CompositionTarget.Rendering -= rendered; }
    }
}
