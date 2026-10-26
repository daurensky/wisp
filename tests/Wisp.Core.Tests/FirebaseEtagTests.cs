using System.Net;
using System.Text;
using Wisp.Core;
using Xunit;

namespace Wisp.Core.Tests;

public sealed class FirebaseEtagTests
{
    [Theory]
    [InlineData("null_etag", true)]
    [InlineData("firebaseHash+/==", false)]
    [InlineData("\"firebaseHash+/==\"", false)]
    public async Task VersionedWritesPreserveFirebaseEtagExactlyIncludingUnquotedValues(string etag, bool empty)
    {
        var handler = new EtagHandler([etag], empty);
        using var cloud = new FirebaseBackend(new("public-api-key", "test-project", "https://test-project.firebaseio.com"), handler);
        await cloud.SignInAsync("alice@example.com", "password", false, "Alice");
        var version = await cloud.GetVersionedAsync<RtcSeat>("communities/room/rtcRooms/voice/s0");
        Assert.Equal(etag, version.ETag);
        Assert.Equal(empty, version.Value is null);
        Assert.True(await cloud.TryPutVersionedAsync("communities/room/rtcRooms/voice/s0", new RtcSeat("alice", new string('a', 32), 123), version.ETag));
        Assert.Equal(etag, handler.IfMatch);
        Assert.False(await cloud.TryPutVersionedAsync("communities/room/rtcRooms/voice/s0", new RtcSeat("alice", new string('a', 32), 124), version.ETag));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MissingOrEmptyEtagNeverFallsBackToAnUnconditionalWrite(string? etag)
    {
        var handler = new EtagHandler(etag is null ? [] : [etag], true);
        using var cloud = new FirebaseBackend(new("public-api-key", "test-project", "https://test-project.firebaseio.com"), handler);
        await cloud.SignInAsync("alice@example.com", "password", false, "Alice");
        await Assert.ThrowsAsync<InvalidDataException>(() => cloud.GetVersionedAsync<RtcSeat>("communities/room/rtcRooms/voice/s0"));
        Assert.Null(handler.IfMatch);
    }

    private sealed class EtagHandler(string[] etags, bool empty) : HttpMessageHandler
    {
        public string? IfMatch;
        private int puts;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = "null";
            if (request.RequestUri!.Host.Contains("identitytoolkit"))
                body = """{"idToken":"fake-id","refreshToken":"fake-refresh","localId":"alice"}""";
            else if (request.RequestUri.AbsolutePath == "/profiles/alice/Info.json")
                body = """{"Id":"alice","Username":"Alice"}""";
            bool versioned = request.Headers.TryGetValues("X-Firebase-ETag", out var requested);
            if (versioned)
            {
                Assert.Equal("true", Assert.Single(requested!));
                if (!empty) body = """{"UserId":"alice","SessionId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","SeenAt":123}""";
            }
            if (request.Method == HttpMethod.Put)
            {
                IfMatch = Assert.Single(request.Headers.NonValidated["if-match"]);
                if (++puts > 1) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PreconditionFailed) { Content = new StringContent("null") });
            }
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (versioned) response.Headers.TryAddWithoutValidation("ETag", etags);
            return Task.FromResult(response);
        }
    }
}
