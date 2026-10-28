using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wisp.Core;

namespace Wisp.Desktop;

/// <summary>Reusable profile surface. Image playback follows the existing loaded/unloaded GIF lifecycle.</summary>
public sealed class ProfileView : UserControl
{
    public ProfileView(UserProfile profile, string? avatarPath, string? bannerPath, Action? editProfile = null)
    {
        var panel = new StackPanel();
        double avatarSize = Ui.Metric("Size.ProfileAvatar");
        var hero = new Grid { Height = Ui.Metric("Height.Banner") + avatarSize / 2 };
        var banner = new Border { Height = Ui.Metric("Height.Banner"), VerticalAlignment = VerticalAlignment.Top, CornerRadius = Ui.Radius(), ClipToBounds = true };
        banner.SetResourceReference(Border.BackgroundProperty, "SurfaceElevated");
        if (bannerPath is not null && File.Exists(bannerPath))
        {
            try
            {
                var image = AvatarCatalog.Image(bannerPath, Ui.Metric("Width.DialogMedium"), mode: GifPlaybackMode.Hover, hoverScope: banner); image.Width = double.NaN; image.Height = Ui.Metric("Height.Banner"); image.Stretch = Stretch.UniformToFill; banner.Child = image;
            }
            catch { /* The profile remains usable if an obsolete local image is missing or invalid. */ }
        }
        hero.Children.Add(banner);
        var avatar = new Border { Child = AvatarCatalog.Circle(avatarPath, avatarSize, GifPlaybackMode.Hover), Width = avatarSize + 8, Height = avatarSize + 8, BorderThickness = new Thickness(4), CornerRadius = new CornerRadius(avatarSize / 2 + 4), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = Ui.Insets(Ui.L, 0, 0, 0) };
        avatar.SetResourceReference(Border.BackgroundProperty, "Surface"); avatar.SetResourceReference(Border.BorderBrushProperty, "Surface"); hero.Children.Add(avatar); panel.Children.Add(hero);
        var body = new StackPanel { Margin = Ui.Insets(Ui.L, Ui.S, Ui.L, Ui.L) };
        body.Children.Add(new TextBlock { Text = profile.Username, FontSize = Ui.Heading, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap });
        var status = profile.Status switch { "idle" => "Нет на месте", "dnd" => "Не беспокоить", "offline" => "Не в сети", _ => "В сети" };
        var presence = new StackPanel { Orientation = Orientation.Horizontal, Margin = Ui.Insets(0, Ui.S, 0, Ui.L) };
        var dot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Margin = Ui.Insets(0, 0, Ui.S, 0), VerticalAlignment = VerticalAlignment.Center };
        dot.SetResourceReference(Border.BackgroundProperty, profile.Status switch { "idle" => "Warning", "dnd" => "Danger", "offline" => "TextDisabled", _ => "Success" }); presence.Children.Add(dot); presence.Children.Add(Ui.Label(status, Ui.Caption, true)); body.Children.Add(presence);
        void Section(string title, string text)
        {
            var heading = Ui.Label(title, Ui.Caption, true); heading.FontWeight = FontWeights.SemiBold; heading.Margin = Ui.Insets(0, Ui.M, 0, Ui.S); body.Children.Add(heading);
            body.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = Ui.Body });
        }
        if (!string.IsNullOrWhiteSpace(profile.Game)) Section("АКТИВНОСТЬ", "Играет в " + profile.Game);
        Section("ОБО МНЕ", string.IsNullOrWhiteSpace(profile.About) ? "Не указано" : profile.About);
        if (editProfile is not null)
        {
            var edit = Buttons.Create("Редактировать профиль", editProfile, icon: "pencil", style: "Button.Primary"); edit.HorizontalAlignment = HorizontalAlignment.Left; edit.Margin = Ui.Insets(0, Ui.Xl, 0, 0); body.Children.Add(edit);
        }
        panel.Children.Add(body); Content = panel;
    }
}

/// <summary>A single profile surface with an explicit, cancellable edit session.</summary>
public sealed class ProfileEditor : UserControl
{
    private readonly Func<UserProfile, Task<UserProfile>> save;
    private readonly ContentControl body = new();
    private readonly TextBlock feedback = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private UserProfile profile;
    private string? avatarPath, bannerPath;
    private TextBox? name, about;
    private bool saving;
    public bool IsEditing { get; private set; }
    public event Action<UserProfile>? Saved;

    public ProfileEditor(UserProfile profile, string? avatarPath, string? bannerPath, Func<UserProfile, Task<UserProfile>> save, bool initialEditing = false)
    {
        this.profile = profile; this.avatarPath = avatarPath; this.bannerPath = bannerPath; this.save = save;
        var root = new StackPanel(); root.Children.Add(body);
        feedback.SetResourceReference(TextBlock.ForegroundProperty, "Danger"); feedback.Margin = Ui.Insets(Ui.L, Ui.S);
        root.Children.Add(feedback); Content = root;
        if (initialEditing) BeginEdit(); else View();
    }

    private void View()
    {
        IsEditing = false;
        body.Content = new ProfileView(profile, avatarPath, bannerPath, BeginEdit);
    }

    private void BeginEdit()
    {
        if (saving) return;
        IsEditing = true; feedback.Visibility = Visibility.Collapsed;
        var panel = new StackPanel();
        var draftAvatar = avatarPath; var draftBanner = bannerPath;
        var hero = new Grid { Height = Ui.Metric("Height.Banner") + Ui.Metric("Size.ProfileAvatar") / 2 };
        var banner = new Border { Height = Ui.Metric("Height.Banner"), VerticalAlignment = VerticalAlignment.Top, ClipToBounds = true, CornerRadius = Ui.Radius() };
        banner.SetResourceReference(Border.BackgroundProperty, "SurfaceElevated"); hero.Children.Add(banner);
        double size = Ui.Metric("Size.ProfileAvatar");
        var avatar = new Border { Width = size + 8, Height = size + 8, BorderThickness = new Thickness(4), CornerRadius = new CornerRadius(size / 2 + 4), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = Ui.Insets(Ui.L, 0, 0, 0) };
        avatar.SetResourceReference(Border.BorderBrushProperty, "Surface"); avatar.SetResourceReference(Border.BackgroundProperty, "Surface"); hero.Children.Add(avatar);
        void Error(Exception ex) { feedback.Text = ex.Message; feedback.Visibility = Visibility.Visible; }
        void Pick(bool isAvatar)
        {
            try
            {
                var picker = new Microsoft.Win32.OpenFileDialog { Filter = "Изображения|*.png;*.jpg;*.jpeg;*.gif", CheckFileExists = true };
                bool? picked = Window.GetWindow(this) is { } owner ? picker.ShowDialog(owner) : picker.ShowDialog();
                if (picked != true) return;
                ValidateImage(picker.FileName);
                if (isAvatar) draftAvatar = picker.FileName; else draftBanner = picker.FileName;
                RenderImages(); feedback.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex) { Error(ex); }
        }
        void RenderImages()
        {
            FrameworkElement? bannerImage = null;
            if (draftBanner is not null && File.Exists(draftBanner))
            {
                try
                {
                    var image = AvatarCatalog.Image(draftBanner, Ui.Metric("Width.DialogMedium"), mode: GifPlaybackMode.Hover);
                    image.Width = double.NaN; image.Height = Ui.Metric("Height.Banner"); image.Stretch = Stretch.UniformToFill; bannerImage = image;
                }
                catch (Exception ex) { Error(ex); }
            }
            banner.Child = EditableImage(bannerImage, "Изменить баннер", () => Pick(false), false);
            avatar.Child = EditableImage(AvatarCatalog.Circle(draftAvatar, size, GifPlaybackMode.Hover), "Изменить аватар", () => Pick(true), true);
        }
        RenderImages(); panel.Children.Add(hero);
        var fields = new StackPanel { Margin = Ui.Insets(Ui.L, Ui.M, Ui.L, Ui.L) };
        var imageActions = new WrapPanel();
        var removeAvatar = Buttons.Create("Удалить аватар", () => { draftAvatar = null; RenderImages(); }, style: "Button.Ghost");
        var removeBanner = Buttons.Create("Удалить баннер", () => { draftBanner = null; RenderImages(); }, style: "Button.Ghost");
        imageActions.Children.Add(removeAvatar); imageActions.Children.Add(removeBanner); fields.Children.Add(imageActions);
        fields.Children.Add(Ui.Label("Имя пользователя", Ui.Caption, true));
        name = new TextBox { Text = profile.Username, MaxLength = 32, Margin = Ui.Insets(0, Ui.Xs, 0, Ui.M) }; Ui.Name(name, "Имя пользователя"); fields.Children.Add(name);
        fields.Children.Add(Ui.Label("О себе", Ui.Caption, true));
        about = new TextBox { Text = profile.About, MaxLength = 2000, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = Ui.Metric("Height.Multiline"), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = Ui.Insets(0, Ui.Xs, 0, Ui.M) }; Ui.Name(about, "О себе"); fields.Children.Add(about);
        fields.Children.Add(Ui.Label("PNG, JPEG или GIF · до 4 МБ · до 2048×2048", Ui.Caption, true));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = Ui.Insets(0, Ui.L, 0, 0) };
        var cancel = Buttons.Create("Отмена", () => { if (!saving) { feedback.Visibility = Visibility.Collapsed; View(); } }, style: "Button.Ghost");
        var saveButton = Buttons.Create("Сохранить", () => { }, style: "Button.Primary");
        saveButton.Click += async (_, _) =>
        {
            if (saving) return;
            try
            {
                saving = true; body.IsEnabled = false; Buttons.Label(saveButton, "Сохранение…", "check");
                ValidateDraft(name.Text, about.Text, draftAvatar, draftBanner);
                var result = await save(profile with { Username = name.Text.Trim(), About = about.Text, AvatarPath = draftAvatar, BannerPath = draftBanner });
                profile = result; avatarPath = result.AvatarPath; bannerPath = result.BannerPath;
                feedback.Visibility = Visibility.Collapsed; View(); Saved?.Invoke(result);
            }
            catch (Exception ex) { Error(ex); }
            finally { saving = false; body.IsEnabled = true; Buttons.Label(saveButton, "Сохранить", "check"); }
        };
        actions.Children.Add(saveButton); actions.Children.Add(cancel); fields.Children.Add(actions); panel.Children.Add(fields); body.Content = panel;
    }

    private static FrameworkElement EditableImage(FrameworkElement? image, string label, Action action, bool circle)
    {
        var layer = new Grid(); if (image is not null) layer.Children.Add(image);
        var overlay = new Border { Visibility = Visibility.Collapsed, CornerRadius = circle ? new CornerRadius(Ui.Metric("Size.ProfileAvatar") / 2) : Ui.Radius(), IsHitTestVisible = false };
        overlay.SetResourceReference(Border.BackgroundProperty, "Overlay");
        var pencil = new Icon { Kind = "pencil", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }; pencil.SetResourceReference(Icon.ForegroundProperty, "OnDanger"); overlay.Child = pencil; layer.Children.Add(overlay);
        var button = Buttons.Create(label, action, label, style: "Button.Ghost"); button.Content = layer; button.Padding = Ui.Insets(0); button.HorizontalContentAlignment = HorizontalAlignment.Stretch; button.VerticalContentAlignment = VerticalAlignment.Stretch;
        if (image is Image animatedImage) GifPlayback.SetScope(animatedImage, button);
        void Update() => overlay.Visibility = button.IsMouseOver || button.IsKeyboardFocusWithin ? Visibility.Visible : Visibility.Collapsed;
        button.MouseEnter += (_, _) => Update(); button.MouseLeave += (_, _) => Update(); button.IsKeyboardFocusWithinChanged += (_, _) => Update();
        return button;
    }

    public static void ValidateDraft(string username, string about, string? avatarPath, string? bannerPath)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(username.Trim(), @"^[\p{L}\p{N}_\-.]{3,32}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw new ArgumentException("Имя: 3–32 буквы, цифры, _, - или точка.");
        if (about.Length > 2000) throw new ArgumentException("Описание: не более 2000 символов.");
        if (avatarPath is not null) ValidateImage(avatarPath);
        if (bannerPath is not null) ValidateImage(bannerPath);
    }

    public static void ValidateImage(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        // Existing cached profile images have a content-addressed .image/.img name.
        if (extension is not ".png" and not ".jpg" and not ".jpeg" and not ".gif" and not ".image" and not ".img")
            throw new InvalidDataException("Поддерживаются PNG, JPEG и GIF.");
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0) throw new InvalidDataException("Файл изображения отсутствует или пуст.");
        if (info.Length > 4 * 1024 * 1024) throw new InvalidDataException("Изображение профиля должно быть не больше 4 МБ.");
        using (var stream = File.OpenRead(path))
        {
            Span<byte> header = stackalloc byte[8]; int count = stream.Read(header);
            bool supported = count >= 8 && header.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
                || count >= 6 && (header[..6].SequenceEqual("GIF87a"u8) || header[..6].SequenceEqual("GIF89a"u8))
                || count >= 3 && header[0] == 255 && header[1] == 216 && header[2] == 255;
            if (!supported) throw new InvalidDataException("Содержимое файла должно быть изображением PNG, JPEG или GIF.");
        }
        AvatarCatalog.Validate(path);
    }
}
