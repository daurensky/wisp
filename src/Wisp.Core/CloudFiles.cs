using System.Security.Cryptography;
using System.Text;

namespace Wisp.Core;

public sealed record FileManifest(string Name, string Kind, long Size, int Chunks);

/// <summary>Bounded RTDB chunks; metadata is published last so chat never points to a partial upload.</summary>
public static class CloudFiles
{
    public const int ChunkBytes = 512 * 1024;
    public static async Task UploadAsync(FirebaseBackend cloud, MediaAsset asset)
    {
        if (asset.Size > LocalStore.MaxAssetBytes) throw new InvalidDataException("Файл больше 16 МБ.");
        var path = $"profiles/{cloud.Profile!.Id}/Files/{asset.Id}";
        await using var stream = File.OpenRead(asset.LocalPath); var buffer = new byte[ChunkBytes]; int index = 0;
        while (stream.Position < stream.Length)
        {
            var count = (int)Math.Min(buffer.Length, stream.Length - stream.Position);
            await stream.ReadExactlyAsync(buffer.AsMemory(0, count));
            await cloud.PutAsync($"{path}/Chunks/c{index++:D2}", buffer.AsSpan(0, count).ToArray());
        }
        await cloud.PutAsync($"{path}/Info", new FileManifest(asset.Name, asset.Kind, asset.Size, index));
    }
    public static async Task<MediaAsset?> DownloadAsync(FirebaseBackend cloud, LocalStore store, string community, string author, string id)
    {
        var path = $"profiles/{author}/Files/{id}"; var meta = await cloud.GetAsync<FileManifest>($"{path}/Info");
        if (meta is null) return null;
        if (meta.Size < 0 || meta.Size > LocalStore.MaxAssetBytes || meta.Chunks != (meta.Size + ChunkBytes - 1) / ChunkBytes || meta.Kind is not ("attachment" or "voice") || meta.Kind == "voice" && meta.Size > LocalStore.MaxVoiceBytes || string.IsNullOrWhiteSpace(meta.Name) || meta.Name.Length > 200 || meta.Name != Path.GetFileName(meta.Name) || meta.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || meta.Name is "." or "..") throw new InvalidDataException("Некорректное вложение.");
        var dir = Path.Combine(store.RootDirectory, "incoming", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir); var file = Path.Combine(dir, "payload.tmp");
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); hash.AppendData(Encoding.UTF8.GetBytes(community + "\0" + meta.Kind + "\0"));
            await using (var target = File.Create(file))
            {
                for (int i = 0; i < meta.Chunks; i++)
                {
                    var bytes = await cloud.GetAsync<byte[]>($"{path}/Chunks/c{i:D2}") ?? throw new InvalidDataException("Часть файла отсутствует.");
                    if (bytes.Length != Math.Min(ChunkBytes, meta.Size - target.Position)) throw new InvalidDataException("Неверный размер части файла.");
                    hash.AppendData(bytes); await target.WriteAsync(bytes);
                }
            }
            if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(id, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Файл повреждён: хеш не совпадает.");
            return store.ImportAsset(community, file, meta.Kind, meta.Name);
        }
        finally { Directory.Delete(dir, true); }
    }
}
