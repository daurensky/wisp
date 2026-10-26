using Wisp.Core;
using Xunit;

namespace Wisp.Core.Tests;

public sealed class IceServerProviderTests
{
    [Theory]
    [InlineData("turn:example.org:3478")]
    [InlineData("turns:example.org:443")]
    [InlineData("stun:example.org:0")]
    [InlineData("stun:example.org:65536")]
    [InlineData("stun:example.org:3478?transport=tcp")]
    [InlineData("stun:example.org:3478;username;password")]
    [InlineData("https://example.org")]
    public void UnsupportedOrCredentialBearingServersAreRejected(string url)
    {
        Assert.Throws<ArgumentException>(() => FileIceServerProvider.Validate(new[] { url }));
    }

    [Fact]
    public async Task SettingsPersistAndInvalidUpdatesDoNotReplaceTheSavedConfiguration()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wisp-ice-" + Guid.NewGuid().ToString("N"));
        try
        {
            var provider = new FileIceServerProvider(directory);
            Assert.Equal(2, (await provider.GetAsync()).Count);
            provider.Save(new[] { " stun:example.org:3478 ", "stun:example.org:3478" });
            Assert.Equal("stun:example.org:3478", Assert.Single(await provider.GetAsync()).urls);
            var saved = File.ReadAllText(Path.Combine(directory, "network.json"));
            Assert.Throws<ArgumentException>(() => provider.Save(new[] { "turn:example.org" }));
            Assert.Equal(saved, File.ReadAllText(Path.Combine(directory, "network.json")));
            provider.Save(Array.Empty<string>());
            Assert.Empty(await provider.GetAsync());
            await Assert.ThrowsAsync<OperationCanceledException>(() => provider.GetAsync(new CancellationToken(true)));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
