using System.Windows.Controls;
using System.Windows.Threading;
using Wisp.Desktop.Media;

namespace Wisp.Desktop;

public partial class RecordingWaveformView : UserControl, IDisposable
{
    private readonly object gate = new();
    private readonly Queue<float> pending = new();
    private readonly Queue<float> visible = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private VoiceRecorder? recorder;
    private TimeSpan duration;
    public RecordingWaveformView()
    {
        InitializeComponent(); Ui.Name(this, "Запись голосового сообщения");
        timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => UpdateTimer();
        IsVisibleChanged += (_, _) => UpdateTimer();
        Unloaded += (_, _) => timer.Stop();
    }
    public void Bind(VoiceRecorder source)
    {
        Dispose(); Reset();
        recorder = source; source.LevelChanged += Level; source.Started += Started; source.Completed += Completed; source.Error += Completed;
        UpdateTimer(); Refresh();
    }
    public void Reset()
    {
        lock (gate) { visible.Clear(); pending.Clear(); duration = TimeSpan.Zero; }
        Bars.Peaks = Array.Empty<float>(); Elapsed.Text = "0:00";
    }
    private void Started() => OnUi(() => { Reset(); UpdateTimer(); });
    private void Completed(string _) => OnUi(() => { Refresh(); timer.Stop(); });
    private void UpdateTimer() { if (IsLoaded && IsVisible && recorder?.IsRecording == true) timer.Start(); else timer.Stop(); }
    private void OnUi(Action action)
    {
        if (Dispatcher.HasShutdownStarted) return;
        if (Dispatcher.CheckAccess()) action(); else Dispatcher.BeginInvoke(action);
    }
    private void Level(float value, TimeSpan elapsed)
    {
        lock (gate)
        {
            duration = elapsed; pending.Enqueue(Math.Clamp(value, 0, 1));
            while (pending.Count > 100) pending.Dequeue();
        }
    }
    private void Refresh()
    {
        TimeSpan elapsed; bool changed = false;
        lock (gate)
        {
            elapsed = duration;
            while (pending.TryDequeue(out var value)) { visible.Enqueue(value); changed = true; }
        }
        while (visible.Count > 100) visible.Dequeue();
        if (changed) Bars.Peaks = visible.ToArray();
        Elapsed.Text = elapsed.ToString("m\\:ss");
    }
    public void Dispose()
    {
        timer.Stop();
        if (recorder is not null) { recorder.LevelChanged -= Level; recorder.Started -= Started; recorder.Completed -= Completed; recorder.Error -= Completed; }
        recorder = null;
    }
}
