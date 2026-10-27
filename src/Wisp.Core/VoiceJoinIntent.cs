namespace Wisp.Core;

/// <summary>Immediate local intent; only the current request may commit asynchronous admission.</summary>
public sealed class VoiceJoinIntent
{
    private long generation;
    public string? ChannelId { get; private set; }
    public bool Connecting { get; private set; }
    public long Begin(string channelId) { ChannelId = channelId; Connecting = true; return ++generation; }
    public bool IsCurrent(long request) => request == generation && ChannelId is not null;
    public bool Complete(long request)
    {
        if (!IsCurrent(request)) return false;
        Connecting = false; return true;
    }
    public void Leave() { generation++; ChannelId = null; Connecting = false; }
}
