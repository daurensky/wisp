using System.Collections.Concurrent;

namespace Wisp.Core;

public sealed record RtcSeat(string UserId, string SessionId, long SeenAt, bool Muted = false, bool Deafened = false);
public sealed record RtcSignal(string FromSession, string ToSession, string Type, string Sdp);

/// <summary>Owner-independent full mesh, Firebase-authenticated SDP, five leased channel seats.</summary>
public sealed class RtcMesh : IAsyncDisposable
{
    private readonly FirebaseBackend cloud;
    private readonly IIceServerProvider iceServers;
    private IReadOnlyList<SIPSorcery.Net.RTCIceServer> currentIceServers = Array.Empty<SIPSorcery.Net.RTCIceServer>();
    private readonly string uid;
    private readonly ConcurrentDictionary<string, Link> links = new();
    private readonly SemaphoreSlim lifecycle = new(1);
    private CancellationTokenSource? stop;
    private Task? loop, leaseGuard;
    private string? community, channel, session, slot;
    private long heartbeatMs;
    private volatile bool expired;
    private DateTimeOffset heartbeat { get => DateTimeOffset.FromUnixTimeMilliseconds(Interlocked.Read(ref heartbeatMs)); set => Interlocked.Exchange(ref heartbeatMs, value.ToUnixTimeMilliseconds()); }
    private bool muted, deafened;
    public bool Joined => session is not null && !expired;
    public IReadOnlyDictionary<string, string> ConnectionPaths => links.Where(p => p.Value.Peer.Connected && p.Value.Peer.SelectedCandidateTypes is not null).ToDictionary(p => p.Key, p => p.Value.Peer.SelectedCandidateTypes!);
    public int ConnectedCount => links.Values.Count(l => l.Peer.Connected);
    public IReadOnlyList<string> ConnectedPeerIds => links.Where(p => !p.Value.Failed && p.Value.Peer.Connected).Select(p => p.Key).ToArray();
    public event Action<WireMessage>? MessageReceived;
    public event Action<string>? Error;
    public event Action? Changed;
    public RtcMesh(FirebaseBackend cloud, IIceServerProvider? iceServers = null) { this.cloud = cloud; this.iceServers = iceServers ?? new StaticIceServerProvider(); uid = cloud.Profile?.Id ?? throw new InvalidOperationException("Firebase login required."); }
    public async Task EnterAsync(string communityId, string channelId, bool mute, bool deafen)
    {
        await lifecycle.WaitAsync();
        try
        {
            await LeaveCoreAsync();
            currentIceServers = await iceServers.GetAsync();
            expired = false; community = communityId; channel = channelId; session = Guid.NewGuid().ToString("N"); muted = mute; deafened = deafen;
            try
            {
                for (var attempt = 0; attempt < 6; attempt++)
                {
                    var seats = await cloud.GetAsync<Dictionary<string, RtcSeat>>(RoomPath) ?? new();
                    long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    slot = seats.FirstOrDefault(p => p.Value.UserId == uid).Key;
                    slot ??= Enumerable.Range(0, Channel.Capacity).Select(i => "s" + i).FirstOrDefault(key => !seats.TryGetValue(key, out var seat) || now - seat.SeenAt > 45000);
                    if (slot is null) throw new InvalidOperationException("Канал заполнен: максимум 5 человек.");
                    try
                    {
                        // The slot rule checks its previous owner atomically at the server.
                        await cloud.PutAsync($"{RoomPath}/{slot}", new RtcSeat(uid, session, now, muted, deafened));
                        break;
                    }
                    catch (InvalidOperationException) when (attempt < 5) { slot = null; await Task.Delay(100); }
                }
                heartbeat = DateTimeOffset.UtcNow; stop = new();
                var token = stop.Token; loop = Task.Run(() => PollAsync(token));
                leaseGuard = Task.Run(async () =>
                {
                    try
                    {
                        while (!token.IsCancellationRequested)
                        {
                            if (DateTimeOffset.UtcNow - heartbeat > TimeSpan.FromSeconds(40))
                            {
                                expired = true; foreach (var link in links.Values) link.Peer.Dispose();
                                stop?.Cancel(); Error?.Invoke("Firebase недоступен: голос остановлен до истечения места в канале. Войди снова."); Changed?.Invoke(); break;
                            }
                            await Task.Delay(1000, token);
                        }
                    }
                    catch (OperationCanceledException) { }
                }); Changed?.Invoke();
            }
            catch { session = null; community = null; channel = null; slot = null; throw; }
        }
        finally { lifecycle.Release(); }
    }
    private string RoomPath => $"communities/{community}/rtcRooms/{channel}";
    private string SignalPath(string from, string to) => $"rtcSignals/{community}/{channel}/{from}/{to}";
    private async Task PollAsync(CancellationToken cancellation)
    {
        string? lastError = null;
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                try
                {
                    if (DateTimeOffset.UtcNow - heartbeat > TimeSpan.FromSeconds(10)) await RenewAsync();
                    var seats = await cloud.GetAsync<Dictionary<string, RtcSeat>>(RoomPath) ?? new();
                    cancellation.ThrowIfCancellationRequested();
                    long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    var active = seats.Values.Where(p => now - p.SeenAt < 45000 && p.SeenAt <= now + 10000).GroupBy(p => p.UserId).ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.SeenAt).First());
                    if (!active.TryGetValue(uid, out var self) || self.SessionId != session) throw new InvalidOperationException("Место в голосовом канале потеряно. Войди снова.");
                    foreach (var id in links.Keys)
                        if (!active.TryGetValue(id, out var seat) || seat.SessionId != links[id].Session)
                            if (links.TryRemove(id, out var old)) old.Peer.Dispose();
                    foreach (var entry in active.Where(p => p.Key != uid).Take(4))
                    {
                        if (!links.TryGetValue(entry.Key, out var link))
                        {
                            var remote = entry.Key; var localChannel = channel!;
                            var peer = new RtcPeer(remote, localChannel, currentIceServers);
                            link = new Link(peer, entry.Value.SessionId, string.CompareOrdinal(uid, remote) < 0);
                            links[remote] = link;
                            peer.StateChanged += state =>
                            {
                                if (cancellation.IsCancellationRequested) return;
                                Changed?.Invoke();
                                if (state is "failed" or "invalid-packet") Fail(remote, link, "Прямое WebRTC-соединение не установлено. Эта сеть может блокировать прямую связь. Выйди из канала и войди снова для повтора.");
                            };
                            peer.MessageReceived += message =>
                            {
                                if (!cancellation.IsCancellationRequested && links.TryGetValue(remote, out var current) && ReferenceEquals(current.Peer, peer)) MessageReceived?.Invoke(message);
                            };
                        }
                        if (!link.Failed && !link.Peer.Connected && DateTimeOffset.UtcNow - link.Created > TimeSpan.FromSeconds(35))
                            Fail(entry.Key, link, "Не удалось соединиться с участником за 35 секунд. Попробуй другую сеть или повторный вход.");
                        if (!link.Failed && !link.Peer.Connected && (link.Negotiation is null || link.Negotiation.IsCompleted))
                            link.Negotiation = NegotiateAsync(entry.Key, link, cancellation);
                    }
                    lastError = null; Changed?.Invoke();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (lastError != ex.Message) { lastError = ex.Message; Error?.Invoke(ex.Message); }
                    if (DateTimeOffset.UtcNow - heartbeat > TimeSpan.FromSeconds(40))
                    {
                        foreach (var l in links.Values) l.Peer.Dispose(); links.Clear(); expired = true; Changed?.Invoke(); break;
                    }
                }
                await Task.Delay(2000, cancellation);
            }
        }
        catch (OperationCanceledException) { }
    }
    private async Task NegotiateAsync(string remote, Link link, CancellationToken cancellation)
    {
        try
        {
            var ownSession = session!; var remoteSession = link.Session;
            if (link.Offerer && !link.LocalPublished)
            {
                var sdp = await link.Peer.OfferAsync(cancellation); cancellation.ThrowIfCancellationRequested();
                await cloud.PutAsync(SignalPath(uid, remote), new RtcSignal(ownSession, remoteSession, "offer", sdp)); link.LocalPublished = true;
            }
            if (link.RemoteApplied) return;
            var signal = await cloud.GetAsync<RtcSignal>(SignalPath(remote, uid)); cancellation.ThrowIfCancellationRequested();
            if (signal is null || signal.FromSession != remoteSession || signal.ToSession != ownSession) return;
            if (link.Offerer && signal.Type == "answer") { link.Peer.AcceptAnswer(signal.Sdp); link.RemoteApplied = true; }
            else if (!link.Offerer && signal.Type == "offer")
            {
                var sdp = await link.Peer.AnswerAsync(signal.Sdp, cancellation); cancellation.ThrowIfCancellationRequested();
                await cloud.PutAsync(SignalPath(uid, remote), new RtcSignal(ownSession, remoteSession, "answer", sdp)); link.RemoteApplied = true;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!cancellation.IsCancellationRequested) Fail(remote, link, "WebRTC: " + ex.Message); }
    }
    private void Fail(string remote, Link link, string message)
    {
        if (link.Failed) return; link.Failed = true; link.Peer.Dispose(); Error?.Invoke(message); Changed?.Invoke();
    }
    private async Task RenewAsync()
    {
        if (session is null) return;
        await cloud.PutAsync($"{RoomPath}/{slot}", new RtcSeat(uid, session, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), muted, deafened)); heartbeat = DateTimeOffset.UtcNow;
    }
    public async Task SetMutedAsync(bool mute, bool deafen) { muted = mute; deafened = deafen; if (Joined) await RenewAsync(); }
    public Task SendAsync(WireMessage message) => Task.WhenAll(links.Values.Where(l => !l.Failed && l.Peer.Connected).Select(l => l.Peer.SendAsync(message)));
    public async Task LeaveAsync()
    {
        await lifecycle.WaitAsync(); try { await LeaveCoreAsync(); } finally { lifecycle.Release(); }
    }
    private async Task LeaveCoreAsync()
    {
        stop?.Cancel();
        foreach (var link in links.Values) link.Peer.Dispose();
        if (loop is not null) await loop;
        if (leaseGuard is not null) await leaseGuard; leaseGuard = null;
        var pending = links.Values.Select(l => l.Negotiation).Where(t => t is not null).Cast<Task>().ToArray();
        if (pending.Length != 0) await Task.WhenAll(pending);
        links.Clear(); loop = null; stop?.Dispose(); stop = null;
        if (community is not null && channel is not null)
        {
            try { var current = slot is null ? null : await cloud.GetAsync<RtcSeat>($"{RoomPath}/{slot}");
                if (current is null || current.UserId == uid && current.SessionId == session)
                {
                    if (slot is not null && current is not null) await cloud.DeleteAsync($"{RoomPath}/{slot}");
                    await cloud.DeleteAsync($"rtcSignals/{community}/{channel}/{uid}");
                } }
            catch (Exception ex) { Error?.Invoke("Выход сохранится после истечения 45 секунд: " + ex.Message); }
        }
        session = null; community = null; channel = null; slot = null; Changed?.Invoke();
    }
    public async ValueTask DisposeAsync() => await LeaveAsync();
    private sealed class Link(RtcPeer peer, string session, bool offerer)
    {
        public RtcPeer Peer { get; } = peer;
        public string Session { get; } = session;
        public bool Offerer { get; } = offerer;
        public bool LocalPublished, RemoteApplied, Failed;
        public Task? Negotiation;
        public DateTimeOffset Created { get; } = DateTimeOffset.UtcNow;
    }
}
