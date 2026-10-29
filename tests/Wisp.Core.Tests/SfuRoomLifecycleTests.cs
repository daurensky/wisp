using System.Net;
using System.Reflection;
using System.Text.Json;
using Wisp.Core;
using Xunit;

namespace Wisp.Core.Tests;

public sealed class SfuRoomLifecycleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LeaveReleasesEveryPeerAndStateAfterWorkerOrPeerFailure(bool peerDisposalFails)
    {
        using var backend=new ServerBackend(new("https://example.com:8443",new string('a',64)),new LoginHandler());
        await backend.SignInAsync("tester@example.com","fixture-password",false,"tester");
        var first=new Peer(peerDisposalFails);var second=new Peer(false);
        var peers=new Queue<Peer>([first,second]);
        await using var room=new SfuRoom(backend,new(true,"wss://example.com:8443/rtc",new string('a',64)),(_,_,_)=>peers.Dequeue());
        var stop=new CancellationTokenSource();
        // Seed an admitted attempt through the existing peer factory without a live SFU/native transport.
        Set(room,"stop",stop);Set(room,"channel","voice");Set(room,"community","community");Set(room,"joined",true);
        Set(room,"participants",new[]{new RtcSeat("peer",new string('s',32),0)});
        Set(room,"reader",Task.FromException(new InvalidOperationException("Worker failed")));
        var create=typeof(SfuRoom).GetMethod("CreateLink",BindingFlags.Instance|BindingFlags.NonPublic)!;
        create.Invoke(room,["pub","user"]);create.Invoke(room,["s_peer","peer"]);
        Assert.Equal(2,first.SubscriberCount);Assert.Equal(2,second.SubscriberCount);
        var diagnostics=new List<Exception>();room.DiagnosticError+=diagnostics.Add;

        await room.LeaveAsync();

        Assert.Equal(1,first.Disposals);Assert.Equal(1,second.Disposals);
        Assert.Equal(0,first.SubscriberCount);Assert.Equal(0,second.SubscriberCount);
        Assert.False(room.Joined);Assert.Null(room.ChannelId);Assert.Empty(room.Participants);
        Assert.Equal(ServerConnectionState.Disconnected,room.ConnectionInfo.State);
        Assert.Throws<ObjectDisposedException>(()=>stop.Token);
        var diagnostic=Assert.IsType<SfuDiagnosticException>(Assert.Single(diagnostics));
        Assert.Contains("Worker failed",diagnostic.OriginalMessage);
        if(peerDisposalFails)Assert.Contains("Peer disposal failed",diagnostic.OriginalMessage);
        await room.LeaveAsync();
        Assert.Single(diagnostics);Assert.Equal(1,first.Disposals);Assert.Equal(1,second.Disposals);
    }
    [Fact]
    public async Task LeaveDrainsPeerWhoseRegistrationFinishesDuringCancellation()
    {
        using var backend=new ServerBackend(new("https://example.com:8443",new string('a',64)),new LoginHandler());
        await backend.SignInAsync("tester@example.com","fixture-password",false,"tester");
        var subscribing=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peer=new Peer(false){OnMessageSubscribe=()=>{subscribing.TrySetResult();release.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();}};
        await using var room=new SfuRoom(backend,new(true,"wss://example.com:8443/rtc",new string('a',64)),(_,_,_)=>peer);
        Set(room,"stop",new CancellationTokenSource());Set(room,"channel","voice");
        var create=typeof(SfuRoom).GetMethod("CreateLink",BindingFlags.Instance|BindingFlags.NonPublic)!;
        Set(room,"reader",Task.Run(()=>create.Invoke(room,["pub","user"])));
        try
        {
            await subscribing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var leaving=room.LeaveAsync();
            Assert.False(leaving.IsCompleted);
            release.SetResult();
            await leaving.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1,peer.Disposals);Assert.Equal(0,peer.SubscriberCount);
        }
        finally { release.TrySetResult(); }
    }

    private static void Set(SfuRoom room,string name,object value)=>typeof(SfuRoom).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(room,value);
    private sealed class LoginHandler:HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellation)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content=new StringContent(JsonSerializer.Serialize(new{accessToken=new string('a',43),refreshToken=new string('b',43),expiresIn=900,profile=new UserProfile("user","tester")}))
        });
    }
    private sealed class Peer(bool disposalFails):IRtcPeer
    {
        private Action<string>? stateChanged;
        private Action<WireMessage>? messageReceived;
        public Action? OnMessageSubscribe {get;init;}
        public int Disposals {get;private set;}
        public int SubscriberCount=>(stateChanged?.GetInvocationList().Length??0)+(messageReceived?.GetInvocationList().Length??0);
        public bool Connected=>true;
        public string? SelectedCandidateTypes=>null;
        public IReadOnlyList<IceCandidateData> LocalCandidates=>[];
        public event Action<string>? StateChanged {add=>stateChanged+=value;remove=>stateChanged-=value;}
        public event Action<WireMessage>? MessageReceived {add{OnMessageSubscribe?.Invoke();messageReceived+=value;}remove=>messageReceived-=value;}
        public event Action? CandidateReady {add{}remove{}}
        public Task<string> OfferAsync(CancellationToken cancellation=default)=>Task.FromResult("");
        public Task<string> AnswerAsync(string offer,CancellationToken cancellation=default)=>Task.FromResult("");
        public void AcceptAnswer(string answer) { }
        public void AddIceCandidate(IceCandidateData candidate) { }
        public void RestartIce() { }
        public Task SendAsync(WireMessage message)=>Task.CompletedTask;
        public void Dispose(){Disposals++;if(disposalFails)throw new InvalidOperationException("Peer disposal failed");}
    }
}
