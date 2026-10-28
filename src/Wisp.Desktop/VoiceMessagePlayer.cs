using NAudio.Wave;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Wisp.Desktop.Media;

namespace Wisp.Desktop;

/// <summary>A lightweight view onto the independent, selected-endpoint voice-message session.</summary>
public sealed class VoiceMessagePlayer : UserControl, IDisposable
{
    private static FilePlaybackSession? session;
    private static string? outputId;
    private static event Action? SessionChanged;
    private readonly string path;
    private readonly Action<string> error;
    private readonly Func<bool> allowed;
    private readonly ToggleIconButton play = new() { IconOff = "play", IconOn = "pause", ToolTipOff = "Воспроизвести", ToolTipOn = "Пауза" };
    private readonly Slider seek = new() { Minimum = 0, Maximum = 1, MinWidth = Ui.Metric("Width.Seek"), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock time = new() { Text = "0:00", FontSize = Ui.Caption, VerticalAlignment = VerticalAlignment.Center, Margin = Ui.Insets(Ui.S, 0, 0, 0) };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly WaveformView waveform = new() { VerticalAlignment = VerticalAlignment.Center };
    private CancellationTokenSource? waveformLoad;
    private bool waveformLoaded;
    private TimeSpan duration;
    private TimeSpan pendingPosition;
    private bool updating,subscribed;
    public VoiceMessagePlayer(string path, Action<string> error, Func<bool>? allowed = null)
    {
        this.path = path; this.error = error; this.allowed = allowed ?? (() => true);
        var row = new DockPanel { Margin = Ui.Insets(0, Ui.S, 0, 0), MaxWidth = Ui.Metric("Width.Player"), HorizontalAlignment = HorizontalAlignment.Left };
        Ui.Name(seek, "Позиция голосового сообщения"); Ui.Secondary(time);
        DockPanel.SetDock(play, Dock.Left); row.Children.Add(play); DockPanel.SetDock(time, Dock.Right); row.Children.Add(time);
        var timeline=new Grid{MinWidth=Ui.Metric("Width.Seek")};timeline.Children.Add(waveform);
        // Retain the native range control as the accessibility/automation value;
        // pointer and keyboard interaction use the actual waveform above it.
        seek.Opacity=0;seek.IsHitTestVisible=false;seek.Focusable=false;timeline.Children.Add(seek);row.Children.Add(timeline);Content=row;
        waveform.SeekRequested+=fraction=>seek.Value=seek.Maximum*fraction;
        play.Click += (_, _) => { try { Toggle(); } catch (Exception ex) { StopAll(); error(ex.Message); } };
        seek.ValueChanged += (_, _) =>
        {
            if(updating)return;
            try{pendingPosition=TimeSpan.FromSeconds(seek.Value);if(IsCurrent)session!.Seek(pendingPosition);SessionChanged?.Invoke();Refresh();}
            catch(Exception ex){error(ex.Message);}
        };
        timer.Tick += (_, _) => Refresh();
        Loaded+=(_,_)=>{if(!subscribed){SessionChanged+=OnSessionChanged;subscribed=true;}Refresh();if(!waveformLoaded)_=LoadWaveformAsync();};
        // Virtualization/navigation detaches only this view; it does not stop audio.
        Unloaded+=(_,_)=>Dispose();
        try { using var metadata = new AudioFileReader(path);duration=metadata.TotalTime;seek.Maximum=Math.Max(.01,duration.TotalSeconds);Refresh(); } catch (Exception ex) { error(ex.Message); }
    }
    private async Task LoadWaveformAsync()
    {
        waveformLoad?.Cancel();var lifetime=new CancellationTokenSource();waveformLoad=lifetime;
        try
        {
            var data=await AudioWaveform.LoadAsync(path,lifetime.Token);
            if(lifetime.IsCancellationRequested)return;
            waveform.Peaks=data.Peaks;waveform.ToolTip="Нажмите на волну для перемотки";waveformLoaded=true;
        }
        catch(OperationCanceledException)when(lifetime.IsCancellationRequested){}
        catch(Exception ex){if(!lifetime.IsCancellationRequested)waveform.ToolTip="Волна недоступна: "+ex.Message;}
        finally{if(ReferenceEquals(waveformLoad,lifetime))waveformLoad=null;lifetime.Dispose();}
    }
    private bool IsCurrent=>session is not null&&StringComparer.OrdinalIgnoreCase.Equals(session.Path,path);
    private void EnsureSession(bool playAfterOpen)
    {
        if(IsCurrent)return;
        session?.Dispose();session=null;
        var next=new FilePlaybackSession(path,outputId);session=next;
        next.Seek(pendingPosition);
        next.PlaybackStopped+=(_,args)=>
        {
            if(Dispatcher.HasShutdownStarted)return;
            Dispatcher.BeginInvoke(()=>
            {
                if(!ReferenceEquals(session,next))return;
                if(args.Exception is not null)error("Голосовое сообщение: "+args.Exception.Message);
                SessionChanged?.Invoke();
            });
        };
        if(playAfterOpen)next.Play();
    }
    private void Toggle()
    {
        if (!allowed()) { error("Звук выключен. Включи наушники для прослушивания."); return; }
        if(IsCurrent&&session!.State==PlaybackState.Playing)session.Pause();
        else {EnsureSession(false);session!.Play();}
        SessionChanged?.Invoke();Refresh();
    }
    public static void ConfigureOutput(string? id)
    {
        outputId=id;session?.ConfigureOutput(id);SessionChanged?.Invoke();
    }
    public static void PauseActive(){session?.Pause();SessionChanged?.Invoke();}
    public static void StopAll(){session?.Dispose();session=null;SessionChanged?.Invoke();}
    private void OnSessionChanged()
    {
        if(Dispatcher.HasShutdownStarted)return;
        if(Dispatcher.CheckAccess())Refresh();else Dispatcher.BeginInvoke(Refresh);
    }
    private void Refresh()
    {
        bool current=IsCurrent;
        var position=current?session!.Position:pendingPosition;
        var length=current?session!.Duration:duration;
        updating=true;seek.Value=Math.Clamp(position.TotalSeconds,seek.Minimum,seek.Maximum);updating=false;
        time.Text=$"{position:m\\:ss} / {length:m\\:ss}";
        waveform.Progress=length.TotalSeconds>0?Math.Clamp(position.TotalSeconds/length.TotalSeconds,0,1):0;
        bool playing=current&&session!.State==PlaybackState.Playing;play.IsChecked=playing;
        if(playing&&IsLoaded)timer.Start();else timer.Stop();
    }
    public void Dispose()
    {
        timer.Stop();waveformLoad?.Cancel();if(subscribed){SessionChanged-=OnSessionChanged;subscribed=false;}
    }
}
