using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading.Channels;

namespace Wisp.Core;

/// <summary>A TLS protected community hosted by one participating desktop. No external service is used.</summary>
public sealed class PeerNode : IAsyncDisposable
{
    private const int MaxFrame = 32 * 1024 * 1024;
    private const int MaxPayload = 23 * 1024 * 1024;
    private readonly object gate = new();
    private readonly ConcurrentDictionary<string, Connection> peers = new();
    private readonly Dictionary<string, PeerPresence> presence = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> requests = new();
    private readonly X509Certificate2 certificate;
    private readonly string trustPath;
    private Dictionary<string, string> trustedPeers;
    private CancellationTokenSource? lifetime;
    private TcpListener? listener;
    private Connection? host;
    private CommunitySnapshot? snapshot;
    private string? token;
    private Task? acceptTask;
    public UserProfile Profile { get; private set; }
    public bool IsConnected => lifetime is { IsCancellationRequested: false } && (IsHost || host != null);
    public bool IsHost => listener != null;
    public IReadOnlyList<PeerPresence> Presence { get { lock (gate) return presence.Values.ToArray(); } }
    public event Action<WireMessage>? MessageReceived;
    public event Action<IReadOnlyList<PeerPresence>>? PresenceChanged;
    public event Action<CommunitySnapshot>? SnapshotReceived;
    public event Action<string>? Error;

    public PeerNode(UserProfile profile, string rootDirectory)
    {
        Profile = Sanitize(profile);
        Directory.CreateDirectory(rootDirectory);
        string path = Path.Combine(rootDirectory, $"identity-{SafeName(profile.Id)}.pfx");
        if (File.Exists(path)) certificate = new X509Certificate2(File.ReadAllBytes(path), (string?)null, X509KeyStorageFlags.Exportable);
        else
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=Wisp peer", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
            byte[] pfx = created.Export(X509ContentType.Pfx);
            File.WriteAllBytes(path, pfx);
            certificate = new X509Certificate2(pfx, (string?)null, X509KeyStorageFlags.Exportable);
        }
        trustPath = Path.Combine(rootDirectory, $"trusted-{SafeName(profile.Id)}.json");
        trustedPeers = File.Exists(trustPath) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(trustPath)) ?? new() : new();
    }

    public async Task<string> HostAsync(CommunitySnapshot communitySnapshot, int port = 45832, string? advertisedHost = null)
    {
        await DisconnectAsync();
        if (communitySnapshot.Community.OwnerId != Profile.Id) throw new InvalidOperationException("Only the community owner can host it.");
        ValidateSnapshot(communitySnapshot);
        snapshot = communitySnapshot with { Members = new() { Profile } };
        token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        lifetime = new CancellationTokenSource();
        listener = new TcpListener(IPAddress.Any, port);
        listener.Start(16);
        lock (gate) presence[Profile.Id] = new(Profile, null, false, false);
        acceptTask = AcceptLoopAsync(lifetime.Token);
        SnapshotReceived?.Invoke(snapshot);
        NotifyPresence();
        int actualPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        advertisedHost ??= GetLocalAddress();
        return new UriBuilder("wisp", advertisedHost, actualPort) { Query = $"key={Fingerprint(certificate)}&token={token}" }.Uri.AbsoluteUri;
    }

    public async Task JoinAsync(string invite)
    {
        await DisconnectAsync();
        if (!Uri.TryCreate(invite, UriKind.Absolute, out var uri) || uri.Scheme != "wisp" || uri.Port is < 1 or > 65535)
            throw new ArgumentException("Use a complete wisp:// invitation.");
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
        if (!query.TryGetValue("key", out var pin) || !query.TryGetValue("token", out var invitationToken) || pin.Length != 64 || invitationToken.Length != 64)
            throw new ArgumentException("The invitation is missing its certificate or secret.");
        var session = new CancellationTokenSource();
        lifetime = session;
        var cancellation = session.Token;
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await tcp.ConnectAsync(uri.Host, uri.Port, timeout.Token);
            var ssl = new SslStream(tcp.GetStream(), false, (_, remote, _, _) => remote != null && FixedEquals(Fingerprint(remote), pin));
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = uri.Host, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ClientCertificates = new X509CertificateCollection { certificate }, CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, timeout.Token);
            var connection = new Connection(tcp, ssl);
            await WriteFrameAsync(ssl, new Frame("hello", Profile, invitationToken), timeout.Token);
            Frame response = await ReadFrameAsync(ssl, timeout.Token, 512 * 1024);
            if (response.Type != "welcome" || response.Snapshot == null || response.Peers == null)
                throw new InvalidOperationException(response.Reason ?? "The community rejected the connection.");
            ValidateSnapshot(response.Snapshot);
            snapshot = response.Snapshot;
            lock (gate)
            {
                if (!ReferenceEquals(lifetime, session) || session.IsCancellationRequested) throw new OperationCanceledException();
                host = connection;
            }
            ReplacePresence(response.Peers);
            SnapshotReceived?.Invoke(snapshot);
            connection.Start(cancellation, () => ReportDisconnect(connection, session, "The hosting participant disconnected."));
            _ = ClientReadLoopAsync(connection, session);
        }
        catch { tcp.Dispose(); await DisconnectAsync(); throw; }
    }

    public async Task SendAsync(WireMessage message)
    {
        if (!IsConnected) throw new InvalidOperationException("Connect to a community first.");
        var authenticated = message with { SenderId = Profile.Id };
        ValidateMessage(authenticated);
        if (IsHost) await RouteAsync(authenticated);
        else if (!host!.Enqueue(new Frame("data", Message: authenticated), IsRealtime(authenticated.Type)))
            throw new IOException("The connection is busy; try again.");
    }

    public async Task<bool> SetVoiceChannelAsync(string? channelId, bool muted, bool deafened)
    {
        if (!IsConnected) return false;
        if (IsHost)
        {
            bool accepted = SetVoice(Profile.Id, channelId, muted, deafened);
            if (accepted) await BroadcastPresenceAsync();
            return accepted;
        }
        string request = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        requests[request] = completion;
        try
        {
            if (!host!.Enqueue(new Frame("voice", ChannelId: channelId, Muted: muted, Deafened: deafened, RequestId: request))) return false;
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(10), lifetime!.Token);
        }
        finally { requests.TryRemove(request, out _); }
    }

    public async Task UpdateProfileAsync(UserProfile profile)
    {
        if (profile.Id != Profile.Id) throw new ArgumentException("The profile identity cannot change.");
        Profile = Sanitize(profile);
        if (!IsConnected) return;
        if (IsHost)
        {
            lock (gate) presence[Profile.Id] = presence[Profile.Id] with { Profile = Profile };
            await BroadcastPresenceAsync();
        }
        else host!.Enqueue(new Frame("profile", Profile));
    }

    private async Task AcceptLoopAsync(CancellationToken cancellation)
    {
        try
        {
            var admissions = new SemaphoreSlim(8);
            while (!cancellation.IsCancellationRequested)
            {
                TcpClient tcp = await listener!.AcceptTcpClientAsync(cancellation);
                if (!await admissions.WaitAsync(0, cancellation)) { tcp.Dispose(); continue; }
                int holding = 1;
                void ReleaseAdmission() { if (Interlocked.Exchange(ref holding, 0) == 1) admissions.Release(); }
                _ = Task.Run(async () => { try { await AcceptPeerAsync(tcp, cancellation, ReleaseAdmission); } finally { ReleaseAdmission(); } }, CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { }
    }

    private async Task AcceptPeerAsync(TcpClient tcp, CancellationToken cancellation, Action releaseAdmission)
    {
        string? id = null;
        Connection? connection = null;
        try
        {
            tcp.NoDelay = true;
            var ssl = new SslStream(tcp.GetStream(), false, (_, remote, _, _) => remote != null);
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(12));
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate, ClientCertificateRequired = true,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13, CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                }, timeout.Token);
                Frame hello = await ReadFrameAsync(ssl, timeout.Token, 16 * 1024);
                if (hello.Type != "hello" || hello.Profile == null || !FixedEquals(hello.Token ?? "", token ?? ""))
                    throw new AuthenticationException("Invalid invitation.");
                UserProfile profile = Sanitize(hello.Profile);
                id = profile.Id;
                string pin = Fingerprint(ssl.RemoteCertificate!);
                lock (gate)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (presence.Count >= 64 || presence.ContainsKey(id)) throw new AuthenticationException("Identity is already online or the community is full.");
                    string binding = snapshot!.Community.Id + ":" + id;
                    if (trustedPeers.TryGetValue(binding, out var known) && !FixedEquals(known, pin)) throw new AuthenticationException("This identity has a different certificate.");
                    trustedPeers[binding] = pin;
                    File.WriteAllText(trustPath, JsonSerializer.Serialize(trustedPeers));
                    presence[id] = new(profile, null, false, false);
                }
                connection = new Connection(tcp, ssl);
                peers[id] = connection;
                await WriteFrameAsync(ssl, new Frame("welcome", Snapshot: CurrentSnapshot(), Peers: Presence.ToList()), timeout.Token);
            }
            connection.Start(cancellation, () => tcp.Dispose());
            releaseAdmission();
            await BroadcastPresenceAsync();
            while (!cancellation.IsCancellationRequested)
            {
                Frame frame = await ReadFrameAsync(ssl, cancellation);
                cancellation.ThrowIfCancellationRequested();
                switch (frame.Type)
                {
                    case "data" when frame.Message != null:
                        var message = frame.Message with { SenderId = id };
                        ValidateMessage(message);
                        await RouteAsync(message);
                        break;
                    case "voice":
                        bool accepted = SetVoice(id, frame.ChannelId, frame.Muted, frame.Deafened);
                        // Presence is queued first, so acknowledgement observes the admitted state.
                        if (accepted) await BroadcastPresenceAsync();
                        connection.Enqueue(new Frame("voice-result", RequestId: frame.RequestId, Accepted: accepted));
                        break;
                    case "profile" when frame.Profile != null:
                        var updated = Sanitize(frame.Profile with { Id = id });
                        lock (gate) presence[id] = presence[id] with { Profile = updated };
                        await BroadcastPresenceAsync();
                        break;
                    default: throw new InvalidDataException("Unknown protocol message.");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException or OperationCanceledException or JsonException or InvalidDataException or ArgumentException or InvalidOperationException)
        { if (!cancellation.IsCancellationRequested && id != null) Error?.Invoke($"Peer disconnected: {ex.Message}"); }
        finally
        {
            if (id != null && peers.TryGetValue(id, out var existing) && ReferenceEquals(existing, connection))
            {
                peers.TryRemove(id, out _);
                lock (gate) presence.Remove(id);
                if (!cancellation.IsCancellationRequested) await BroadcastPresenceAsync();
            }
            connection?.Dispose();
            tcp.Dispose();
        }
    }

    private async Task ClientReadLoopAsync(Connection connection, CancellationTokenSource session)
    {
        var cancellation = session.Token;
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                var frame = await ReadFrameAsync(connection.Stream, cancellation);
                if (!ReferenceEquals(host, connection)) return;
                cancellation.ThrowIfCancellationRequested();
                switch (frame.Type)
                {
                    case "data" when frame.Message != null:
                        ValidateMessage(frame.Message);
                        lock (gate)
                        {
                            if (!presence.ContainsKey(frame.Message.SenderId)) throw new InvalidDataException("Unknown sender.");
                            if (IsVoiceData(frame.Message.Type) && (!presence.TryGetValue(Profile.Id, out var self) || self.VoiceChannelId == null || self.VoiceChannelId != frame.Message.ChannelId))
                                throw new InvalidDataException("Media is outside the joined voice channel.");
                        }
                        MessageReceived?.Invoke(frame.Message);
                        break;
                    case "presence" when frame.Peers != null:
                        if (frame.Snapshot == null) throw new InvalidDataException("Presence snapshot is missing.");
                        ValidateSnapshot(frame.Snapshot);
                        if (snapshot == null || frame.Snapshot.Community.Id != snapshot.Community.Id || frame.Snapshot.Community.OwnerId != snapshot.Community.OwnerId)
                            throw new InvalidDataException("Community identity changed.");
                        ReplacePresence(frame.Peers);
                        if (frame.Snapshot != null) { snapshot = frame.Snapshot; SnapshotReceived?.Invoke(snapshot); }
                        break;
                    case "voice-result" when frame.RequestId != null:
                        if (requests.TryGetValue(frame.RequestId, out var completion)) completion.TrySetResult(frame.Accepted);
                        break;
                    default: throw new InvalidDataException("Unknown host message.");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException or OperationCanceledException or JsonException or InvalidDataException or ObjectDisposedException or ArgumentException)
        { if (!cancellation.IsCancellationRequested) ReportDisconnect(connection, session, ex.Message); }
    }

    private bool SetVoice(string id, string? channel, bool muted, bool deafened)
    {
        lock (gate)
        {
            if (!presence.TryGetValue(id, out var current)) return false;
            if (channel != null && (snapshot!.Channels.All(c => c.Id != channel || !c.IsVoice) ||
                presence.Values.Count(p => p.VoiceChannelId == channel && p.Profile.Id != id) >= Channel.Capacity)) return false;
            presence[id] = current with { VoiceChannelId = channel, Muted = muted || deafened, Deafened = deafened };
            return true;
        }
    }

    private Task RouteAsync(WireMessage message)
    {
        PeerPresence sender;
        PeerPresence[] targets;
        lock (gate)
        {
            sender = presence.GetValueOrDefault(message.SenderId) ?? throw new InvalidDataException("Unknown sender.");
            if (message.Type == "chat")
            {
                var chat = JsonSerializer.Deserialize<ChatMessage>(message.Payload)!;
                message = message with { Payload = JsonSerializer.SerializeToUtf8Bytes(chat with { AuthorName = sender.Profile.Username }) };
            }
            bool voice = IsVoiceData(message.Type);
            if (voice && (sender.VoiceChannelId == null || sender.VoiceChannelId != message.ChannelId)) throw new InvalidDataException("Join the voice channel before sending media.");
            if (message.Type == "audio" && (sender.Muted || sender.Deafened)) return Task.CompletedTask;
            targets = presence.Values.Where(p => p.Profile.Id != message.SenderId && (!voice || p.VoiceChannelId == sender.VoiceChannelId) &&
                !(p.Deafened && message.Type is "audio" or "sound")).ToArray();
        }
        var frame = new Frame("data", Message: message);
        foreach (var target in targets)
        {
            if (target.Profile.Id == Profile.Id) MessageReceived?.Invoke(message);
            else if (peers.TryGetValue(target.Profile.Id, out var peer))
            {
                if (!peer.Enqueue(frame, IsRealtime(message.Type)) && !IsRealtime(message.Type)) peer.Dispose();
            }
        }
        return Task.CompletedTask;
    }

    private void ValidateMessage(WireMessage message)
    {
        if (message.Payload == null || message.Payload.Length > MaxPayload) throw new InvalidDataException("Media exceeds 16 MiB.");
        if (message.Type is not ("chat" or "audio" or "screen" or "screen-stop" or "asset" or "sound")) throw new InvalidDataException("Unknown media type.");
        if (message.Type == "audio" && message.Payload.Length > 4096) throw new InvalidDataException("Audio packet is too large.");
        if (message.Type == "screen" && message.Payload.Length > 2 * 1024 * 1024) throw new InvalidDataException("Screen frame is too large.");
        if (message.Type == "asset")
        {
            var asset = JsonSerializer.Deserialize<AssetTransfer>(message.Payload) ?? throw new InvalidDataException("Invalid asset.");
            if (string.IsNullOrWhiteSpace(asset.Id) || asset.Id.Length > 128 || string.IsNullOrWhiteSpace(asset.Name) || asset.Name.Length > 255 ||
                string.IsNullOrWhiteSpace(asset.Kind) || asset.Kind.Length > 32 || asset.Data == null || asset.Data.Length > 16 * 1024 * 1024)
                throw new InvalidDataException("Asset exceeds its limits.");
        }
        if ((message.Type is "sound" or "screen-stop") && message.Payload.Length > 512) throw new InvalidDataException("Command is too large.");
        if (message.Type == "chat")
        {
            if (message.Payload.Length > 64 * 1024) throw new InvalidDataException("Message is too long.");
            var chat = JsonSerializer.Deserialize<ChatMessage>(message.Payload) ?? throw new InvalidDataException("Invalid chat message.");
            if (chat.AuthorId != message.SenderId || chat.ChannelId != message.ChannelId || chat.Text == null || chat.Text.Length > 8000 || string.IsNullOrWhiteSpace(chat.AuthorName) || chat.AuthorName.Length > 32 || string.IsNullOrWhiteSpace(chat.Id) || chat.Id.Length > 128 ||
                chat.AttachmentId?.Length > 128 || snapshot!.Channels.All(c => c.Id != message.ChannelId)) throw new InvalidDataException("Invalid chat author or channel.");
        }
    }

    private CommunitySnapshot CurrentSnapshot()
    { lock (gate) return snapshot! with { Members = presence.Values.Select(p => p.Profile).ToList() }; }

    private Task BroadcastPresenceAsync()
    {
        CommunitySnapshot current;
        lock (gate)
        {
            current = CurrentSnapshot();
            snapshot = current;
            var frame = new Frame("presence", Peers: presence.Values.ToList(), Snapshot: current);
            foreach (var peer in peers.Values) if (!peer.Enqueue(frame)) peer.Dispose();
        }
        SnapshotReceived?.Invoke(current);
        NotifyPresence();
        return Task.CompletedTask;
    }

    private void ReplacePresence(List<PeerPresence> values)
    {
        if (values.Count > 64 || values.Any(p => p == null || p.Profile == null) || values.Select(p => p.Profile.Id).Distinct().Count() != values.Count)
            throw new InvalidDataException("Invalid presence list.");
        var safe = values.Select(p => p with { Profile = Sanitize(p.Profile) }).ToList();
        if (safe.Any(p => p.VoiceChannelId != null && snapshot!.Channels.All(c => c.Id != p.VoiceChannelId || !c.IsVoice)) ||
            safe.Where(p => p.VoiceChannelId != null).GroupBy(p => p.VoiceChannelId).Any(g => g.Count() > Channel.Capacity))
            throw new InvalidDataException("Invalid voice presence.");
        lock (gate) { presence.Clear(); foreach (var value in safe) presence[value.Profile.Id] = value; }
        NotifyPresence();
    }
    private void NotifyPresence() => PresenceChanged?.Invoke(Presence);
    private void ReportDisconnect(Connection expected, CancellationTokenSource session, string reason)
    {
        // Cancel only the captured generation, never whichever session happens to be current now.
        try { session.Cancel(); } catch (ObjectDisposedException) { }
        lock (gate)
        {
            if (!ReferenceEquals(host, expected) || !ReferenceEquals(lifetime, session)) return;
            expected.Dispose();
            host = null;
            presence.Clear();
            foreach (var request in requests.Values) request.TrySetResult(false);
        }
        NotifyPresence();
        Error?.Invoke(reason);
    }
    public async Task DisconnectAsync()
    {
        lifetime?.Cancel();
        listener?.Stop(); listener = null;
        host?.Dispose(); host = null;
        foreach (var peer in peers.Values) peer.Dispose();
        peers.Clear();
        if (acceptTask != null) { try { await acceptTask; } catch (OperationCanceledException) { } acceptTask = null; }
        lock (gate) presence.Clear();
        foreach (var request in requests.Values) request.TrySetResult(false);
        requests.Clear();
        lifetime?.Dispose(); lifetime = null;
        snapshot = null; token = null;
        NotifyPresence();
    }
    public async ValueTask DisposeAsync() { await DisconnectAsync(); certificate.Dispose(); }

    private static void ValidateSnapshot(CommunitySnapshot value)
    {
        if (value.Community == null || string.IsNullOrWhiteSpace(value.Community.Id) || value.Community.Id.Length > 128 || string.IsNullOrWhiteSpace(value.Community.Name) || value.Community.Name.Length > 80 ||
            string.IsNullOrWhiteSpace(value.Community.OwnerId) || value.Community.OwnerId.Length > 128 || value.Channels == null || value.Members == null || value.Channels.Count > 100 || value.Members.Count > 64 ||
            value.Channels.Any(c => c == null || string.IsNullOrWhiteSpace(c.Id) || c.Id.Length > 128 || c.CommunityId != value.Community.Id || string.IsNullOrWhiteSpace(c.Name) || c.Name.Length > 64) ||
            value.Channels.Select(c => c.Id).Distinct().Count() != value.Channels.Count || value.Members.Any(p => p == null) || value.Members.Select(p => p.Id).Distinct().Count() != value.Members.Count ||
            value.Members.All(p => p.Id != value.Community.OwnerId))
            throw new InvalidDataException("Invalid community.");
        for (int i = 0; i < value.Members.Count; i++) value.Members[i] = Sanitize(value.Members[i]);
    }
    private static UserProfile Sanitize(UserProfile value)
    {
        if (string.IsNullOrWhiteSpace(value.Id) || value.Id.Length > 128 || string.IsNullOrWhiteSpace(value.Username) || value.Username.Length is < 3 or > 32 ||
            !System.Text.RegularExpressions.Regex.IsMatch(value.Username, @"^[\p{L}\p{N}_.-]+$") || value.About == null || value.About.Length > 2000 || value.Game?.Length > 200 ||
            value.AvatarAssetId?.Length > 100 || value.BannerAssetId?.Length > 100)
            throw new ArgumentException("Invalid profile.");
        return value with { AvatarPath = null, BannerPath = null, Status = value.Status is "online" or "idle" or "dnd" or "offline" ? value.Status : "online" };
    }
    private static bool IsRealtime(string type) => type is "audio" or "screen";
    private static bool IsVoiceData(string type) => type is "audio" or "screen" or "screen-stop" or "sound";
    private static string SafeName(string text) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
    private static string Fingerprint(X509Certificate certificate) => Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));
    private static bool FixedEquals(string a, string b) => CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(a), System.Text.Encoding.UTF8.GetBytes(b));
    private static string GetLocalAddress()
    {
        try { return Dns.GetHostAddresses(Dns.GetHostName()).First(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a)).ToString(); }
        catch { return "127.0.0.1"; }
    }
    private static async Task<Frame> ReadFrameAsync(SslStream stream, CancellationToken cancellation, int maxFrame = MaxFrame)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellation);
        int size = BinaryPrimitives.ReadInt32BigEndian(header);
        if (size < 2 || size > maxFrame) throw new InvalidDataException("Invalid frame length.");
        byte[] payload = new byte[size];
        await stream.ReadExactlyAsync(payload, cancellation);
        return JsonSerializer.Deserialize<Frame>(payload) ?? throw new InvalidDataException("Empty frame.");
    }
    private static async Task WriteFrameAsync(SslStream stream, Frame frame, CancellationToken cancellation)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(frame);
        if (payload.Length > MaxFrame) throw new InvalidDataException("Frame exceeds transport limit.");
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellation);
        await stream.WriteAsync(payload, cancellation);
    }
    private sealed record Frame(string Type, UserProfile? Profile = null, string? Token = null, CommunitySnapshot? Snapshot = null,
        List<PeerPresence>? Peers = null, WireMessage? Message = null, string? ChannelId = null, bool Muted = false, bool Deafened = false,
        string? RequestId = null, bool Accepted = false, string? Reason = null);

    private sealed class Connection(TcpClient client, SslStream stream) : IDisposable
    {
        private readonly System.Threading.Channels.Channel<Frame> control = System.Threading.Channels.Channel.CreateBounded<Frame>(new BoundedChannelOptions(8) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        private readonly System.Threading.Channels.Channel<Frame> realtime = System.Threading.Channels.Channel.CreateBounded<Frame>(new BoundedChannelOptions(4) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
        private readonly SemaphoreSlim ready = new(0);
        private readonly CancellationTokenSource stop = new();
        private int disposed;
        public SslStream Stream => stream;
        public bool Enqueue(Frame frame, bool lossy = false)
        {
            if (Volatile.Read(ref disposed) != 0) return false;
            bool success = (lossy ? realtime : control).Writer.TryWrite(frame);
            if (success && ready.CurrentCount < 32) ready.Release();
            return success;
        }
        public void Start(CancellationToken cancellation, Action onFailure)
        {
            _ = Task.Run(async () =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, stop.Token);
                try
                {
                    while (!linked.IsCancellationRequested)
                    {
                        await ready.WaitAsync(linked.Token);
                        while (control.Reader.TryRead(out var frame)) await WriteFrameAsync(stream, frame, linked.Token);
                        if (realtime.Reader.TryRead(out var media)) await WriteFrameAsync(stream, media, linked.Token);
                    }
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
                { if (!linked.IsCancellationRequested) onFailure(); }
            });
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            stop.Cancel(); control.Writer.TryComplete(); realtime.Writer.TryComplete();
            stream.Dispose(); client.Dispose();
        }
    }
}
