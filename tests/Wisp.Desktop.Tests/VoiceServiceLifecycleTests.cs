using System.IO;
using System.Reflection;
using System.Windows.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Wisp.Core;
using Wisp.Desktop.Media;
using Xunit;

namespace Wisp.Desktop.Tests;

public sealed class VoiceServiceLifecycleTests
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StoppingKeepsDispatcherResponsiveDuringDeviceRefreshAndNativeCleanup(bool refreshing)
    {
        await using var voice = new VoiceService();
        var deviceSwitch = Field<SemaphoreSlim>(voice, "deviceSwitch");
        using var releaseCapture = new ManualResetEventSlim();
        var capture = new FakeInput { ReleaseStop = releaseCapture };
        SetField(voice, "input", capture);
        SetField(voice, "running", true);
        if (refreshing) await deviceSwitch.WaitAsync();
        bool held = refreshing;
        var started = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var heartbeat = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher? dispatcher = null;
        var thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(() =>
            {
                try
                {
                    var stopping = voice.StopAsync();
                    Assert.False(stopping.IsCompleted);
                    Assert.False(Field<bool>(voice, "running"));
                    Assert.Same(stopping, voice.StopAsync());
                    started.TrySetResult(stopping);
                    dispatcher.BeginInvoke(() => heartbeat.TrySetResult());
                }
                catch (Exception error) { started.TrySetException(error); heartbeat.TrySetException(error); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try
        {
            await heartbeat.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var stopping = await started.Task;
            Assert.False(stopping.IsCompleted);
            if (held)
            {
                Assert.Equal(0, capture.StopCount);
                deviceSwitch.Release(); held = false;
            }
            await capture.StopStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            releaseCapture.Set();
            await stopping.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, capture.StopCount);
            Assert.True(capture.Disposed);
            Assert.NotEqual(thread.ManagedThreadId, capture.StopThreadId);
        }
        finally
        {
            if (held) deviceSwitch.Release();
            releaseCapture.Set();
            dispatcher?.BeginInvokeShutdown(DispatcherPriority.Send);
            Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public async Task FailedNativeStopStillDisposesCaptureAndClearsPlaybackState()
    {
        await using var voice = new VoiceService();
        var capture = new FakeInput { FailStop = true };
        SetField(voice, "input", capture);
        SetField(voice, "running", true);
        SetField(voice, "mixer", new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(48000, 1)));

        await voice.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(capture.Disposed);
        Assert.Equal(1, capture.StopCount);
        Assert.Null(Field<object?>(voice, "input"));
        Assert.Null(Field<object?>(voice, "output"));
        Assert.Null(Field<object?>(voice, "inputDevice"));
        Assert.Null(Field<object?>(voice, "mixer"));
        Assert.False(Field<bool>(voice, "running"));
    }

    [Fact]
    public async Task DisposalKeepsMicrophoneProcessingAliveUntilCaptureHasStopped()
    {
        await using var voice = new VoiceService();
        using var releaseCapture = new ManualResetEventSlim();
        var capture = new FakeInput { ReleaseStop = releaseCapture };
        var processor = new FakeProcessor();
        SetField(voice, "input", capture);
        SetField(voice, "running", true);
        SetField(voice, "micProcessing", new MicProcessingChain(processor));

        var disposing = voice.DisposeAsync().AsTask();
        try
        {
            await capture.StopStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(disposing.IsCompleted);
            Assert.Equal(0, processor.Disposals);
        }
        finally { releaseCapture.Set(); }

        await disposing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(capture.Disposed);
        Assert.Equal(1, processor.Disposals);
        await voice.DisposeAsync();
        Assert.Equal(1, processor.Disposals);
    }

    private static T Field<T>(VoiceService voice, string name) => (T)typeof(VoiceService).GetField(name, Fields)!.GetValue(voice)!;
    private static void SetField(VoiceService voice, string name, object value) => typeof(VoiceService).GetField(name, Fields)!.SetValue(voice, value);

    private sealed class FakeInput : IWaveIn
    {
        public bool FailStop, Disposed;
        public int StopCount, StopThreadId;
        public ManualResetEventSlim? ReleaseStop;
        public TaskCompletionSource StopStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public WaveFormat WaveFormat { get; set; } = new(48000, 16, 1);
        public event EventHandler<WaveInEventArgs>? DataAvailable { add { } remove { } }
        public event EventHandler<StoppedEventArgs>? RecordingStopped { add { } remove { } }
        public void StartRecording() { }
        public void StopRecording()
        {
            StopCount++;
            StopThreadId = Environment.CurrentManagedThreadId;
            StopStarted.TrySetResult();
            ReleaseStop?.Wait();
            if (FailStop) throw new IOException("Endpoint stop failed.");
        }
        public void Dispose() => Disposed = true;
    }

    private sealed class FakeProcessor : IMicProcessor
    {
        public bool Enabled { get; set; } = true;
        public int Disposals;
        public float Process(Span<short> frame) => 0;
        public void Dispose() => Disposals++;
    }
}
