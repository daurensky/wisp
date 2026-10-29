namespace Wisp.Core;

public static class PresenceProjection
{
    public static IReadOnlyDictionary<string, PeerPresence> Project(
        PeerPresence local,
        IEnumerable<PeerPresence> peers,
        IReadOnlyDictionary<string, UserProfile>? members = null,
        IEnumerable<(string ChannelId, IReadOnlyList<RtcSeat> Participants)>? cloudRooms = null,
        (string ChannelId, IReadOnlyList<RtcSeat> Participants)? rtcRoom = null)
    {
        var online = new Dictionary<string, PeerPresence>();
        foreach (var person in peers)
            if (person.Profile.Id != local.Profile.Id) online.TryAdd(person.Profile.Id, person);

        if (cloudRooms is not null)
        {
            var latestSeat = new Dictionary<string, long>();
            foreach (var room in cloudRooms)
                ApplyRoom(local.Profile.Id, room.ChannelId, room.Participants, online, members, latestSeat);
        }
        // The connected RTC roster is authoritative, even over newer cloud seats.
        if (rtcRoom is { } rtc)
            ApplyRoom(local.Profile.Id, rtc.ChannelId, rtc.Participants, online, members, null);

        if (local.Deafened && !local.Muted) local = local with { Muted = true };
        online[local.Profile.Id] = local;
        return online;
    }

    private static void ApplyRoom(
        string localUserId,
        string channelId,
        IReadOnlyList<RtcSeat> participants,
        Dictionary<string, PeerPresence> online,
        IReadOnlyDictionary<string, UserProfile>? members,
        Dictionary<string, long>? latestSeat)
    {
        var ids = participants.Select(seat => seat.UserId).ToHashSet();
        foreach (var person in online.Values.Where(person => person.VoiceChannelId == channelId && !ids.Contains(person.Profile.Id)).ToArray())
            online[person.Profile.Id] = person with { VoiceChannelId = null, Muted = false, Deafened = false };

        foreach (var seat in participants)
        {
            if (seat.UserId == localUserId) continue;
            if (latestSeat is not null)
            {
                if (latestSeat.TryGetValue(seat.UserId, out var seenAt) && seenAt > seat.SeenAt) continue;
                latestSeat[seat.UserId] = seat.SeenAt;
            }
            var person = online.GetValueOrDefault(seat.UserId)?.Profile ?? members?.GetValueOrDefault(seat.UserId);
            if (person is not null)
                online[seat.UserId] = new PeerPresence(person, channelId, seat.Muted || seat.Deafened, seat.Deafened);
        }
    }
}
