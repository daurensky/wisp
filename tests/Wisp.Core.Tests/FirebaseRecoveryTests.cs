using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Wisp.Core;
using Xunit;

namespace Wisp.Core.Tests;

public sealed class FirebaseRecoveryTests
{
    [Fact]
    public async Task ServerClockIgnoresClientSkewAndUsesMonotonicTimeForPresence()
    {
        var clock = new ManualClock();
        var server = new FakeDatabase(clock);
        using var cloud = await Login(server, clock);
        Assert.True(cloud.HasServerTime);
        Assert.InRange(Math.Abs((cloud.ServerNow() - server.Now).TotalSeconds), 0, 1);
        clock.JumpWallClock(TimeSpan.FromDays(-30));
        clock.Advance(TimeSpan.FromSeconds(20));
        await cloud.PresenceAsync("room", cloud.Profile!, null);
        Assert.InRange(Math.Abs(server.Read("communities/room/presence/alice/SeenAt")!.GetValue<long>() - server.Now.ToUnixTimeMilliseconds()), 0, 1000);
    }

    [Fact]
    public async Task OnlyPreconditionFailureRetriesSeatClaimAndCleanupDoesNotTouchFiles()
    {
        var clock = new ManualClock();
        var server = new FakeDatabase(clock) { ConflictPuts = 1 };
        using var cloud = await Login(server, clock);
        await using var mesh = new RtcMesh(cloud, new StaticIceServerProvider([]), monitorNetwork: false);
        var enter = mesh.EnterAsync("room", "voice", false, false);
        await Eventually(() => server.SeatWrites == 1);
        clock.Advance(TimeSpan.FromMilliseconds(101));
        await enter.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(mesh.Joined);
        Assert.Equal(2, server.SeatWrites);
        Assert.Contains("rtcSignals/room/voice/alice", server.Deletes);
        Assert.Contains("rtcCandidates/room/voice/alice", server.Deletes);
        await mesh.LeaveAsync();
        Assert.Null(server.Read("communities/room/rtcRooms/voice/s0"));
        Assert.DoesNotContain(server.Deletes, path => path.Contains("Files") || path.Contains("files") || path.Contains("messages"));
    }

    [Fact]
    public async Task PermissionDeniedIsTypedAndNeverRetriedAsASeatConflict()
    {
        var clock = new ManualClock();
        var server = new FakeDatabase(clock) { DenySeat = true };
        using var cloud = await Login(server, clock);
        await using var mesh = new RtcMesh(cloud, new StaticIceServerProvider([]), monitorNetwork: false);
        var error = await Assert.ThrowsAsync<FirebaseRequestException>(() => mesh.EnterAsync("room", "voice", false, false));
        Assert.Equal(FirebaseFailure.PermissionDenied, error.Failure);
        Assert.Equal(1, server.SeatWrites);
        Assert.False(mesh.Joined);
    }

    [Fact]
    public async Task GuardStopsTransportAtFortySecondsEvenWhenRenewalIsUnreachable()
    {
        var clock = new ManualClock();
        var server = new FakeDatabase(clock);
        using var cloud = await Login(server, clock);
        var peers = new List<FakePeer>();
        server.SeedBob();
        await using var mesh = new RtcMesh(cloud, new StaticIceServerProvider([]), (_, _, _) => { var p = new FakePeer(); peers.Add(p); return p; }, false);
        await mesh.EnterAsync("room", "voice", false, false);
        await Eventually(() => peers.Count == 1 && server.SseOpened == 1);
        server.HangSeatReads = true;
        // Let the independent watchdog observe 40s before any late network renewal can succeed.
        clock.Advance(TimeSpan.FromSeconds(40));
        await Eventually(() => !mesh.Joined);
        Assert.True(peers[0].Disposed);
        Assert.True(server.MaxSse <= 1);
        server.HangSeatReads = false;
    }

    [Fact]
    public async Task LostSlotAndLeavingAnOldWindowNeverDeleteTheNewSessionsSeatOrSignals()
    {
        var clock = new ManualClock();
        var server = new FakeDatabase(clock);
        using var cloud = await Login(server, clock);
        await using var mesh = new RtcMesh(cloud, new StaticIceServerProvider([]), monitorNetwork: false);
        await mesh.EnterAsync("room", "voice", false, false);
        await Eventually(() => server.SseOpened == 1);
        server.Write("communities/room/rtcRooms/voice/s0", JsonSerializer.SerializeToNode(new RtcSeat("alice", new string('f', 32), server.Now.ToUnixTimeMilliseconds())));
        server.Write("rtcSignals/room/voice/alice/bob", JsonValue.Create("new-session-signal"));
        await Eventually(() => !mesh.Joined);
        await mesh.LeaveAsync();
        Assert.Equal(new string('f', 32), server.Read("communities/room/rtcRooms/voice/s0/SessionId")!.GetValue<string>());
        Assert.Equal("new-session-signal", server.Read("rtcSignals/room/voice/alice/bob")!.GetValue<string>());
    }

    [Fact]
    public async Task FailedPairGetsANewNativePeerAndAttemptAfterBackoffWithoutLeavingRoom()
    {
        var clock = new ManualClock();
        var server = new FakeDatabase(clock);
        server.SeedBob();
        using var cloud = await Login(server, clock);
        var peers = new List<FakePeer>();
        await using var mesh = new RtcMesh(cloud, new StaticIceServerProvider([]), (_, _, _) => { var p = new FakePeer(); peers.Add(p); return p; }, false);
        await mesh.EnterAsync("room", "voice", false, false);
        await Eventually(() => server.Read("rtcSignals/room/voice/alice/bob/Attempt") is not null);
        var first = server.Read("rtcSignals/room/voice/alice/bob/Attempt")!.GetValue<string>();
        peers[0].Fail();
        clock.Advance(TimeSpan.FromMilliseconds(1999));
        await Task.Delay(50);
        Assert.Single(peers);
        clock.Advance(TimeSpan.FromMilliseconds(2));
        // Wake the event loop rather than waiting for a real-time poll timeout.
        server.Broadcast();
        await Eventually(() => peers.Count == 2 && server.Read("rtcSignals/room/voice/alice/bob/Attempt")?.GetValue<string>() != first);
        Assert.True(mesh.Joined);
        Assert.True(peers[0].Disposed);
        Assert.Equal(32, server.Read("rtcSignals/room/voice/alice/bob/Attempt")!.GetValue<string>().Length);
    }

    [Fact]
    public async Task RoomParticipantsExposeLiveMuteAndDeafenWithoutChangingPresenceSchema()
    {
        var clock = new ManualClock(); var server = new FakeDatabase(clock); server.SeedBob();
        using var cloud = await Login(server, clock);
        await using var mesh = new RtcMesh(cloud, new StaticIceServerProvider([]), (_, _, _) => new FakePeer(), false);
        await mesh.EnterAsync("room", "voice", false, false);
        await Eventually(() => mesh.Participants.Any(seat => seat.UserId == "bob"));
        server.Write("communities/room/rtcRooms/voice/s1", JsonSerializer.SerializeToNode(new RtcSeat("bob", new string('b', 32), server.Now.ToUnixTimeMilliseconds(), true, true)));
        await Eventually(() => mesh.Participants.Any(seat => seat.UserId == "bob" && seat.Muted && seat.Deafened));
        await mesh.SetMutedAsync(true, true);
        await Eventually(() => mesh.Participants.Any(seat => seat.UserId == "alice" && seat.Muted && seat.Deafened));
        Assert.Equal("voice", mesh.ChannelId);
    }

    [Fact]
    public async Task RetryBudgetUsesAllFourDelaysThenStopsCreatingPeers()
    {
        var clock = new ManualClock();
        var server = new FakeDatabase(clock); server.SeedBob();
        using var cloud = await Login(server, clock);
        var peers = new List<FakePeer>(); var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await using var mesh = new RtcMesh(cloud, new StaticIceServerProvider([]), (_, _, _) => { var p = new FakePeer(); peers.Add(p); return p; }, false);
        mesh.Error += errors.Enqueue;
        await mesh.EnterAsync("room", "voice", false, false);
        await Eventually(() => peers.Count == 1 && server.Read("rtcSignals/room/voice/alice/bob/Attempt") is not null);
        var attempts = new HashSet<string>();
        foreach (int seconds in new[] { 2, 5, 15, 30 })
        {
            int count = peers.Count;
            attempts.Add(server.Read("rtcSignals/room/voice/alice/bob/Attempt")!.GetValue<string>());
            peers[^1].Fail();
            clock.Advance(TimeSpan.FromSeconds(seconds)); server.SeedBob();
            await Eventually(() => peers.Count == count + 1 && server.Read("rtcSignals/room/voice/alice/bob/Attempt") is { } value && !attempts.Contains(value.GetValue<string>()));
        }
        Assert.Equal(5, peers.Count);
        peers[^1].Fail();
        clock.Advance(TimeSpan.FromSeconds(31)); server.SeedBob();
        await Task.Delay(100);
        Assert.Equal(5, peers.Count);
        Assert.Contains(errors, error => error.Contains("после повторных попыток"));
    }

    [Fact]
    public async Task SseParsesPutPatchKeepAliveCancelAndRefreshesRevokedTokens()
    {
        var clock = new ManualClock();
        var server = new FakeDatabase(clock);
        using var cloud = await Login(server, clock);
        server.StreamScripts.Enqueue("event: keep-alive\ndata: null\n\nevent: put\ndata: {\"path\":\"/\",\"data\":{\"s0\":1}}\n\nevent: auth_revoked\ndata: \"expired\"\n\n");
        server.StreamScripts.Enqueue("event: patch\ndata: {\"path\":\"/\",\"data\":{\"s0\":null,\"s1/UserId\":\"bob\"}}\n\nevent: cancel\ndata: \"permission denied\"\n\n");
        var tree = new FirebaseJsonTree();
        var ex = await Assert.ThrowsAsync<FirebaseRequestException>(async () =>
        {
            await foreach (var change in cloud.WatchAsync("communities/room/rtcRooms/voice")) tree.Apply(change);
        });
        Assert.Equal(FirebaseFailure.StreamCancelled, ex.Failure);
        Assert.Null(tree.Root!["s0"]);
        Assert.Equal("bob", tree.Root["s1"]!["UserId"]!.GetValue<string>());
        Assert.Equal(1, server.TokenRefreshes);
        Assert.True(server.MaxSse <= 1);
    }

    private static async Task<FirebaseBackend> Login(FakeDatabase server, ManualClock clock)
    {
        var cloud = new FirebaseBackend(new("public-api-key", "test-project", "https://test-project.firebaseio.com"), server, clock);
        await cloud.SignInAsync("alice@example.com", "long-password", false, "Alice");
        return cloud;
    }

    private static async Task Eventually(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class FakePeer : IRtcPeer
    {
        public bool Connected => false;
        public string? SelectedCandidateTypes => null;
        public IReadOnlyList<IceCandidateData> LocalCandidates => [];
        public bool Disposed;
        public event Action<string>? StateChanged;
        public event Action<WireMessage>? MessageReceived { add { } remove { } }
        public event Action? CandidateReady { add { } remove { } }
        public Task<string> OfferAsync(CancellationToken cancellation = default) => Task.FromResult("fake-offer");
        public Task<string> AnswerAsync(string offer, CancellationToken cancellation = default) => Task.FromResult("fake-answer");
        public void AcceptAnswer(string answer) { }
        public void AddIceCandidate(IceCandidateData candidate) { }
        public void RestartIce() { }
        public Task SendAsync(WireMessage message) => Task.CompletedTask;
        public void Dispose() => Disposed = true;
        public void Fail() => StateChanged?.Invoke("failed");
    }

    private sealed class ManualClock : TimeProvider
    {
        private long ticks;
        private DateTimeOffset wall = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
        private readonly List<ClockTimer> timers = [];
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref ticks);
        public override DateTimeOffset GetUtcNow() => wall.AddTicks(GetTimestamp());
        public void JumpWallClock(TimeSpan offset) => wall += offset;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ClockTimer(this, callback, state);
            lock (timers) timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }
        public void Advance(TimeSpan duration)
        {
            Interlocked.Add(ref ticks, duration.Ticks);
            ClockTimer[] snapshot;
            lock (timers) snapshot = timers.ToArray();
            foreach (var timer in snapshot) timer.Tick();
        }
        private sealed class ClockTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            private long due = long.MaxValue, periodTicks;
            private bool disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock.GetTimestamp() + dueTime.Ticks;
                periodTicks = period == Timeout.InfiniteTimeSpan ? 0 : period.Ticks;
                return !disposed;
            }
            public void Tick()
            {
                if (disposed || clock.GetTimestamp() < due) return;
                due = periodTicks > 0 ? clock.GetTimestamp() + periodTicks : long.MaxValue;
                callback(state);
            }
            public void Dispose() => disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class FakeDatabase(ManualClock clock) : HttpMessageHandler
    {
        private readonly object gate = new();
        private JsonNode root = new JsonObject();
        private readonly List<EventStream> streams = [];
        private int revision;
        public int SeatWrites, ConflictPuts, SseOpened, MaxSse, TokenRefreshes;
        public bool DenySeat, HangSeatReads;
        public List<string> Deletes { get; } = [];
        public Queue<string> StreamScripts { get; } = [];
        public DateTimeOffset Now => new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero).AddTicks(clock.GetTimestamp());
        public JsonNode? Read(string path)
        {
            lock (gate)
            {
                JsonNode? value = root;
                foreach (var key in path.Split('/')) value = value?[key];
                return value?.DeepClone();
            }
        }
        public void Write(string path, JsonNode? value)
        {
            lock (gate)
            {
                var parts = path.Split('/');
                JsonNode node = root;
                foreach (var key in parts[..^1]) { node[key] ??= new JsonObject(); node = node[key]!; }
                if (value is null) ((JsonObject)node).Remove(parts[^1]); else node[parts[^1]] = value.DeepClone();
                revision++;
                Broadcast();
            }
        }
        public void SeedBob() => Write("communities/room/rtcRooms/voice/s1", JsonSerializer.SerializeToNode(new RtcSeat("bob", new string('b', 32), Now.ToUnixTimeMilliseconds())));
        public void Broadcast()
        {
            lock (gate)
                foreach (var stream in streams.Where(stream => !stream.Closed))
                    stream.Send("event: put\ndata: " + JsonSerializer.Serialize(new { path = "/", data = Read("communities/room/rtcRooms/voice") }) + "\n\n");
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host.Contains("identitytoolkit")) return Response(HttpStatusCode.OK, """{"idToken":"id-value","refreshToken":"refresh-value","localId":"alice"}""");
            if (request.RequestUri.Host.Contains("securetoken"))
            {
                Interlocked.Increment(ref TokenRefreshes);
                return Response(HttpStatusCode.OK, """{"id_token":"new-id","refresh_token":"new-refresh","user_id":"alice"}""");
            }
            string path = request.RequestUri.AbsolutePath.Trim('/');
            path = path[..^5];
            if (request.Headers.Accept.Any(value => value.MediaType == "text/event-stream"))
            {
                SseOpened++;
                if (StreamScripts.TryDequeue(out var script)) return Response(HttpStatusCode.OK, script);
                var stream = new EventStream();
                lock (gate)
                {
                    streams.Add(stream);
                    MaxSse = Math.Max(MaxSse, streams.Count(item => !item.Closed));
                    Broadcast();
                }
                var response = Response(HttpStatusCode.OK, "null");
                response.Content = new StreamContent(stream);
                return response;
            }
            if (request.Method == HttpMethod.Get)
            {
                if (HangSeatReads && path == "communities/room/rtcRooms/voice/s0")
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                var value = path == "profiles/alice/Info" ? JsonSerializer.SerializeToNode(new UserProfile("alice", "Alice")) : Read(path);
                var response = Response(HttpStatusCode.OK, value?.ToJsonString() ?? "null");
                response.Headers.ETag = new EntityTagHeaderValue(ETag(path));
                return response;
            }
            if (request.Method == HttpMethod.Delete)
            {
                lock (gate) Deletes.Add(path);
                Write(path, null);
                return Response(HttpStatusCode.OK, "null");
            }
            if (path.StartsWith("communities/room/rtcRooms/voice/s"))
            {
                Interlocked.Increment(ref SeatWrites);
                if (DenySeat) return Response(HttpStatusCode.Forbidden, """{"error":"Permission denied"}""");
                if (ConflictPuts-- > 0) return Response(HttpStatusCode.PreconditionFailed, "null");
            }
            if (request.Headers.TryGetValues("if-match", out var values) && values.Single() != ETag(path))
                return Response(HttpStatusCode.PreconditionFailed, "null");
            Write(path, JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)));
            return Response(HttpStatusCode.OK, "null");
        }
        private string ETag(string path) => '"' + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Read(path)?.ToJsonString() ?? "null"))) + '"';
        private HttpResponseMessage Response(HttpStatusCode code, string json)
        {
            var response = new HttpResponseMessage(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            response.Headers.Date = Now;
            return response;
        }
    }

    private sealed class EventStream : Stream
    {
        private readonly Channel<byte[]> bytes = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
        private byte[] current = [];
        private int offset;
        public bool Closed;
        public void Send(string text) => bytes.Writer.TryWrite(Encoding.UTF8.GetBytes(text));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (offset == current.Length) { current = await bytes.Reader.ReadAsync(cancellationToken); offset = 0; }
            int count = Math.Min(buffer.Length, current.Length - offset);
            current.AsMemory(offset, count).CopyTo(buffer);
            offset += count;
            return count;
        }
        protected override void Dispose(bool disposing) { Closed = true; bytes.Writer.TryComplete(); base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
