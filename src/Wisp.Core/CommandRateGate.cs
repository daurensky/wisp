namespace Wisp.Core;

/// <summary>Shared monotonic one-second limit, used at both ends of screen key-frame requests.</summary>
public sealed class CommandRateGate(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly object gate = new();
    private long? previous;
    public bool TryEnter()
    {
        lock (gate)
        {
            long now = clock.GetTimestamp();
            if (previous is { } last && clock.GetElapsedTime(last, now) < TimeSpan.FromSeconds(1)) return false;
            previous = now; return true;
        }
    }
}
