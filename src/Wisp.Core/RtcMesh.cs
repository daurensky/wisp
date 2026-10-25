using System.Collections.Concurrent;

namespace Wisp.Core;

public sealed record RtcSeat(string SessionId, long SeenAt, bool Muted = false, bool Deafened = false);
public sealed record RtcSignal(string FromSession, string ToSession, string Type, string Sdp);

/// <summary>Owner-independent full mesh, Firebase-authenticated SDP, five leased channel seats.</summary>
public sealed class RtcMesh : IAsyncDisposable
{
    private readonly FirebaseBackend cloud;
    private readonly string uid;
    private readonly ConcurrentDictionary<string, Link> links = new();
    private readonly SemaphoreSlim lifecycle = new(1);
    private CancellationTokenSource? stop;
    private Task? loop;
    private string? community, channel, session;
    private DateTimeOffset heartbeat;
    private bool muted, deafened;
    public bool Joined => session is not null;
    public int ConnectedCount => links.Values.Count(l => l.Peer.Connected);
    public IReadOnlyList<string> ConnectedPeerIds => links.Where(p => !p.Value.Failed && p.Value.Peer.Connected).Select(p => p.Key).ToArray();
    public event Action<WireMessage>? MessageReceived;
    public event Action<string>? Error;
    public event Action? Changed;
    public RtcMesh(FirebaseBackend cloud) { this.cloud = cloud; uid = cloud.Profile?.Id ?? throw new InvalidOperationException("Firebase login required."); }
    public async Task EnterAsync(string communityId, string channelId, bool mute, bool deafen)
    {
        await lifecycle.WaitAsync();
        try
        {
            await LeaveCoreAsync();
            community = communityId; channel = channelId; session = Guid.NewGuid().ToString("N"); muted = mute; deafened = deafen;
            try
            {
                var seats = await cloud.GetAsync<Dictionary<string, RtcSeat>>(RoomPath) ?? new();
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (seats.Count(p => p.Key != uid && now - p.Value.SeenAt < 45000) >= Channel.Capacity)
                    throw new InvalidOperationException("Канал заполнен: максимум 5 человек.");
                var changes = seats.Where(p => now - p.Value.SeenAt > 45000).ToDictionary(p => p.Key, p => (object?)null);
                changes[uid] = new RtcSeat(session, now, muted, deafened);
                // RTDB validates the final room size atomically, including simultaneous arrivals.
                try { await cloud.PatchAsync(RoomPath, changes); }
                catch (InvalidOperationException)
                {
                    var current = await cloud.GetAsync<Dictionary<string, RtcSeat>>(RoomPath) ?? new();
                    if (current.Count(p => p.Key != uid && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - p.Value.SeenAt < 45000) >= Channel.Capacity)
                        throw new InvalidOperationException("Канал заполнен: максимум 5 человек.");
                    throw;
                }
                heartbeat = DateTimeOffset.UtcNow; stop = new();
                var token = stop.Token; loop = Task.Run(() => PollAsync(token)); Changed?.Invoke();
            }
            catch { session = null; community = null; channel = null; throw; }
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
                    var active = seats.Where(p => now - p.Value.SeenAt < 45000 && p.Value.SeenAt <= now + 10000).ToDictionary(p => p.Key, p => p.Value);
                    if (!active.TryGetValue(uid, out var self) || self.SessionId != session) throw new InvalidOperationException("Место в голосовом канале потеряно. Войди снова.");
                    foreach (var id in links.Keys)
                        if (!active.TryGetValue(id, out var seat) || seat.SessionId != links[id].Session)
                            if (links.TryRemove(id, out var old)) old.Peer.Dispose();
                    foreach (var entry in active.Where(p => p.Key != uid).Take(4))
                    {
                        if (!links.TryGetValue(entry.Key, out var link))
                        {
                            var remote = entry.Key; var localChannel = channel!;
                            var peer = new RtcPeer(remote, localChannel);
                            link = new Link(peer, entry.Value.SessionId, string.CompareOrdinal(uid, remote) < 0);
                            links[remote] = link;
                            peer.StateChanged += state =>
                            {
                                if (cancellation.IsCancellationRequested) return;
                                Changed?.Invoke();
                                if (state is "failed" or "invalid-packet") Fail(remote, link, "Прямое WebRTC-соединение не установлено. В этой сети STUN без TURN может быть недостаточно. Выйди из канала и войди снова для повтора.");
                            };
                            peer.MessageReceived += message =>
                            {
                                if (!cancellation.IsCancellationRequested && links.TryGetValue(remote, out var current) && ReferenceEquals(current.Peer, peer)) MessageReceived?.Invoke(message);
                            };
                        }
                        if (!link.Failed && !link.Peer.Connected && DateTimeOffset.UtcNow - link.Created > TimeSpan.FromSeconds(35))
                            Fail(entry.Key, link, "Не удалось соединиться с участником за 35 секунд. TURN отключён; попробуй другую сеть или повторный вход.");
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
                        foreach (var l in links.Values) l.Peer.Dispose(); links.Clear(); session = null; Changed?.Invoke(); break;
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
        await cloud.PutAsync($"{RoomPath}/{uid}", new RtcSeat(session, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), muted, deafened)); heartbeat = DateTimeOffset.UtcNow;
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
        var pending = links.Values.Select(l => l.Negotiation).Where(t => t is not null).Cast<Task>().ToArray();
        if (pending.Length != 0) await Task.WhenAll(pending);
        links.Clear(); loop = null; stop?.Dispose(); stop = null;
        if (community is not null && channel is not null)
        {
            try { await cloud.DeleteAsync($"{RoomPath}/{uid}"); await cloud.DeleteAsync($"rtcSignals/{community}/{channel}/{uid}"); }
            catch (Exception ex) { Error?.Invoke("Выход сохранится после истечения 45 секунд: " + ex.Message); }
        }
        session = null; community = null; channel = null; Changed?.Invoke();
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
