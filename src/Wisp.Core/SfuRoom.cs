using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using SIPSorcery.Net;

namespace Wisp.Core;

/// <summary>One SRTP uplink and up to four source-bound downlinks. The SFU never transcodes.</summary>
public sealed class SfuRoom(FirebaseBackend cloud, VoiceServerSettings settings, IIceServerProvider ice, RtcPeerFactory? peerFactory = null) : IVoiceRoom
{
    private readonly ConcurrentDictionary<string, Link> links = new();
    private readonly SemaphoreSlim lifecycle = new(1);
    private readonly string uid = cloud.Profile?.Id ?? throw new InvalidOperationException("Firebase login required.");
    private ClientWebSocket? socket;
    private CancellationTokenSource? stop;
    private Channel<object>? outbound;
    private SfuSeatLease? seat;
    private Task? reader, writer, maintenance;
    private TaskCompletionSource? ready, published;
    private IReadOnlyList<RTCIceServer> servers = Array.Empty<RTCIceServer>();
    private string? channel;
    private volatile bool joined;
    private int failed;
    public bool Joined => joined && Volatile.Read(ref failed) == 0;
    public string? ChannelId => Joined ? channel : null;
    public string TransportName => "WebRTC · SFU";
    public IReadOnlyList<RtcSeat> Participants => seat?.Participants ?? Array.Empty<RtcSeat>();
    public IReadOnlyList<string> ConnectedPeerIds => Joined ? links.Values.Where(l => l.Remote != uid && l.Peer.Connected).Select(l => l.Remote).ToArray() : Array.Empty<string>();
    public int ConnectedCount => ConnectedPeerIds.Count;
    public IReadOnlyDictionary<string, string> ConnectionPaths => ConnectedPeerIds.ToDictionary(id => id, _ => "SFU");
    public event Action<WireMessage>? MessageReceived;
    public event Action<string>? Error;
    public event Action? Changed;

    private sealed class Link(IRtcPeer peer, string remote)
    {
        public IRtcPeer Peer { get; } = peer;
        public string Remote { get; } = remote;
        public HashSet<string> Sent { get; } = new();
        public bool Answered;
        public volatile bool LocalPublished;
    }
    private static TaskCompletionSource Completion() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private void Queue(object value)
    {
        if (stop?.IsCancellationRequested != false) return;
        if (outbound?.Writer.TryWrite(value) != true) Fail("Очередь сигналинга SFU переполнена. Подключись снова.");
    }
    private void Fail(string error)
    {
        if (Interlocked.Exchange(ref failed, 1) != 0) return;
        joined = false;
        ready?.TrySetException(new IOException(error)); published?.TrySetException(new IOException(error));
        stop?.Cancel(); socket?.Abort();
        foreach (var l in links.Values) l.Peer.Dispose();
        Error?.Invoke(error); Changed?.Invoke();
    }
    public async Task EnterAsync(string communityId, string channelId, bool mute, bool deafen)
    {
        await lifecycle.WaitAsync();
        try
        {
            await LeaveCoreAsync();
            var endpoint = settings.Endpoint();
            failed = 0; joined = false; channel = channelId;
            ready = Completion(); published = Completion();
            stop = new CancellationTokenSource();
            outbound = System.Threading.Channels.Channel.CreateBounded<object>(new BoundedChannelOptions(128) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(45));
            var token = deadline.Token;
            try
            {
                servers = await ice.GetAsync(token);
                seat = new SfuSeatLease(cloud, communityId, channelId);
                seat.Changed += () => Changed?.Invoke(); seat.Lost += Fail;
                await seat.ClaimAsync(mute, deafen, token);
                socket = new ClientWebSocket();
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);
                socket.Options.RemoteCertificateValidationCallback = (_, cert, _, errors) => settings.ValidateCertificate(cert, errors);
                await socket.ConnectAsync(endpoint, token);
                writer = Task.Run(() => WriteAsync(stop.Token));
                reader = Task.Run(() => ReadAsync(stop.Token));
                Queue(new { type = "join", version = 1, token = await cloud.GetAccessTokenAsync(token), community = communityId, channel = channelId, session = seat.Session });
                await ready.Task.WaitAsync(token);
                var pub = CreateLink("pub", uid);
                var sdp = await pub.Peer.OfferAsync(token);
                Queue(new { type = "offer", id = "pub", sdp });
                pub.LocalPublished = true;
                maintenance = Task.Run(() => MaintainAsync(stop.Token));
                await published.Task.WaitAsync(token);
                joined = true; Changed?.Invoke();
            }
            catch
            {
                await LeaveCoreAsync();
                throw;
            }
        }
        finally { lifecycle.Release(); }
    }
    private Link CreateLink(string id, string remote)
    {
        var peer = peerFactory?.Invoke(remote, channel!, servers) ?? new RtcPeer(remote, channel!, servers);
        var link = new Link(peer, remote);
        if (!links.TryAdd(id, link)) { peer.Dispose(); throw new InvalidDataException("Повторное соединение SFU."); }
        peer.MessageReceived += m => { if (Joined && remote != uid && links.TryGetValue(id, out var active) && ReferenceEquals(active, link)) MessageReceived?.Invoke(m); };
        peer.StateChanged += state =>
        {
            if (stop?.IsCancellationRequested != false || !links.ContainsKey(id)) return;
            if (state is "failed" or "invalid-packet" or "restart-required") { Fail("Соединение SFU потеряно. Подключись к каналу снова."); return; }
            if (id == "pub" && peer.Connected) published?.TrySetResult();
            Changed?.Invoke();
        };
        return link;
    }
    private async Task WriteAsync(CancellationToken token)
    {
        try
        {
            await foreach (var value in outbound!.Reader.ReadAllAsync(token))
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(8));
                await socket!.SendAsync(bytes, WebSocketMessageType.Text, true, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { Fail("Не удалось отправить сигнал SFU. Проверь соединение с сервером."); }
    }
    private async Task ReadAsync(CancellationToken token)
    {
        byte[] buffer = new byte[70000];
        try
        {
            while (!token.IsCancellationRequested)
            {
                int count = 0;
                WebSocketReceiveResult result;
                do
                {
                    if (count == buffer.Length) throw new InvalidDataException();
                    result = await socket!.ReceiveAsync(new ArraySegment<byte>(buffer, count, buffer.Length - count), token);
                    if (result.MessageType != WebSocketMessageType.Text) throw new InvalidDataException();
                    count += result.Count;
                } while (!result.EndOfMessage);
                using var json = JsonDocument.Parse(buffer.AsMemory(0, count));
                var m = json.RootElement; string type = m.GetProperty("type").GetString()!;
                switch (type)
                {
                    case "ready":
                        if (m.GetProperty("version").GetInt32() != 1) throw new InvalidDataException();
                        ready!.TrySetResult(); break;
                    case "answer":
                        if (m.GetProperty("id").GetString() != "pub" || !links.TryGetValue("pub", out var pub) || pub.Answered) throw new InvalidDataException();
                        pub.Answered = true; pub.Peer.AcceptAnswer(m.GetProperty("sdp").GetString()!); break;
                    case "offer":
                        string id = m.GetProperty("id").GetString()!, remote = m.GetProperty("peer").GetString()!;
                        if (id.Length > 180 || remote.Length is 0 or > 128 || remote == uid || links.Count >= 5 || !id.StartsWith("s_", StringComparison.Ordinal) || links.Values.Any(l => l.Remote == remote)) throw new InvalidDataException();
                        var sub = CreateLink(id, remote);
                        var answer = await sub.Peer.AnswerAsync(m.GetProperty("sdp").GetString()!, token);
                        Queue(new { type = "answer", id, sdp = answer }); sub.LocalPublished = true; break;
                    case "remove":
                        if (links.TryRemove(m.GetProperty("id").GetString()!, out var removed)) removed.Peer.Dispose();
                        Changed?.Invoke(); break;
                    case "report":
                        string viewer = m.GetProperty("peer").GetString()!;
                        double loss = m.TryGetProperty("loss", out var v) ? v.GetDouble() : 0, jitter = m.TryGetProperty("jitter", out v) ? v.GetDouble() : 0;
                        if (Joined && viewer != uid && ConnectedPeerIds.Contains(viewer) && double.IsFinite(loss) && loss is >= 0 and <= 1 && double.IsFinite(jitter) && jitter is >= 0 and <= 30000)
                            MessageReceived?.Invoke(new("video-report", viewer, channel, JsonSerializer.SerializeToUtf8Bytes(new VideoNetworkReport(loss, jitter, null))));
                        break;
                    default: throw new InvalidDataException();
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { Fail("Соединение SFU закрыто: проверь доступ к каналу, сервер и сеть."); }
    }
    private async Task MaintainAsync(CancellationToken token)
    {
        try
        {
            int tick = 0;
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(token))
            {
                foreach (var (id, link) in links.Where(pair => pair.Value.LocalPublished))
                    foreach (var c in link.Peer.LocalCandidates)
                        if (link.Sent.Add(c.Candidate)) Queue(new { type = "candidate", id, candidate = new { candidate = c.Candidate, sdpMid = c.SdpMid, sdpMLineIndex = c.SdpMLineIndex } });
                if (links.TryGetValue("pub", out var pub) && pub.Peer.Connected) published?.TrySetResult();
                if (++tick % 300 == 0) Queue(new { type = "auth", token = await cloud.GetAccessTokenAsync(token) });
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { Fail("Не удалось обновить подключение SFU. Подключись снова."); }
    }
    public Task SetMutedAsync(bool mute, bool deafen) => seat?.SetMutedAsync(mute, deafen) ?? Task.CompletedTask;
    public Task SendAsync(WireMessage message) => Joined && links.TryGetValue("pub", out var pub) ? pub.Peer.SendAsync(message) : Task.CompletedTask;
    public async Task LeaveAsync()
    {
        await lifecycle.WaitAsync(); try { await LeaveCoreAsync(); } finally { lifecycle.Release(); }
    }
    private async Task LeaveCoreAsync()
    {
        joined = false; stop?.Cancel(); socket?.Abort();
        foreach (var l in links.Values) l.Peer.Dispose();
        try { await Task.WhenAll(new[] { reader, writer, maintenance }.OfType<Task>()); } catch (OperationCanceledException) { }
        links.Clear();
        if (seat is not null) { seat.Lost -= Fail; await seat.DisposeAsync(); seat = null; }
        socket?.Dispose(); socket = null; stop?.Dispose(); stop = null;
        outbound = null; reader = writer = maintenance = null; channel = null;
        Changed?.Invoke();
    }
    public async ValueTask DisposeAsync() => await LeaveAsync();
}
