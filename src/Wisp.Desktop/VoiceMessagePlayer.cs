using NAudio.Wave;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Wisp.Desktop;

public sealed class VoiceMessagePlayer : UserControl, IDisposable
{
    private static WeakReference<VoiceMessagePlayer>? active;
    private readonly string path;
    private readonly Action<string> error;
    private readonly Func<bool> allowed;
    private readonly Button play = new() { Content = "▶", ToolTip = "Воспроизвести / пауза" };
    private readonly Slider seek = new() { Minimum = 0, Maximum = 1, MinWidth = 150, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock time = new() { Text = "0:00", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private AudioFileReader? reader;
    private WaveOutEvent? output;
    private bool updating;
    public VoiceMessagePlayer(string path, Action<string> error, Func<bool>? allowed = null)
    {
        this.path = path; this.error = error; this.allowed = allowed ?? (() => true);
        var row = new DockPanel { Margin = new Thickness(0, 8, 0, 0), MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left };
        DockPanel.SetDock(play, Dock.Left); row.Children.Add(play); DockPanel.SetDock(time, Dock.Right); row.Children.Add(time); row.Children.Add(seek); Content = row;
        play.Click += (_, _) => { try { Toggle(); } catch (Exception ex) { Dispose(); error(ex.Message); } };
        seek.ValueChanged += (_, _) => { if (updating) return; try { EnsureReader(); reader!.CurrentTime = TimeSpan.FromSeconds(Math.Clamp(seek.Value, 0, reader.TotalTime.TotalSeconds)); Refresh(); } catch (Exception ex) { error(ex.Message); } };
        timer.Tick += (_, _) => Refresh(); Unloaded += (_, _) => Dispose();
        try { using var metadata = new AudioFileReader(path); seek.Maximum = Math.Max(.01, metadata.TotalTime.TotalSeconds); time.Text = $"0:00 / {metadata.TotalTime:m\\:ss}"; } catch (Exception ex) { error(ex.Message); }
    }
    private void EnsureReader() => reader ??= new AudioFileReader(path);
    private void Toggle()
    {
        if (!allowed()) { error("Звук выключен. Включи наушники для прослушивания."); return; }
        if (output?.PlaybackState == PlaybackState.Playing) { Pause(); return; }
        if (active?.TryGetTarget(out var previous) == true && previous != this) previous.Pause();
        EnsureReader();
        if (output is null)
        {
            output = new WaveOutEvent { DesiredLatency = 100 }; output.Init(reader);
        }
        if (reader!.CurrentTime >= reader.TotalTime - TimeSpan.FromMilliseconds(50)) reader.CurrentTime = TimeSpan.Zero;
        active = new(this); output!.Play(); play.Content = "Ⅱ"; timer.Start(); Refresh();
    }
    private void Pause() { output?.Pause(); play.Content = "▶"; timer.Stop(); Refresh(); }
    public static void PauseActive() { if (active?.TryGetTarget(out var player) == true) player.Pause(); }
    private void Refresh()
    {
        if (reader is null) return;
        updating = true; seek.Value = reader.CurrentTime.TotalSeconds; updating = false;
        time.Text = $"{reader.CurrentTime:m\\:ss} / {reader.TotalTime:m\\:ss}";
        if (output?.PlaybackState == PlaybackState.Stopped) { play.Content = "▶"; timer.Stop(); }
    }
    public void Dispose() { timer.Stop(); output?.Stop(); output?.Dispose(); output = null; reader?.Dispose(); reader = null; play.Content = "▶"; }
}
