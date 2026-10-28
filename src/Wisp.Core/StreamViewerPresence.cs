namespace Wisp.Core;

/// <summary>Tracks explicit view intent for one broadcast. Replayed network baselines stay silent.</summary>
public sealed class StreamViewerPresence
{
    private string? session;
    private readonly HashSet<string> viewers = new(StringComparer.Ordinal);
    public void Start(string sessionId) { session = sessionId; viewers.Clear(); }
    public void Stop() { session = null; viewers.Clear(); }
    public void Retain(IEnumerable<string> connected) => viewers.IntersectWith(connected);
    public SoundEvent? Observe(string sessionId, string viewerId, bool viewing, bool restoring = false)
    {
        if (session != sessionId || string.IsNullOrWhiteSpace(viewerId)) return null;
        bool changed = viewing ? viewers.Add(viewerId) : viewers.Remove(viewerId);
        return !changed || restoring ? null : viewing ? SoundEvent.VoiceJoined : SoundEvent.VoiceLeft;
    }
}
