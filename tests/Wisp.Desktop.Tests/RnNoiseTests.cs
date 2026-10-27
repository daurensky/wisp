using System.Diagnostics;
using System.IO;
using Wisp.Desktop.Media;
using Xunit;
using Xunit.Abstractions;

namespace Wisp.Desktop.Tests;

public sealed class RnNoiseFactAttribute : FactAttribute
{
    public RnNoiseFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("WISP_REQUIRE_RNNOISE") == "1") return;
        try { using var probe = new RnNoiseProcessor(); }
        catch (Exception ex) { Skip = "RNNoise unavailable: " + ex.GetType().Name; }
    }
}

public sealed class RnNoiseTests(ITestOutputHelper log)
{
    [RnNoiseFact]
    public void SilenceStaysSilentAndDisabledProcessorIsBitExact()
    {
        using var processor = new RnNoiseProcessor();
        var frame = new short[960];
        for (int count = 0; count < 20; count++)
        {
            Assert.InRange(processor.Process(frame), 0, 1);
            Assert.All(frame, sample => Assert.InRange((int)sample, -1, 1));
        }
        processor.Enabled = false;
        var random = new Random(21);
        for (int i = 0; i < frame.Length; i++) frame[i] = (short)random.Next(short.MinValue, short.MaxValue + 1);
        var expected = (short[])frame.Clone();
        Assert.Equal(-1, processor.Process(frame));
        Assert.Equal(expected, frame);
    }

    [RnNoiseFact]
    public void StationaryNoiseEnergyFallsAfterModelWarmup()
    {
        using var processor = new RnNoiseProcessor();
        var random = new Random(42);
        var frame = new short[960];
        double before = 0, after = 0;
        for (int count = 0; count < 200; count++)
        {
            for (int i = 0; i < frame.Length; i++) frame[i] = (short)random.Next(-500, 501);
            if (count >= 100) before += Energy(frame);
            processor.Process(frame);
            if (count >= 100) after += Energy(frame);
        }
        log.WriteLine($"White-noise energy ratio: {after / before:0.000000}");
        Assert.True(after < before * .5, "Steady low-amplitude white noise should lose at least half its energy.");
    }

    [RnNoiseFact]
    public void ToneIsNotEntirelyErasedAndFullScaleInputDoesNotWrap()
    {
        using var processor = new RnNoiseProcessor();
        var frame = new short[960];
        double outputEnergy = 0;
        for (int count = 0; count < 100; count++)
        {
            for (int i = 0; i < frame.Length; i++)
                frame[i] = (short)(8000 * Math.Sin(2 * Math.PI * 1000 * (count * 960 + i) / 48000));
            processor.Process(frame);
            outputEnergy += Energy(frame);
        }
        Assert.True(outputEnergy > 1000, "A 1 kHz tone is not speech, but must not produce an all-zero stream.");
        for (int count = 0; count < 20; count++)
        {
            for (int i = 0; i < frame.Length; i++) frame[i] = i % 2 == 0 ? short.MinValue : short.MaxValue;
            Assert.InRange(processor.Process(frame), 0, 1);
        }
    }

    [RnNoiseFact]
    public void DisableEnableAndExplicitResetMatchAFreshState()
    {
        using var processor = new RnNoiseProcessor();
        var frame = new short[960];
        for (int count = 0; count < 10; count++) { Array.Fill(frame, (short)12000); processor.Process(frame); }
        processor.Enabled = false;
        processor.Enabled = true; // Capture need not observe the disabled state to reset history.
        using var fresh = new RnNoiseProcessor();
        Array.Fill(frame, (short)3000);
        var expected = (short[])frame.Clone();
        fresh.Process(expected); processor.Process(frame);
        Assert.Equal(expected, frame);
        processor.RequestReset();
        Array.Clear(frame); processor.Process(frame);
        Assert.All(frame, sample => Assert.InRange((int)sample, -1, 1));
    }

    [RnNoiseFact]
    public void MeasureNativeFrameCostAndSteadyStateAllocations()
    {
        using var processor = new RnNoiseProcessor();
        var frame = new short[960];
        var timings = new double[1000];
        for (int count = 0; count < 1100; count++)
        {
            for (int i = 0; i < frame.Length; i++)
                frame[i] = (short)(4000 * Math.Sin(2 * Math.PI * 160 * (count * 960 + i) / 48000) + (i % 97) * 15);
            long start = Stopwatch.GetTimestamp();
            processor.Process(frame);
            if (count >= 100) timings[count - 100] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        Array.Sort(timings);
        double mean = timings.Average();
        log.WriteLine($"RNNoise Windows x64: mean={mean:0.000} ms; p95={timings[949]:0.000} ms; max={timings[^1]:0.000} ms per 20 ms frame; one-core duty={mean / 20 * 100:0.00}%.");
        if (mean > 5) log.WriteLine("WARNING: processing exceeds 25% of the 20 ms frame budget.");
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) processor.Process(frame);
        Assert.Equal(allocated, GC.GetAllocatedBytesForCurrentThread());
        Assert.Throws<ArgumentException>(() => processor.Process(new short[480]));
        processor.Dispose();
        Assert.Throws<ObjectDisposedException>(() => processor.Process(frame));
    }

    [Fact]
    public void AudioSettingsDefaultToEnabledAndPreserveOldDeviceSettings()
    {
        string directory = Path.Combine(Path.GetTempPath(), "wisp-audio-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Assert.True(AudioDeviceSettings.Load(directory).NoiseSuppression);
            File.WriteAllText(Path.Combine(directory, "audio.json"), "{\"InputId\":\"mic\",\"OutputId\":\"headphones\"}");
            var old = AudioDeviceSettings.Load(directory);
            Assert.True(old.NoiseSuppression);
            (old with { NoiseSuppression = false }).Save(directory);
            var saved = AudioDeviceSettings.Load(directory);
            Assert.False(saved.NoiseSuppression);
            Assert.Equal("mic", saved.InputId);
            Assert.Equal("headphones", saved.OutputId);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static double Energy(short[] frame)
    {
        double sum = 0;
        foreach (short value in frame) sum += (double)value * value;
        return sum;
    }
}
