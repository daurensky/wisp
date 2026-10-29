using Wisp.Core;

namespace Wisp.Desktop;

public sealed partial class MainWindow
{
    private ProfileEditor CreateProfileEditor(bool initialEditing = false) => new(profile,
        avatars.Resolve(profile, store), profile.BannerPath ?? (profile.BannerAssetId is null ? null : store.GetAsset(profile.BannerAssetId)?.LocalPath),
        SaveEditedProfile, initialEditing,message=>Notify(message,true));

    private void EditProfile() => ModalHost.Open(this, "Мой профиль", new System.Windows.Controls.ScrollViewer{Content=CreateProfileEditor(true),VerticalScrollBarVisibility=System.Windows.Controls.ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=System.Windows.Controls.ScrollBarVisibility.Disabled}, ProfileLayout.CardWidth);

    private async Task<UserProfile> SaveEditedProfile(UserProfile proposed)
    {
        ProfileEditor.ValidateDraft(proposed.Username, proposed.About, proposed.AvatarPath, proposed.BannerPath);
        var previous = profile;
        var copied = new List<string>();
        string? PersistImage(string? source, string? current)
        {
            if (source is null || source == current) return source;
            var directory = Path.Combine(store.RootDirectory, "profile-images"); Directory.CreateDirectory(directory);
            var destination = Path.Combine(directory, Guid.NewGuid().ToString("N") + Path.GetExtension(source));
            File.Copy(source, destination); copied.Add(destination); return destination;
        }
        try
        {
            var avatar = PersistImage(proposed.AvatarPath, previous.AvatarPath);
            var banner = PersistImage(proposed.BannerPath, previous.BannerPath);
            profile = proposed with { AvatarPath = avatar, BannerPath = banner, AvatarAssetId = null, BannerAssetId = banner is null ? null : previous.BannerAssetId, AvatarRevision = avatar is null ? null : AvatarCatalog.Revision(avatar) };
            // Validate and persist first; a failed upload restores the previous local profile,
            // while the editor retains its draft and exposes the error for a retry.
            store.SaveProfile(profile);
            await PublishProfile();
            if (community is not null) store.UpsertMember(community.Id, profile);
            RenderMembers();RenderChannels(); UpdateStatus();
            return profile;
        }
        catch
        {
            profile = previous; store.SaveProfile(previous);
            foreach (var path in copied) { try { File.Delete(path); } catch (IOException) { } }
            throw;
        }
    }
}
