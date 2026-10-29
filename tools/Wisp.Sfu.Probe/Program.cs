// Real native client against the PostgreSQL-backed server. No auth bypass.
using System.Text.Json;
using Wisp.Core;
var settings = new VoiceServerSettings(true,args[0],args[1]);
var endpoint = new UriBuilder(args[0]) { Scheme="https",Path="/" }.Uri.AbsoluteUri.TrimEnd('/');
var config = new ServerConfig(endpoint,args[1]);
TaskCompletionSource? delayedSource=null;
var clients=new List<SfuRoom>();var backends=new List<ServerBackend>();
string sourceId="";string? invitation=null;
var observed=new System.Collections.Concurrent.ConcurrentBag<(int Owner,RtcPeer Peer)>();
try
{
    for (int i = 0; i < 6; i++)
    {
        var cloud=new ServerBackend(config);backends.Add(cloud);
        await cloud.SignInAsync("user"+i+"@example.com","fixture-password-123",true,"user"+i);
        await cloud.SaveProfileAsync(cloud.Profile! with{About="native-profile-"+i});
        if(i==0)
        {
            sourceId=cloud.Profile!.Id;
            var community=new Community("community","Native probe",sourceId);
            await cloud.CreateCommunityAsync(new(community,[new("voice","community","Voice",true),new("text","community","Text")],[cloud.Profile]));
            await cloud.SendMessageAsync("community",new ChatMessage("native-message","text",sourceId,"user0","native persistent chat",DateTimeOffset.UtcNow));
            invitation=await cloud.CreateInviteAsync("community");
        }
        else await cloud.JoinCommunityAsync(invitation!);
        if(i==1)
        {
            var history=await cloud.MessagesAsync("community","text");
            if(history.Count!=1||history[0].Text!="native persistent chat"||history[0].AuthorId!=sourceId)throw new Exception("Persistent chat DTO round trip failed");
            var ownerProfile=await cloud.GetAsync<UserProfile>($"profiles/{sourceId}/Info");if(ownerProfile?.About!="native-profile-0")throw new Exception("Profile round trip failed");
            var mediaRoot=Path.Combine(Path.GetTempPath(),"wisp-media-probe-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(mediaRoot);
            try
            {
                var file=Path.Combine(mediaRoot,"native.txt");await File.WriteAllTextAsync(file,"content-addressed upload from .NET to PostgreSQL");
                using var senderStore=new LocalStore(Path.Combine(mediaRoot,"sender"));using var receiverStore=new LocalStore(Path.Combine(mediaRoot,"receiver"));
                var snapshot=(await cloud.CommunitiesAsync()).Single();senderStore.SaveSnapshot(snapshot);receiverStore.SaveSnapshot(snapshot);
                var asset=senderStore.ImportAsset("community",file,"attachment");await CloudFiles.UploadAsync(backends[0],asset);
                var downloadedFile=await CloudFiles.DownloadAsync(cloud,receiverStore,"community",sourceId,asset.Id);
                if(downloadedFile is null||await File.ReadAllTextAsync(downloadedFile.LocalPath)!=await File.ReadAllTextAsync(file))throw new Exception("Persistent attachment DTO/hash round trip failed");
                var emojiFile=Path.Combine(mediaRoot,"existing.gif");
                await File.WriteAllBytesAsync(emojiFile,Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7"));
                var emojiAsset=senderStore.ImportAsset("community",emojiFile,"emoji");
                await CommunityAssets.UploadEmojiAsync(backends[0],emojiAsset);
                await CommunityAssets.UploadEmojiAsync(cloud,emojiAsset);
                var emojiMessage=new ChatMessage("member-emoji","text",cloud.Profile!.Id,cloud.Profile.Username,"hello :fixture:",DateTimeOffset.UtcNow,Emojis:new(){[":fixture:"]=emojiAsset.Id});
                await cloud.SendMessageAsync("community",emojiMessage);
                if(!(await backends[0].MessagesAsync("community","text")).Any(message=>message.Id==emojiMessage.Id&&message.Emojis?.GetValueOrDefault(":fixture:")==emojiAsset.Id))throw new Exception("Ordinary member cannot send existing server emoji");
                var otherEmojiFile=Path.Combine(mediaRoot,"new.gif");await File.WriteAllBytesAsync(otherEmojiFile,(await File.ReadAllBytesAsync(emojiFile)).Concat(new byte[]{0}).ToArray());
                var another=senderStore.ImportAsset("community",otherEmojiFile,"emoji");
                try{await CommunityAssets.UploadEmojiAsync(cloud,another);throw new Exception("Member created a new server emoji without management rights");}
                catch(ServerRequestException denied)when(denied.Status==System.Net.HttpStatusCode.Forbidden){}
                Console.WriteLine("PASS: owner uploads emoji; ordinary member reuses helper and sends it; new uploads stay manager-only");

            }
            finally{Directory.Delete(mediaRoot,true);}
            Console.WriteLine("PASS: real account/profile/chat/attachment API DTOs and PostgreSQL persistence");
            await cloud.CreateCommunityAsync(new(new Community("reverse","Reverse invite",cloud.Profile!.Id),[new("reverse-voice","reverse","Voice",true)],[cloud.Profile]));
            string reverseInvite=await cloud.CreateInviteAsync("reverse");
            await backends[0].JoinCommunityAsync(" \r\n"+reverseInvite+" \r\n");
            if(!(await backends[0].CommunitiesAsync()).Any(s=>s.Community.Id=="reverse"))throw new Exception("Reverse invitation missing community");
            Console.WriteLine("PASS: new account joins existing community and reverse invitation also works");
        }
        int owner=i;
        var room=new SfuRoom(cloud,settings,(remote,channel,servers)=>
        {
            var peer=new RtcPeer(remote,channel,servers);observed.Add((owner,peer));
            return owner==5?new SlowAnswerPeer(peer,()=>{if(remote==sourceId)delayedSource?.TrySetResult();}):peer;
        });
        clients.Add(room); room.Error += error => Console.WriteLine("ROOM " + i + ": " + error);
        var joining=room.EnterAsync("community","voice",false,false);
        if(await Task.WhenAny(joining,Task.Delay(10000))!=joining)
        {
            foreach(var p in observed)
            {
                const System.Reflection.BindingFlags flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
                var pc=(SIPSorcery.Net.RTCPeerConnection)typeof(RtcPeer).GetField("pc",flags)!.GetValue(p.Peer)!;
                var c=typeof(RtcPeer).GetField("control",flags)!.GetValue(p.Peer) as SIPSorcery.Net.RTCDataChannel;
                var e=typeof(RtcPeer).GetField("events",flags)!.GetValue(p.Peer) as SIPSorcery.Net.RTCDataChannel;
                Console.WriteLine($"PENDING owner={p.Owner} pc={pc.connectionState} ice={pc.iceConnectionState} control={c?.readyState} events={e?.readyState}");
            }
        }
        await joining;
        Console.WriteLine("Joined " + i);
    }
    await Until(() => clients.All(c => c.ConnectedCount == 5), "all 30 downlinks connected");
    await Until(() => clients.All(c => c.ConnectionInfo.PingMilliseconds is >= 0), "pinned HTTPS health RTT for every joined client");
    // Force HTTP token rotation while the active WSS still uses the old token.
    typeof(ServerBackend).GetField("expires",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.SetValue(backends[0],default(DateTimeOffset));
    await backends[0].GetAccessTokenAsync();
    await Task.Delay(22000);
    if(clients.Any(c=>!c.Joined))throw new Exception("Token refresh disconnected an active voice session");
    Console.WriteLine("PASS: rotating HTTP token preserves voice across periodic SFU authorization");
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
    foreach (var m in received.Values.Where(m => m.Type != "video-report")) if (m.SenderId != sourceId || m.ChannelId != "voice") throw new Exception("Forged identity accepted");
    if (!received["audio"].Payload.SequenceEqual(new byte[]{1,2,3}) || !received["screen-audio"].Payload.SequenceEqual(new byte[]{4,5,6}) || !received["asset"].Payload.SequenceEqual(bulk) || !received["screen-h264"].Payload.AsSpan().EndsWith(h264.AsSpan(4))) throw new Exception("Media payload changed");
    Console.WriteLine("PASS: six users, voice + independent share audio + H264 + events + fragmented control; bound source identity");
    var viewerPresence=new StreamViewerPresence();viewerPresence.Start("view-session");
    var viewCues=new System.Collections.Concurrent.ConcurrentQueue<SoundEvent>();int viewEvents=0;
    void ObserveView(WireMessage message)
    {
        if(message.Type!="screen-state")return;
        using var payload=JsonDocument.Parse(message.Payload);var state=payload.RootElement;
        if(state.GetProperty("TargetId").GetString()!=sourceId)return;
        lock(viewerPresence)
        {
            var cue=viewerPresence.Observe(state.GetProperty("SessionId").GetString()!,message.SenderId,state.GetProperty("Viewing").GetBoolean(),state.GetProperty("Restoring").GetBoolean());
            if(cue is{} sound)viewCues.Enqueue(sound);
        }
        Interlocked.Increment(ref viewEvents);
    }
    a.MessageReceived+=ObserveView;
    try
    {
        async Task View(SfuRoom viewer,bool watching,bool restoring=false)
        {
            int next=viewEvents+1;
            await viewer.SendAsync(new("screen-state","forged","wrong",JsonSerializer.SerializeToUtf8Bytes(new{TargetId=sourceId,SessionId="view-session",Viewing=watching,Restoring=restoring})));
            await Until(()=>viewEvents>=next,"viewer event arrival at broadcaster");
        }
        await View(b,true);await View(b,true);await View(clients[2],true);
        await View(b,false);await View(b,false);await View(clients[2],true,true);
        if(!viewCues.SequenceEqual(new[]{SoundEvent.VoiceJoined,SoundEvent.VoiceJoined,SoundEvent.VoiceLeft})||clients.Any(client=>!client.Joined))throw new Exception("Viewer presence duplicated a cue or stopped another media session");
        int voiceBeforeViewExit=voice;await a.SendAsync(new("audio",sourceId,"voice",new byte[]{1,2,3}));
        await Until(()=>voice>voiceBeforeViewExit,"voice remains connected after viewer leaves viewing");
        Console.WriteLine("PASS: two viewers, explicit view join/leave, duplicate/recovery deduplication, voice survives viewer exit");
    }
    finally{a.MessageReceived-=ObserveView;}
    // A 2MB file must not starve microphone packets while sharing.
    received.TryRemove("asset", out _); int before = voice;
    var transfer = a.SendAsync(new("asset", "user0", null, new byte[2000000]));
    for (int i = 0; i < 60; i++) { await a.SendAsync(new("audio", "user0", "voice", [1,2,3])); await a.SendAsync(new("screen-h264", "user0", "voice", H264Frame.Pack(60,h264))); await Task.Delay(20); }
    await transfer;
    await Until(() => received.TryGetValue("asset",out var m) && m.Payload.Length == 2000000, "2MB attachment delivery");
    if (voice-before<40) throw new Exception("Voice starved during media/file transfer");
    Console.WriteLine("PASS: voice continues during video and 2MB file transfer");
    await a.SetMutedAsync(true, true);
    await Until(() => b.Participants.Any(s => s.UserId == sourceId && s.Muted && s.Deafened), "mute/deafen presence");
    await clients[5].LeaveAsync();
    await Until(() => clients.Take(5).All(c => c.ConnectedCount==4), "leave removes source without disconnecting others");
    delayedSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
    await clients[5].EnterAsync("community","voice",false,false);
    await delayedSource.Task.WaitAsync(TimeSpan.FromSeconds(10));
    await clients[0].LeaveAsync();
    await Until(() => clients.Skip(1).All(c => c.Joined && c.ConnectedCount == 4), "departure during delayed SDP does not disconnect remaining users");
    await clients[0].EnterAsync("community", "voice", false, false);
    await Until(() => clients.All(c=>c.ConnectedCount==5), "fresh rejoin recreates source links");
    Console.WriteLine("PASS: mute/deafen presence, delayed SDP/ICE, source departure during answer and fresh rejoin");
    await using(var replacement=new SfuRoom(backends[0],settings))
    {
        await replacement.EnterAsync("community","voice",false,false);
        await Until(()=>!clients[0].Joined&&replacement.Joined,"authenticated takeover releases stale session");
        await clients[0].EnterAsync("community","voice",false,false);
        await Until(()=>!replacement.Joined&&clients[0].Joined,"fresh attempt takes account back");
    }
    var socket=(System.Net.WebSockets.ClientWebSocket)typeof(SfuRoom).GetField("socket",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(clients[0])!;
    socket.Abort();await Until(()=>!clients[0].Joined,"abrupt signaling loss");
    await clients[0].EnterAsync("community","voice",false,false);
    await Until(()=>clients.All(c=>c.Joined&&c.ConnectedCount==5),"same instance recovers after abrupt signaling loss");
    Console.WriteLine("PASS: account takeover, asynchronous failed-attempt cleanup and same-instance recovery");
}
catch
{
    foreach(var p in observed)
    {
        const System.Reflection.BindingFlags flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
        var pc=(SIPSorcery.Net.RTCPeerConnection)typeof(RtcPeer).GetField("pc",flags)!.GetValue(p.Peer)!;
        var control=typeof(RtcPeer).GetField("control",flags)!.GetValue(p.Peer) as SIPSorcery.Net.RTCDataChannel;
        var events=typeof(RtcPeer).GetField("events",flags)!.GetValue(p.Peer) as SIPSorcery.Net.RTCDataChannel;
        Console.WriteLine($"STATE owner={p.Owner} connected={p.Peer.Connected} pc={pc.connectionState} ice={pc.iceConnectionState} control={control?.readyState} events={events?.readyState}");
    }
    throw;
}
finally { foreach (var room in clients) await room.DisposeAsync(); foreach (var cloud in backends) cloud.Dispose(); }
static async Task Until(Func<bool> check,string stage) {using var end=new CancellationTokenSource(TimeSpan.FromSeconds(25));try{while(!check())await Task.Delay(25,end.Token);}catch(OperationCanceledException){throw new Exception("Timed out: "+stage);}}
// Force at least one maintenance tick after local SDP exists but before its
// answer is published. Candidates must not overtake SDP on the websocket.
sealed class SlowAnswerPeer(IRtcPeer inner, Action prepared) : IRtcPeer
{
    public bool Connected => inner.Connected;
    public string? SelectedCandidateTypes => inner.SelectedCandidateTypes;
    public IReadOnlyList<IceCandidateData> LocalCandidates => inner.LocalCandidates;
    public event Action<string>? StateChanged { add => inner.StateChanged += value; remove => inner.StateChanged -= value; }
    public event Action<WireMessage>? MessageReceived { add => inner.MessageReceived += value; remove => inner.MessageReceived -= value; }
    public event Action? CandidateReady { add => inner.CandidateReady += value; remove => inner.CandidateReady -= value; }
    public Task<string> OfferAsync(CancellationToken token = default) => inner.OfferAsync(token);
    public async Task<string> AnswerAsync(string offer, CancellationToken token = default) { var answer = await inner.AnswerAsync(offer, token); prepared(); await Task.Delay(1300, token); return answer; }
    public void AcceptAnswer(string answer) => inner.AcceptAnswer(answer);
    public void AddIceCandidate(IceCandidateData candidate) => inner.AddIceCandidate(candidate);
    public void RestartIce() => inner.RestartIce();
    public Task SendAsync(WireMessage m) => inner.SendAsync(m);
    public void Dispose() => inner.Dispose();
}
