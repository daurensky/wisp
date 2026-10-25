using System.Text.Json;
using Wisp.Core;
using Xunit;

namespace Wisp.Core.Tests;

public sealed class PeerNodeTests
{
    [Fact]
    public async Task DirectTlsSessionDeliversChatAndNormalizesAuthorName()
    {
        await using var test = new Session();
        var host = test.Create("Host");
        var client = test.Create("Alice");
        string invitation = await host.HostAsync(Snapshot(host), 0, "127.0.0.1");
        await client.JoinAsync(invitation);
        var delivered = new TaskCompletionSource<WireMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.MessageReceived += m => delivered.TrySetResult(m);
        var chat = new ChatMessage("message1", "text", client.Profile.Id, "SomeoneElse", "hello", DateTimeOffset.UtcNow);
        await client.SendAsync(new("chat", "spoofed-id", "text", JsonSerializer.SerializeToUtf8Bytes(chat)));
        var message = await delivered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(client.Profile.Id, message.SenderId);
        var received = JsonSerializer.Deserialize<ChatMessage>(message.Payload)!;
        Assert.Equal("Alice", received.AuthorName);
        Assert.Equal("hello", received.Text);
        Assert.True(client.IsConnected);
        Assert.Equal(2, host.Presence.Count);
    }

    [Fact]
    public async Task VoiceChannelAdmitsAtMostFiveIncludingHost()
    {
        await using var test = new Session();
        var host = test.Create("Host");
        string invitation = await host.HostAsync(Snapshot(host), 0, "127.0.0.1");
        Assert.True(await host.SetVoiceChannelAsync("voice", false, false));
        var clients = new List<PeerNode>();
        for (int i = 0; i < 5; i++)
        {
            var peer = test.Create($"Peer{i}");
            clients.Add(peer);
            await peer.JoinAsync(invitation);
            Assert.Equal(i < 4, await peer.SetVoiceChannelAsync("voice", false, false));
        }
        Assert.Equal(5, host.Presence.Count(p => p.VoiceChannelId == "voice"));
        Assert.True(await clients[0].SetVoiceChannelAsync(null, false, false));
        Assert.True(await clients[4].SetVoiceChannelAsync("voice", false, false));
        Assert.False(await clients[4].SetVoiceChannelAsync("unknown-channel", false, false));
    }

    [Fact]
    public async Task InvitationSecretAndCertificatePinAreRequired()
    {
        await using var test = new Session();
        var host = test.Create("Host");
        var client = test.Create("Alice");
        string invitation = await host.HostAsync(Snapshot(host), 0, "127.0.0.1");
        var uri = new Uri(invitation);
        string wrongToken = invitation[..invitation.IndexOf("token=", StringComparison.Ordinal)] + "token=" + new string('0', 64);
        await Assert.ThrowsAnyAsync<Exception>(() => client.JoinAsync(wrongToken));
        Assert.False(client.IsConnected);
        string wrongPin = new UriBuilder(uri) { Query = "key=" + new string('0', 64) + "&token=" + uri.Query.Split("token=")[1] }.Uri.AbsoluteUri;
        await Assert.ThrowsAnyAsync<Exception>(() => client.JoinAsync(wrongPin));
        Assert.False(client.IsConnected);
        await client.JoinAsync(invitation);
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task InvalidChannelAndForgedChatIdentityAreRejected()
    {
        await using var test = new Session();
        var host = test.Create("Host");
        var client = test.Create("Alice");
        string invitation = await host.HostAsync(Snapshot(host), 0, "127.0.0.1");
        await client.JoinAsync(invitation);
        var forged = new ChatMessage("forged", "text", host.Profile.Id, "Host", "forged", DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.SendAsync(new("chat", host.Profile.Id, "text", JsonSerializer.SerializeToUtf8Bytes(forged))));
        var unknownChannel = forged with { AuthorId = client.Profile.Id, ChannelId = "unknown" };
        await Assert.ThrowsAsync<InvalidDataException>(() => client.SendAsync(new("chat", client.Profile.Id, "unknown", JsonSerializer.SerializeToUtf8Bytes(unknownChannel))));
        Assert.False(await client.SetVoiceChannelAsync("text", false, false));
        // Voice channels support their own text chat too.
        var voiceChat = forged with { AuthorId = client.Profile.Id, ChannelId = "voice", Text = new string('x', 8000) };
        await client.SendAsync(new("chat", client.Profile.Id, "voice", JsonSerializer.SerializeToUtf8Bytes(voiceChat)));
    }

    private static CommunitySnapshot Snapshot(PeerNode host) => new(
        new Community("community", "Test community", host.Profile.Id),
        new List<Channel> { new("text", "community", "general"), new("voice", "community", "Lounge", true) },
        new List<UserProfile> { host.Profile });

    [Fact]
    public async Task FullSixteenMiBAssetSurvivesNestedEnvelopeAndTls()
    {
        await using var test = new Session();
        var host = test.Create("Host");
        var client = test.Create("Alice");
        string invitation = await host.HostAsync(Snapshot(host), 0, "127.0.0.1");
        await client.JoinAsync(invitation);
        var delivered = new TaskCompletionSource<WireMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.MessageReceived += m => { if (m.Type == "asset") delivered.TrySetResult(m); };
        byte[] data = new byte[16 * 1024 * 1024];
        data[0] = 42; data[^1] = 77;
        var asset = new AssetTransfer("sample-asset", "sample.bin", "attachment", data);
        await host.SendAsync(new("asset", host.Profile.Id, null, JsonSerializer.SerializeToUtf8Bytes(asset)));
        var result = await delivered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var received = JsonSerializer.Deserialize<AssetTransfer>(result.Payload)!;
        Assert.Equal(data.Length, received.Data.Length);
        Assert.Equal(42, received.Data[0]);
        Assert.Equal(77, received.Data[^1]);
    }

    [Fact]
    public async Task VoiceAudioIsIsolatedByChannelAndDeafeningSuppressesIt()
    {
        await using var test = new Session();
        var host = test.Create("Host");
        var alice = test.Create("Alice");
        var bob = test.Create("Bob");
        var snapshot = Snapshot(host);
        snapshot.Channels.Add(new("voice2", "community", "Another voice", true));
        string invitation = await host.HostAsync(snapshot, 0, "127.0.0.1");
        await alice.JoinAsync(invitation);
        await bob.JoinAsync(invitation);
        Assert.True(await host.SetVoiceChannelAsync("voice", false, false));
        Assert.True(await alice.SetVoiceChannelAsync("voice", false, false));
        Assert.True(await bob.SetVoiceChannelAsync("voice2", false, false));
        var normal = new TaskCompletionSource<WireMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var forbidden = new TaskCompletionSource<WireMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        int hostAudio = 0;
        host.MessageReceived += m => { if (m.Type == "audio") { Interlocked.Increment(ref hostAudio); normal.TrySetResult(m); } };
        bob.MessageReceived += m => { if (m.Type == "audio") forbidden.TrySetResult(m); };
        await alice.SendAsync(new("audio", alice.Profile.Id, "voice", new byte[] { 1, 2, 3 }));
        await normal.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotSame(forbidden.Task, await Task.WhenAny(forbidden.Task, Task.Delay(200)));
        Assert.True(await alice.SetVoiceChannelAsync("voice", false, true));
        Assert.True(host.Presence.Single(p => p.Profile.Id == alice.Profile.Id).Muted);
        var aliceAudio = new TaskCompletionSource<WireMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        alice.MessageReceived += m => { if (m.Type == "audio") aliceAudio.TrySetResult(m); };
        await host.SendAsync(new("audio", host.Profile.Id, "voice", new byte[] { 4 }));
        await alice.SendAsync(new("audio", alice.Profile.Id, "voice", new byte[] { 5 }));
        Assert.NotSame(aliceAudio.Task, await Task.WhenAny(aliceAudio.Task, Task.Delay(200)));
        Assert.Equal(1, Volatile.Read(ref hostAudio));
    }

    private sealed class Session : IAsyncDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "wisp-peer-tests-" + Guid.NewGuid().ToString("N"));
        private readonly List<PeerNode> nodes = new();
        public PeerNode Create(string username)
        {
            var peer = new PeerNode(new UserProfile(Guid.NewGuid().ToString("N"), username), root);
            nodes.Add(peer);
            return peer;
        }
        public async ValueTask DisposeAsync()
        {
            foreach (var peer in Enumerable.Reverse(nodes)) await peer.DisposeAsync();
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
