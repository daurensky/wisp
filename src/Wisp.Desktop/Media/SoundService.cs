using System.Threading.Channels;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Wisp.Core;

namespace Wisp.Desktop.Media;

/// <summary>Bounded original notifications and an independent soundboard shared render stream.</summary>
public sealed class SoundService : IDisposable
{
    // Only built-in application cues are attenuated; independent soundboard,
    // voice and shared-audio renderers retain their own gain.
    public const float BuiltInEffectsGain = .5f;
    private readonly object gate = new();
    private readonly Func<string?, IWavePlayer> createOutput;
    private readonly record struct NotificationCue(SoundEvent Event,int Generation);
    private readonly System.Threading.Channels.Channel<NotificationCue> pending = System.Threading.Channels.Channel.CreateBounded<NotificationCue>(new BoundedChannelOptions(4)
    { SingleReader = false, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource stop = new();
    private readonly Dictionary<SoundEvent, float[]> samples = new();
    private readonly Task worker;
    private string? outputId;
    private IWavePlayer? notifications, soundboard;
    private MixingSampleProvider? mixer;
    private AudioFileReader? file;
    private bool disposed;
    private volatile bool enabled = true;
    private float volume = .8f;
    private float fileGain=1;
    private string? playingFilePath;
    public float FileGain { get { lock(gate)return fileGain; } set { lock(gate){fileGain=float.IsFinite(value)?Math.Clamp(value,0,1):1;if(file is not null)file.Volume=fileGain;} } }
    public void SetFileVolume(float gain)=>FileGain=gain;
    public void SetFileVolume(string path,float gain) { lock(gate)if(StringComparer.OrdinalIgnoreCase.Equals(playingFilePath,Path.GetFullPath(path)))FileGain=gain; }
    private int notificationGeneration;
    public event Action<string>? Error;
    public bool NotificationsEnabled { get => enabled; set {enabled = value;if(!value)StopNotifications();} }
    public float NotificationVolume { get => Volatile.Read(ref volume); set => Volatile.Write(ref volume, float.IsFinite(value) ? Math.Clamp(value, 0, 1) : .8f); }

    public SoundService(Func<string?, IWavePlayer>? outputFactory = null)
    {
        createOutput = outputFactory ?? (id => new SharedAudioOutput(id));
        worker = Task.Run(ProcessNotifications);
    }
    public static string FileName(SoundEvent cue) => cue switch
    {
        SoundEvent.VoiceJoined => "voice-join.wav", SoundEvent.VoiceLeft => "voice-leave.wav",
        SoundEvent.ScreenShareStarted => "stream-start.wav", SoundEvent.MessageReceived => "message.wav",
        SoundEvent.MicMuted => "mute.wav", SoundEvent.MicUnmuted => "unmute.wav",
        SoundEvent.Deafened => "deafen.wav", SoundEvent.Undeafened => "undeafen.wav",
        _ => throw new ArgumentOutOfRangeException(nameof(cue))
    };
    public void ConfigureOutput(AudioDeviceSettings settings)
    {
        lock (gate)
        {
            if (disposed || outputId == settings.OutputId) return;
            outputId = settings.OutputId;
            notifications?.Stop(); notifications?.Dispose(); notifications = null; mixer = null;
            if (soundboard is not null && file is not null)
            {
                bool playing = soundboard.PlaybackState == PlaybackState.Playing;
                var previous=soundboard;soundboard=null;
                try{previous.Stop();}finally{previous.Dispose();}
                try
                {
                    soundboard = CreateOutput();
                    soundboard.Init(file); if (playing) soundboard.Play();
                }
                catch
                {
                    soundboard?.Dispose();soundboard=null;
                    throw;
                }
            }
        }
    }
    public void ConfigureOutput(string? selectedOutputId)=>ConfigureOutput(new AudioDeviceSettings(OutputId:selectedOutputId));
    public void Play(SoundEvent cue) { if (enabled && !stop.IsCancellationRequested) pending.Writer.TryWrite(new(cue,Volatile.Read(ref notificationGeneration))); }
    /// <summary>Invalidates queued/current cues without stopping call, message or soundboard output.</summary>
    public void StopNotifications()
    {
        lock(gate)
        {
            Interlocked.Increment(ref notificationGeneration);
            while(pending.Reader.TryRead(out _)){}
            mixer?.RemoveAllMixerInputs();
        }
    }
    private async Task ProcessNotifications()
    {
        try
        {
            await foreach (var pendingCue in pending.Reader.ReadAllAsync(stop.Token))
            {
                if (!enabled) continue;
                try
                {
                    lock (gate)
                    {
                        if (disposed) return;
                        if(!enabled||pendingCue.Generation!=Volatile.Read(ref notificationGeneration))continue;
                        var cue=pendingCue.Event;
                        if (!samples.TryGetValue(cue, out var sound)) samples[cue] = sound = Load(cue);
                        if (mixer is null || notifications?.PlaybackState == PlaybackState.Stopped)
                        {
                            notifications?.Dispose(); notifications = null;
                            mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)) { ReadFully = true };
                            try { notifications = CreateOutput(); notifications.Init(mixer.ToWaveProvider()); notifications.Play(); }
                            catch { notifications?.Dispose(); notifications = null; mixer = null; throw; }
                        }
                        if (mixer.MixerInputs.Count() >= 4) continue;
                        mixer.AddMixerInput(new MonoToStereoSampleProvider(new CueSamples(sound, NotificationVolume * BuiltInEffectsGain)));
                    }
                }
                catch (Exception ex) { Error?.Invoke("Звук уведомления недоступен: " + ex.Message); }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
    private static float[] Load(SoundEvent cue)
    {
        using var wave = new WaveFileReader(Path.Combine(AppContext.BaseDirectory, "Assets", "Sounds", FileName(cue)));
        if (wave.WaveFormat.Encoding != WaveFormatEncoding.Pcm || wave.WaveFormat.SampleRate != 48000 || wave.WaveFormat.BitsPerSample != 16 || wave.WaveFormat.Channels != 1)
            throw new InvalidDataException("Wisp Original требует WAV 48 кГц / 16-bit PCM / mono.");
        var result = new float[checked((int)(wave.Length / 2))];
        var provider = wave.ToSampleProvider(); int offset = 0;
        while (offset < result.Length) { int read = provider.Read(result, offset, result.Length - offset); if (read == 0) break; offset += read; }
        if (offset != result.Length) throw new InvalidDataException("Неполный WAV уведомления.");
        return result;
    }
    public void PlayFile(string path, float gain = 1)
    {
        lock (gate)
        {
            if (disposed) return;
            StopFileCore();
            try { playingFilePath=Path.GetFullPath(path);FileGain=gain;file = new AudioFileReader(path) { Volume = fileGain }; soundboard = CreateOutput(); soundboard.Init(file); soundboard.Play(); }
            catch { StopFileCore(); throw; }
        }
    }
    public void StopFile() { lock (gate) StopFileCore(); }
    private IWavePlayer CreateOutput()
    {
        var output=createOutput(outputId);
        output.PlaybackStopped+=(_,args)=>{if(!disposed&&args.Exception is{} error)Error?.Invoke("Воспроизведение звука остановлено: "+error.Message);};
        return output;
    }
    private void StopFileCore() { soundboard?.Stop(); soundboard?.Dispose(); soundboard = null; file?.Dispose(); file = null;playingFilePath=null; }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return; disposed = true; stop.Cancel(); pending.Writer.TryComplete();
            notifications?.Stop(); notifications?.Dispose(); notifications = null; mixer = null; StopFileCore(); samples.Clear();
        }
        _ = worker.ContinueWith(_ => stop.Dispose(), TaskScheduler.Default);
    }
    private sealed class CueSamples(float[] data, float gain) : ISampleProvider
    {
        private int position;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);
        public int Read(float[] buffer, int offset, int count)
        {
            int available = Math.Min(count, data.Length - position);
            for (int i = 0; i < available; i++) buffer[offset + i] = data[position + i] * gain;
            position += available; return available;
        }
    }
}
