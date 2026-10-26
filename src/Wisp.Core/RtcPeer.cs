using System.Text.Json;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
namespace Wisp.Core;

/// <summary>SRTP Opus voice, separate SRTP share audio/H.264 video; SCTP only carries control/files.</summary>
public sealed class RtcPeer : IRtcPeer
{
    public const string GoogleStun = "stun:stun.l.google.com:19302";
    private readonly RTCPeerConnection pc;
    private readonly SemaphoreSlim sendLock = new(1);
    private readonly RtcFragments controlFragments = new(32 * 1024 * 1024);
    private readonly object receiveGate = new();
    private RTCDataChannel? control, events;
    private int disposed;
    private readonly AudioSequenceTracker[] audioSequences = [new(), new()];
    private readonly object audioOrder = new();
    private readonly RtpMediaClock[] audioClocks = [new(48000, 960), new(48000, 960)];
    private readonly RtpMediaClock videoClock = new(90000, 1);
    private readonly uint[] initialAudioTimestamp = new uint[2];
    private uint initialVideoTimestamp;
    private readonly object sendAudioGate = new(), sendVideoGate = new();
    private readonly object candidateGate = new();
    private readonly Queue<RTCIceCandidateInit> pendingCandidates = new();
    private bool remoteDescriptionApplied;
    public event Action? CandidateReady;
    public IReadOnlyList<IceCandidateData> LocalCandidates => pc.GetRtpChannel().Candidates.Take(64).Select(candidate =>
    {
        var init = JsonSerializer.Deserialize<RTCIceCandidateInit>(candidate.toJSON())!;
        return new IceCandidateData(init.candidate, init.sdpMid ?? "0", init.sdpMLineIndex);
    }).ToArray();
    public bool Connected => pc.connectionState == RTCPeerConnectionState.connected && pc.iceConnectionState == RTCIceConnectionState.connected && control?.readyState == RTCDataChannelState.open && events?.readyState == RTCDataChannelState.open;
    public string? SelectedCandidateTypes
    {
        get
        {
            if (!Connected) return null;
            var selected = pc.GetRtpChannel()?.NominatedEntry;
            return selected is null ? null : $"{selected.LocalCandidate.type}/{selected.RemoteCandidate.type}";
        }
    }
    public event Action<string>? StateChanged;
    public event Action<WireMessage>? MessageReceived;
    private readonly string remoteId, channelId;
    public RtcPeer(string remoteId, string channelId, IReadOnlyList<RTCIceServer>? iceServers = null)
    {
        this.remoteId = remoteId; this.channelId = channelId;
        pc = new RTCPeerConnection(new RTCConfiguration { iceServers = iceServers?.ToList() ?? FileIceServerProvider.Defaults.Select(url => new RTCIceServer { urls = url }).ToList() });
        pc.addTrack(new MediaStreamTrack(new AudioFormat(111, "opus", 48000, 2, "minptime=10;useinbandfec=1")));
        pc.addTrack(new MediaStreamTrack(new AudioFormat(112, "opus", 48000, 2, "stereo=1;minptime=10")));
        pc.addTrack(new MediaStreamTrack(new VideoFormat(96, "H264", 90000, "packetization-mode=1;profile-level-id=42e02a;level-asymmetry-allowed=1")));
        initialAudioTimestamp[0] = pc.AudioStreamList[0].LocalTrack.Timestamp;
        initialAudioTimestamp[1] = pc.AudioStreamList[1].LocalTrack.Timestamp;
        initialVideoTimestamp = pc.VideoLocalTrack.Timestamp;
        pc.OnReceiveReport += (_, type, report) =>
        {
            if (type != SDPMediaTypesEnum.video || Volatile.Read(ref disposed) != 0) return;
            var samples = report.ReceiverReport?.ReceptionReports ?? report.SenderReport?.ReceptionReports;
            if (samples is null) return;
            foreach (var sample in samples.Where(sample => sample.SSRC == pc.VideoLocalTrack.Ssrc).Take(1))
            {
                double? rtt = null;
                if (sample.LastSenderReportTimestamp != 0)
                {
                    double ntpSeconds = (DateTimeOffset.UtcNow - DateTimeOffset.UnixEpoch).TotalSeconds + 2208988800d;
                    uint middleNtp = unchecked((uint)(ulong)(ntpSeconds * 65536));
                    double measured = unchecked(middleNtp - sample.LastSenderReportTimestamp - sample.DelaySinceLastSenderReport) * 1000d / 65536;
                    if (measured <= 30000) rtt = measured;
                }
                MessageReceived?.Invoke(new("video-report", remoteId, channelId, JsonSerializer.SerializeToUtf8Bytes(new VideoNetworkReport(sample.FractionLost / 256d, sample.Jitter / 90d, rtt))));
            }
        };
        pc.OnRtpPacketReceivedByIndex += (index, _, type, packet) =>
        {
            if (Volatile.Read(ref disposed) != 0 || type != SDPMediaTypesEnum.audio || index is < 0 or > 1 || packet.Payload.Length is 0 or > 1275) return;
            int missing;
            lock (audioOrder)
            {
                if (!audioSequences[index].Accept(packet.Header.SequenceNumber, out missing)) return;
                MessageReceived?.Invoke(new WireMessage(index == 1 ? "screen-audio" : "audio", remoteId, channelId, packet.Payload) { MissingAudioFrames = missing });
            }
        };
        pc.OnVideoFrameReceived += (_, _, bytes, _) => { if (Volatile.Read(ref disposed) == 0 && bytes.Length is > 0 and <= 2 * 1024 * 1024) MessageReceived?.Invoke(new("screen-h264", remoteId, channelId, bytes)); };
        pc.onicecandidate += _ => CandidateReady?.Invoke();
        pc.oniceconnectionstatechange += state =>
        {
            if (state is RTCIceConnectionState.disconnected or RTCIceConnectionState.failed)
                StateChanged?.Invoke(state.ToString());
            else StateChanged?.Invoke("ice-" + state);
        };
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
                if (message.Type is not ("asset" or "sound" or "screen-start" or "screen-stop" or "screen-keyframe" or "screen-state") || message.Payload is null || message.Payload.Length > 23 * 1024 * 1024) throw new InvalidDataException("Invalid RTC message.");
                if (message.Type is "screen-keyframe" or "screen-state" && message.Payload.Length > 512) throw new InvalidDataException("Oversize screen command.");
                MessageReceived?.Invoke(message with { SenderId = remoteId, ChannelId = channelId });
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException) { StateChanged?.Invoke("invalid-packet"); Dispose(); }
        };
    }
    public async Task<string> OfferAsync(CancellationToken cancellation = default)
    {
        Bind(await pc.createDataChannel("wisp-control-v2", new RTCDataChannelInit { ordered = true }));
        Bind(await pc.createDataChannel("wisp-events-v2", new RTCDataChannelInit { ordered = true })); cancellation.ThrowIfCancellationRequested();
        var offer = pc.createOffer(); await pc.setLocalDescription(offer); return offer.sdp;
    }
    public async Task<string> AnswerAsync(string offer, CancellationToken cancellation = default)
    {
        ApplyRemote(offer, RTCSdpType.offer); cancellation.ThrowIfCancellationRequested(); var answer = pc.createAnswer(); await pc.setLocalDescription(answer); return answer.sdp;
    }
    public void AcceptAnswer(string answer) => ApplyRemote(answer, RTCSdpType.answer);
    private void ApplyRemote(string sdp, RTCSdpType type)
    {
        if (sdp.Length > 64000 || pc.setRemoteDescription(new RTCSessionDescriptionInit { sdp = sdp, type = type }) != SetDescriptionResultEnum.OK) throw new InvalidDataException("WebRTC SDP rejected.");
        lock (candidateGate)
        {
            remoteDescriptionApplied = true;
            while (pendingCandidates.TryDequeue(out var candidate)) pc.addIceCandidate(candidate);
        }
    }
    public void AddIceCandidate(IceCandidateData candidate)
    {
        if (candidate.Candidate is null || candidate.Candidate.Length > 2048 || candidate.SdpMid is null ||
            candidate.SdpMid.Length > 64 || candidate.SdpMLineIndex is < 0 or > 3)
            throw new InvalidDataException("Некорректный ICE-кандидат.");
        var init = new RTCIceCandidateInit
        {
            candidate = candidate.Candidate, sdpMid = candidate.SdpMid,
            sdpMLineIndex = (ushort)candidate.SdpMLineIndex
        };
        lock (candidateGate)
        {
            if (!remoteDescriptionApplied)
            {
                if (pendingCandidates.Count >= 64) throw new InvalidDataException("Слишком много ICE-кандидатов.");
                pendingCandidates.Enqueue(init);
            }
            else pc.addIceCandidate(init);
        }
    }
    public void RestartIce()
    {
        if (Volatile.Read(ref disposed) != 0) return;
        var channel = pc.GetRtpChannel();
        var user = channel.RemoteIceUser;
        var password = channel.RemoteIcePassword;
        pc.restartIce();
        // 10.0.17 Restart resets the checklist but keeps the remote credentials. Re-arm checking explicitly.
        if (user is not null && password is not null) pc.SetRemoteCredentials(user, password);
        CandidateReady?.Invoke();
        StateChanged?.Invoke("ice-restart");
    }
    public async Task SendAsync(WireMessage message)
    {
        if (!Connected || Volatile.Read(ref disposed) != 0) return;
        if (message.Type is "audio" or "screen-audio")
        {
            if (message.Payload.Length is 0 or > 1275) return;
            int index = message.Type == "audio" ? 0 : 1;
            lock (sendAudioGate)
            {
                var stream = pc.AudioStreamList[index];
                stream.SendRtpRaw(message.Payload, unchecked(initialAudioTimestamp[index] + audioClocks[index].Next()), 0, index == 0 ? 111 : 112);
            }
            return;
        }
        if (message.Type == "screen-h264") { if (H264Frame.TryUnpack(message.Payload, out var fps, out var data))
            {
                lock (sendVideoGate)
                {
                    uint timestamp = unchecked(initialVideoTimestamp + videoClock.Next());
                    foreach (var nal in H264Packetiser.ParseNals(data))
                    {
                        if (nal.NAL.Length <= 1200) pc.VideoStream.SendRtpRaw(nal.NAL, timestamp, nal.IsLast ? 1 : 0, 96);
                        else
                        {
                            // RFC 6184 FU-A. The public SendRtpRaw API preserves explicit timestamps;
                            // 10.0.17's SendVideo advances an internally-set timestamp by a fixed duration.
                            for (int offset = 1; offset < nal.NAL.Length; offset += 1198)
                            {
                                int length = Math.Min(1198, nal.NAL.Length - offset);
                                bool last = offset + length == nal.NAL.Length;
                                var payload = new byte[length + 2];
                                payload[0] = (byte)((nal.NAL[0] & 0xe0) | 28);
                                payload[1] = (byte)((nal.NAL[0] & 31) | (offset == 1 ? 128 : 0) | (last ? 64 : 0));
                                Buffer.BlockCopy(nal.NAL, offset, payload, 2, length);
                                pc.VideoStream.SendRtpRaw(payload, timestamp, nal.IsLast && last ? 1 : 0, 96);
                            }
                        }
                    }
                }
            } return; }
        if (message.Type is "screen-start" or "screen-stop" or "screen-keyframe" or "screen-state" or "sound") { var command = JsonSerializer.SerializeToUtf8Bytes(message); if (command.Length <= 4096) events!.send(command); return; }
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
