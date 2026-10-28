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
                var image = AvatarCatalog.Image(bannerPath, Ui.Metric("Width.DialogMedium")); image.Width = double.NaN; image.Height = Ui.Metric("Height.Banner"); image.Stretch = Stretch.UniformToFill; banner.Child = image;
            }
            catch { /* The profile remains usable if an obsolete local image is missing or invalid. */ }
        }
        hero.Children.Add(banner);
        var avatar = new Border { Child = AvatarCatalog.Circle(avatarPath, avatarSize), Width = avatarSize + 8, Height = avatarSize + 8, BorderThickness = new Thickness(4), CornerRadius = new CornerRadius(avatarSize / 2 + 4), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = Ui.Insets(Ui.L, 0, 0, 0) };
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
        Section("ОБО МНЕ", string.IsNullOrWhiteSpace(profile.About) ? "Пока ничего о себе не рассказал." : profile.About);
        if (editProfile is not null)
        {
            var edit = Buttons.Create("Редактировать профиль", editProfile, icon: "pencil", style: "Button.Primary"); edit.HorizontalAlignment = HorizontalAlignment.Left; edit.Margin = Ui.Insets(0, Ui.Xl, 0, 0); body.Children.Add(edit);
        }
        panel.Children.Add(body); Content = panel;
    }
}
