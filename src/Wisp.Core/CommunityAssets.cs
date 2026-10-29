using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Wisp.Core;

/// <summary>Server emoji data belongs to the community, not the author's public profile.</summary>
public static class CommunityAssets
{
    public static async Task UploadEmojiAsync(ServerBackend cloud, MediaAsset asset)
    {
        if (asset.Kind != "emoji" || asset.Size is <= 0 or > 256 * 1024 ||
            string.IsNullOrWhiteSpace(asset.Name) || asset.Name.Length > 200 ||
            !Regex.IsMatch(asset.Id, "^[a-f0-9]{64}$"))
            throw new InvalidDataException("Эмодзи: изображение до 256 КиБ, имя до 200 символов.");
        var bytes = await File.ReadAllBytesAsync(asset.LocalPath);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(asset.CommunityId + "\0emoji\0"));
        hash.AppendData(bytes);
        if (bytes.Length != asset.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(asset.Id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Локальный эмодзи повреждён.");
        var path = $"communities/{asset.CommunityId}/emojis/{asset.Id}";
        bool Matches(AssetTransfer? value)=>value?.Kind=="emoji"&&value.Id==asset.Id&&value.Data is not null&&value.Data.AsSpan().SequenceEqual(bytes);
        // Reading existing server emoji is available to every member; only a new
        // immutable upload requires management rights. Do not issue a forbidden
        // PUT when an ordinary participant inserts an already published emoji.
        var published=await cloud.GetAsync<AssetTransfer>(path);
        if(published is not null)
        {
            if(!Matches(published))throw new InvalidDataException("Облачный эмодзи отличается от локального.");
            return;
        }
        var transfer = new AssetTransfer(asset.Id, asset.Name, "emoji", bytes);
        if (!await cloud.TryCreateAsync(path, transfer)&&!Matches(await cloud.GetAsync<AssetTransfer>(path)))
            throw new InvalidDataException("Облачный эмодзи отличается от локального.");
    }
}
