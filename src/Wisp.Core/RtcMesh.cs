using System.Collections.Concurrent;
using System.Net.NetworkInformation;
using System.Text.Json;
using SIPSorcery.Net;

namespace Wisp.Core;

public sealed record RtcSeat(string UserId, string SessionId, long SeenAt, bool Muted = false, bool Deafened = false);
public sealed record RtcSignal(string FromSession, string ToSession, string Type, string Sdp, string Attempt = "", long CreatedAt = 0);
public sealed record RtcCandidateMeta(string FromSession, string ToSession, string Attempt, long CreatedAt);
public sealed record RtcCandidate(string Candidate, string SdpMid, int SdpMLineIndex, long CreatedAt);
public sealed record RtcWake(string FromSession, string ToSession, string Attempt, string Kind, string Version, long CreatedAt);

/// <summary>Five leased seats, deterministic offer roles, one room SSE stream and bounded recovery.</summary>
public sealed class RtcMesh : IAsyncDisposable
{
    private static readonly string[] SlotNames = ["s0", "s1", "s2", "s3", "s4"];
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30)];
    private readonly FirebaseBackend cloud;
    private readonly IIceServerProvider iceServers;
    private readonly RtcPeerFactory peerFactory;
    private readonly TimeProvider clock;
    private readonly string uid;
    private readonly bool monitorNetwork;
    private readonly ConcurrentDictionary<string, Link> links = new();
    private readonly SemaphoreSlim lifecycle = new(1), lease = new(1), wake = new(0, 1);
    private readonly object roomGate = new();
    private FirebaseJsonTree room = new();
    private IReadOnlyList<RTCIceServer> currentIceServers = Array.Empty<RTCIceServer>();
    private CancellationTokenSource? stop, networkDebounce;
    private Task? pollLoop, eventLoop, renewalLoop, leaseGuard;
    private string? community, channel, session, slot;
    private long renewedAt;
    private volatile bool expired, streamHealthy;
    private bool muted, deafened;
    private string? lastError;

    public bool Joined => session is not null && !expired;
    public string? ChannelId => Joined ? channel : null;
    public IReadOnlyList<RtcSeat> Participants
    {
        get
        {
            if (!Joined) return Array.Empty<RtcSeat>();
            lock (roomGate)
            {
                var snapshot = room.Root is null ? JsonSerializer.SerializeToElement<object?>(null) : JsonSerializer.SerializeToElement(room.Root);
                long now = ServerMilliseconds;
                return ReadSeats(snapshot).Values.Where(seat => now - seat.SeenAt < 45000 && seat.SeenAt <= now + 10000).ToArray();
            }
        }
    }
    public int ConnectedCount => links.Values.Count(link => !link.Failed && link.Peer.Connected);
    public IReadOnlyList<string> ConnectedPeerIds => links.Where(pair => !pair.Value.Failed && pair.Value.Peer.Connected).Select(pair => pair.Key).ToArray();
    public IReadOnlyDictionary<string, string> ConnectionPaths => links.Where(pair => !pair.Value.Failed && pair.Value.Peer.Connected && pair.Value.Peer.SelectedCandidateTypes is not null).ToDictionary(pair => pair.Key, pair => pair.Value.Peer.SelectedCandidateTypes!);
    public event Action<WireMessage>? MessageReceived;
    public event Action<string>? Error;
    public event Action? Changed;

    public RtcMesh(FirebaseBackend cloud, IIceServerProvider? iceServers = null,
        RtcPeerFactory? peerFactory = null, bool monitorNetwork = true)
    {
        this.cloud = cloud;
        this.iceServers = iceServers ?? new StaticIceServerProvider();
        this.peerFactory = peerFactory ?? ((remote, room, servers) => new RtcPeer(remote, room, servers));
        this.monitorNetwork = monitorNetwork;
        clock = cloud.Clock;
        uid = cloud.Profile?.Id ?? throw new InvalidOperationException("Firebase login required.");
    }

    public async Task EnterAsync(string communityId, string channelId, bool mute, bool deafen)
    {
        await lifecycle.WaitAsync();
        try
        {
            await LeaveCoreAsync();
            await cloud.EnsureServerTimeAsync();
            currentIceServers = await iceServers.GetAsync();
            community = communityId;
            channel = channelId;
            session = Guid.NewGuid().ToString("N");
            muted = mute;
            deafened = deafen;
            expired = false;
            streamHealthy = false;
            room = new FirebaseJsonTree();
            try
            {
                slot = await ClaimSeatAsync();
                // No live files or chat data are touched; these are only our stale SDP/candidate records.
                await cloud.DeleteAsync(SignalPrefix(uid));
                await cloud.DeleteAsync(CandidatePrefix(uid));
                await cloud.DeleteAsync($"{RoomPath}/events/{slot}");
                Interlocked.Exchange(ref renewedAt, clock.GetTimestamp());
                stop = new CancellationTokenSource();
                var token = stop.Token;
                pollLoop = Task.Run(() => PollAsync(token));
                eventLoop = Task.Run(() => EventsAsync(token));
                renewalLoop = Task.Run(() => RenewLoopAsync(token));
                leaseGuard = Task.Run(() => GuardAsync(token));
                if (monitorNetwork) NetworkChange.NetworkAddressChanged += NetworkChanged;
                Changed?.Invoke();
            }
            catch
            {
                if (slot is not null) await ReleaseOwnSeatAsync();
                session = null;
                community = channel = slot = null;
                throw;
            }
        }
        finally { lifecycle.Release(); }
    }

    private string RoomPath => $"communities/{community}/rtcRooms/{channel}";
    private string SignalPrefix(string from) => $"rtcSignals/{community}/{channel}/{from}";
    private string SignalPath(string from, string to) => $"{SignalPrefix(from)}/{to}";
    private string CandidatePrefix(string from) => $"rtcCandidates/{community}/{channel}/{from}";
    private string CandidatePath(string from, string to) => $"{CandidatePrefix(from)}/{to}";
    private long ServerMilliseconds => cloud.ServerNow().ToUnixTimeMilliseconds();
    private TimeSpan Since(long timestamp) => clock.GetElapsedTime(timestamp, clock.GetTimestamp());
    private Task Delay(TimeSpan duration, CancellationToken token) => Task.Delay(duration, clock, token);

    private async Task<string> ClaimSeatAsync()
    {
        for (int conflict = 0; conflict < 6; conflict++)
        {
            var data = await cloud.GetAsync<JsonElement>(RoomPath);
            var seats = ReadSeats(data);
            long now = ServerMilliseconds;
            string? candidate = SlotNames.FirstOrDefault(key => seats.TryGetValue(key, out var own) && own.UserId == uid)
                ?? SlotNames.FirstOrDefault(key => !seats.TryGetValue(key, out var old) || now - old.SeenAt > 45000);
            if (candidate is null) throw new InvalidOperationException("Канал заполнен: максимум 5 человек.");
            var current = await cloud.GetVersionedAsync<RtcSeat>($"{RoomPath}/{candidate}");
            if (current.Value is { } seat && seat.UserId != uid && now - seat.SeenAt <= 45000)
                continue;
            if (await cloud.TryPutVersionedAsync($"{RoomPath}/{candidate}", new RtcSeat(uid, session!, ServerMilliseconds, muted, deafened), current.ETag))
                return candidate;
            // Only HTTP 412 is a retryable slot race. Permission/auth/network failures propagate immediately.
            await Delay(TimeSpan.FromMilliseconds(100), CancellationToken.None);
        }
        throw new InvalidOperationException("Место одновременно занял другой участник. Попробуй войти ещё раз.");
    }

    private async Task EventsAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await foreach (var update in cloud.WatchAsync(RoomPath, token))
                {
                    lock (roomGate) room.Apply(update);
                    streamHealthy = true;
                    Wake();
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex) { Report("Поток комнаты недоступен; использую временный опрос. " + SafeMessage(ex)); }
            finally { streamHealthy = false; Wake(); }
            try { await Delay(TimeSpan.FromSeconds(2), token); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task PollAsync(CancellationToken token)
    {
        long? lastFallbackPoll = null;
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (!streamHealthy && (lastFallbackPoll is null || Since(lastFallbackPoll.Value) >= TimeSpan.FromSeconds(1)))
                    {
                        lastFallbackPoll = clock.GetTimestamp();
                        var snapshot = await cloud.GetAsync<JsonElement>(RoomPath, token);
                        lock (roomGate)
                            room.Apply(new FirebaseStreamEvent("put", "/", snapshot.ValueKind == JsonValueKind.Undefined ? JsonSerializer.SerializeToElement<object?>(null) : snapshot));
                    }
                    JsonElement current;
                    lock (roomGate) current = room.Root is null ? JsonSerializer.SerializeToElement<object?>(null) : JsonSerializer.SerializeToElement(room.Root);
                    var seats = ReadSeats(current);
                    long now = ServerMilliseconds;
                    var active = seats.Where(pair => now - pair.Value.SeenAt < 45000 && pair.Value.SeenAt <= now + 10000)
                        .ToDictionary(pair => pair.Key, pair => pair.Value);
                    if (!active.Values.Any(seat => seat.UserId == uid && seat.SessionId == session))
                    {
                        Expire("Место в голосовом канале потеряно. Голос остановлен.");
                        break;
                    }
                    foreach (var remote in links.Keys)
                    {
                        if (!active.Values.Any(seat => seat.UserId == remote && seat.SessionId == links[remote].Session))
                            RemoveLink(remote);
                    }
                    foreach (var pair in active.Where(pair => pair.Value.UserId != uid).Take(4))
                    {
                        var remote = pair.Value.UserId;
                        if (!links.TryGetValue(remote, out var link))
                        {
                            link = CreateLink(remote, pair.Value.SessionId, pair.Key, 0);
                            links[remote] = link;
                        }
                        ProcessWake(current, remote, link);
                        if (link.Failed)
                        {
                            if (!link.Exhausted && Since(link.FailedAt) >= RetryDelays[link.Retries] && (link.Negotiation is null || link.Negotiation.IsCompleted))
                            {
                                link.Peer.Dispose();
                                var retry = CreateLink(remote, link.Session, pair.Key, link.Retries + 1);
                                links[remote] = retry;
                                if (!retry.Offerer) { retry.RequestRetry = ValidAttempt(link.Attempt); retry.Attempt = link.Attempt; }
                                link = retry;
                            }
                            else continue;
                        }
                        if (link.RestartRequested && (link.Negotiation is null || link.Negotiation.IsCompleted))
                        {
                            link.RestartRequested = false;
                            Restart(link);
                        }
                        if (link.Peer.Connected)
                        {
                            link.Retries = 0;
                            link.DisconnectedAt = null;
                            link.RestartedAt = null;
                        }
                        else if (link.RestartedAt is { } restarted && Since(restarted) >= TimeSpan.FromSeconds(5))
                        {
                            Fail(remote, link, "ICE не восстановился; повторяю соединение.");
                            continue;
                        }
                        else if (!link.Peer.Connected && link.DisconnectedAt is { } disconnected && Since(disconnected) >= TimeSpan.FromSeconds(5) && link.RestartedAt is null)
                            Restart(link);
                        else if (!link.Peer.Connected && Since(link.CreatedAt) >= TimeSpan.FromSeconds(20) && link.DisconnectedAt is null)
                        {
                            Fail(remote, link, "Прямая связь не установлена; повторяю попытку.");
                            continue;
                        }
                        if ((link.SignalDirty || !link.Peer.Connected || link.CandidatesDirty || link.RequestRetry) && (link.Negotiation is null || link.Negotiation.IsCompleted))
                            link.Negotiation = NegotiateAsync(remote, link, token);
                    }
                    Changed?.Invoke();
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex) { Report(SafeMessage(ex)); }
                var timeout = streamHealthy && links.Values.All(link => link.Peer.Connected)
                    ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(1);
                await wake.WaitAsync(timeout, token);
            }
        }
        catch (OperationCanceledException) { }
    }

    private Link CreateLink(string remote, string remoteSession, string remoteSlot, int retries)
    {
        var peer = peerFactory(remote, channel!, currentIceServers);
        var link = new Link(peer, remoteSession, remoteSlot, string.CompareOrdinal(uid, remote) < 0, clock.GetTimestamp(), retries);
        BindPeer(remote, link, peer);
        return link;
    }

    private void BindPeer(string remote, Link link, IRtcPeer peer)
    {
        peer.StateChanged += state =>
        {
            if (stop?.IsCancellationRequested != false || !IsCurrent(remote, link) || !ReferenceEquals(link.Peer, peer)) return;
            if (state == "invalid-packet") Fail(remote, link, "Участник отправил некорректные данные; соединение закрыто.", true);
            else if (state == "restart-required") Fail(remote, link, "ICE требует нового соединения; повторяю согласование.");
            else if (state == "failed") Fail(remote, link, "Соединение с участником потеряно; повторяю попытку.");
            else if (state == "disconnected") link.DisconnectedAt ??= clock.GetTimestamp();
            Wake();
            Changed?.Invoke();
        };
        peer.CandidateReady += () => { if (ReferenceEquals(link.Peer, peer)) { link.CandidatesDirty = true; Wake(); } };
        peer.MessageReceived += message => { if (IsCurrent(remote, link) && ReferenceEquals(link.Peer, peer) && Joined) MessageReceived?.Invoke(message); };
    }

    private bool IsCurrent(string remote, Link link) => links.TryGetValue(remote, out var current) && ReferenceEquals(current, link) && !link.Failed;

    private async Task NegotiateAsync(string remote, Link link, CancellationToken token)
    {
        try
        {
            if (!IsCurrent(remote, link)) return;
            if (link.RequestRetry)
            {
                link.RequestRetry = false;
                await PublishWakeAsync(link, "retry", token);
            }
            if (link.Offerer && !link.LocalPublished)
            {
                link.Attempt = Guid.NewGuid().ToString("N");
                await CleanupExpiredPairAsync(remote, token);
                await cloud.DeleteAsync(CandidatePath(uid, remote), token);
                var sdp = await link.Peer.OfferAsync(token);
                if (!IsCurrent(remote, link)) return;
                await cloud.PutAsync(SignalPath(uid, remote), new RtcSignal(session!, link.Session, "offer", sdp, link.Attempt, ServerMilliseconds), token);
                link.LocalPublished = true;
                await PublishWakeAsync(link, "signal", token);
            }
            RtcSignal? signal = null;
            if (link.SignalDirty || !link.RemoteApplied)
            {
                link.SignalDirty = false;
                try { signal = await cloud.GetAsync<RtcSignal>(SignalPath(remote, uid), token); }
                catch (FirebaseRequestException ex) when (ex.Failure == FirebaseFailure.PermissionDenied) { }
            }
            if (signal is not null && signal.FromSession == link.Session && signal.ToSession == session && ValidAttempt(signal.Attempt) && ServerMilliseconds - signal.CreatedAt <= 600000)
            {
                if (link.Offerer && signal.Type == "answer" && signal.Attempt == link.Attempt && !link.RemoteApplied)
                {
                    link.Peer.AcceptAnswer(signal.Sdp);
                    link.RemoteApplied = true;
                }
                else if (!link.Offerer && signal.Type == "offer" && (!link.RemoteApplied || signal.Attempt != link.Attempt))
                {
                    if (link.RemoteApplied)
                    {
                        var previous = link.Peer;
                        link.Peer = peerFactory(remote, channel!, currentIceServers);
                        BindPeer(remote, link, link.Peer);
                        previous.Dispose();
                        link.SentCandidates.Clear();
                        link.ReceivedCandidates.Clear();
                        link.CandidateMetaPublished = false;
                        link.CandidatesDirty = true;
                    }
                    link.Attempt = signal.Attempt;
                    await cloud.DeleteAsync(CandidatePath(uid, remote), token);
                    var sdp = await link.Peer.AnswerAsync(signal.Sdp, token);
                    if (!IsCurrent(remote, link)) return;
                    await cloud.PutAsync(SignalPath(uid, remote), new RtcSignal(session!, link.Session, "answer", sdp, link.Attempt, ServerMilliseconds), token);
                    link.LocalPublished = link.RemoteApplied = true;
                    await PublishWakeAsync(link, "signal", token);
                }
            }
            if (link.LocalPublished && IsCurrent(remote, link))
            {
                await FlushCandidatesAsync(remote, link, token);
                await ReadCandidatesAsync(remote, link, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (IsCurrent(remote, link)) Fail(remote, link, "WebRTC: " + SafeMessage(ex)); }
    }

    private async Task CleanupExpiredPairAsync(string remote, CancellationToken token)
    {
        // Conditional cleanup is limited to the pair we participate in. Permission denials mean it is still fresh.
        foreach (var path in new[] { SignalPath(remote, uid), CandidatePath(remote, uid) })
        {
            try
            {
                await cloud.DeleteAsync(path, token);
            }
            catch (FirebaseRequestException ex) when (ex.Failure == FirebaseFailure.PermissionDenied) { }
        }
    }

    private async Task FlushCandidatesAsync(string remote, Link link, CancellationToken token)
    {
        if (!link.CandidatesDirty && link.CandidateMetaPublished) return;
        link.CandidatesDirty = false;
        var path = CandidatePath(uid, remote);
        await cloud.PutAsync($"{path}/Meta", new RtcCandidateMeta(session!, link.Session, link.Attempt, ServerMilliseconds), token);
        link.CandidateMetaPublished = true;
        foreach (var candidate in link.Peer.LocalCandidates.Take(64))
        {
            if (!link.SentCandidates.Add(candidate.Candidate)) continue;
            int index = link.SentCandidates.Count - 1;
            await cloud.PutAsync($"{path}/c{index:D2}", new RtcCandidate(candidate.Candidate, candidate.SdpMid, candidate.SdpMLineIndex, ServerMilliseconds), token);
        }
        await PublishWakeAsync(link, "candidates", token);
    }

    private async Task ReadCandidatesAsync(string remote, Link link, CancellationToken token)
    {
        JsonElement data;
        try { data = await cloud.GetAsync<JsonElement>(CandidatePath(remote, uid), token); }
        catch (FirebaseRequestException ex) when (ex.Failure == FirebaseFailure.PermissionDenied) { return; }
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("Meta", out var metaJson)) return;
        var meta = metaJson.Deserialize<RtcCandidateMeta>();
        if (meta is null || meta.FromSession != link.Session || meta.ToSession != session || meta.Attempt != link.Attempt || ServerMilliseconds - meta.CreatedAt > 600000) return;
        foreach (var item in data.EnumerateObject().Where(item => item.Name.Length == 3 && item.Name[0] == 'c').Take(64))
        {
            var candidate = item.Value.Deserialize<RtcCandidate>();
            if (candidate is null || candidate.Candidate.Length > 2048 || !link.ReceivedCandidates.Add(candidate.Candidate)) continue;
            link.Peer.AddIceCandidate(new IceCandidateData(candidate.Candidate, candidate.SdpMid, candidate.SdpMLineIndex));
        }
    }

    private async Task PublishWakeAsync(Link link, string kind, CancellationToken token)
    {
        if (slot is null) return;
        await cloud.PutAsync($"{RoomPath}/events/{slot}/{link.Slot}", new RtcWake(session!, link.Session, link.Attempt, kind, Guid.NewGuid().ToString("N"), ServerMilliseconds), token);
    }

    private void ProcessWake(JsonElement current, string remote, Link link)
    {
        if (slot is null || current.ValueKind != JsonValueKind.Object || !current.TryGetProperty("events", out var events) ||
            !events.TryGetProperty(link.Slot, out var from) || !from.TryGetProperty(slot, out var data)) return;
        var update = data.Deserialize<RtcWake>();
        if (update is null || update.FromSession != link.Session || update.ToSession != session || update.Version == link.LastWake || ServerMilliseconds - update.CreatedAt > 600000) return;
        link.LastWake = update.Version;
        if (update.Kind == "retry" && link.Offerer && update.Attempt == link.Attempt)
            Fail(remote, link, "Участник запросил восстановление прямой связи.");
        else link.SignalDirty = true;
    }

    private void Fail(string remote, Link link, string message, bool fatal = false)
    {
        if (link.Failed) return;
        link.Failed = true;
        link.Peer.Dispose();
        link.FailedAt = clock.GetTimestamp();
        link.Exhausted = fatal || link.Retries >= RetryDelays.Length;
        Report(link.Exhausted ? "Не удалось восстановить участника после повторных попыток. Проверь сеть и доступ к сигналингу." : message);
        Wake();
        Changed?.Invoke();
    }

    private void Restart(Link link)
    {
        if (link.Failed || !link.RemoteApplied) return;
        link.RestartedAt = clock.GetTimestamp();
        link.CandidatesDirty = true;
        link.SentCandidates.Clear();
        link.ReceivedCandidates.Clear();
        link.Peer.RestartIce();
    }

    private void NetworkChanged(object? sender, EventArgs args)
    {
        var next = new CancellationTokenSource();
        var old = Interlocked.Exchange(ref networkDebounce, next);
        try { old?.Cancel(); } catch (ObjectDisposedException) { }
        _ = Task.Run(async () =>
        {
            try
            {
                await Delay(TimeSpan.FromMilliseconds(1500), next.Token);
                if (!Joined || stop?.IsCancellationRequested != false) return;
                currentIceServers = await iceServers.GetAsync(next.Token);
                foreach (var link in links.Values) link.RestartRequested = true;
                Wake();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Report("Смена сети: " + SafeMessage(ex)); }
            finally { Interlocked.CompareExchange(ref networkDebounce, null, next); next.Dispose(); }
        });
    }

    private async Task RenewLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Delay(TimeSpan.FromSeconds(10), token);
                try { await RenewAsync(token); }
                catch (FirebaseRequestException ex) when (ex.Failure == FirebaseFailure.PermissionDenied) { Expire("Место в канале потеряно или доступ отозван."); break; }
                catch (Exception ex) when (ex is not OperationCanceledException) { Report("Не удалось продлить место: " + SafeMessage(ex)); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task RenewAsync(CancellationToken token)
    {
        await lease.WaitAsync(token);
        try
        {
            if (!Joined || slot is null) return;
            var path = $"{RoomPath}/{slot}";
            var current = await cloud.GetVersionedAsync<RtcSeat>(path, token);
            if (current.Value?.UserId != uid || current.Value.SessionId != session)
            {
                Expire("Место в голосовом канале занято другой сессией.");
                return;
            }
            if (!await cloud.TryPutVersionedAsync(path, new RtcSeat(uid, session!, ServerMilliseconds, muted, deafened), current.ETag, token))
                throw new InvalidOperationException("Одновременное изменение места; продление будет повторено.");
            Interlocked.Exchange(ref renewedAt, clock.GetTimestamp());
        }
        finally { lease.Release(); }
    }

    private async Task GuardAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (Since(Interlocked.Read(ref renewedAt)) >= TimeSpan.FromSeconds(40))
                {
                    Expire("Аренда места не продлена: голос остановлен до истечения серверных 45 секунд.");
                    break;
                }
                await Delay(TimeSpan.FromSeconds(1), token);
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Expire(string message)
    {
        if (expired) return;
        expired = true;
        foreach (var link in links.Values) link.Peer.Dispose();
        stop?.Cancel();
        Report(message);
        Changed?.Invoke();
    }

    public async Task SetMutedAsync(bool mute, bool deafen)
    {
        muted = mute;
        deafened = deafen;
        if (Joined && stop is not null) await RenewAsync(stop.Token);
    }
    public Task SendAsync(WireMessage message) => Task.WhenAll(links.Values.Where(link => !link.Failed && link.Peer.Connected).Select(link => link.Peer.SendAsync(message)));
    public async Task LeaveAsync()
    {
        await lifecycle.WaitAsync();
        try { await LeaveCoreAsync(); }
        finally { lifecycle.Release(); }
    }

    private async Task<bool> ReleaseOwnSeatAsync()
    {
        if (slot is null) return false;
        var path = $"{RoomPath}/{slot}";
        var current = await cloud.GetVersionedAsync<RtcSeat>(path);
        if (current.Value?.UserId != uid || current.Value.SessionId != session) return false;
        return await cloud.TryPutVersionedAsync<object?>(path, null, current.ETag);
    }

    private async Task LeaveCoreAsync()
    {
        if (monitorNetwork) NetworkChange.NetworkAddressChanged -= NetworkChanged;
        var pendingNetwork = Interlocked.Exchange(ref networkDebounce, null);
        try { pendingNetwork?.Cancel(); } catch (ObjectDisposedException) { }
        stop?.Cancel();
        foreach (var link in links.Values) link.Peer.Dispose();
        var tasks = new[] { pollLoop, eventLoop, renewalLoop, leaseGuard }.Where(task => task is not null).Cast<Task>()
            .Concat(links.Values.Select(link => link.Negotiation).Where(task => task is not null).Cast<Task>()).ToArray();
        try { await Task.WhenAll(tasks); } catch (OperationCanceledException) { }
        if (session is not null && community is not null && channel is not null)
        {
            try
            {
                // Compare-and-delete each current pair BEFORE releasing the slot. An older window must
                // never erase records written by a newly joined session of the same account.
                foreach (var remote in links.Keys)
                    foreach (var path in new[] { SignalPath(uid, remote), CandidatePath(uid, remote) })
                    {
                        try
                        {
                            var version = await cloud.GetVersionedAsync<JsonElement>(path);
                            var data = version.Value;
                            if (data.ValueKind != JsonValueKind.Object) continue;
                            if (data.TryGetProperty("Meta", out var meta)) data = meta;
                            if (data.TryGetProperty("FromSession", out var from) && from.GetString() == session)
                                await cloud.TryPutVersionedAsync<object?>(path, null, version.ETag);
                        }
                        catch (FirebaseRequestException ex) when (ex.Failure == FirebaseFailure.PermissionDenied) { }
                    }
                await ReleaseOwnSeatAsync();
            }
            catch (Exception ex) { Report("Очистка сессии завершится после истечения аренды: " + SafeMessage(ex)); }
        }
        links.Clear();
        stop?.Dispose();
        stop = null;
        pollLoop = eventLoop = renewalLoop = leaseGuard = null;
        session = community = channel = slot = null;
        streamHealthy = false;
        lastError = null;
        Changed?.Invoke();
    }

    private void RemoveLink(string remote)
    {
        if (links.TryRemove(remote, out var link)) link.Peer.Dispose();
    }
    private void Wake() { try { if (wake.CurrentCount == 0) wake.Release(); } catch (SemaphoreFullException) { } }
    private void Report(string message) { if (message == lastError) return; lastError = message; Error?.Invoke(message); }
    private static string SafeMessage(Exception ex) => ex is HttpRequestException ? "Сеть Firebase недоступна." : ex.Message;
    private static bool ValidAttempt(string? value) => value?.Length == 32 && value.All(character => char.IsAsciiHexDigit(character));
    private static Dictionary<string, RtcSeat> ReadSeats(JsonElement data)
    {
        var result = new Dictionary<string, RtcSeat>();
        if (data.ValueKind != JsonValueKind.Object) return result;
        foreach (var key in SlotNames)
            if (data.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Object && value.Deserialize<RtcSeat>() is { UserId.Length: > 0, SessionId.Length: 32 } seat)
                result[key] = seat;
        return result;
    }
    public async ValueTask DisposeAsync() => await LeaveAsync();

    private sealed class Link(IRtcPeer peer, string session, string slot, bool offerer, long createdAt, int retries)
    {
        public IRtcPeer Peer { get; set; } = peer;
        public string Session { get; } = session;
        public string Slot { get; } = slot;
        public bool Offerer { get; } = offerer;
        public long CreatedAt { get; } = createdAt;
        public string Attempt = "", LastWake = "";
        public bool LocalPublished, RemoteApplied, Failed, Exhausted;
        public volatile bool SignalDirty = true, CandidatesDirty = true;
        public bool CandidateMetaPublished, RequestRetry;
        public volatile bool RestartRequested;
        public int Retries = retries;
        public long FailedAt;
        public long? DisconnectedAt, RestartedAt;
        public Task? Negotiation;
        public HashSet<string> SentCandidates { get; } = new(StringComparer.Ordinal);
        public HashSet<string> ReceivedCandidates { get; } = new(StringComparer.Ordinal);
    }
}
