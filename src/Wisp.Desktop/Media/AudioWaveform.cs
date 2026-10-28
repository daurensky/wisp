using NAudio.Wave;

namespace Wisp.Desktop.Media;

public sealed record AudioWaveformData(float[] Peaks, TimeSpan Duration);

/// <summary>Small peak envelopes, computed off the UI thread without retaining decoded audio.</summary>
public static class AudioWaveform
{
    private readonly record struct Key(string Path, long Length, long Modified);
    private static readonly object gate = new();
    private static readonly Dictionary<Key, Task<AudioWaveformData>> cache = new();
    private static readonly Queue<Key> order = new();

    public static Task<AudioWaveformData> LoadAsync(string path, CancellationToken cancellation = default)
    {
        var file = new FileInfo(path);
        var key = new Key(file.FullName, file.Length, file.LastWriteTimeUtc.Ticks);
        Task<AudioWaveformData> result;
        lock (gate)
        {
            if (!cache.TryGetValue(key, out result!))
            {
                result = Task.Run(() => Read(file.FullName));
                cache[key] = result; order.Enqueue(key);
                while (order.Count > 32) cache.Remove(order.Dequeue());
            }
        }
        return result.WaitAsync(cancellation);
    }

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
