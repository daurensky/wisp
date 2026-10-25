using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Wisp.Core;

/// <summary>Local, device-owned accounts and community history. No central account service is used.</summary>
public sealed class LocalStore : IDisposable
{
    public const long MaxAssetBytes = 16 * 1024 * 1024;
    public const long MaxVoiceBytes = 5 * 1024 * 1024;
    private const int PasswordIterations = 210_000;
    private readonly object gate = new();
    private readonly SqliteConnection connection;
    private bool disposed;
    public string RootDirectory { get; }

    public LocalStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = Path.GetFullPath(rootDirectory);
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(Path.Combine(RootDirectory, "media"));
        connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(RootDirectory, "wisp.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        connection.Open();
        Execute("""
            PRAGMA foreign_keys = ON;
            PRAGMA journal_mode = WAL;
            PRAGMA busy_timeout = 5000;
            CREATE TABLE IF NOT EXISTS profiles (
              id TEXT PRIMARY KEY, username TEXT NOT NULL, about TEXT NOT NULL DEFAULT '',
              avatar TEXT, banner TEXT, status TEXT NOT NULL DEFAULT 'online', game TEXT,
              avatar_asset_id TEXT, banner_asset_id TEXT);
            CREATE TABLE IF NOT EXISTS accounts (
              profile_id TEXT PRIMARY KEY REFERENCES profiles(id), username_key TEXT NOT NULL UNIQUE,
              salt BLOB NOT NULL, password_hash BLOB NOT NULL, iterations INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS communities (
              id TEXT PRIMARY KEY, name TEXT NOT NULL, owner_id TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS channels (
              id TEXT PRIMARY KEY, community_id TEXT NOT NULL REFERENCES communities(id),
              name TEXT NOT NULL, is_voice INTEGER NOT NULL CHECK(is_voice IN (0,1)));
            CREATE INDEX IF NOT EXISTS channels_community ON channels(community_id);
            CREATE TABLE IF NOT EXISTS members (
              community_id TEXT NOT NULL REFERENCES communities(id),
              profile_id TEXT NOT NULL REFERENCES profiles(id), PRIMARY KEY(community_id,profile_id));
            CREATE TABLE IF NOT EXISTS messages (
              id TEXT PRIMARY KEY, channel_id TEXT NOT NULL REFERENCES channels(id), author_id TEXT NOT NULL,
              author_name TEXT NOT NULL, text TEXT NOT NULL, sent_at TEXT NOT NULL, attachment_id TEXT);
            CREATE INDEX IF NOT EXISTS messages_channel_time ON messages(channel_id,sent_at,id);
            CREATE TABLE IF NOT EXISTS assets (
              id TEXT PRIMARY KEY, community_id TEXT NOT NULL REFERENCES communities(id), name TEXT NOT NULL,
              kind TEXT NOT NULL, local_path TEXT NOT NULL, size INTEGER NOT NULL CHECK(size >= 0));
            CREATE INDEX IF NOT EXISTS assets_community_kind ON assets(community_id,kind);
            """);
        // Older local databases keep their history and gain profile media references in place.
        var profileColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var command = Command("PRAGMA table_info(profiles)"))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) profileColumns.Add(reader.GetString(1));
        if (!profileColumns.Contains("avatar_asset_id")) Execute("ALTER TABLE profiles ADD COLUMN avatar_asset_id TEXT");
        if (!profileColumns.Contains("banner_asset_id")) Execute("ALTER TABLE profiles ADD COLUMN banner_asset_id TEXT");
        Execute("PRAGMA user_version = 2");
    }

    public UserProfile Register(string username, string password)
    {
        username = ValidateUsername(username);
        ValidatePassword(password);
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, PasswordIterations, HashAlgorithmName.SHA256, 32);
        var profile = new UserProfile(Guid.NewGuid().ToString("N"), username);
        lock (gate)
        {
            ThrowIfDisposed();
            using var transaction = connection.BeginTransaction();
            try
            {
                UpsertProfile(profile);
                Execute("INSERT INTO accounts(profile_id,username_key,salt,password_hash,iterations) VALUES($id,$name,$salt,$hash,$iterations)",
                    ("$id", profile.Id), ("$name", UsernameKey(username)), ("$salt", salt), ("$hash", hash), ("$iterations", PasswordIterations));
                transaction.Commit();
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                throw new ArgumentException("Это имя пользователя уже занято на этом компьютере.", nameof(username), ex);
            }
        }
        return profile;
    }

    public UserProfile? Login(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || password is null || password.Length > 1024) return null;
        lock (gate)
        {
            ThrowIfDisposed();
            using var cmd = Command("SELECT p.id,p.username,p.about,p.avatar,p.banner,p.status,p.game,p.avatar_asset_id,p.banner_asset_id,a.salt,a.password_hash,a.iterations FROM accounts a JOIN profiles p ON p.id=a.profile_id WHERE a.username_key=$name", ("$name", UsernameKey(username.Trim())));
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                // Keep unknown-user checks computationally comparable to wrong-password checks.
                _ = Rfc2898DeriveBytes.Pbkdf2(password, new byte[16], PasswordIterations, HashAlgorithmName.SHA256, 32);
                return null;
            }
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, (byte[])reader[9], reader.GetInt32(11), HashAlgorithmName.SHA256, 32);
            return CryptographicOperations.FixedTimeEquals(actual, (byte[])reader[10]) ? ReadProfile(reader) : null;
        }
    }

    public void SaveProfile(UserProfile profile)
    {
        ValidateProfile(profile);
        lock (gate)
        {
            ThrowIfDisposed();
            using var transaction = connection.BeginTransaction();
            try
            {
                // A local account's login name follows its edited display username.
                Execute("UPDATE accounts SET username_key=$name WHERE profile_id=$id", ("$name", UsernameKey(profile.Username)), ("$id", profile.Id));
                UpsertProfile(profile);
                transaction.Commit();
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                throw new ArgumentException("Это имя пользователя уже занято на этом компьютере.", nameof(profile), ex);
            }
        }
    }

    public List<Community> GetCommunities()
    {
        lock (gate)
        {
            using var cmd = Command("SELECT id,name,owner_id FROM communities ORDER BY name,id");
            using var reader = cmd.ExecuteReader();
            var result = new List<Community>();
            while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            return result;
        }
    }

    public Community CreateCommunity(string name, string ownerId)
    {
        name = ValidateName(name, 80, "сообщества");
        ValidateId(ownerId);
        var community = new Community(Guid.NewGuid().ToString("N"), name, ownerId);
        lock (gate)
        {
            using var transaction = connection.BeginTransaction();
            Execute("INSERT INTO communities(id,name,owner_id) VALUES($id,$name,$owner)", ("$id", community.Id), ("$name", name), ("$owner", ownerId));
            Execute("INSERT INTO members(community_id,profile_id) VALUES($community,$profile)", ("$community", community.Id), ("$profile", ownerId));
            CreateChannel(community.Id, "general", false);
            CreateChannel(community.Id, "Lounge", true);
            transaction.Commit();
        }
        return community;
    }

    public List<Channel> GetChannels(string communityId)
    {
        lock (gate)
        {
            using var cmd = Command("SELECT id,community_id,name,is_voice FROM channels WHERE community_id=$id ORDER BY is_voice,name,id", ("$id", communityId));
            using var reader = cmd.ExecuteReader();
            var result = new List<Channel>();
            while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3)));
            return result;
        }
    }

    public Channel CreateChannel(string communityId, string name, bool isVoice)
    {
        name = ValidateName(name, 64, "канала");
        ValidateId(communityId);
        var channel = new Channel(Guid.NewGuid().ToString("N"), communityId, name, isVoice);
        lock (gate)
        {
            Execute("INSERT INTO channels(id,community_id,name,is_voice) VALUES($id,$community,$name,$voice)", ("$id", channel.Id), ("$community", communityId), ("$name", name), ("$voice", isVoice));
        }
        return channel;
    }

    public void SaveSnapshot(CommunitySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateId(snapshot.Community.Id);
        ValidateId(snapshot.Community.OwnerId);
        ValidateName(snapshot.Community.Name, 80, "сообщества");
        if (snapshot.Channels.Count > 100 || snapshot.Members.Count > 1000) throw new ArgumentException("Слишком большое сообщество.");
        if (snapshot.Channels.Any(c => c.CommunityId != snapshot.Community.Id)) throw new ArgumentException("Канал принадлежит другому сообществу.");
        foreach (var channel in snapshot.Channels) { ValidateId(channel.Id); ValidateName(channel.Name, 64, "канала"); }
        foreach (var member in snapshot.Members) ValidateProfile(member);
        lock (gate)
        {
            using var transaction = connection.BeginTransaction();
            using (var existing = Command("SELECT owner_id FROM communities WHERE id=$id", ("$id", snapshot.Community.Id)))
                if (existing.ExecuteScalar() is string ownerId && ownerId != snapshot.Community.OwnerId)
                    throw new ArgumentException("Владелец существующего сообщества не может меняться через сетевой снимок.", nameof(snapshot));
            Execute("INSERT INTO communities(id,name,owner_id) VALUES($id,$name,$owner) ON CONFLICT(id) DO UPDATE SET name=excluded.name",
                ("$id", snapshot.Community.Id), ("$name", snapshot.Community.Name), ("$owner", snapshot.Community.OwnerId));
            foreach (var channel in snapshot.Channels)
                Execute("INSERT INTO channels(id,community_id,name,is_voice) VALUES($id,$community,$name,$voice) ON CONFLICT(id) DO UPDATE SET name=excluded.name,is_voice=excluded.is_voice WHERE channels.community_id=excluded.community_id",
                    ("$id", channel.Id), ("$community", channel.CommunityId), ("$name", channel.Name), ("$voice", channel.IsVoice));
            foreach (var member in snapshot.Members) UpsertMember(snapshot.Community.Id, member);
            transaction.Commit();
        }
    }

    public List<UserProfile> GetMembers(string communityId)
    {
        lock (gate)
        {
            using var cmd = Command("SELECT p.id,p.username,p.about,p.avatar,p.banner,p.status,p.game,p.avatar_asset_id,p.banner_asset_id FROM profiles p JOIN members m ON p.id=m.profile_id WHERE m.community_id=$id ORDER BY p.username,p.id", ("$id", communityId));
            using var reader = cmd.ExecuteReader();
            var result = new List<UserProfile>();
            while (reader.Read()) result.Add(ReadProfile(reader));
            return result;
        }
    }

    public void UpsertMember(string communityId, UserProfile profile)
    {
        ValidateProfile(profile);
        lock (gate)
        {
            // Never replace a local account profile with data received from another peer.
            using var local = Command("SELECT 1 FROM accounts WHERE profile_id=$id", ("$id", profile.Id));
            if (local.ExecuteScalar() is null) UpsertProfile(profile);
            Execute("INSERT OR IGNORE INTO members(community_id,profile_id) VALUES($community,$profile)", ("$community", communityId), ("$profile", profile.Id));
        }
    }

    public List<ChatMessage> GetMessages(string channelId, int limit = 100)
    {
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        lock (gate)
        {
            using var cmd = Command("SELECT id,channel_id,author_id,author_name,text,sent_at,attachment_id FROM (SELECT * FROM messages WHERE channel_id=$channel ORDER BY sent_at DESC,id DESC LIMIT $limit) ORDER BY sent_at,id", ("$channel", channelId), ("$limit", limit));
            using var reader = cmd.ExecuteReader();
            var result = new List<ChatMessage>();
            while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture), NullableString(reader, 6)));
            return result;
        }
    }

    public void SaveMessage(ChatMessage message)
    {
        ValidateId(message.Id);
        ValidateId(message.ChannelId);
        ValidateId(message.AuthorId);
        if (message.Text is null || message.Text.Length > 8000 || (string.IsNullOrWhiteSpace(message.Text) && message.AttachmentId is null)) throw new ArgumentException("Сообщение пустое или слишком длинное.");
        ValidateUsername(message.AuthorName);
        lock (gate)
            Execute("INSERT OR IGNORE INTO messages(id,channel_id,author_id,author_name,text,sent_at,attachment_id) VALUES($id,$channel,$author,$name,$text,$time,$attachment)",
                ("$id", message.Id), ("$channel", message.ChannelId), ("$author", message.AuthorId), ("$name", message.AuthorName), ("$text", message.Text), ("$time", message.SentAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)), ("$attachment", message.AttachmentId));
    }

    public MediaAsset ImportAsset(string communityId, string sourcePath, string kind)
    {
        ValidateId(communityId);
        if (kind is not ("attachment" or "voice" or "emoji" or "sound" or "avatar" or "banner")) throw new ArgumentException("Неизвестный тип медиа.", nameof(kind));
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var name = Path.GetFileName(sourcePath);
        if (name.Length > 255 || string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Некорректное имя файла.", nameof(sourcePath));
        var maximum = kind == "voice" ? MaxVoiceBytes : MaxAssetBytes;
        var mediaDirectory = Path.Combine(RootDirectory, "media");
        var temporary = Path.Combine(mediaDirectory, Guid.NewGuid().ToString("N") + ".tmp");
        long size = 0;
        string digest;
        try
        {
            using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                if (source.Length > maximum) throw new ArgumentException($"Максимальный размер файла: {maximum / (1024 * 1024)} МБ.");
                hash.AppendData(Encoding.UTF8.GetBytes(communityId + "\0" + kind + "\0"));
                var buffer = new byte[64 * 1024];
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    size += read;
                    if (size > maximum) throw new ArgumentException($"Максимальный размер файла: {maximum / (1024 * 1024)} МБ.");
                    hash.AppendData(buffer, 0, read);
                    destination.Write(buffer, 0, read);
                }
                digest = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            }
            // The original filename is metadata only. It never controls a filesystem path.
            var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
            if (!Regex.IsMatch(extension, @"^\.[a-z0-9]{1,10}$", RegexOptions.CultureInvariant)) extension = ".bin";
            var storedPath = Path.Combine(mediaDirectory, digest + extension);
            var asset = new MediaAsset(digest, communityId, name, kind, storedPath, size);
            lock (gate)
            {
                ThrowIfDisposed();
                File.Move(temporary, storedPath, true);
                Execute("INSERT INTO assets(id,community_id,name,kind,local_path,size) VALUES($id,$community,$name,$kind,$path,$size) ON CONFLICT(id) DO UPDATE SET name=excluded.name,local_path=excluded.local_path,size=excluded.size",
                    ("$id", asset.Id), ("$community", communityId), ("$name", name), ("$kind", kind), ("$path", storedPath), ("$size", size));
            }
            return asset;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public List<MediaAsset> GetAssets(string communityId, string? kind = null)
    {
        lock (gate)
        {
            using var cmd = Command("SELECT id,community_id,name,kind,local_path,size FROM assets WHERE community_id=$community AND ($kind IS NULL OR kind=$kind) ORDER BY name,id", ("$community", communityId), ("$kind", kind));
            using var reader = cmd.ExecuteReader();
            var result = new List<MediaAsset>();
            while (reader.Read()) result.Add(ReadAsset(reader));
            return result;
        }
    }

    public MediaAsset? GetAsset(string id)
    {
        lock (gate)
        {
            using var cmd = Command("SELECT id,community_id,name,kind,local_path,size FROM assets WHERE id=$id", ("$id", id));
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadAsset(reader) : null;
        }
    }

    private void UpsertProfile(UserProfile profile) => Execute("""
        INSERT INTO profiles(id,username,about,avatar,banner,status,game,avatar_asset_id,banner_asset_id) VALUES($id,$username,$about,$avatar,$banner,$status,$game,$avatarAsset,$bannerAsset)
        ON CONFLICT(id) DO UPDATE SET username=excluded.username,about=excluded.about,avatar=excluded.avatar,banner=excluded.banner,status=excluded.status,game=excluded.game,avatar_asset_id=excluded.avatar_asset_id,banner_asset_id=excluded.banner_asset_id
        """, ("$id", profile.Id), ("$username", profile.Username), ("$about", profile.About), ("$avatar", profile.AvatarPath), ("$banner", profile.BannerPath), ("$status", profile.Status), ("$game", profile.Game), ("$avatarAsset", profile.AvatarAssetId), ("$bannerAsset", profile.BannerAssetId));

    private static string ValidateUsername(string username)
    {
        ArgumentNullException.ThrowIfNull(username);
        username = username.Trim();
        if (!Regex.IsMatch(username, @"^[\p{L}\p{N}_\-.]{3,32}$", RegexOptions.CultureInvariant)) throw new ArgumentException("Имя: 3–32 буквы, цифры, _, - или точка.", nameof(username));
        return username;
    }
    private static string UsernameKey(string username) => username.Normalize(NormalizationForm.FormKC).ToUpperInvariant();
    private static void ValidatePassword(string password)
    {
        if (password is null || password.Length is < 8 or > 1024) throw new ArgumentException("Пароль должен содержать от 8 до 1024 символов.", nameof(password));
    }
    private static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || id.Any(char.IsControl)) throw new ArgumentException("Некорректный идентификатор.", nameof(id));
    }
    private static string ValidateName(string name, int maximum, string label)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > maximum || name.Any(char.IsControl)) throw new ArgumentException($"Название {label}: от 1 до {maximum} символов.", nameof(name));
        return name.Trim();
    }
    private static void ValidateProfile(UserProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ValidateId(profile.Id);
        if (ValidateUsername(profile.Username) != profile.Username) throw new ArgumentException("Имя не должно содержать пробелы по краям.");
        if (profile.About is null || profile.About.Length > 2000 || profile.Game?.Length > 200 || profile.Status.Length > 32 || profile.AvatarPath?.Length > 4096 || profile.BannerPath?.Length > 4096 || profile.AvatarAssetId?.Length > 128 || profile.BannerAssetId?.Length > 128) throw new ArgumentException("Профиль слишком большой.");
    }
    private static UserProfile ReadProfile(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), NullableString(reader, 3), NullableString(reader, 4), reader.GetString(5), NullableString(reader, 6), NullableString(reader, 7), NullableString(reader, 8));
    private static MediaAsset ReadAsset(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetInt64(5));
    private static string? NullableString(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);

    private SqliteCommand Command(string sql, params (string Name, object? Value)[] parameters)
    {
        ThrowIfDisposed();
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
    private void Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        command.ExecuteNonQuery();
    }
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            connection.Dispose();
            disposed = true;
        }
    }
}
