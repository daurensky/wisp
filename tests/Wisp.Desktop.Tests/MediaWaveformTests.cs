using System.IO;
using System.Reflection;
using NAudio.Wave;
using Wisp.Desktop.Media;
using Xunit;

namespace Wisp.Desktop.Tests;

public sealed class MediaWaveformTests
{
    [Fact]
    public void PeakEnvelopeReflectsActualSamplesAndRejectsNonFiniteSamples()
    {
        var samples = new SampleArray([0, .25f, -.75f, .1f, .5f, float.NaN, float.PositiveInfinity, -1]);
        Assert.Equal(new[] { .25f, .75f, .5f, 1 }, AudioWaveform.Compute(samples, 8, 4));
    }

    [Fact]
    public async Task WaveformAnalysisCachesEnvelopeWithoutKeepingFileOpenAndInvalidatesChangedContent()
    {
        var path = TempPath();
        try
        {
            WriteWave(path, 9600, .5f);
            var first = await AudioWaveform.LoadAsync(path); var second = await AudioWaveform.LoadAsync(path);
            Assert.Same(first.Peaks, second.Peaks); Assert.InRange(first.Duration.TotalSeconds, .199, .201);
            Assert.All(first.Peaks, value => Assert.InRange(value, .499f, .501f));
            using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            WriteWave(path, 4800, .25f); File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));
            var replaced = await AudioWaveform.LoadAsync(path);
            Assert.NotSame(first.Peaks, replaced.Peaks); Assert.All(replaced.Peaks, value => Assert.InRange(value, .249f, .251f));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task RecordingUsesBorrowedCallPcmAndFinalizesDownsampledWavWithoutAnotherCapture()
    {
        var path = TempPath();
        try
        {
            using var voice = new VoiceService(); using var recorder = new VoiceRecorder();
            var levels = new List<float>(); recorder.LevelChanged += (peak, _) => levels.Add(peak);
            recorder.Start(path, voice); Assert.True(recorder.UsesSharedSource);
            var subscription = typeof(VoiceService).GetField("MicrophonePcmAvailable", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var feed = Assert.IsType<Action<ReadOnlyMemory<byte>>>(subscription.GetValue(voice));
            // Splitting after an odd number of input samples must preserve the
            // sample pair used by the streaming 48kHz -> 24kHz conversion.
            feed(Pcm([-1000, 1000, 2000])); feed(Pcm([4000]));
            await recorder.StopAsync();
            Assert.False(recorder.IsRecording); Assert.Null(subscription.GetValue(voice));
            using var recorded = new WaveFileReader(path); var buffer = new byte[4];
            Assert.Equal(24000, recorded.WaveFormat.SampleRate); Assert.Equal(1, recorded.WaveFormat.Channels);
            Assert.Equal(4, recorded.Read(buffer, 0, buffer.Length));
            Assert.Equal(0, BitConverter.ToInt16(buffer, 0)); Assert.Equal(3000, BitConverter.ToInt16(buffer, 2));
            Assert.Contains(levels, value => value is > .091f and < .092f);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task RecordingAutomaticallyStopsAtSixtySecondsWithBoundedOutput()
    {
        var path = TempPath();
        try
        {
            using var recorder = new VoiceRecorder();
            var completed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            recorder.Completed += value => completed.TrySetResult(value);
            recorder.StartExternal(path);
            var twoSeconds = new byte[48000 * 2 * 2];
            for (int i = 0; i < 30; i++) recorder.AppendPcm(twoSeconds);
            Assert.Equal(path, await completed.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.False(recorder.IsRecording); Assert.Equal(TimeSpan.FromSeconds(60), recorder.Duration);
            using var wav = new WaveFileReader(path); Assert.Equal(24000L * 2 * 60, wav.Length);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task MalformedBorrowedPcmStopsOnlyRecordingAndReleasesSubscriptionForRetry()
    {
        var path = TempPath(); var retry = TempPath();
        try
        {
            using var voice = new VoiceService(); using var recorder = new VoiceRecorder();
            int started = 0; recorder.Started += () => started++;
            recorder.Start(path, voice);
            var subscription = typeof(VoiceService).GetField("MicrophonePcmAvailable", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert.IsType<Action<ReadOnlyMemory<byte>>>(subscription.GetValue(voice))(new byte[] { 1 });
            await Assert.ThrowsAsync<InvalidDataException>(() => recorder.StopAsync());
            Assert.False(recorder.IsRecording); Assert.Null(subscription.GetValue(voice));
            recorder.Start(retry, voice); Assert.True(recorder.UsesSharedSource); Assert.Equal(2, started);
            Assert.IsType<Action<ReadOnlyMemory<byte>>>(subscription.GetValue(voice))(Pcm([1000, 1000]));
            Assert.Equal(retry, await recorder.StopAsync()); Assert.Null(subscription.GetValue(voice));
            using var wav = new WaveFileReader(retry); Assert.Equal(2, wav.Length);
        }
        finally { File.Delete(path); File.Delete(retry); }
    }

    [Fact]
    public void SoundboardGainChangesOnlyMatchingPcmAndSurvivesEndpointSwitch()
    {
        var path = TempPath();
        try
        {
            WriteWave(path, 32, .25f); var outputs = new List<Render>();
            using var sounds = new SoundService(_ => { var output = new Render(); outputs.Add(output); return output; });
            sounds.PlayFile(path, .25f); Assert.InRange(Read(outputs[0]), .0624f, .0626f);
            sounds.SetFileVolume(path + ".different", 0); Assert.InRange(Read(outputs[0]), .0624f, .0626f);
            sounds.SetFileVolume(path, .5f); Assert.InRange(Read(outputs[0]), .1249f, .1251f);
            sounds.ConfigureOutput("another-headphone"); Assert.InRange(Read(outputs[1]), .1249f, .1251f);
            Assert.All(outputs, output => Assert.Equal(1f, output.Volume));
        }
        finally { File.Delete(path); }
    }

    private static string TempPath() => Path.Combine(Path.GetTempPath(), "wisp-media-" + Guid.NewGuid().ToString("N") + ".wav");
    private static byte[] Pcm(short[] values)
    {
        var bytes = new byte[values.Length * 2]; Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length); return bytes;
    }
    private static void WriteWave(string path, int frames, float amplitude)
    {
        using var wav = new WaveFileWriter(path, new WaveFormat(48000, 16, 1));
        var sample = BitConverter.GetBytes((short)(amplitude * 32768));
        for (int i = 0; i < frames; i++) wav.Write(sample, 0, sample.Length);
    }
    private static float Read(Render output)
    {
        var buffer = new byte[4]; Assert.Equal(4, output.Source!.Read(buffer, 0, 4)); return BitConverter.ToSingle(buffer);
    }
    private sealed class SampleArray(float[] source) : ISampleProvider
    {
        private int offset;
        public WaveFormat WaveFormat => WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);
        public int Read(float[] buffer, int destination, int count)
        {
            int read = Math.Min(count, source.Length - offset); Array.Copy(source, offset, buffer, destination, read); offset += read; return read;
        }
    }
    private sealed class Render : IWavePlayer
    {
        public IWaveProvider? Source;
        public float Volume { get; set; } = 1;
        public PlaybackState PlaybackState { get; private set; }
        public WaveFormat OutputWaveFormat => Source?.WaveFormat ?? WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);
        public event EventHandler<StoppedEventArgs>? PlaybackStopped;
        public void Init(IWaveProvider provider) => Source = provider;
        public void Play() => PlaybackState = PlaybackState.Playing;
        public void Pause() => PlaybackState = PlaybackState.Paused;
        public void Stop() { PlaybackState = PlaybackState.Stopped; PlaybackStopped?.Invoke(this, new StoppedEventArgs()); }
        public void Dispose() => Stop();
    }
}
