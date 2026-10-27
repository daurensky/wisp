using System.Net;
using Wisp.Core;
using Xunit;
namespace Wisp.Core.Tests;
public sealed class SfuHealthTests
{
    private static VoiceServerSettings Settings => new(true, "wss://example.com:8443/rtc", new string('a',64));
    [Theory]
    [InlineData(0,ConnectionQuality.Good)]
    [InlineData(99,ConnectionQuality.Good)]
    [InlineData(100,ConnectionQuality.Fair)]
    [InlineData(199,ConnectionQuality.Fair)]
    [InlineData(200,ConnectionQuality.Poor)]
    public void PingBandsAreExplicit(double latency, ConnectionQuality quality) => Assert.Equal(quality, new SfuConnectionInfo(Settings.Url,ServerConnectionState.Connected,latency).Quality);
    [Fact]
    public async Task MeasuresPinnedServersHealthPathWithoutSendingCredentials()
    {
        using var probe=new SfuHealthProbe(Settings,new Handler((r,_)=>
        {
            Assert.Equal("https://example.com:8443/healthz",r.RequestUri!.AbsoluteUri);
            Assert.Null(r.Headers.Authorization);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("wisp-sfu protocol=2\n")});
        }));
        Assert.True(await probe.MeasureAsync() is >=0);
    }
    [Theory]
    [InlineData("unrelated web server")]
    [InlineData("wisp-sfu protocol=3")]
    public async Task RejectsUnrelatedOrIncompatibleHealthResponses(string body)
    {
        using var probe=new SfuHealthProbe(Settings,new Handler((_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(body)})));
        Assert.Null(await probe.MeasureAsync());
    }
    [Fact]
    public async Task FailedHealthProbeDoesNotKeepAnOldLatencyAndCancellationPropagates()
    {
        using var probe=new SfuHealthProbe(Settings,new Handler((_,_)=>throw new HttpRequestException("offline")));
        Assert.Null(await probe.MeasureAsync());
        var stale=new SfuConnectionInfo(Settings.Url,ServerConnectionState.Unreachable);
        Assert.Null(stale.PingMilliseconds);Assert.Equal(ConnectionQuality.Fair,stale.Quality);
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        using var wait=new SfuHealthProbe(Settings,new Handler(async (_,token)=>{await Task.Delay(10000,token);return new HttpResponseMessage(HttpStatusCode.OK);}));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>wait.MeasureAsync(cancelled.Token));
    }
    private sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken cancellation)=>send(r,cancellation);
    }
}
