using System.Text.Json;
namespace Wisp.Core;
public sealed record RtcSeat(string UserId,string SessionId,long SeenAt,bool Muted=false,bool Deafened=false);
public static class RoomRoster
{
    public static IReadOnlyList<RtcSeat> ActiveParticipants(JsonElement snapshot,DateTimeOffset now)
    {
        if(snapshot.ValueKind!=JsonValueKind.Object)return Array.Empty<RtcSeat>();
        long time=now.ToUnixTimeMilliseconds();var result=new List<RtcSeat>();
        foreach(var property in snapshot.EnumerateObject())
        {
            if(property.Value.ValueKind!=JsonValueKind.Object)continue;
            try{var seat=property.Value.Deserialize<RtcSeat>();if(seat is {UserId.Length:>0,SessionId.Length:32}&&time-seat.SeenAt<45000&&seat.SeenAt<=time+10000)result.Add(seat);}catch(JsonException){}
        }
        return result;
    }
}
