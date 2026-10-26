using SIPSorcery.Net;

namespace Wisp.Core;

public sealed record IceCandidateData(string Candidate, string SdpMid, int SdpMLineIndex);

public interface IRtcPeer : IDisposable
{
    bool Connected { get; }
    string? SelectedCandidateTypes { get; }
    IReadOnlyList<IceCandidateData> LocalCandidates { get; }
    event Action<string>? StateChanged;
    event Action<WireMessage>? MessageReceived;
    event Action? CandidateReady;
    Task<string> OfferAsync(CancellationToken cancellation = default);
    Task<string> AnswerAsync(string offer, CancellationToken cancellation = default);
    void AcceptAnswer(string answer);
    void AddIceCandidate(IceCandidateData candidate);
    void RestartIce();
    Task SendAsync(WireMessage message);
}

public delegate IRtcPeer RtcPeerFactory(string remoteId, string channelId, IReadOnlyList<RTCIceServer> servers);
