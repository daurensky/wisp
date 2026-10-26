using System.Net;
using System.Text;
using System.Text.Json;
using Wisp.Core;
using Xunit;

namespace Wisp.Core.Tests;

public sealed class CloudFilesTests
{
    [Fact]
    public async Task ChunkedFilesRoundTripAndCorruptTransfersDoNotReachTheCache()
    {
        var root = Path.Combine(Path.GetTempPath(), "wisp-files-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var sender = new LocalStore(Path.Combine(root, "sender"));
            var user = sender.Register("Alice", "password123"); var community = sender.CreateCommunity("Friends", user.Id);
            var bytes = new byte[600000]; Random.Shared.NextBytes(bytes); var file = Path.Combine(root, "test.bin"); await File.WriteAllBytesAsync(file, bytes);
            var asset = sender.ImportAsset(community.Id, file, "attachment");
            var server = new MemoryServer(user.Id);
            using var cloud = new FirebaseBackend(new("public-key", "test", "https://test.firebaseio.com"), server);
            await cloud.SignInAsync("alice@example.com", "password123", true, "Alice");
            await CloudFiles.UploadAsync(cloud, asset);
            await CloudFiles.UploadAsync(cloud, asset); // Immutable content can be reused without rewriting chunks.
            Assert.All(server.WrittenPaths, path => Assert.StartsWith($"communities/{community.Id}/files/", path));
            using var receiver = new LocalStore(Path.Combine(root, "receiver")); receiver.SaveSnapshot(new(community, sender.GetChannels(community.Id), new() { user }));
            server.Corrupt = true;
            await Assert.ThrowsAsync<InvalidDataException>(() => CloudFiles.DownloadAsync(cloud, receiver, community.Id, user.Id, asset.Id));
            Assert.Null(receiver.GetAsset(asset.Id));
            server.Corrupt = false;
            var downloaded = await CloudFiles.DownloadAsync(cloud, receiver, community.Id, user.Id, asset.Id);
            Assert.NotNull(downloaded); Assert.Equal("test.bin", downloaded.Name); Assert.Equal(bytes, await File.ReadAllBytesAsync(downloaded.LocalPath));
            Assert.Equal(2, server.ChunkWrites);
            server.MoveToLegacy($"communities/{community.Id}/files/{asset.Id}", $"profiles/{user.Id}/Files/{asset.Id}");
            Assert.NotNull(await CloudFiles.DownloadAsync(cloud, receiver, community.Id, user.Id, asset.Id));
            server.ReadPaths.Clear();
            Assert.Null(await CloudFiles.DownloadAsync(cloud, receiver, community.Id, "another-user", asset.Id));
            Assert.DoesNotContain(server.ReadPaths, path => path.StartsWith("profiles/another-user/Files/"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private sealed class MemoryServer(string uid) : HttpMessageHandler
    {
        private readonly Dictionary<string,string> records = new();
        public bool Corrupt;
        public int ChunkWrites;
        public List<string> WrittenPaths { get; } = new();
        public List<string> ReadPaths { get; } = new();
        public void MoveToLegacy(string from, string to)
        {
            foreach (var key in records.Keys.Where(key => key.StartsWith(from + "/")).ToList())
            {
                records[to + key[from.Length..]] = records[key];
                records.Remove(key);
            }
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string data = "null"; var uri = request.RequestUri!;
            if (uri.Host == "identitytoolkit.googleapis.com") data = JsonSerializer.Serialize(new { idToken = "token", refreshToken = "refresh", localId = uid });
            else
            {
                var path = uri.AbsolutePath.TrimStart('/')[..^5];
                if (request.Method == HttpMethod.Put)
                {
                    if (request.Headers.Contains("if-match") && records.ContainsKey(path))
                        return new(HttpStatusCode.PreconditionFailed) { Content = new StringContent("null") };
                    records[path] = await request.Content!.ReadAsStringAsync(cancellationToken);
                    WrittenPaths.Add(path);
                    if (path.Contains("/Chunks/")) ChunkWrites++;
                }
                else if (request.Method == HttpMethod.Patch)
                {
                    using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                    foreach (var p in json.RootElement.EnumerateObject()) records[path + "/" + p.Name] = p.Value.GetRawText();
                }
                else if (request.Method == HttpMethod.Get)
                {
                    ReadPaths.Add(path);
                    data = records.GetValueOrDefault(path, "null");
                    if (Corrupt && path.Contains("/Chunks/")) { var bytes = JsonSerializer.Deserialize<byte[]>(data)!; bytes[0] ^= 1; data = JsonSerializer.Serialize(bytes); }
                }
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(data, Encoding.UTF8, "application/json") };
        }
    }
}
