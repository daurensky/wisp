using System.Net;
using System.Text;
using System.Text.Json;
using Wisp.Core;
using Xunit;

namespace Wisp.Core.Tests;

public sealed class FirebaseBackendTests
{
    [Fact]
    public async Task AuthenticationUsesFirebaseIdentityAndWritesReadableProfileWithoutLocalPaths()
    {
        var handler = new Stub();
        using var backend = new FirebaseBackend(new("public-api-key", "test-project", "https://test-project.firebaseio.com"), handler);
        var profile = await backend.SignInAsync("user@example.com", "long-password", true, "Alice");
        Assert.Equal("firebase-uid", profile.Id);
        Assert.Equal("refresh-value", backend.RefreshToken);
        Assert.Equal(4, handler.Calls.Count);
        Assert.Contains("accounts:signUp", handler.Calls[0].Uri);
        var saved = JsonDocument.Parse(handler.Calls[3].Body).RootElement.GetProperty("Info").Deserialize<UserProfile>()!;
        Assert.Equal("Alice", saved.Username);
        await backend.SaveProfileAsync(profile with { AvatarPath = "C:/private/avatar.png" });
        Assert.DoesNotContain("C:/private", handler.Calls[^1].Body);
        await Assert.ThrowsAsync<ArgumentException>(() => backend.GetAsync<string>("../outside"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => backend.SaveProfileAsync(profile with { Id = "someone-else" }));
    }
    [Fact]
    public void NonFirebaseDatabaseEndpointsAreRejected() => Assert.Throws<ArgumentException>(() => new FirebaseBackend(new("api-key", "test", "https://example.com")));

    private sealed class Stub : HttpMessageHandler
    {
        public List<(string Uri, string Body)> Calls { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add((request.RequestUri!.ToString(), request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            var body = Calls.Count == 1 ? """{"idToken":"id-value","refreshToken":"refresh-value","localId":"firebase-uid"}""" : "null";
            return new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
