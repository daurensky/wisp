using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Wisp.Core;

public sealed record FileManifest(string Name, string Kind, long Size, int Chunks);

/// <summary>Community-scoped, immutable RTDB chunks. Publish metadata only after a complete upload.</summary>
public static class CloudFiles
{
    public const int ChunkBytes = 512 * 1024;
    public const int MaximumChunks = 32;

    public static async Task UploadAsync(FirebaseBackend cloud, MediaAsset asset)
    {
        ValidateId(asset.Id);
        var manifest = new FileManifest(asset.Name, asset.Kind, asset.Size,
            checked((int)((asset.Size + ChunkBytes - 1) / ChunkBytes)));
        ValidateManifest(manifest);
        var path = $"communities/{asset.CommunityId}/files/{asset.Id}";
        var published = await cloud.GetAsync<FileManifest>($"{path}/Info");
        if (published is not null)
        {
            RequireSameContent(published, manifest);
            return;
        }

        await using var stream = File.OpenRead(asset.LocalPath);
        if (stream.Length != asset.Size)
            throw new InvalidDataException("Размер локального вложения изменился.");

        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(asset.CommunityId + "\0" + asset.Kind + "\0"));
            var validationBuffer = new byte[64 * 1024];
            int read;
            while ((read = await stream.ReadAsync(validationBuffer)) > 0)
                hash.AppendData(validationBuffer, 0, read);
            if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(asset.Id, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Локальное вложение повреждено.");
        }
        stream.Position = 0;

        var buffer = new byte[ChunkBytes];
        int index = 0;
        while (stream.Position < stream.Length)
        {
            int count = (int)Math.Min(buffer.Length, stream.Length - stream.Position);
            await stream.ReadExactlyAsync(buffer.AsMemory(0, count));
            var bytes = buffer.AsSpan(0, count).ToArray();
            var chunkPath = $"{path}/Chunks/c{index++:D2}";
            if (!await cloud.TryCreateAsync(chunkPath, bytes))
            {
                var existing = await cloud.GetAsync<byte[]>(chunkPath);
                if (existing is null || !existing.AsSpan().SequenceEqual(bytes))
                    throw new InvalidDataException("Облачная часть файла отличается от локальной.");
            }
        }
        if (!await cloud.TryCreateAsync($"{path}/Info", manifest))
        {
            var existing = await cloud.GetAsync<FileManifest>($"{path}/Info")
                ?? throw new InvalidDataException("Метаданные вложения отсутствуют.");
            RequireSameContent(existing, manifest);
        }
    }

    public static async Task<MediaAsset?> DownloadAsync(
        FirebaseBackend cloud, LocalStore store, string community, string author, string id)
    {
        ValidateId(id);
        var path = $"communities/{community}/files/{id}";
        var meta = await cloud.GetAsync<FileManifest>($"{path}/Info");
        // Old originals are private to their author. Never probe somebody else's legacy directory.
        if (meta is null && cloud.Profile?.Id == author)
        {
            path = $"profiles/{author}/Files/{id}";
            meta = await cloud.GetAsync<FileManifest>($"{path}/Info");
        }
        if (meta is null)
            return null;
        ValidateManifest(meta);

        var directory = Path.Combine(store.RootDirectory, "incoming", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "payload.tmp");
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(Encoding.UTF8.GetBytes(community + "\0" + meta.Kind + "\0"));
            await using (var target = File.Create(file))
            {
                for (int i = 0; i < meta.Chunks; i++)
                {
                    var bytes = await cloud.GetAsync<byte[]>($"{path}/Chunks/c{i:D2}")
                        ?? throw new InvalidDataException("Часть файла отсутствует.");
                    if (bytes.Length != Math.Min(ChunkBytes, meta.Size - target.Position))
                        throw new InvalidDataException("Неверный размер части файла.");
                    hash.AppendData(bytes);
                    await target.WriteAsync(bytes);
                }
            }
            if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(id, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Файл повреждён: хеш не совпадает.");
            return store.ImportAsset(community, file, meta.Kind, meta.Name);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static void RequireSameContent(FileManifest existing, FileManifest expected)
    {
        ValidateManifest(existing);
        if (existing.Kind != expected.Kind || existing.Size != expected.Size || existing.Chunks != expected.Chunks)
            throw new InvalidDataException("Вложение с этим идентификатором имеет другие метаданные.");
    }

    private static void ValidateId(string id)
    {
        if (id is null || !Regex.IsMatch(id, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("Некорректный идентификатор вложения.");
    }

    private static void ValidateManifest(FileManifest meta)
    {
        if (meta.Size < 0 || meta.Size > LocalStore.MaxAssetBytes ||
            meta.Chunks is < 0 or > MaximumChunks ||
            meta.Chunks != (meta.Size + ChunkBytes - 1) / ChunkBytes ||
            meta.Kind is not ("attachment" or "voice") ||
            meta.Kind == "voice" && meta.Size > LocalStore.MaxVoiceBytes ||
            string.IsNullOrWhiteSpace(meta.Name) || meta.Name.Length > 200 ||
            meta.Name != Path.GetFileName(meta.Name) ||
            meta.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || meta.Name is "." or "..")
            throw new InvalidDataException("Некорректное вложение: максимум 16 МиБ (голос — 5 МиБ), имя до 200 символов.");
    }
}
