using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Wisp.Core;

namespace Wisp.Desktop;

public sealed partial class MainWindow
{
    private ContextMenu CommunityContextMenu(Community target)
    {
        var menu = new ContextMenu();
        var settings = new MenuItem { Header = "Настройки сервера" };
        settings.Click += (_, _) => OpenCommunitySettings(target);
        menu.Items.Add(settings);
        return menu;
    }

    private FrameworkElement CommunityIcon(Community target, FrameworkElement hoverScope)
    {
        var asset = target.IconAssetId is null ? null : store.GetAsset(target.IconAssetId);
        return asset is not null && File.Exists(asset.LocalPath)
            ? AvatarCatalog.Circle(asset.LocalPath, Ui.Metric("Size.RailItem") - 8, GifPlaybackMode.Hover, hoverScope)
            : Ui.Label(target.Name[..Math.Min(2, target.Name.Length)].ToUpperInvariant(), Ui.Subtitle);
    }

    private async Task RefreshCommunityIcon(Community target)
    {
        if (cloud is null || target.IconAssetId is null || store.GetAsset(target.IconAssetId) is not null) return;
        var transfer = await cloud.GetAsync<AssetTransfer>($"communities/{target.Id}/icon");
        if (transfer is null || transfer.Kind != "icon" || transfer.Id != target.IconAssetId || transfer.Data.Length > 4 * 1024 * 1024) return;
        var temporary = Path.Combine(store.RootDirectory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, transfer.Data);
            AvatarCatalog.Validate(temporary);
            var imported = store.ImportAsset(target.Id, temporary, "icon", transfer.Name);
            if (imported.Id != target.IconAssetId) { store.RemoveAsset(imported.Id); throw new InvalidDataException("Значок сервера повреждён."); }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void OpenCommunitySettings(Community target) => Run(async () =>
    {
        var snapshot = cloud is null ? null : await cloud.GetAsync<CommunitySnapshot>($"communities/{target.Id}/snapshot");
        if (snapshot is not null) { store.SaveSnapshot(snapshot); target = snapshot.Community; }
        await RefreshCommunityIcon(target);
        bool manager = store.CanManageCommunity(target.Id, profile.Id);
        var dialog = new FormDialog(this, "Настройки сервера", 600);
        dialog.Note($"{target.Name} · {store.GetMembers(target.Id).Count} участников");
        var name = dialog.Field("Название сервера", target.Name); name.IsReadOnly = !manager;
        var preview = new ContentControl { Content = CommunityIcon(target, dialog), HorizontalAlignment = HorizontalAlignment.Left, Margin = Ui.Insets(0, Ui.M) };
        dialog.Add(preview); string? selectedIcon = null;
        if (manager)
        {
            dialog.Action("Изменить значок", () =>
            {
                var pick = new Microsoft.Win32.OpenFileDialog { Filter = "Изображения|*.png;*.jpg;*.jpeg;*.gif" };
                if (pick.ShowDialog(this) != true) return;
                ProfileEditor.ValidateImage(pick.FileName); selectedIcon = pick.FileName;
                preview.Content = AvatarCatalog.Circle(selectedIcon, 72, GifPlaybackMode.Hover, preview);
            });
            dialog.Action("Сохранить изменения", () => Run(async () =>
            {
                if (string.IsNullOrWhiteSpace(name.Text) || name.Text.Trim().Length > 80) throw new InvalidOperationException("Название сервера: от 1 до 80 символов.");
                if (cloud is null)
                {
                    target = target with { Name = name.Text.Trim() };
                    if (selectedIcon is not null) target = target with { IconAssetId = store.ImportAsset(target.Id, selectedIcon, "icon").Id };
                    store.SaveSnapshot(new(target, store.GetChannels(target.Id), store.GetMembers(target.Id), store.GetMembers(target.Id).ToDictionary(m => m.Id, m => store.GetMemberRole(target.Id, m.Id))));
                }
                else
                {
                    await cloud.ManageCommunityAsync(target.Id, new("rename", Name: name.Text.Trim()));
                    if (selectedIcon is not null)
                    {
                        var icon = store.ImportAsset(target.Id, selectedIcon, "icon");
                        await cloud.ManageCommunityAsync(target.Id, new("icon", Asset: new(icon.Id, icon.Name, "icon", await File.ReadAllBytesAsync(icon.LocalPath))));
                    }
                    await ReloadCommunityMetadata(target.Id);
                }
                dialog.Close(); Notify("Настройки сервера сохранены.");
            }));
        }
        dialog.Action("Участники и права", () => { dialog.Close(); CommunityMembers(target); });
        dialog.Action("Эмодзи сервера", () => { dialog.Close(); CommunityMediaSettings(target, "emoji"); });
        dialog.Action("Звуковая панель сервера", () => { dialog.Close(); CommunityMediaSettings(target, "sound"); });
        if (!manager) dialog.Note("Изменять сервер могут владелец и администраторы.");
        dialog.Accept("Закрыть"); dialog.ShowDialog();
    });

    private async Task ReloadCommunityMetadata(string id)
    {
        if (cloud is null) { RenderServers(); return; }
        var snapshot = await cloud.GetAsync<CommunitySnapshot>($"communities/{id}/snapshot");
        if (snapshot is null) return;
        store.SaveSnapshot(snapshot); await RefreshCommunityIcon(snapshot.Community);
        if (community?.Id == id) { community = snapshot.Community; serverTitle.Text = community.Name; memberRenderKey = ""; channelRenderKey = ""; RenderChannels(); RenderMembers(); }
        if (voiceCommunity?.Id == id) voiceCommunity = snapshot.Community;
        RenderServers(); UpdateStatus();
    }

    private void CommunityMembers(Community target) => Run(async () =>
    {
        await ReloadCommunityMetadata(target.Id);
        var dialog = new FormDialog(this, "Участники и права", 620);
        string currentRole = store.GetMemberRole(target.Id, profile.Id);
        foreach (var member in store.GetMembers(target.Id))
        {
            string role = store.GetMemberRole(target.Id, member.Id);
            var row = new DockPanel { Margin = Ui.Insets(0, Ui.S) };
            var actions = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(actions, Dock.Right); row.Children.Add(actions);
            if (cloud is not null && currentRole == "owner" && member.Id != target.OwnerId)
                actions.Children.Add(Btn(role == "admin" ? "Снять администратора" : "Назначить администратора", () => Run(async () =>
                {
                    if (!ConfirmCommunityAction(role == "admin" ? $"Снять роль администратора у {member.Username}?" : $"Назначить {member.Username} администратором?")) return;
                    await cloud.ManageCommunityAsync(target.Id, new("role", UserId: member.Id, Role: role == "admin" ? "member" : "admin"));
                    await ReloadCommunityMetadata(target.Id); dialog.Close(); CommunityMembers(target);
                })));
            if (cloud is not null && currentRole is "owner" or "admin" && member.Id != profile.Id && member.Id != target.OwnerId && (currentRole == "owner" || role != "admin"))
            {
                var remove = Btn("Исключить", () => Run(async () =>
                {
                    if (!ConfirmCommunityAction($"Исключить {member.Username} из сервера?")) return;
                    await cloud.ManageCommunityAsync(target.Id, new("kick", UserId: member.Id));
                    await ReloadCommunityMetadata(target.Id); dialog.Close(); CommunityMembers(target);
                })); Ui.Style(remove, "Button.Danger"); actions.Children.Add(remove);
            }
            row.Children.Add(Ui.Label($"{member.Username}\n{RoleLabel(role)}", Ui.Body)); dialog.Add(row);
        }
        dialog.Note("Владелец управляет администраторами. Администраторы управляют сервером, медиа и обычными участниками.");
        dialog.Accept("Закрыть"); dialog.ShowDialog();
    });

    private static string RoleLabel(string role) => role switch { "owner" => "Владелец", "admin" => "Администратор", _ => "Участник" };
    private bool ConfirmCommunityAction(string text)
    {
        var confirm = new FormDialog(this, "Подтверждение"); confirm.Note(text); confirm.Accept("Подтвердить");
        return confirm.ShowDialog() == true;
    }

    private void CommunityMediaSettings(Community target, string kind) => Run(async () =>
    {
        string collection = kind == "emoji" ? "emojis" : "sounds";
        bool manager = store.CanManageCommunity(target.Id, profile.Id);
        var remote = cloud is null ? null : await cloud.GetAsync<Dictionary<string, AssetTransfer>>($"communities/{target.Id}/{collection}");
        var assets = remote?.Values.ToDictionary(a => a.Id) ?? store.GetAssets(target.Id, kind).ToDictionary(a => a.Id, a => new AssetTransfer(a.Id, a.Name, a.Kind, Array.Empty<byte>()));
        var dialog = new FormDialog(this, kind == "emoji" ? "Эмодзи сервера" : "Звуковая панель сервера", 620);
        foreach (var asset in assets.Values.OrderBy(a => a.Name))
        {
            var row = new DockPanel { Margin = Ui.Insets(0, Ui.S) };
            var actions = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(actions, Dock.Right); row.Children.Add(actions);
            if (kind == "sound") actions.Children.Add(Btn("Прослушать", () => Run(async () => { var local = await EnsureSoundAsync(target.Id, asset.Id); if (local is not null&&!deafened) sounds.PlayFile(local.LocalPath); })));
            if (manager)
            {
                actions.Children.Add(Btn("Переименовать", () =>
                {
                    var edit = new FormDialog(this, "Название"); var field = edit.Field("Название", asset.Name); edit.Accept();
                    if (edit.ShowDialog() != true) return;
                    Run(async () => { if (cloud is not null) await cloud.ManageCommunityAsync(target.Id, new("renameAsset", Name: field.Text.Trim(), Kind: kind, AssetId: asset.Id)); store.RenameAsset(asset.Id, field.Text); dialog.Close(); CommunityMediaSettings(target, kind); });
                }));
                actions.Children.Add(Btn("Удалить", () => Run(async () =>
                {
                    if (!ConfirmCommunityAction($"Удалить «{asset.Name}»?")) return;
                    if (cloud is not null) await cloud.ManageCommunityAsync(target.Id, new("deleteAsset", Kind: kind, AssetId: asset.Id));
                    store.RemoveAsset(asset.Id); dialog.Close(); CommunityMediaSettings(target, kind);
                })));
            }
            row.Children.Add(Ui.Label(asset.Name, Ui.Body)); dialog.Add(row);
        }
        if (assets.Count == 0) dialog.Note("Нет файлов.");
        if (manager) dialog.FooterAction(kind == "emoji" ? "Загрузить эмодзи" : "Загрузить звук", () =>
        {
            var pick = new Microsoft.Win32.OpenFileDialog { Filter = kind == "emoji" ? "Изображения|*.png;*.gif;*.jpg;*.jpeg" : "Аудио|*.wav;*.mp3" };
            if (pick.ShowDialog(this) != true) return;
            long maximum = kind == "emoji" ? 256 * 1024 : 2 * 1024 * 1024;
            if (new FileInfo(pick.FileName).Length > maximum) throw new InvalidOperationException(kind == "emoji" ? "Эмодзи: до 256 КиБ." : "Звук: до 2 МиБ.");
            if (kind == "emoji") AvatarCatalog.Validate(pick.FileName);
            var preview = new FormDialog(this, "Предварительный просмотр");
            var name = preview.Field("Название", Path.GetFileName(pick.FileName));
            if (kind == "emoji") preview.Add(AvatarCatalog.Image(pick.FileName, 96));
            else preview.Action("Прослушать", () => {if(!deafened)sounds.PlayFile(pick.FileName);});
            preview.Accept("Загрузить");
            if (preview.ShowDialog() != true) return;
            Run(async () =>
            {
                var local = store.ImportAsset(target.Id, pick.FileName, kind, name.Text.Trim());
                if (cloud is not null) { if (kind == "emoji") await CommunityAssets.UploadEmojiAsync(cloud, local); else await cloud.PutAsync($"communities/{target.Id}/sounds/{local.Id}", new AssetTransfer(local.Id, local.Name, kind, await File.ReadAllBytesAsync(local.LocalPath))); }
                dialog.Close(); CommunityMediaSettings(target, kind);
            });
        });
        dialog.Accept("Закрыть"); dialog.ShowDialog();
    });
}
