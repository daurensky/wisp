using Microsoft.Data.Sqlite;
using Wisp.Core;
using Xunit;

namespace Wisp.Core.Tests;

public sealed class LocalStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "wisp-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void SavedSessionsRestoreOnlyTheCorrectAccountAndCanBeRevoked()
    {
        string session;
        string id;
        using (var store = new LocalStore(directory))
        {
            var profile = store.Register("Alice", "correct horse battery"); id = profile.Id;
            session = store.CreateSession(profile.Id);
            Assert.Equal(id, store.RestoreSession(session)?.Id);
            Assert.Null(store.RestoreSession("not-a-session"));
        }
        using var reopened = new LocalStore(directory);
        Assert.Equal(id, reopened.RestoreSession(session)?.Id);
        reopened.RevokeSession(session);
        Assert.Null(reopened.RestoreSession(session));
    }

    [Fact]
    public void AccountsPersistAndPasswordsRequireAnExactMatch()
    {
        string id;
        using (var store = new LocalStore(directory))
        {
            id = store.Register("Alice", "correct horse battery").Id;
            Assert.Null(store.Login("Alice", "wrong password"));
            Assert.Null(store.Login("unknown", "correct horse battery"));
            Assert.Equal(id, store.Login("ALICE", "correct horse battery")?.Id);
            Assert.Throws<ArgumentException>(() => store.Register("alice", "another strong password"));
        }
        using var reopened = new LocalStore(directory);
        Assert.Equal(id, reopened.Login("alice", "correct horse battery")?.Id);
    }

    [Theory]
    [InlineData("ab", "password123")]
    [InlineData("alice", "short")]
    [InlineData("invalid/name", "password123")]
    public void InvalidCredentialsDoNotCreateAccounts(string username, string password)
    {
        using var store = new LocalStore(directory);
        Assert.Throws<ArgumentException>(() => store.Register(username, password));
    }

    [Fact]
    public void PasswordsHaveIndependentSaltsAndAreNotStoredAsPlaintext()
    {
        using var store = new LocalStore(directory);
        store.Register("alice", "same password");
        store.Register("bobby", "same password");
        using var database = new SqliteConnection($"Data Source={Path.Combine(directory, "wisp.db")};Pooling=False");
        database.Open();
        using var command = database.CreateCommand();
        command.CommandText = "SELECT hex(salt),hex(password_hash),iterations FROM accounts";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        var firstSalt = reader.GetString(0);
        var firstHash = reader.GetString(1);
        Assert.True(reader.GetInt32(2) >= 210_000);
        Assert.True(reader.Read());
        Assert.NotEqual(firstSalt, reader.GetString(0));
        Assert.NotEqual(firstHash, reader.GetString(1));
    }

    [Fact]
    public void EditingProfileChangesLoginAndCannotStealAnotherLocalUsername()
    {
        using var store = new LocalStore(directory);
        var alice = store.Register("alice", "password123");
        store.Register("bobby", "password123");
        store.SaveProfile(alice with { Username = "alice2", About = "About me" });
        Assert.Null(store.Login("alice", "password123"));
        Assert.Equal("About me", store.Login("alice2", "password123")?.About);
        Assert.Throws<ArgumentException>(() => store.SaveProfile(alice with { Username = "bobby" }));
        Assert.Equal("alice2", store.Login("alice2", "password123")?.Username);
    }

    [Fact]
    public void CommunityHistoryIsIsolatedOrderedAndIdempotent()
    {
        using var store = new LocalStore(directory);
        var alice = store.Register("alice", "password123");
        var community = store.CreateCommunity("Friends", alice.Id);
        var channels = store.GetChannels(community.Id);
        Assert.Equal(2, channels.Count);
        Assert.Contains(channels, c => c.IsVoice && Channel.Capacity == 5);
        Assert.Single(store.GetMembers(community.Id));
        var text = channels.Single(c => !c.IsVoice);
        var other = store.CreateChannel(community.Id, "other", false);
        var now = DateTimeOffset.UtcNow;
        var later = new ChatMessage("later", text.Id, alice.Id, alice.Username, "later", now);
        store.SaveMessage(later);
        store.SaveMessage(new("earlier", text.Id, alice.Id, alice.Username, "earlier", now.AddMinutes(-1)));
        store.SaveMessage(later with { Text = "duplicate overwrite attempt" });
        store.SaveMessage(new("other", other.Id, alice.Id, alice.Username, "other room", now));
        Assert.Equal(new[] { "earlier", "later" }, store.GetMessages(text.Id).Select(m => m.Text));
        Assert.Equal("later", Assert.Single(store.GetMessages(text.Id, 1)).Text);
    }

    [Fact]
    public void SnapshotCannotChangeExistingChannelCommunityOrOverwriteLocalProfile()
    {
        using var store = new LocalStore(directory);
        var alice = store.Register("alice", "password123");
        var first = store.CreateCommunity("First", alice.Id);
        var second = new Community("second", "Second", "remote-owner");
        var firstChannel = store.GetChannels(first.Id)[0];
        store.SaveSnapshot(new(second, [firstChannel with { CommunityId = second.Id, Name = "hijacked" }], [alice with { Username = "hijacked" }]));
        Assert.NotEqual("hijacked", store.GetChannels(first.Id)[0].Name);
        Assert.Equal("alice", store.Login("alice", "password123")?.Username);
        Assert.Throws<ArgumentException>(() => store.SaveSnapshot(new(second, [firstChannel], [])));
    }

    [Fact]
    public void SnapshotCannotTransferOwnershipOfAnExistingCommunity()
    {
        using var store = new LocalStore(directory);
        var alice = store.Register("alice", "password123");
        var community = store.CreateCommunity("Friends", alice.Id);
        Assert.Throws<ArgumentException>(() => store.SaveSnapshot(new(community with { OwnerId = "attacker", Name = "stolen" }, [], [])));
        Assert.Equal(community, Assert.Single(store.GetCommunities()));
    }

    [Fact]
    public void ProfileMediaReferencesPersistAndNetworkMembersCannotReplaceLocalReferences()
    {
        UserProfile profile;
        string communityId;
        using (var store = new LocalStore(directory))
        {
            profile = store.Register("alice", "password123") with { AvatarAssetId = "avatar-hash", BannerAssetId = "banner-hash" };
            store.SaveProfile(profile);
            communityId = store.CreateCommunity("Friends", profile.Id).Id;
            store.UpsertMember(communityId, profile with { AvatarAssetId = "malicious", BannerAssetId = null });
            Assert.Equal(profile, Assert.Single(store.GetMembers(communityId)));
        }
        using var reopened = new LocalStore(directory);
        Assert.Equal(profile, reopened.Login("alice", "password123"));
        Assert.Equal(profile, Assert.Single(reopened.GetMembers(communityId)));
    }

    [Fact]
    public void ExistingVersionOneDatabaseMigratesProfileMediaColumnsWithoutLosingData()
    {
        Directory.CreateDirectory(directory);
        using (var database = new SqliteConnection($"Data Source={Path.Combine(directory, "wisp.db")};Pooling=False"))
        {
            database.Open();
            using var command = database.CreateCommand();
            command.CommandText = """
                CREATE TABLE profiles(id TEXT PRIMARY KEY,username TEXT NOT NULL,about TEXT NOT NULL DEFAULT '',avatar TEXT,banner TEXT,status TEXT NOT NULL DEFAULT 'online',game TEXT);
                INSERT INTO profiles(id,username,about) VALUES('old-user','oldname','Existing profile');
                PRAGMA user_version=1;
                """;
            command.ExecuteNonQuery();
        }
        using var store = new LocalStore(directory);
        var community = store.CreateCommunity("Migrated", "old-user");
        var old = Assert.Single(store.GetMembers(community.Id));
        Assert.Equal("Existing profile", old.About);
        Assert.Null(old.AvatarAssetId);
        store.SaveProfile(old with { AvatarAssetId = "avatar-hash", BannerAssetId = "banner-hash" });
        Assert.Equal("banner-hash", Assert.Single(store.GetMembers(community.Id)).BannerAssetId);
    }

    [Fact]
    public void MediaUsesContentAddressedStorageAndRetainsOriginalFilenameOnlyAsMetadata()
    {
        using var store = new LocalStore(directory);
        var alice = store.Register("alice", "password123");
        var community = store.CreateCommunity("Friends", alice.Id);
        var source = Path.Combine(directory, "example.txt");
        File.WriteAllText(source, "hello media");
        var first = store.ImportAsset(community.Id, source, "attachment");
        var second = store.ImportAsset(community.Id, source, "attachment");
        Assert.Equal(first.Id, second.Id);
        Assert.Equal("example.txt", first.Name);
        Assert.NotEqual(source, first.LocalPath);
        Assert.StartsWith(Path.Combine(directory, "media") + Path.DirectorySeparatorChar, first.LocalPath);
        Assert.Equal("hello media", File.ReadAllText(first.LocalPath));
        Assert.Single(store.GetAssets(community.Id));
        Assert.Equal(first, store.GetAsset(first.Id));
    }

    [Fact]
    public void OversizedVoiceFilesAreRejectedAndDoNotLeaveTemporaryFiles()
    {
        using var store = new LocalStore(directory);
        var alice = store.Register("alice", "password123");
        var community = store.CreateCommunity("Friends", alice.Id);
        var source = Path.Combine(directory, "large.wav");
        using (var file = File.Create(source)) file.SetLength(LocalStore.MaxVoiceBytes + 1);
        Assert.Throws<ArgumentException>(() => store.ImportAsset(community.Id, source, "voice"));
        Assert.Empty(store.GetAssets(community.Id));
        Assert.Empty(Directory.GetFiles(Path.Combine(directory, "media")));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
