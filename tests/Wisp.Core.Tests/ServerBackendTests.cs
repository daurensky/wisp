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
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> send):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Task.FromResult(send(request));
    }
}
