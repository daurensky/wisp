// Cross-runtime probe: real SIPSorcery/SFU RTP + SCTP, entirely synthetic Firebase.
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Wisp.Core;

var settings = new VoiceServerSettings(true, args[0], args[1]);
var db = new FixtureDatabase();
var clients = new List<SfuRoom>(); var backends = new List<FirebaseBackend>();
try
{
    for (int i = 0; i < 5; i++)
    {
        var cloud = new FirebaseBackend(new("fixture", "fixture", "https://fixture.firebaseio.com"), new FixtureHttp(db, "user" + i));
        backends.Add(cloud); await cloud.SignInAsync("fixture@example.com", "fixture", false, "fixture");
        var room = new SfuRoom(cloud, settings, new StaticIceServerProvider(Array.Empty<string>()));
        clients.Add(room); room.Error += error => Console.WriteLine("ROOM " + i + ": " + error);
        await room.EnterAsync("community", "voice", false, false);
        Console.WriteLine("Joined " + i);
    }
    await Until(() => clients.All(c => c.ConnectedCount == 4), "all 20 downlinks connected");
    var a = clients[0]; var b = clients[1];
    var received = new System.Collections.Concurrent.ConcurrentDictionary<string, WireMessage>();
    int voice = 0;
    b.MessageReceived += m => { if (m.Type == "audio") Interlocked.Increment(ref voice); received.TryAdd(m.Type, m); };
    var h264 = Enumerable.Repeat((byte)0x55, 30000).ToArray(); h264[0] = h264[1] = h264[2] = 0; h264[3] = 1; h264[4] = 0x65;
    for (int i = 0; i < 10; i++)
    {
        await a.SendAsync(new("audio", "forged", "wrong", [1, 2, 3]));
        await a.SendAsync(new("screen-audio", "forged", "wrong", [4, 5, 6]));
        await a.SendAsync(new("screen-h264", "forged", "wrong", H264Frame.Pack(60, h264)));
        await Task.Delay(30);
    }
    await a.SendAsync(new("screen-start", "forged", "wrong", [7, 8, 9]));
    var bulk = new byte[128000]; Random.Shared.NextBytes(bulk);
    await a.SendAsync(new("asset", "forged", null, bulk));
    await Until(() => new[] { "audio", "screen-audio", "screen-h264", "screen-start", "asset" }.All(received.ContainsKey), "voice/share/video/events/file delivery");
    foreach (var m in received.Values.Where(m => m.Type != "video-report")) if (m.SenderId != "user0" || m.ChannelId != "voice") throw new Exception("Forged identity accepted");
    if (!received["audio"].Payload.SequenceEqual(new byte[]{1,2,3}) || !received["screen-audio"].Payload.SequenceEqual(new byte[]{4,5,6}) || !received["asset"].Payload.SequenceEqual(bulk) || !received["screen-h264"].Payload.AsSpan().EndsWith(h264.AsSpan(4))) throw new Exception("Media payload changed");
    Console.WriteLine("PASS: five users, voice + independent share audio + H264 + events + fragmented control; bound source identity");
    // A 2MB file must not starve microphone packets while sharing.
    received.TryRemove("asset", out _); int before = voice;
    var transfer = a.SendAsync(new("asset", "user0", null, new byte[2000000]));
    for (int i = 0; i < 50; i++) { await a.SendAsync(new("audio", "user0", "voice", [1,2,3])); await a.SendAsync(new("screen-h264", "user0", "voice", H264Frame.Pack(60,h264))); await Task.Delay(20); }
    await transfer;
    await Until(() => received.TryGetValue("asset",out var m) && m.Payload.Length == 2000000, "2MB attachment delivery");
    if (voice-before<40) throw new Exception("Voice starved during media/file transfer");
    Console.WriteLine("PASS: voice continues during video and 2MB file transfer");
    await a.SetMutedAsync(true, true);
    await Until(() => b.Participants.Any(s => s.UserId == "user0" && s.Muted && s.Deafened), "mute/deafen presence");
    await clients[4].LeaveAsync();
    await Until(() => clients.Take(4).All(c => c.ConnectedCount==3), "leave removes source without disconnecting others");
    await clients[4].EnterAsync("community","voice",false,false);
    await Until(() => clients.All(c=>c.ConnectedCount==4), "fresh rejoin recreates source links");
    Console.WriteLine("PASS: mute/deafen presence, clean departure and rejoin");
}
finally { foreach (var room in clients) await room.DisposeAsync(); foreach (var cloud in backends) cloud.Dispose(); }
static async Task Until(Func<bool> check,string stage) {using var end=new CancellationTokenSource(TimeSpan.FromSeconds(25));try{while(!check())await Task.Delay(25,end.Token);}catch(OperationCanceledException){throw new Exception("Timed out: "+stage);}}
sealed class FixtureDatabase { public readonly object Gate=new();public readonly Dictionary<string,string> Values=new();public long Version; }
sealed class FixtureHttp(FixtureDatabase db,string uid):HttpMessageHandler
{
 protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req,CancellationToken token)
 {
    string path=req.RequestUri!.AbsolutePath;
    if(path.Contains("accounts:signIn"))return Reply(new{idToken="fixture-"+uid,refreshToken="fixture",localId=uid});
    if(path.Contains("profiles/"))return Reply(new UserProfile(uid,uid));
    string key=path.Replace(".json","").Trim('/');
    string? content=req.Content is null?null:await req.Content.ReadAsStringAsync(token);
    lock(db.Gate)
    {
        if(req.Method==HttpMethod.Put)
        {
            if(req.Headers.TryGetValues("if-match",out var etags)&&etags.Single()!="\"v"+db.Version+"\"")return new(HttpStatusCode.PreconditionFailed);
            if(content=="null")db.Values.Remove(key);else db.Values[key]=content!;db.Version++;
            return Reply(true);
        }
        if(db.Values.TryGetValue(key,out var value))return ReplyRaw(value);
        var children=db.Values.Where(p=>p.Key.StartsWith(key+"/")).ToDictionary(p=>p.Key[(key.Length+1)..],p=>JsonSerializer.Deserialize<JsonElement>(p.Value));
        return children.Count>0?Reply(children):ReplyRaw("null");
    }
 }
 private HttpResponseMessage Reply(object value)=>ReplyRaw(JsonSerializer.Serialize(value));
 private HttpResponseMessage ReplyRaw(string value){var response=new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(value)};response.Headers.Date=DateTimeOffset.UtcNow;response.Headers.TryAddWithoutValidation("ETag","\"v"+db.Version+"\"");return response;}
}
