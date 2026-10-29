using NAudio.Wave;

namespace Wisp.Desktop.Media;

public sealed record AudioWaveformData(float[] Peaks, TimeSpan Duration);

/// <summary>Small peak envelopes, computed off the UI thread without retaining decoded audio.</summary>
public static class AudioWaveform
{
    private static readonly AudioWaveformCache cache = new(Read);

    public static Task<AudioWaveformData> LoadAsync(string path, CancellationToken cancellation = default) =>
        cache.LoadAsync(path, cancellation);

    private static AudioWaveformData Read(string path)
    {
        using var reader = new AudioFileReader(path);
        if (reader.TotalTime > TimeSpan.FromMinutes(10)) throw new InvalidDataException("Запись слишком длинная для предпросмотра волны.");
        long sampleCount = reader.Length / sizeof(float);
        return new AudioWaveformData(Compute(reader, sampleCount, 144), reader.TotalTime);
    }

    public static float[] Compute(ISampleProvider samples, long expectedSamples, int bars = 144)
    {
        if (expectedSamples <= 0 || bars is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(expectedSamples));
        var peaks = new float[bars]; var buffer = new float[4096]; long position = 0;
        int count;
        while ((count = samples.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i < count; i++, position++)
            {
                int index = (int)Math.Min(bars - 1, position * bars / expectedSamples);
                float value = float.IsFinite(buffer[i]) ? Math.Clamp(Math.Abs(buffer[i]), 0, 1) : 0;
                peaks[index] = Math.Max(peaks[index], value);
            }
            if (position > 48000L * 8 * 600) throw new InvalidDataException("Превышен предел анализа аудио.");
        }
        return peaks;
    }
}

internal sealed class AudioWaveformCache(Func<string, AudioWaveformData> analyze)
{
    private readonly record struct Key(string Path, long Length, long Modified);
    private readonly object gate = new();
    private readonly SemaphoreSlim decoders = new(2);
    private readonly Dictionary<Key, Task<AudioWaveformData>> inFlight = new();
    private readonly Dictionary<Key, Task<AudioWaveformData>> completed = new();
    private readonly Queue<Key> completedOrder = new();

    public Task<AudioWaveformData> LoadAsync(string path, CancellationToken cancellation = default)
    {
        if (cancellation.IsCancellationRequested) return Task.FromCanceled<AudioWaveformData>(cancellation);
        var file = new FileInfo(path);
        var key = new Key(file.FullName, file.Length, file.LastWriteTimeUtc.Ticks);
        Task<AudioWaveformData> result;
        lock (gate)
        {
            if (!completed.TryGetValue(key, out result!) && !inFlight.TryGetValue(key, out result!))
            {
                var completion = new TaskCompletionSource<AudioWaveformData>(TaskCreationOptions.RunContinuationsAsynchronously);
                result = completion.Task;
                inFlight.Add(key, result);
                _ = AnalyzeAsync(key, completion);
            }
        }
        return result.WaitAsync(cancellation);
    }

    private async Task AnalyzeAsync(Key key, TaskCompletionSource<AudioWaveformData> completion)
    {
        try
        {
            await decoders.WaitAsync().ConfigureAwait(false);
            AudioWaveformData data;
            try { data = await Task.Run(() => analyze(key.Path)).ConfigureAwait(false); }
            finally { decoders.Release(); }
            lock (gate)
            {
                inFlight.Remove(key);
                completed.Add(key, completion.Task);
                completedOrder.Enqueue(key);
                while (completedOrder.Count > 32) completed.Remove(completedOrder.Dequeue());
                completion.SetResult(data);
            }
        }
        catch (Exception error)
        {
            lock (gate) inFlight.Remove(key);
            completion.SetException(error);
            // Shared analysis can outlive every cancelled consumer.
            _ = completion.Task.Exception;
        }
    }
}
