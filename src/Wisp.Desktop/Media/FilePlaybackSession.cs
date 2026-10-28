using NAudio.Wave;

namespace Wisp.Desktop.Media;

/// <summary>A voice-message playback session independent of a virtualized WPF row.</summary>
public sealed class FilePlaybackSession : IDisposable
{
    private readonly Func<string?,IWavePlayer> factory;
    private AudioFileReader? reader;
    private IWavePlayer? output;
    public string Path {get;}
    public string? OutputId {get;private set;}
    public PlaybackState State=>output?.PlaybackState??PlaybackState.Stopped;
    public TimeSpan Position=>reader?.CurrentTime??TimeSpan.Zero;
    public TimeSpan Duration=>reader?.TotalTime??TimeSpan.Zero;
    public event EventHandler<StoppedEventArgs>? PlaybackStopped;
    public FilePlaybackSession(string path,string? outputId=null,Func<string?,IWavePlayer>? outputFactory=null)
    {
        Path=path;OutputId=outputId;factory=outputFactory??(id=>new SharedAudioOutput(id));
        reader=new AudioFileReader(path);
    }
    private void OpenOutput()
    {
        output=factory(OutputId);
        output.PlaybackStopped+=OnStopped;
        try{output.Init(reader!);}catch{output.PlaybackStopped-=OnStopped;output.Dispose();output=null;throw;}
    }
    private void OnStopped(object? sender,StoppedEventArgs args)=>PlaybackStopped?.Invoke(this,args);
    public void Play()
    {
        ObjectDisposedException.ThrowIf(reader is null,this);
        if(output is null)OpenOutput();
        if(Position>=Duration-TimeSpan.FromMilliseconds(50))reader.CurrentTime=TimeSpan.Zero;
        output!.Play();
    }
    public void Pause()=>output?.Pause();
    public void Seek(TimeSpan position)
    {
        ObjectDisposedException.ThrowIf(reader is null,this);
        reader.CurrentTime=TimeSpan.FromSeconds(Math.Clamp(position.TotalSeconds,0,Duration.TotalSeconds));
    }
    public void ConfigureOutput(string? id)
    {
        if(OutputId==id)return;
        ObjectDisposedException.ThrowIf(reader is null,this);
        bool initialized=output is not null,playing=State==PlaybackState.Playing;var position=Position;
        CloseOutput();OutputId=id;reader.CurrentTime=position;
        if(initialized){OpenOutput();if(playing)output!.Play();}
    }
    private void CloseOutput()
    {
        if(output is null)return;
        var previous=output;output=null;previous.PlaybackStopped-=OnStopped;
        try{previous.Stop();}finally{previous.Dispose();}
    }
    public void Dispose(){try{CloseOutput();}finally{reader?.Dispose();reader=null;}}
}
