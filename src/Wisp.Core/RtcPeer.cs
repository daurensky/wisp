using System.Text.Json;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
namespace Wisp.Core;

/// <summary>SRTP Opus voice, separate SRTP share audio/H.264 video; SCTP only carries control/files.</summary>
public sealed class RtcPeer : IDisposable
{
    public const string GoogleStun = "stun:stun.l.google.com:19302";
    private readonly RTCPeerConnection pc;
    private readonly SemaphoreSlim sendLock = new(1);
    private readonly RtcFragments controlFragments = new(32 * 1024 * 1024);
    private readonly TaskCompletionSource gathered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object receiveGate = new();
    private RTCDataChannel? control, events;
    private int disposed;
    public bool Connected => pc.connectionState == RTCPeerConnectionState.connected && control?.readyState == RTCDataChannelState.open && events?.readyState == RTCDataChannelState.open;
    public event Action<string>? StateChanged;
    public event Action<WireMessage>? MessageReceived;
    private readonly string remoteId, channelId;
    public RtcPeer(string remoteId, string channelId, bool useGoogleStun = true)
    {
        this.remoteId = remoteId; this.channelId = channelId;
        pc = new RTCPeerConnection(new RTCConfiguration { iceServers = useGoogleStun ? new List<RTCIceServer> { new() { urls = GoogleStun }, new() { urls = "stun:stun1.l.google.com:19302" } } : new() });
        pc.addTrack(new MediaStreamTrack(new AudioFormat(111, "opus", 48000, 2, "minptime=10;useinbandfec=1")));
        pc.addTrack(new MediaStreamTrack(new AudioFormat(112, "opus", 48000, 2, "stereo=1;minptime=10")));
        pc.addTrack(new MediaStreamTrack(new VideoFormat(96, "H264", 90000, "packetization-mode=1;profile-level-id=42e02a;level-asymmetry-allowed=1")));
        pc.OnRtpPacketReceivedByIndex += (index, _, type, packet) =>
        {
            if (Volatile.Read(ref disposed) == 0 && type == SDPMediaTypesEnum.audio && packet.Payload.Length is > 0 and <= 1275)
                MessageReceived?.Invoke(new(index == 1 ? "screen-audio" : "audio", remoteId, channelId, packet.Payload));
        };
        pc.OnVideoFrameReceived += (_, _, bytes, _) => { if (Volatile.Read(ref disposed) == 0 && bytes.Length is > 0 and <= 2 * 1024 * 1024) MessageReceived?.Invoke(new("screen-h264", remoteId, channelId, bytes)); };
        pc.onicegatheringstatechange += state => { if (state == RTCIceGatheringState.complete) gathered.TrySetResult(); };
        pc.onconnectionstatechange += state => StateChanged?.Invoke(state.ToString()); pc.ondatachannel += Bind;
    }
    private void Bind(RTCDataChannel dc)
    {
        if (dc.label == "wisp-control-v2") control = dc; else if (dc.label == "wisp-events-v2") events = dc; else { dc.close(); return; }
        dc.onopen += () => StateChanged?.Invoke("stream-open");
        dc.onmessage += (_, _, bytes) =>
        {
            if (Volatile.Read(ref disposed) != 0) return;
            try
            {
                byte[]? complete; if (dc.label == "wisp-events-v2") { if (bytes.Length > 4096) throw new InvalidDataException(); complete = bytes; } else { lock (receiveGate) complete = controlFragments.Receive(bytes); } if (complete is null) return;
                var message = JsonSerializer.Deserialize<WireMessage>(complete) ?? throw new InvalidDataException();
                if (message.Type is not ("asset" or "sound" or "screen-start" or "screen-stop") || message.Payload is null || message.Payload.Length > 23 * 1024 * 1024) throw new InvalidDataException("Invalid RTC message.");
                MessageReceived?.Invoke(message with { SenderId = remoteId, ChannelId = channelId });
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException) { StateChanged?.Invoke("invalid-packet"); Dispose(); }
        };
    }
    public async Task<string> OfferAsync(CancellationToken cancellation = default)
    {
        Bind(await pc.createDataChannel("wisp-control-v2", new RTCDataChannelInit { ordered = true }));
        Bind(await pc.createDataChannel("wisp-events-v2", new RTCDataChannelInit { ordered = true })); await GatherAsync(cancellation);
        var offer = pc.createOffer(); await pc.setLocalDescription(offer); return offer.sdp;
    }
    public async Task<string> AnswerAsync(string offer, CancellationToken cancellation = default)
    {
        ApplyRemote(offer, RTCSdpType.offer); await GatherAsync(cancellation); var answer = pc.createAnswer(); await pc.setLocalDescription(answer); return answer.sdp;
    }
    public void AcceptAnswer(string answer) => ApplyRemote(answer, RTCSdpType.answer);
    private void ApplyRemote(string sdp, RTCSdpType type)
    {
        if (sdp.Length > 64000 || pc.setRemoteDescription(new RTCSessionDescriptionInit { sdp = sdp, type = type }) != SetDescriptionResultEnum.OK) throw new InvalidDataException("WebRTC SDP rejected.");
    }
    private async Task GatherAsync(CancellationToken cancellation)
    {
        if (pc.iceGatheringState == RTCIceGatheringState.complete) return;
        try { await gathered.Task.WaitAsync(TimeSpan.FromSeconds(8), cancellation); } catch (TimeoutException) { }
    }
    public async Task SendAsync(WireMessage message)
    {
        if (!Connected || Volatile.Read(ref disposed) != 0) return;
        if (message.Type is "audio" or "screen-audio")
        {
            if (message.Payload.Length is 0 or > 1275) return;
            pc.AudioStreamList[message.Type == "audio" ? 0 : 1].SendAudio(960, message.Payload); return;
        }
        if (message.Type == "screen-h264") { if (H264Frame.TryUnpack(message.Payload, out var fps, out var data)) pc.SendVideo((uint)(90000 / fps), data); return; }
        if (message.Type is "screen-start" or "screen-stop" or "sound") { var command = JsonSerializer.SerializeToUtf8Bytes(message); if (command.Length <= 4096) events!.send(command); return; }
        if (!await sendLock.WaitAsync(0)) throw new IOException("Передача файла уже выполняется. Повтори после её завершения.");
        try
        {
            if (control?.readyState != RTCDataChannelState.open) return;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(message); if (bytes.Length > 32 * 1024 * 1024) throw new InvalidDataException("RTC file too large.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            foreach (var packet in RtcFragments.Encode(bytes))
            {
                while (control.bufferedAmount > 64000) { timeout.Token.ThrowIfCancellationRequested(); await Task.Delay(10, timeout.Token); }
                if (Volatile.Read(ref disposed) != 0) return; control.send(packet);
            }
        }
        finally { sendLock.Release(); }
    }
    public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) pc.Close("Wisp channel left"); }
}
