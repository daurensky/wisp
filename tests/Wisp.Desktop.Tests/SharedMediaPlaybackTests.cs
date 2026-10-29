using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Wisp.Desktop.Media;
using Xunit;

namespace Wisp.Desktop.Tests;

public sealed class SharedMediaPlaybackTests
{
    [Fact]
    public void VoiceMessageKeepsPositionWhenOutputChangesAndMixesWithCallPcm()
    {
        var path=Path.Combine(Path.GetTempPath(),"wisp-playback-"+Guid.NewGuid().ToString("N")+".wav");
        try
        {
            using(var writer=new WaveFileWriter(path,new WaveFormat(48000,16,1)))
            {
                var bytes=new byte[48000*2];
                for(int i=0;i<bytes.Length;i+=2){bytes[i]=0;bytes[i+1]=32;} // .25 PCM
                writer.Write(bytes,0,bytes.Length);
            }
            var outputs=new List<FakeOutput>();var endpoints=new List<string?>();
            using var session=new FilePlaybackSession(path,"headphones-a",id=>
            {
                endpoints.Add(id);var output=new FakeOutput();outputs.Add(output);return output;
            });
            // Seeking or opening a row must not claim a render endpoint.
            session.Seek(TimeSpan.FromSeconds(.2));Assert.Empty(outputs);session.Seek(TimeSpan.Zero);
            session.Play();Assert.Equal(PlaybackState.Playing,session.State);
            var pcm=outputs[0].Source!.ToSampleProvider();
            var mixer=new MixingSampleProvider(pcm.WaveFormat);
            mixer.AddMixerInput(pcm);mixer.AddMixerInput(new ConstantPcm(pcm.WaveFormat,.125f));
            var buffer=new float[960];Assert.Equal(buffer.Length,mixer.Read(buffer,0,buffer.Length));
            Assert.All(buffer,value=>Assert.InRange(value,.374f,.376f));
            Assert.InRange(session.Position.TotalMilliseconds,19,21);
            session.Pause();Assert.Equal(PlaybackState.Paused,session.State);
            session.Seek(TimeSpan.FromSeconds(.4));
            session.ConfigureOutput("headphones-b");
            Assert.True(outputs[0].Disposed);Assert.Equal(new[]{"headphones-a","headphones-b"},endpoints);
            Assert.InRange(session.Position.TotalSeconds,.399,.401);
            Assert.NotEqual(PlaybackState.Playing,session.State);
            session.Play();session.ConfigureOutput("headphones-c");
            Assert.Equal(PlaybackState.Playing,session.State);
            Assert.True(outputs[1].Disposed);Assert.InRange(session.Position.TotalSeconds,.399,.401);
        }
        finally{File.Delete(path);}
    }

    [Fact]
    public void FailedOutputInitializationCanBeRetriedAndReleasesItsResources()
    {
        var path=Path.Combine(Path.GetTempPath(),"wisp-playback-failure-"+Guid.NewGuid().ToString("N")+".wav");
        try
        {
            using(var writer=new WaveFileWriter(path,new WaveFormat(48000,16,1)))writer.Write(new byte[9600],0,9600);
            var failed=new FakeOutput{FailInitialization=true};var healthy=new FakeOutput();int attempts=0;
            using(var session=new FilePlaybackSession(path,"chosen",_=>attempts++==0?failed:healthy))
            {
                Assert.Throws<IOException>(()=>session.Play());Assert.True(failed.Disposed);
                session.Play();Assert.Equal(PlaybackState.Playing,session.State);
            }
            Assert.True(healthy.Disposed);
            using var exclusive=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None);
        }
        finally{File.Delete(path);}
    }

    private sealed class ConstantPcm(WaveFormat format,float value):ISampleProvider
    {
        public WaveFormat WaveFormat=>format;
        public int Read(float[] samples,int offset,int count){Array.Fill(samples,value,offset,count);return count;}
    }
    private sealed class FakeOutput:IWavePlayer
    {
        public IWaveProvider? Source;
        public bool Disposed;
        public bool FailInitialization;
        public PlaybackState PlaybackState{get;private set;}
        public float Volume{get;set;}=1;
        public WaveFormat OutputWaveFormat=>Source?.WaveFormat??new WaveFormat(48000,16,1);
        public event EventHandler<StoppedEventArgs>? PlaybackStopped;
        public void Init(IWaveProvider source){if(FailInitialization)throw new IOException("Output unavailable");Source=source;}
        public void Play()=>PlaybackState=PlaybackState.Playing;
        public void Pause()=>PlaybackState=PlaybackState.Paused;
        public void Stop(){PlaybackState=PlaybackState.Stopped;PlaybackStopped?.Invoke(this,new StoppedEventArgs());}
        public void Dispose(){Stop();Disposed=true;}
    }
}
