namespace Wisp.Core;

public sealed record UserProfile(string Id, string Username, string About = "", string? AvatarPath = null, string? BannerPath = null, string Status = "online", string? Game = null, string? AvatarAssetId = null, string? BannerAssetId = null);
public sealed record Community(string Id, string Name, string OwnerId);
public sealed record Channel(string Id, string CommunityId, string Name, bool IsVoice = false) { public const int Capacity = 5; }
public sealed record ChatMessage(string Id, string ChannelId, string AuthorId, string AuthorName, string Text, DateTimeOffset SentAt, string? AttachmentId = null);
public sealed record MediaAsset(string Id, string CommunityId, string Name, string Kind, string LocalPath, long Size);
public sealed record PeerPresence(UserProfile Profile, string? VoiceChannelId, bool Muted, bool Deafened);
public sealed record WireMessage(string Type, string SenderId, string? ChannelId, byte[] Payload);
public sealed record CommunitySnapshot(Community Community, List<Channel> Channels, List<UserProfile> Members);
public sealed record AssetTransfer(string Id, string Name, string Kind, byte[] Data);
