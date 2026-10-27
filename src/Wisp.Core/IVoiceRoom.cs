namespace Wisp.Core;

/// <summary>The desktop uses the same presence/media contract for direct and SFU rooms.</summary>
public interface IVoiceRoom : IAsyncDisposable
{
    bool Joined { get; }
    string? ChannelId { get; }
    string TransportName { get; }
    IReadOnlyList<RtcSeat> Participants { get; }
    int ConnectedCount { get; }
    IReadOnlyList<string> ConnectedPeerIds { get; }
    IReadOnlyDictionary<string, string> ConnectionPaths { get; }
    event Action<WireMessage>? MessageReceived;
    event Action<string>? Error;
    event Action? Changed;
    Task EnterAsync(string communityId, string channelId, bool mute, bool deafen);
    Task SetMutedAsync(bool mute, bool deafen);
    Task SendAsync(WireMessage message);
    Task LeaveAsync();
}
