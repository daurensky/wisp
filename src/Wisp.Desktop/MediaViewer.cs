using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Wisp.Core;

namespace Wisp.Desktop;

public static class MediaViewer
{
    public static ModalHandle Show(Window owner, MediaAsset asset, Action<string> error, Func<bool>? canPlay = null)
    {
        var allowed = canPlay ?? (() => true);
        var body = new DockPanel { Margin = Ui.Insets(Ui.M), MaxHeight = Math.Max(180, owner.ActualHeight - 150) };
        var download = Buttons.Tool("download", "Скачать вложение", () => { try { AttachmentCard.Save(owner, asset); } catch (Exception ex) { error(ex.Message); } });
        var heading = new DockPanel(); DockPanel.SetDock(download, Dock.Right); heading.Children.Add(download); heading.Children.Add(Ui.Label(asset.Name)); DockPanel.SetDock(heading, Dock.Top); body.Children.Add(heading);
        MediaElement? media = null; DispatcherTimer? timer = null;
        if (AttachmentCard.IsImage(asset.Name))
        {
            var image = AvatarCatalog.Image(asset.LocalPath, 960, mode: GifPlaybackMode.Always); image.Width = image.Height = double.NaN; image.Stretch = Stretch.Uniform; image.MaxHeight = Math.Max(160, owner.ActualHeight - 230); body.Children.Add(image);
        }
        else
        {
            media = new MediaElement { Source = new Uri(Path.GetFullPath(asset.LocalPath)), LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, Stretch = Stretch.Uniform, ScrubbingEnabled = true, Volume = .7, IsMuted = true };
            var controls = new DockPanel { Margin = Ui.Insets(0, Ui.S) }; var toggle = new ToggleIconButton { IconOff = "play", IconOn = "pause", ToolTipOff = "Воспроизвести", ToolTipOn = "Пауза" };
            var seek = new Slider { Minimum = 0, Maximum = 1, VerticalAlignment = VerticalAlignment.Center }; Ui.Name(seek, "Позиция видео");
            var time = Ui.Label("0:00", Ui.Caption, true); var volume = new Slider { Minimum = 0, Maximum = 1, Value = .7, Width = 90, ToolTip = "Громкость видео" }; Ui.Name(volume, "Громкость видео");
            bool playing = false, updating = false;
            toggle.Click += (_, _) =>
            {
                if (!allowed()) { playing = false; media.IsMuted = true; media.Pause(); toggle.IsChecked = false; error("Звук выключен. Включи наушники для прослушивания."); return; }
                playing = !playing;
                if (playing)
                {
                    if (media.NaturalDuration.HasTimeSpan && media.Position >= media.NaturalDuration.TimeSpan - TimeSpan.FromMilliseconds(50)) media.Position = TimeSpan.Zero;
                    media.IsMuted = false; media.Play();
                }
                else media.Pause();
                toggle.IsChecked = playing;
            };
            seek.ValueChanged += (_, _) => { if (!updating) media.Position = TimeSpan.FromSeconds(seek.Value); };
            volume.ValueChanged += (_, _) => media.Volume = volume.Value;
            media.MediaOpened += (_, _) => { if (media.NaturalDuration.HasTimeSpan) seek.Maximum = Math.Max(.01, media.NaturalDuration.TimeSpan.TotalSeconds); media.Pause(); media.Position = TimeSpan.Zero; media.IsMuted = !allowed(); };
            media.MediaEnded += (_, _) => { playing = false; toggle.IsChecked = false; media.Pause(); };
            media.MediaFailed += (_, args) => { error("Не удалось открыть медиа: " + args.ErrorException.Message); };
            DockPanel.SetDock(toggle, Dock.Left); controls.Children.Add(toggle); DockPanel.SetDock(volume, Dock.Right); controls.Children.Add(volume); DockPanel.SetDock(time, Dock.Right); controls.Children.Add(time); controls.Children.Add(seek); DockPanel.SetDock(controls, Dock.Bottom); body.Children.Add(controls); body.Children.Add(media);
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            timer.Tick += (_, _) =>
            {
                bool enabled = allowed(); media.IsMuted = !enabled;
                if (!enabled && playing) { media.Pause(); playing = false; toggle.IsChecked = false; }
                updating = true; seek.Value = Math.Clamp(media.Position.TotalSeconds, 0, seek.Maximum); updating = false; time.Text = $"{media.Position:m\\:ss}";
            };
            media.Loaded += (_, _) => { media.Play(); timer.Start(); };
        }
        var handle = ModalHost.Open(owner, "", body, 980); handle.Closed += () => { timer?.Stop(); media?.Close(); };
        return handle;
    }
}
