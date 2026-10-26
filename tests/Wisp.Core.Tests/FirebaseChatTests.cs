using System.Net;
using System.Text;
using System.Text.Json;
using Wisp.Core;
using Xunit;

namespace Wisp.Core.Tests;

public sealed class FirebaseChatTests
{
    [Fact]
    public async Task CloudChatUsesBoundedSlotsAndReadsLegacyTokenMaps()
    {
        var handler = new ChatServer();
        using var cloud = new FirebaseBackend(new("public-key", "test", "https://test.firebaseio.com"), handler);
        var user = await cloud.SignInAsync("alice@example.com", "password123", true, "Alice");
        var message = new ChatMessage("message", "room", user.Id, user.Username, "hello :wave:", DateTimeOffset.UtcNow,
            Emojis: new() { [":wave:"] = new string('a', 64) });
        await cloud.SendMessageAsync("community", message);
        using (var json = JsonDocument.Parse(handler.Message!))
        {
            var slot = json.RootElement.GetProperty("Emojis").GetProperty("e00");
            Assert.Equal(":wave:", slot.GetProperty("Token").GetString());
        }
        Assert.Equal(new string('a', 64), Assert.Single(await cloud.MessagesAsync("community", "room")).Emojis![":wave:"]);
        handler.Message = JsonSerializer.Serialize(message);
        Assert.Equal(new string('a', 64), Assert.Single(await cloud.MessagesAsync("community", "room")).Emojis![":wave:"]);
        var many = Enumerable.Range(0, 33).ToDictionary(i => $":emoji{i}:", _ => new string('a', 64));
        await Assert.ThrowsAsync<InvalidDataException>(() => cloud.SendMessageAsync("community", message with { Emojis = many }));
    }

    private sealed class ChatServer : HttpMessageHandler
    {
        public string? Message;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = "null";
            if (request.RequestUri!.Host == "identitytoolkit.googleapis.com")
                body = """{"idToken":"token","refreshToken":"refresh","localId":"alice"}""";
            else if (request.RequestUri.AbsolutePath.Contains("/messages/"))
            {
                if (request.Method == HttpMethod.Put)
                    Message = await request.Content!.ReadAsStringAsync(cancellationToken);
                else if (Message is not null)
                    body = "{\"message\":" + Message + "}";
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
