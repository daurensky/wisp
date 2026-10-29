using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Wisp.Desktop.Media;

/// <summary>Owned, shared-mode WASAPI render stream routed to Wisp's chosen endpoint.
/// Uses neutral Other/default category; never changes endpoint/system volume.</summary>
public sealed class SharedAudioOutput(string? outputId = null, int latencyMilliseconds = 80, Action<string>? warning = null) : IWavePlayer
{
    private MMDevice? endpoint;
    private WasapiOut? output;
    private VolumeSampleProvider? samples;
    private float volume=1;
    public event EventHandler<StoppedEventArgs>? PlaybackStopped;
    public PlaybackState PlaybackState=>output?.PlaybackState??PlaybackState.Stopped;
    public WaveFormat OutputWaveFormat=>output?.OutputWaveFormat??WaveFormat.CreateIeeeFloatWaveFormat(48000,2);
    public float Volume
    {
        get=>volume;
        set
        {
            if(!float.IsFinite(value)||value is <0 or >1)throw new ArgumentOutOfRangeException(nameof(value));
            volume=value;if(samples is not null)samples.Volume=value;
        }
    }
    public void Init(IWaveProvider source)
    {
        if(output is not null)throw new InvalidOperationException("The render stream is already initialized.");
        using var catalog=new MMDeviceEnumerator();
        endpoint=AudioDeviceSettings.Resolve(catalog,outputId,DataFlow.Render,text=>warning?.Invoke(text));
        try
        {
            samples=new VolumeSampleProvider(source.ToSampleProvider()){Volume=volume};
            output=new WasapiOut(endpoint,AudioClientShareMode.Shared,true,Math.Clamp(latencyMilliseconds,40,250));
            output.PlaybackStopped+=(_,args)=>PlaybackStopped?.Invoke(this,args);
            output.Init(new SampleToWaveProvider(samples));
        }
        catch {Dispose();throw;}
    }
    public void Play()=>output?.Play();
    public void Pause()=>output?.Pause();
    public void Stop()=>output?.Stop();
    public void Dispose()
    {
        var previous=output;var device=endpoint;output=null;samples=null;endpoint=null;
        try{previous?.Dispose();}finally{device?.Dispose();}
    }
}
