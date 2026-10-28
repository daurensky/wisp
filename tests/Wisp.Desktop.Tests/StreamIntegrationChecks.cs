using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Wisp.Core;
using Wisp.Desktop;
using Wisp.Desktop.Media;
using Xunit;

namespace Wisp.Desktop.Tests;

/// <summary>Runs inside the existing STA UI harness; no capture or audio endpoint is opened.</summary>
internal static class StreamIntegrationChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object? Get(object instance, string name) => instance.GetType().GetField(name, Private)!.GetValue(instance);
    private static void Set(object instance, string name, object? value) => instance.GetType().GetField(name, Private)!.SetValue(instance, value);
    private static object? Call(MainWindow window, string name, params object?[] arguments) => typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, arguments);

    public static void Check(MainWindow main, LocalStore store, UserProfile profile, Community community)
    {
        var channel = store.GetChannels(community.Id).First(c => c.IsVoice);
        var room = new Room(channel.Id, Enumerable.Range(0, 7).Select(_ => Guid.NewGuid().ToString("N")).ToArray());
        var oldRoom = Get(main, "rtc"); var oldChannel = Get(main, "voiceChannelId"); var oldCommunity = Get(main, "voiceCommunity");
        var oldDeafened = Get(main, "deafened"); var oldSharing = Get(main, "sharing");
        var screen = (ScreenShareService)Get(main, "screen")!;
        var infoProperty = typeof(ScreenShareService).GetProperty("CurrentInfo")!;
        var oldInfo = screen.CurrentInfo;
        var remote = (Dictionary<string, ScreenStreamInfo>)Get(main, "remoteStreams")!;
        var viewers = (Dictionary<string, ScreenViewer>)Get(main, "viewers")!;
        var ledger = (StreamViewerPresence)Get(main, "streamViewers")!;
        var audiences = (IDictionary)Get(main, "remoteAudiences")!;
        Assert.Empty(viewers); Assert.Empty(remote);
        var info = new ScreenStreamInfo(1280, 720, 30, 4_000_000) { SessionId = Guid.NewGuid().ToString("N") };
        void State(string sender, object state) => Call(main, "HandleStreamState", new WireMessage("screen-state", sender, channel.Id, JsonSerializer.SerializeToUtf8Bytes(state)));
        try
        {
            Set(main, "rtc", room); Set(main, "voiceChannelId", channel.Id); Set(main, "voiceCommunity", community);
            Set(main, "deafened", true); Set(main, "sharing", true); infoProperty.SetValue(screen, info); ledger.Start(info.SessionId);
            foreach (var id in room.ConnectedPeerIds)
                State(id, new { TargetId = profile.Id, info.SessionId, Viewing = true });
            Assert.Equal(7, ledger.Viewers.Count);
            State(room.ConnectedPeerIds[0], new { TargetId = profile.Id, info.SessionId, Viewing = true, Restoring = true });
            Assert.Equal(7, ledger.Viewers.Count);
            State("unconnected", new { TargetId = profile.Id, info.SessionId, Viewing = true });
            State(room.ConnectedPeerIds[0], new { TargetId = profile.Id, SessionId = "stale", Viewing = false });
            Assert.Equal(7, ledger.Viewers.Count);
            room.Sent.Clear(); Call(main, "BroadcastViewerSnapshot");
            Assert.Equal(4, room.Sent.Count);
            var all = new List<string>(); long revision = -1;
            for (int page = 0; page < room.Sent.Count; page++)
            {
                var packet = room.Sent[page]; Assert.Equal("screen-state", packet.Type); Assert.True(packet.Payload.Length <= 512);
                using var json = JsonDocument.Parse(packet.Payload); var state = json.RootElement;
                Assert.Equal(profile.Id, state.GetProperty("TargetId").GetString());
                Assert.Equal(page, state.GetProperty("Page").GetInt32()); Assert.Equal(4, state.GetProperty("Pages").GetInt32());
                if (revision < 0) revision = state.GetProperty("Revision").GetInt64();
                Assert.Equal(revision, state.GetProperty("Revision").GetInt64());
                all.AddRange(state.GetProperty("Viewers").EnumerateArray().Select(value => value.GetString()!));
            }
            Assert.Equal(ledger.Viewers.OrderBy(id => id), all.OrderBy(id => id));
            State(room.ConnectedPeerIds[0], new { TargetId = profile.Id, info.SessionId, Viewing = false });
            Assert.Equal(6, ledger.Viewers.Count);
            room.Active = room.Active.Skip(1).ToArray(); Call(main, "ReconcileStreamViewers"); Assert.Equal(6, ledger.Viewers.Count);

            Set(main, "sharing", false); infoProperty.SetValue(screen, null); ledger.Stop();
            string host = room.Active[0], other = room.Active[1]; remote[host] = info;
            State(other, new { TargetId = host, info.SessionId, Viewers = new[] { other }, Revision = 1L, Page = 0, Pages = 1 });
            Assert.False(audiences.Contains(host)); // Sender cannot impersonate the source.
            State(host, new { TargetId = host, SessionId = "previous", Viewers = new[] { other }, Revision = 1L, Page = 0, Pages = 1 });
            Assert.False(audiences.Contains(host));
            State(host, new { TargetId = host, info.SessionId, Viewers = new[] { profile.Id }, Revision = 2L, Page = 0, Pages = 2 });
            var audience = audiences[host]!;
            var visible = (HashSet<string>)audience.GetType().GetField("Visible")!.GetValue(audience)!;
            Assert.Empty(visible); // Do not paint an incomplete replacement snapshot.
            State(host, new { TargetId = host, info.SessionId, Viewers = new[] { other, "outsider" }, Revision = 2L, Page = 1, Pages = 2 });
            Assert.Equal(new[] { other, profile.Id }.OrderBy(id => id), visible.OrderBy(id => id));
            State(host, new { TargetId = host, info.SessionId, Viewers = Array.Empty<string>(), Revision = 1L, Page = 0, Pages = 1 });
            Assert.Equal(2, visible.Count); // Replayed revision does not erase viewers.
            State(host, new { TargetId = host, info.SessionId, Viewers = Array.Empty<string>(), Revision = 3L, Page = 0, Pages = 1 });
            Assert.Empty(visible);

            var player = (ShareAudioPlayback)Get(main, "sharePlayback")!;
            var viewer = (ScreenViewer)Call(main, "CreateViewer", host)!;
            var view = Get(viewer, "view")!;
            var slider = (Slider)view.GetType().GetField("VolumeSlider", Private | BindingFlags.Public)!.GetValue(view)!;
            slider.Value = 35; Assert.Equal(.35f, player.GetVolume(host)); Assert.Equal(1, player.GetVolume(other));
            slider.Value = 0; Assert.Equal(0, player.GetVolume(host));
            viewer.Close(); viewer = (ScreenViewer)Call(main, "CreateViewer", host)!; Assert.Equal(0, viewer.Volume);
            view = Get(viewer, "view")!;
            var mute = (Button)view.GetType().GetField("MuteButton", Private | BindingFlags.Public)!.GetValue(view)!;
            mute.RaiseEvent(new System.Windows.RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal(.35f, viewer.Volume); Assert.Equal(.35f, player.GetVolume(host));
            Call(main, "StopVideoPeer", host, true); Assert.True(viewer.IsClosed); Assert.False(viewers.ContainsKey(host)); Assert.False(remote.ContainsKey(host));
            Assert.False(audiences.Contains(host)); Assert.Equal(.35f, player.GetVolume(host));
        }
        finally
        {
            foreach (var viewer in viewers.Values.ToArray()) viewer.Close(); viewers.Clear(); remote.Clear(); audiences.Clear(); ledger.Stop();
            Set(main, "rtc", oldRoom); Set(main, "voiceChannelId", oldChannel); Set(main, "voiceCommunity", oldCommunity);
            Set(main, "deafened", oldDeafened); Set(main, "sharing", oldSharing); infoProperty.SetValue(screen, oldInfo);
            Call(main, "UpdateStatus");
        }
    }

    private sealed class Room(string channel, string[] active) : IVoiceRoom
    {
        public string[] Active { get; set; } = active;
        public List<WireMessage> Sent { get; } = new();
        public bool Joined => true;
        public string ChannelId => channel;
        public string TransportName => "test SFU";
        public IReadOnlyList<RtcSeat> Participants => Array.Empty<RtcSeat>();
        public int ConnectedCount => Active.Length;
        public IReadOnlyList<string> ConnectedPeerIds => Active;
        public IReadOnlyDictionary<string, string> ConnectionPaths => new Dictionary<string, string>();
        public event Action<WireMessage>? MessageReceived { add { } remove { } }
        public event Action<string>? Error { add { } remove { } }
        public event Action? Changed { add { } remove { } }
        public Task EnterAsync(string communityId, string channelId, bool mute, bool deafen, CancellationToken cancellation = default) => Task.CompletedTask;
        public Task SetMutedAsync(bool mute, bool deafen) => Task.CompletedTask;
        public Task SendAsync(WireMessage message) { Sent.Add(message); return Task.CompletedTask; }
        public Task LeaveAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
