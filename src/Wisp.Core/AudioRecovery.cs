namespace Wisp.Core;

/// <summary>Ordered RTP audio with bounded loss metadata; rollover is ordinary sequence progression.</summary>
public sealed class AudioSequenceTracker
{
    private ushort? previous;
    public bool Accept(ushort sequence, out int missing)
    {
        missing = 0;
        if (previous is { } last)
        {
            int delta = unchecked((short)(sequence - last));
            if (delta <= 0) return false;
            missing = Math.Min(3, delta - 1);
        }
        previous = sequence;
        return true;
    }
}

/// <summary>Mono 48kHz PCM ring, 40–80ms adaptive priming and short cross-fades when trimming latency.</summary>
public sealed class PcmJitterBuffer(TimeProvider? timeProvider = null)
{
    private const int Rate = 48000;
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly object gate = new();
    private readonly short[] data = new short[Rate * 240 / 1000];
    private int head, count, target = Rate * 40 / 1000, stablePackets;
    private bool priming = true;
    private long? previousArrival;
    private double jitter;
    private float lastSample;
    public int TargetMilliseconds { get { lock (gate) return target * 1000 / Rate; } }
    public int BufferedSamples { get { lock (gate) return count; } }
    public long TrimmedSamples { get; private set; }

    public void ObserveArrival(int missingFrames = 0)
    {
        lock (gate)
        {
            long now = clock.GetTimestamp();
            if (previousArrival is { } previous)
            {
                double delta = clock.GetElapsedTime(previous, now).TotalMilliseconds;
                if (delta < 200) jitter += (Math.Abs(delta - 20 * (Math.Clamp(missingFrames, 0, 3) + 1)) - jitter) / 16;
            }
            previousArrival = now;
            int desired = (int)Math.Clamp(40 + jitter * 2, 40, 80) * Rate / 1000;
            if (desired > target) { target = desired; stablePackets = 0; }
            else if (++stablePackets >= 100) { target = Math.Max(desired, target - Rate * 10 / 1000); stablePackets = 0; }
        }
    }
    public void Write(ReadOnlySpan<short> samples)
    {
        lock (gate)
        {
            if (samples.Length > data.Length) samples = samples[^data.Length..];
            if (count + samples.Length > data.Length) Trim(count + samples.Length - data.Length);
            foreach (short sample in samples) { data[(head + count) % data.Length] = sample; count++; }
        }
    }
    public void Read(Span<float> output)
    {
        lock (gate)
        {
            output.Clear();
            if (priming && count < target) return;
            priming = false;
            if (count > target + Rate * 40 / 1000) Trim(Math.Min(Rate * 10 / 1000, count - target));
            int take = Math.Min(output.Length, count);
            for (int i = 0; i < take; i++) output[i] = data[(head + i) % data.Length] / 32768f;
            head = (head + take) % data.Length; count -= take;
            if (take > 0) lastSample = output[take - 1];
            if (take < output.Length)
            {
                int fade = Math.Min(240, output.Length - take);
                for (int i = 0; i < fade; i++) output[take + i] = lastSample * (1f - (i + 1f) / fade);
                lastSample = 0; priming = true; target = Math.Min(Rate * 80 / 1000, target + Rate * 20 / 1000); stablePackets = 0;
            }
        }
    }
    private void Trim(int samples)
    {
        int skip = Math.Min(samples, count), next = (head + skip) % data.Length;
        int blend = Math.Min(240, count - skip);
        for (int i = 0; i < blend; i++)
        {
            double mix = (i + 1d) / blend;
            data[(next + i) % data.Length] = (short)(data[(head + i) % data.Length] * (1 - mix) + data[(next + i) % data.Length] * mix);
        }
        head = next; count -= skip; TrimmedSamples += skip;
    }
    public void Clear()
    {
        lock (gate) { head = count = 0; priming = true; previousArrival = null; lastSample = 0; }
    }
}
