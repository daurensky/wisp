using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using NAudio.Wave;
using Wisp.Core;
using Wisp.Desktop.Media;
using Xunit;
namespace Wisp.Desktop.Tests;

public sealed class SoundServiceTests
{
    public static IEnumerable<object[]> OriginalSamples()
    {
        yield return [SoundEvent.VoiceJoined,"a8fc116bba8f1e30d39b346708e0676b7a0a27f722e23deb03e59fa7b06cf382"];
        yield return [SoundEvent.VoiceLeft,"55fa9633d3c5c77f10b015e0dd11f534494dc92121d07ce8928af856317fb3fc"];
        yield return [SoundEvent.ScreenShareStarted,"3ed5453729dbac9c245a4dbe1ab7b9c2213937ca95d008b805f067e3dc8e18ac"];
        yield return [SoundEvent.MessageReceived,"6e93f65f66d5924274569dfe60dee557400ae24d211ab36667c236ef8417097b"];
        yield return [SoundEvent.MicMuted,"54ac4f25932ef3cb5474dabcecc37d969e81ebb3b24698de4c13e4d0fe45c0e4"];
        yield return [SoundEvent.MicUnmuted,"530a2e1f6ceac33365982c7675b6cfdfd1073aed0fff355543719358f877f562"];
        yield return [SoundEvent.Deafened,"cc08e5278900056d7285f6ddee781619a21ad6b27af0216ceb433aba82fab64b"];
        yield return [SoundEvent.Undeafened,"249b899e53ec3a66b37c6c2de14ede8015ac14048d89512a0c4bf3375454bec7"];
    }
    [Theory]
    [MemberData(nameof(OriginalSamples))]
    public void PackagedOriginalWavsHaveExpectedBytesAndUncompressedFormat(SoundEvent cue,string sha256)
    {
        string path=Path.Combine(AppContext.BaseDirectory,"Assets","Sounds",SoundService.FileName(cue));
        Assert.Equal(sha256,Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant());
        using var wav=new WaveFileReader(path);
        Assert.Equal(WaveFormatEncoding.Pcm,wav.WaveFormat.Encoding);Assert.Equal(48000,wav.WaveFormat.SampleRate);
        Assert.Equal(16,wav.WaveFormat.BitsPerSample);Assert.Equal(1,wav.WaveFormat.Channels);
        Assert.InRange(wav.TotalTime.TotalSeconds,.25,1);
    }
    [Fact]
    public async Task NotificationRendersIndependentlyOfExistingSoundboardOnSelectedEndpoint()
    {
        var outputs=new ConcurrentQueue<FakeOutput>();
        using var sounds=new SoundService(endpoint=>{Assert.Equal("chosen-output",endpoint);var output=new FakeOutput();outputs.Enqueue(output);return output;});
        sounds.ConfigureOutput(new AudioDeviceSettings(OutputId:"chosen-output"));
        sounds.PlayFile(Path.Combine(AppContext.BaseDirectory,"Assets","Sounds","voice-join.wav"));
        Assert.True(outputs.TryPeek(out var board));Assert.Equal(PlaybackState.Playing,board!.PlaybackState);
        sounds.Play(SoundEvent.MessageReceived);
        var deadline=DateTime.UtcNow.AddSeconds(3);
        while(outputs.Count<2&&DateTime.UtcNow<deadline)await Task.Delay(10);
        Assert.Equal(2,outputs.Count);
        var notification=outputs.Last();await notification.Initialized.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var buffer=new byte[48000];
        bool audible=false;deadline=DateTime.UtcNow.AddSeconds(2);
        while(!audible&&DateTime.UtcNow<deadline){notification.Source!.Read(buffer,0,buffer.Length);audible=buffer.Any(b=>b!=0);if(!audible)await Task.Delay(10);}
        Assert.True(audible,"Original samples must reach the independent notification renderer.");
        Assert.Equal(PlaybackState.Playing,board.PlaybackState);Assert.Equal(0,board.Stops);
        sounds.StopFile();Assert.True(board.Stops>0);Assert.Equal(0,notification.Stops);
    }
    private sealed class FakeOutput:IWavePlayer
    {
        public IWaveProvider? Source;
        public readonly TaskCompletionSource Initialized=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Stops;
        public bool Disposed;
        public bool FailInitialization;
        public PlaybackState PlaybackState{get;private set;}
        public WaveFormat OutputWaveFormat=>Source?.WaveFormat??WaveFormat.CreateIeeeFloatWaveFormat(48000,2);
        public float Volume{get;set;}=1;
        public event EventHandler<StoppedEventArgs>? PlaybackStopped;
        public void Init(IWaveProvider provider){if(FailInitialization)throw new IOException("Render endpoint unavailable");Source=provider;Initialized.TrySetResult();}
        public void Play()=>PlaybackState=PlaybackState.Playing;
        public void Pause()=>PlaybackState=PlaybackState.Paused;
        public void Stop(){Stops++;PlaybackState=PlaybackState.Stopped;PlaybackStopped?.Invoke(this,new StoppedEventArgs());}
        public void Dispose(){Disposed=true;}
    }

    [Fact]
    public void FailedSoundboardOutputSwitchReleasesBothRenderersAndCanPlayAgain()
    {
        var oldOutput=new FakeOutput();var rejected=new FakeOutput{FailInitialization=true};var replacement=new FakeOutput();
        int created=0;
        using var sounds=new SoundService(_=>created++ switch{0=>oldOutput,1=>rejected,_=>replacement});
        string path=Path.Combine(AppContext.BaseDirectory,"Assets","Sounds","voice-join.wav");
        sounds.PlayFile(path);
        Assert.Throws<IOException>(()=>sounds.ConfigureOutput("missing-endpoint"));
        Assert.True(oldOutput.Disposed);Assert.True(rejected.Disposed);
        sounds.PlayFile(path);Assert.Equal(PlaybackState.Playing,replacement.PlaybackState);
    }

    [Fact]
    public async Task ClearingNotificationsDiscardsActiveAndQueuedCuesButAllowsLocalAcknowledgement()
    {
        var output=new FakeOutput();
        using var sounds=new SoundService(_=>output);
        sounds.Play(SoundEvent.MessageReceived);
        await output.Initialized.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var bytes=new byte[4800];
        async Task WaitForSound()
        {
            var deadline=DateTime.UtcNow.AddSeconds(2);
            while(DateTime.UtcNow<deadline)
            {
                output.Source!.Read(bytes,0,bytes.Length);
                if(bytes.Any(b=>b!=0))return;
                await Task.Delay(10);
            }
            Assert.Fail("Expected notification samples did not reach the renderer.");
        }
        await WaitForSound();
        for(int i=0;i<16;i++)sounds.Play(SoundEvent.MessageReceived);
        sounds.StopNotifications();
        output.Source!.Read(bytes,0,bytes.Length);Assert.All(bytes,b=>Assert.Equal(0,b));
        await Task.Delay(30); // A worker-dequeued cue from the previous generation must stay invalid.
        output.Source.Read(bytes,0,bytes.Length);Assert.All(bytes,b=>Assert.Equal(0,b));
        sounds.Play(SoundEvent.Deafened);await WaitForSound();
        sounds.NotificationsEnabled=false;
        output.Source.Read(bytes,0,bytes.Length);Assert.All(bytes,b=>Assert.Equal(0,b));
        Assert.Equal(PlaybackState.Playing,output.PlaybackState);Assert.Equal(0,output.Stops);
    }
}
