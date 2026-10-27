namespace Wisp.Core;

/// <summary>Monotonic RTP clock: skipped frames and DTX gaps remain visible on the wire.</summary>
public sealed class RtpMediaClock(int rate, uint minimumStep, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;
    private long? origin;
    private ulong previous;
    public uint Next()
    {
        long now = time.GetTimestamp();
        if (origin is null) { origin = now; return 0; }
        var elapsed = (ulong)(time.GetElapsedTime(origin.Value, now).TotalSeconds * rate);
        previous = Math.Max(previous + minimumStep, elapsed);
        return unchecked((uint)previous);
    }
}

public sealed record VideoNetworkReport(double Loss, double JitterMilliseconds, double? RoundTripMilliseconds);

/// <summary>Conservative shared-stream reduction after three bad receiver reports; never exceeds the user's cap.</summary>
public sealed class VideoRateController(int bitrate, int fps, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;
    private readonly Dictionary<string, (int Bad, long Seen)> reports = new();
    private long? lastChange;
    private readonly int initialBitrate = bitrate, initialFps = fps;
    public int Bitrate { get; private set; } = bitrate;
    public int FramesPerSecond { get; private set; } = fps;
    public bool Observe(string peer, VideoNetworkReport report)
    {
        if (!double.IsFinite(report.Loss) || report.Loss is < 0 or > 1 || !double.IsFinite(report.JitterMilliseconds)) return false;
        long now = time.GetTimestamp();
        foreach (var key in reports.Where(pair => time.GetElapsedTime(pair.Value.Seen, now) > TimeSpan.FromSeconds(15)).Select(pair => pair.Key).ToArray()) reports.Remove(key);
        reports.TryGetValue(peer, out var previous);
        int bad = report.Loss >= .08 ? previous.Bad + 1 : 0;
        reports[peer] = (bad, now);
        if (bad < 3 || (lastChange is { } changed && time.GetElapsedTime(changed, now) < TimeSpan.FromSeconds(5))) return false;
        int next = Math.Max(500000, Bitrate * 4 / 5);
        int nextFps = next < initialBitrate * .6 ? Math.Min(30, initialFps) : FramesPerSecond;
        if (next < initialBitrate * .35) nextFps = 15;
        if (next == Bitrate && nextFps == FramesPerSecond) return false;
        Bitrate = next; FramesPerSecond = nextFps; lastChange = now;
        reports[peer] = (0, now);
        return true;
    }
}
