using System.Text.Json;
using System.Text.RegularExpressions;

namespace Wisp.Core;

/// <summary>Fixed emoji slots make a server-enforced count possible in RTDB rules.</summary>
internal sealed record FirebaseEmoji(string Token, string AssetId);
internal sealed record FirebaseChatMessage(
    string Id, string ChannelId, string AuthorId, string AuthorName,
    string Text, DateTimeOffset SentAt, string? AttachmentId,
    Dictionary<string, FirebaseEmoji>? Emojis)
{
    public const int MaximumEmojis = 32;

    public static FirebaseChatMessage From(ChatMessage message)
    {
        if (message.Emojis?.Count > MaximumEmojis)
            throw new InvalidDataException("В сообщении допускается не более 32 разных пользовательских эмодзи.");
        Dictionary<string, FirebaseEmoji>? slots = null;
        if (message.Emojis?.Count > 0)
        {
            slots = new();
            int index = 0;
            foreach (var pair in message.Emojis)
            {
                if (!Regex.IsMatch(pair.Key, @"^:[\p{L}\p{N}_-]{1,40}:$") ||
                    !Regex.IsMatch(pair.Value, "^[a-f0-9]{64}$"))
                    throw new InvalidDataException("Некорректная ссылка на эмодзи.");
                slots.Add($"e{index++:D2}", new FirebaseEmoji(pair.Key, pair.Value));
            }
        }
        return new(message.Id, message.ChannelId, message.AuthorId, message.AuthorName,
            message.Text, message.SentAt, message.AttachmentId, slots);
    }

    public static ChatMessage Read(JsonElement json)
    {
        // Existing chat history keeps its original token -> asset dictionary.
        Dictionary<string, string>? emojis = null;
        if (json.TryGetProperty("Emojis", out var map) && map.ValueKind == JsonValueKind.Object)
        {
            emojis = new();
            foreach (var item in map.EnumerateObject().Take(MaximumEmojis))
            {
                string token, asset;
                if (item.Value.ValueKind == JsonValueKind.String)
                {
                    token = item.Name;
                    asset = item.Value.GetString()!;
                }
                else
                {
                    var reference = item.Value.Deserialize<FirebaseEmoji>()
                        ?? throw new InvalidDataException("Некорректный эмодзи.");
                    token = reference.Token;
                    asset = reference.AssetId;
                }
                if (token is null || asset is null || !Regex.IsMatch(token, @"^:[\p{L}\p{N}_-]{1,40}:$") ||
                    !Regex.IsMatch(asset, "^[a-f0-9]{64}$") || !emojis.TryAdd(token, asset))
                    throw new InvalidDataException("Некорректный эмодзи.");
            }
        }
        return new(
            json.GetProperty("Id").GetString()!, json.GetProperty("ChannelId").GetString()!,
            json.GetProperty("AuthorId").GetString()!, json.GetProperty("AuthorName").GetString()!,
            json.GetProperty("Text").GetString()!, json.GetProperty("SentAt").GetDateTimeOffset(),
            json.TryGetProperty("AttachmentId", out var attachment) && attachment.ValueKind == JsonValueKind.String
                ? attachment.GetString() : null, emojis);
    }
}
