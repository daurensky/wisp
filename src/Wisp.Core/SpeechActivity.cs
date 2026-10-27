namespace Wisp.Core;

/// <summary>UI speech indicator only; never gates or changes microphone samples.</summary>
public sealed class SpeechActivity(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private long? lastSpeech;
    private long? lastObserved;
    private int attackFrames;
    public bool Speaking => lastSpeech is { } timestamp && clock.GetElapsedTime(timestamp) < TimeSpan.FromMilliseconds(240);

    public void Observe(ReadOnlySpan<short> samples)
    {
        if (samples.IsEmpty) return;
        long now = clock.GetTimestamp();
        if (lastObserved is { } previous && clock.GetElapsedTime(previous, now) > TimeSpan.FromMilliseconds(80)) attackFrames = 0;
        lastObserved = now;
        long energy = 0;
        int peak = 0;
        foreach (short sample in samples)
        {
            energy += (long)sample * sample;
            peak = Math.Max(peak, Math.Abs((int)sample));
        }
        // Two voiced 20 ms frames avoid flashing on an isolated click.
        bool voiced = energy / samples.Length >= 150L * 150 && peak >= 300;
        attackFrames = voiced ? Math.Min(2, attackFrames + 1) : 0;
        if (voiced && (attackFrames >= 2 || Speaking)) lastSpeech = clock.GetTimestamp();
    }
    public void Reset() { lastSpeech = lastObserved = null; attackFrames = 0; }
}
