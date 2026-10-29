using System.Net;
using System.Text.Json;
using Wisp.Core;
using Xunit;
namespace Wisp.Core.Tests;
public sealed class ServerBackendTests
{
    private static ServerConfig Config=>new("https://example.com:8443",new string('a',64));
    [Fact]
    public async Task UsesOnlyPinnedServerRoutesAndBearerHeadersForData()
    {
        var paths=new List<string>();
        using var backend=new ServerBackend(Config,new Handler(request=>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            Assert.Equal("example.com",request.RequestUri.Host);Assert.Equal("",request.RequestUri.Query);
            if(request.RequestUri.AbsolutePath=="/v1/auth/register")return Reply(new{accessToken=new string('a',43),refreshToken=new string('b',43),expiresIn=900,profile=new UserProfile("user","tester")});
            Assert.Equal("Bearer",request.Headers.Authorization!.Scheme);Assert.Equal(new string('a',43),request.Headers.Authorization.Parameter);
            return Reply(new Dictionary<string,bool>{{"community",true}});
        }));
        string? savedRefresh=null;backend.SessionChanged+=token=>savedRefresh=token;
        await backend.SignInAsync("tester@example.com","fixture-password",true,"tester");
        Assert.Equal(new string('b',43),savedRefresh);
        var communities=await backend.GetAsync<Dictionary<string,bool>>("users/user/communities");
        Assert.True(communities!["community"]);Assert.Equal(new[]{"/v1/auth/register","/v1/data/users/user/communities"},paths);
        await Assert.ThrowsAsync<ArgumentException>(()=>backend.GetAsync<object>("../private"));
    }
    [Fact]
    public async Task CopiedInvitesAllowWrappedWhitespaceAndExplainExpiredCodes()
    {
        int calls=0;
        using var backend=new ServerBackend(Config,new Handler(request=>
        {
            calls++;
            if(request.RequestUri!.AbsolutePath=="/v1/auth/register")return Reply(new{accessToken=new string('a',43),refreshToken=new string('b',43),expiresIn=900,profile=new UserProfile("user","tester")});
            Assert.Equal("/v1/invites/accept",request.RequestUri.AbsolutePath);
            using var json=JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Equal(new string('c',43),json.RootElement.GetProperty("token").GetString());
            return new HttpResponseMessage(HttpStatusCode.NotFound){Content=new StringContent("{}")};
        }));
        await backend.SignInAsync("tester@example.com","fixture-password",true,"tester");
        var error=await Assert.ThrowsAsync<ServerRequestException>(()=>backend.JoinCommunityAsync("  WISP-INVITE:"+new string('c',20)+"\r\n"+new string('c',23)+"  "));
        Assert.Contains("Приглашение не найдено",error.Message);
        await Assert.ThrowsAsync<ArgumentException>(()=>backend.JoinCommunityAsync("wisp://old-invitation"));
        Assert.Equal(2,calls);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancellingOneRequestDoesNotCancelTheSharedSessionRefresh(bool cancelRefreshStarter)
    {
        var clock=new Clock();
        var refreshStarted=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRefresh=new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        int refreshes=0,dataRequests=0;
        CancellationToken refreshCancellation=default;
        using var backend=new ServerBackend(Config,new Handler(async (request,token)=>
        {
            if(request.RequestUri!.AbsolutePath=="/v1/auth/login")return Session('a','b');
            if(request.RequestUri.AbsolutePath=="/v1/auth/refresh")
            {
                Interlocked.Increment(ref refreshes);refreshCancellation=token;refreshStarted.TrySetResult();
                return await releaseRefresh.Task.WaitAsync(token);
            }
            Interlocked.Increment(ref dataRequests);
            Assert.Equal(new string('c',43),request.Headers.Authorization!.Parameter);
            return Reply(7);
        }),clock);
        await backend.SignInAsync("tester@example.com","fixture-password",false,"tester");
        clock.Advance(TimeSpan.FromMinutes(2));
        using var firstCancellation=new CancellationTokenSource();
        using var secondCancellation=new CancellationTokenSource();
        var first=backend.GetAsync<int>("users/user/first",firstCancellation.Token);
        await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second=backend.GetAsync<int>("users/user/second",secondCancellation.Token);
        try
        {
            (cancelRefreshStarter?firstCancellation:secondCancellation).Cancel();
            var cancelled=cancelRefreshStarter?first:second;
            var remaining=cancelRefreshStarter?second:first;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>cancelled.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(refreshCancellation.IsCancellationRequested);
            Assert.False(remaining.IsCompleted);
            releaseRefresh.SetResult(Session('c','d'));
            Assert.Equal(7,await remaining.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(1,refreshes);Assert.Equal(1,dataRequests);
            Assert.Equal(new string('d',43),backend.RefreshToken);
        }
        finally
        {
            releaseRefresh.TrySetResult(Session('c','d'));
            try { await Task.WhenAll(first,second); } catch(OperationCanceledException) { }
        }
    }
    [Fact]
    public async Task PrecancelledRequestsDoNotStartSessionRefresh()
    {
        var clock=new Clock();int calls=0;
        using var backend=new ServerBackend(Config,new Handler(request=>
        {
            Interlocked.Increment(ref calls);return Session('a','b');
        }),clock);
        await backend.SignInAsync("tester@example.com","fixture-password",false,"tester");
        clock.Advance(TimeSpan.FromMinutes(2));
        using var cancellation=new CancellationTokenSource();cancellation.Cancel();
        Func<Task>[] requests=[()=>backend.GetAccessTokenAsync(cancellation.Token),
            ()=>backend.GetAsync<object>("users/user/data",cancellation.Token),
            ()=>backend.PutAsync("users/user/data",7,cancellation.Token),
            ()=>backend.DeleteAsync("users/user/data",cancellation.Token)];
        foreach(var request in requests)await Assert.ThrowsAnyAsync<OperationCanceledException>(request);
        Assert.Equal(1,calls);
    }
    [Fact]
    public void CachesAndSessionsAreIsolatedByServerAndOldDataIsNotDeleted()
    {
        var a=Config.DataDirectory("root");var b=(Config with{Url="https://other.example.com:8443"}).DataDirectory("root");
        Assert.NotEqual(a,b);Assert.Contains("servers",a);Assert.NotEqual("root",a);
        Assert.Equal("wss://example.com:8443/rtc",Config.VoiceSettings().Endpoint().AbsoluteUri);
    }
    [Fact]
    public void DynamicRosterSupportsMoreThanFiveParticipants()
    {
        long now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var seats=Enumerable.Range(0,12).ToDictionary(i=>"user"+i,i=>new RtcSeat("user"+i,new string('a',32),now));
        Assert.Equal(12,RoomRoster.ActiveParticipants(JsonSerializer.SerializeToElement(seats),DateTimeOffset.FromUnixTimeMilliseconds(now)).Count);
    }
    private static HttpResponseMessage Reply(object data)=>new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(data))};
    private static HttpResponseMessage Session(char access,char refresh)=>Reply(new{accessToken=new string(access,43),refreshToken=new string(refresh,43),expiresIn=180,profile=new UserProfile("user","tester")});
    private sealed class Handler:HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send;
        public Handler(Func<HttpRequestMessage,HttpResponseMessage> send):this((request,_)=>Task.FromResult(send(request))) { }
        public Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send)=>this.send=send;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>send(request,token);
    }
    private sealed class Clock:TimeProvider
    {
        private DateTimeOffset now=DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow()=>now;
        public void Advance(TimeSpan elapsed)=>now+=elapsed;
    }
}
