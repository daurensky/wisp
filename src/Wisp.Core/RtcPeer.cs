using System.Buffers.Binary;
using System.Text.Json;
using SIPSorcery.Net;

namespace Wisp.Core;

/// <summary>Three encrypted SCTP streams: lossy Opus, lossy JPEG, reliable bounded control/files.</summary>
public sealed class RtcPeer : IDisposable
{
    public const string GoogleStun = "stun:stun.l.google.com:19302";
    private readonly RTCPeerConnection pc;
    private readonly SemaphoreSlim sendLock = new(1);
    private readonly RtcFragments controlFragments = new(32 * 1024 * 1024);
    private readonly RtcFragments frameFragments = new(2 * 1024 * 1024);
    private readonly TaskCompletionSource gathered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object receiveGate = new();
    private RTCDataChannel? audio, frames, control;
    private uint audioSequence, lastAudioSequence;
    private int disposed, frameSending;
    public bool Connected => pc.connectionState == RTCPeerConnectionState.connected && audio?.readyState == RTCDataChannelState.open && control?.readyState == RTCDataChannelState.open;
    public event Action<string>? StateChanged;
    public event Action<WireMessage>? MessageReceived;
    private readonly string remoteId, channelId;
    public RtcPeer(string remoteId, string channelId, bool useGoogleStun = true)
    {
        this.remoteId = remoteId; this.channelId = channelId;
        pc = new RTCPeerConnection(new RTCConfiguration { iceServers = useGoogleStun ? new List<RTCIceServer> { new() { urls = GoogleStun }, new() { urls = "stun:stun1.l.google.com:19302" } } : new() });
        pc.onicegatheringstatechange += state => { if (state == RTCIceGatheringState.complete) gathered.TrySetResult(); };
        pc.onconnectionstatechange += state => StateChanged?.Invoke(state.ToString());
        pc.ondatachannel += Bind;
    }
    private void Bind(RTCDataChannel dc)
    {
        switch (dc.label) { case "opus": audio = dc; break; case "screen": frames = dc; break; case "control": control = dc; break; default: dc.close(); return; }
        dc.onopen += () => StateChanged?.Invoke("stream-open");
        dc.onmessage += (_, _, bytes) =>
        {
            if (Volatile.Read(ref disposed) != 0) return;
            try
            {
                if (dc.label == "opus")
                {
                    if (bytes.Length is < 5 or > 1279) return;
                    var sequence = BinaryPrimitives.ReadUInt32BigEndian(bytes);
                    if (sequence <= lastAudioSequence) return; lastAudioSequence = sequence;
                    MessageReceived?.Invoke(new("audio", remoteId, channelId, bytes[4..]));
                }
                else
                {
                    byte[]? complete; lock (receiveGate) complete = (dc.label == "screen" ? frameFragments : controlFragments).Receive(bytes);
                    if (complete is null) return;
                    if (dc.label == "screen") MessageReceived?.Invoke(new("screen", remoteId, channelId, complete));
                    else
                    {
                        var message = JsonSerializer.Deserialize<WireMessage>(complete) ?? throw new InvalidDataException();
                        if (message.Type is not ("asset" or "sound" or "screen-stop") || message.Payload is null || message.Payload.Length > 23 * 1024 * 1024) throw new InvalidDataException("Invalid RTC message.");
                        MessageReceived?.Invoke(message with { SenderId = remoteId, ChannelId = channelId });
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException) { StateChanged?.Invoke("invalid-packet"); Dispose(); }
        };
    }
    public async Task<string> OfferAsync(CancellationToken cancellation = default)
    {
        Bind(await pc.createDataChannel("opus", new RTCDataChannelInit { ordered = false, maxRetransmits = 0 }));
        Bind(await pc.createDataChannel("screen", new RTCDataChannelInit { ordered = true, maxRetransmits = 0 }));
        Bind(await pc.createDataChannel("control", new RTCDataChannelInit { ordered = true }));
        await GatherAsync(cancellation);
        var offer = pc.createOffer(); await pc.setLocalDescription(offer); return offer.sdp;
    }
    public async Task<string> AnswerAsync(string offer, CancellationToken cancellation = default)
    {
        ApplyRemote(offer, RTCSdpType.offer); await GatherAsync(cancellation);
        var answer = pc.createAnswer(); await pc.setLocalDescription(answer); return answer.sdp;
    }
    public void AcceptAnswer(string answer) => ApplyRemote(answer, RTCSdpType.answer);
    private void ApplyRemote(string sdp, RTCSdpType type)
    {
        if (sdp.Length > 64000 || pc.setRemoteDescription(new RTCSessionDescriptionInit { sdp = sdp, type = type }) != SetDescriptionResultEnum.OK)
            throw new InvalidDataException("WebRTC SDP rejected.");
    }
    private async Task GatherAsync(CancellationToken cancellation)
    {
        if (pc.iceGatheringState == RTCIceGatheringState.complete) return;
        try { await gathered.Task.WaitAsync(TimeSpan.FromSeconds(8), cancellation); }
        catch (TimeoutException) { /* Continue with available host/reflexive candidates, never add a relay. */ }
    }
    public async Task SendAsync(WireMessage message)
    {
        if (!Connected || Volatile.Read(ref disposed) != 0) return;
        if (message.Type == "audio")
        {
            if (message.Payload.Length is 0 or > 1275 || audio!.bufferedAmount > 16000) return;
            var packet = new byte[4 + message.Payload.Length]; BinaryPrimitives.WriteUInt32BigEndian(packet, ++audioSequence); message.Payload.CopyTo(packet, 4); audio.send(packet); return;
        }
        bool screen = message.Type == "screen";
        if (screen && (message.Payload.Length > 2 * 1024 * 1024 || Interlocked.Exchange(ref frameSending, 1) != 0)) return;
        if (!screen && !await sendLock.WaitAsync(0)) throw new IOException("Передача файла уже выполняется. Повтори после её завершения.");
        try
        {
            var dc = screen ? frames : control;
            if (dc?.readyState != RTCDataChannelState.open) return;
            var bytes = screen ? message.Payload : JsonSerializer.SerializeToUtf8Bytes(message);
            if (bytes.Length > 32 * 1024 * 1024) throw new InvalidDataException("RTC file too large.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var frameDeadline = DateTimeOffset.UtcNow.AddMilliseconds(150);
            foreach (var packet in RtcFragments.Encode(bytes))
            {
                while (dc.bufferedAmount > 128000) { if (screen && DateTimeOffset.UtcNow > frameDeadline) return; timeout.Token.ThrowIfCancellationRequested(); await Task.Delay(screen ? 2 : 10, timeout.Token); }
                if (Volatile.Read(ref disposed) != 0) return;
                dc.send(packet);
            }
        }
        finally { if (screen) Interlocked.Exchange(ref frameSending, 0); else sendLock.Release(); }
    }
    public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) pc.Close("Wisp channel left"); }
}
