using Wisp.Core;
using Xunit;

namespace Wisp.Core.Tests;

public sealed class PresenceProjectionTests
{
    private static readonly UserProfile Local = new("local", "Local");

    [Fact]
    public void FirstPresenceSourceWinsAndSavedMembersAloneAreNotOnline()
    {
        var directProfile = new UserProfile("remote", "Current name", Game: "Current game");
        var direct = new PeerPresence(directProfile, "direct-room", true, false);
        var cloud = new PeerPresence(directProfile with { Username = "Old cloud name" }, "cloud-room", false, false);
        var cloudOnly = new PeerPresence(new UserProfile("cloud-only", "Cloud only"), null, false, false);
        var saved = Members(directProfile with { Username = "Saved name" }, new UserProfile("offline", "Offline"));

        var result = PresenceProjection.Project(Self(), new[] { direct, cloud, cloudOnly }, saved);

        Assert.Equal(3, result.Count);
        Assert.Equal(direct, result["remote"]);
        Assert.Equal(cloudOnly, result["cloud-only"]);
        Assert.DoesNotContain("offline", result.Keys);
        Assert.Equal("Saved name", saved["remote"].Username);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NewestCloudSeatWinsRegardlessOfRoomOrder(bool newestFirst)
    {
        var person = new UserProfile("remote", "Remote");
        var older = Room("old-room", Seat(person.Id, 100, muted: true));
        var newer = Room("new-room", Seat(person.Id, 200, deafened: true));

        var result = PresenceProjection.Project(Self(), Array.Empty<PeerPresence>(), Members(person),
            newestFirst ? new[] { newer, older } : new[] { older, newer });

        Assert.Equal(new PeerPresence(person, "new-room", true, true), result[person.Id]);
    }

    [Fact]
    public void EqualCloudTimestampsKeepTheLaterRoom()
    {
        var person = new UserProfile("remote", "Remote");

        var result = PresenceProjection.Project(Self(), Array.Empty<PeerPresence>(), Members(person),
            new[] { Room("first", Seat(person.Id, 100)), Room("second", Seat(person.Id, 100, muted: true)) });

        Assert.Equal(new PeerPresence(person, "second", true, false), result[person.Id]);
    }

    [Fact]
    public void EmptyCloudRoomClearsStaleVoiceStateWithoutRemovingOnlinePresence()
    {
        var stale = new PeerPresence(new UserProfile("stale", "Stale"), "empty-room", true, true);
        var elsewhere = new PeerPresence(new UserProfile("elsewhere", "Elsewhere"), "other-room", true, false);

        var result = PresenceProjection.Project(Self(), new[] { stale, elsewhere },
            cloudRooms: new[] { Room("empty-room") });

        Assert.Equal(stale with { VoiceChannelId = null, Muted = false, Deafened = false }, result["stale"]);
        Assert.Equal(elsewhere, result["elsewhere"]);
        Assert.Equal(new PeerPresence(stale.Profile, "empty-room", true, true), stale);
    }

    [Fact]
    public void RtcRosterOverridesNewerCloudSeatsAndClearsOnlyItsOwnChannel()
    {
        var departed = new UserProfile("departed", "Departed");
        var moved = new UserProfile("moved", "Moved");
        var elsewhere = new UserProfile("elsewhere", "Elsewhere");

        var result = PresenceProjection.Project(Self(), Array.Empty<PeerPresence>(), Members(departed, moved, elsewhere),
            new[] { Room("rtc-room", Seat(departed.Id, 500, deafened: true)),
                Room("other-room", Seat(moved.Id, 500, muted: true), Seat(elsewhere.Id, 500)) },
            Room("rtc-room", Seat(moved.Id, 1)));

        Assert.Equal(new PeerPresence(departed, null, false, false), result[departed.Id]);
        Assert.Equal(new PeerPresence(moved, "rtc-room", false, false), result[moved.Id]);
        Assert.Equal(new PeerPresence(elsewhere, "other-room", false, false), result[elsewhere.Id]);

        var emptyRtc = PresenceProjection.Project(Self(), result.Values, rtcRoom: Room("rtc-room"));
        Assert.Equal(new PeerPresence(moved, null, false, false), emptyRtc[moved.Id]);
        Assert.Equal(result[elsewhere.Id], emptyRtc[elsewhere.Id]);
    }

    [Fact]
    public void SeatsUsePresenceProfilesThenSavedProfilesAndOmitUnknownUsers()
    {
        var current = new UserProfile("current", "Current", Game: "Playing");
        var saved = new UserProfile("saved", "Saved");
        var incoming = new PeerPresence(current, null, false, false);

        var result = PresenceProjection.Project(Self(), new[] { incoming },
            Members(current with { Username = "Old name" }, saved),
            new[] { Room("room", Seat(current.Id, 100), Seat(saved.Id, 100), Seat("unknown", 100)) });

        Assert.Equal(3, result.Count);
        Assert.Equal(current, result[current.Id].Profile);
        Assert.Equal(saved, result[saved.Id].Profile);
        Assert.DoesNotContain("unknown", result.Keys);
        Assert.Null(incoming.VoiceChannelId);
    }

    [Theory]
    [InlineData("local-room", false, false, false)]
    [InlineData(null, true, false, true)]
    [InlineData(null, false, true, true)]
    public void LocalProfileChannelAndAudioStateOverrideAllRemoteSources(string? channel, bool muted, bool deafened, bool effectiveMute)
    {
        var local = new PeerPresence(Local, channel, muted, deafened);
        var remoteSelf = new PeerPresence(Local with { Username = "Stale self" }, "stale-room", true, true);

        var result = PresenceProjection.Project(local, new[] { remoteSelf },
            cloudRooms: new[] { Room("cloud-room", Seat(Local.Id, 500, muted: true)) },
            rtcRoom: Room("rtc-room", Seat(Local.Id, 1, deafened: true)));

        Assert.Equal(new PeerPresence(Local, channel, effectiveMute, deafened), Assert.Single(result).Value);
    }

    private static PeerPresence Self() => new(Local, null, false, false);

    private static Dictionary<string, UserProfile> Members(params UserProfile[] profiles) => profiles.ToDictionary(person => person.Id);

    private static RtcSeat Seat(string userId, long seenAt, bool muted = false, bool deafened = false) =>
        new(userId, "0123456789abcdef0123456789abcdef", seenAt, muted, deafened);

    private static (string ChannelId, IReadOnlyList<RtcSeat> Participants) Room(string channel, params RtcSeat[] seats) => (channel, seats);
}
