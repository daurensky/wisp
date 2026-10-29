using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wisp.Core;
using Wisp.Desktop.Media;

namespace Wisp.Desktop;

public enum SettingsSection { Account, Appearance, Audio, Notifications }

/// <summary>Existing application operations, injected without coupling settings to a voice session or navigation.</summary>
public sealed class SettingsContext
{
    public required UserProfile Profile { get; init; }
    public Func<UserProfile>? CurrentProfile { get; init; }
    public Func<UserProfile, string?>? ResolveAvatar { get; init; }
    public Func<FrameworkElement>? ProfileSurface { get; init; }
    public string? AvatarPath { get; init; }
    public string? BannerPath { get; init; }
    public required AudioDeviceSettings Audio { get; init; }
    public bool NoiseSuppressionAvailable { get; init; }
    public required Func<AudioDeviceSettings, Task> ApplyAudio { get; init; }
    public required Action EditProfile { get; init; }
    public required Action ShowNotifications { get; init; }
    public Action? SignOut { get; init; }
    public bool NotificationsEnabled { get; init; } = true;
    public float NotificationVolume { get; init; } = .8f;
    public Action<bool, float>? ApplyNotifications { get; init; }
    public Action<string>? ReportError { get; init; }
    public Func<IReadOnlyList<AudioDeviceChoice>>? InputDevices { get; init; }
    public Func<IReadOnlyList<AudioDeviceChoice>>? OutputDevices { get; init; }
}

/// <summary>One native settings surface, with a sidebar and actual controls for existing Wisp settings.</summary>
public sealed class SettingsView : UserControl
{
    private readonly SettingsContext context;
    private readonly ContentControl page = new();
    private readonly SettingsShell shell = new();
    public SettingsShell Shell => shell;
    private readonly StackPanel accountIdentity = new();
    private readonly TextBlock feedback = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private AudioDeviceSettings audio;
    private bool notificationsEnabled;
    private float notificationVolume;
    public SettingsSection SelectedSection { get; private set; }

    public SettingsView(SettingsContext context)
    {
        this.context = context; audio = context.Audio;
        notificationsEnabled = context.NotificationsEnabled; notificationVolume = float.IsFinite(context.NotificationVolume) ? Math.Clamp(context.NotificationVolume, 0, 1) : .8f;
        MinHeight = 240;
        RefreshIdentity(); shell.Footer = accountIdentity;
        foreach (var (section, label, icon) in new[]
        {
            (SettingsSection.Account,"Аккаунт и профиль","users"),
            (SettingsSection.Appearance,"Внешний вид","sun"),
            (SettingsSection.Audio,"Голос и звук","headphones"),
            (SettingsSection.Notifications,"Уведомления","bell")
        }) shell.Sections.Add(new(section.ToString(),label,icon));
        shell.SectionSelected += key => { if (Enum.TryParse<SettingsSection>(key,out var section)) SelectSection(section); };
        var content = new DockPanel();
        SetResourceReference(BackgroundProperty,"Surface");
        content.SetResourceReference(Panel.BackgroundProperty,"Surface");
        page.SetResourceReference(BackgroundProperty,"Surface");
        feedback.SetResourceReference(TextBlock.ForegroundProperty, "Danger"); feedback.Margin=Ui.Insets(0,Ui.M,0,0);DockPanel.SetDock(feedback,Dock.Bottom);content.Children.Add(feedback);content.Children.Add(page);
        shell.PageContent=content;Content=shell;SelectSection(SettingsSection.Account);
    }

    public void SelectSection(SettingsSection section)
    {
        SelectedSection = section; feedback.Visibility = Visibility.Collapsed;
        shell.Select(section.ToString());
        try
        {
            var next = section switch
            {
                SettingsSection.Account => Account(),
                SettingsSection.Appearance => Appearance(), SettingsSection.Audio => Audio(),
                SettingsSection.Notifications => Notifications(), _ => throw new ArgumentOutOfRangeException(nameof(section))
            };
            // Replace a fully constructed page in one dispatcher turn. Fading
            // the persistent presenter to zero exposes the previous surface.
            Motion.Stop(page); page.Opacity = 1; page.RenderTransform = Transform.Identity;
            page.Content = next;
        }
        catch (Exception ex) { Error(ex); }
    }

    private StackPanel Section(string title, string description)
    {
        var panel = new StackPanel(); panel.Children.Add(new TextBlock { Text = title, FontSize = Ui.Heading, FontWeight = FontWeights.SemiBold, Margin = Ui.Insets(0, 0, 0, Ui.S) });
        var note = Note(description); note.Margin = Ui.Insets(0, 0, 0, Ui.Xl); panel.Children.Add(note); return panel;
    }
    private static TextBlock Note(string text)
    {
        var note = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = Ui.Body }; Ui.Secondary(note); return note;
    }
    private Button Action(string title, Action action, string style = "Button.Secondary")
    {
        var button = Buttons.Create(title, () => { try { action(); } catch (Exception ex) { Error(ex); } }, style: style);
        button.HorizontalAlignment = HorizontalAlignment.Left; button.Margin = Ui.Insets(0, Ui.M, 0, 0); return button;
    }
    private void Error(Exception exception) { feedback.Text = exception.Message; feedback.Visibility = Visibility.Visible; DiagnosticLog.Write("settings", exception); context.ReportError?.Invoke(exception.Message); }

    private void RefreshIdentity()
    {
        var current = context.CurrentProfile?.Invoke() ?? context.Profile;
        accountIdentity.Children.Clear();
        accountIdentity.Children.Add(AvatarCatalog.Circle(context.ResolveAvatar?.Invoke(current) ?? current.AvatarPath ?? context.AvatarPath, 40, GifPlaybackMode.Hover, coverFrame: current.AvatarCoverFrame));
        accountIdentity.Children.Add(Ui.Label(current.Username, Ui.Body));
        var appName = Ui.Label("WISP · НАСТРОЙКИ", Ui.Caption, true); appName.Margin = Ui.Insets(0, Ui.Xs, 0, 0); accountIdentity.Children.Add(appName);
    }

    private FrameworkElement Account()
    {
        var profile = context.CurrentProfile?.Invoke() ?? context.Profile;
        var panel = Section("Аккаунт и профиль", "Изображения, имя и информация о себе.");
        var surface = context.ProfileSurface?.Invoke() ?? new ProfileView(profile,
            context.ResolveAvatar is not null ? context.ResolveAvatar(profile) : profile.AvatarPath ?? context.AvatarPath,
            context.CurrentProfile is not null ? profile.BannerPath : profile.BannerPath ?? context.BannerPath, EditProfile);
        if (surface is ProfileEditor editor) editor.Saved += _ => RefreshIdentity();
        panel.Children.Add(surface);
        if (context.SignOut is not null) panel.Children.Add(Action("Выйти из аккаунта", context.SignOut, "Button.Danger"));
        return panel;
    }
    private void EditProfile() { context.EditProfile(); SelectSection(SelectedSection); }
    private FrameworkElement Appearance()
    {
        var panel = Section("Внешний вид", "Тема и анимации сохраняются на этом компьютере.");
        panel.Children.Add(Ui.Label("Тема", Ui.Caption, true));
        var theme = new ComboBox { ItemsSource = new[] { "Тёмная", "Светлая" }, SelectedIndex = ThemeManager.IsLight ? 1 : 0, Margin = Ui.Insets(0, Ui.Xs, 0, Ui.L) };
        theme.SelectionChanged += (_, _) => { try { if ((theme.SelectedIndex == 1) != ThemeManager.IsLight) ThemeManager.Toggle(); } catch (Exception ex) { Error(ex); } }; panel.Children.Add(theme);
        var reduce = new CheckBox { Content = "Уменьшить анимации", IsChecked = Motion.Reduce, Margin = Ui.Insets(0, Ui.S) };
        void ChangeMotion() { try { Motion.SetReduced(reduce.IsChecked == true); } catch (Exception ex) { Error(ex); } }
        reduce.Checked += (_, _) => ChangeMotion(); reduce.Unchecked += (_, _) => ChangeMotion(); panel.Children.Add(reduce);
        panel.Children.Add(Note("Отключает переходы и воспроизведение GIF. Системное отключение анимаций также учитывается.")); return panel;
    }
    private FrameworkElement Audio()
    {
        var panel = Section("Голос и звук", "Общее устройство вывода для разговора, голосовых сообщений и уведомлений. Устройства можно менять во время звонка.");
        ComboBox Devices(string label, IReadOnlyList<AudioDeviceChoice> devices, string? selected)
        {
            panel.Children.Add(Ui.Label(label, Ui.Caption, true));
            var choices = devices.Count == 0 ? new[] { new AudioDeviceChoice(null, "По умолчанию Windows") } : devices;
            var box = new ComboBox { ItemsSource = choices, DisplayMemberPath = nameof(AudioDeviceChoice.Name), SelectedIndex = Math.Max(0, choices.ToList().FindIndex(device => device.Id == selected)), Margin = Ui.Insets(0, Ui.Xs, 0, Ui.L) };
            panel.Children.Add(box); return box;
        }
        var microphone = Devices("Микрофон", context.InputDevices?.Invoke() ?? AudioDeviceSettings.List(true), audio.InputId);
        var headphones = Devices("Устройство воспроизведения", context.OutputDevices?.Invoke() ?? AudioDeviceSettings.List(false), audio.OutputId);
        var suppression = new CheckBox { Content = context.NoiseSuppressionAvailable ? "Шумоподавление RNNoise" : "Шумоподавление RNNoise · недоступно", IsChecked = audio.NoiseSuppression, IsEnabled = context.NoiseSuppressionAvailable, Margin = Ui.Insets(0, Ui.S) }; panel.Children.Add(suppression);
        panel.Children.Add(Note("Подавляет фоновый шум микрофона. Не изменяет звук голосовых сообщений или демонстрации."));
        var apply = Action("Применить", () => { }, "Button.Primary");
        apply.Click += async (_, _) =>
        {
            try
            {
                apply.IsEnabled = false;
                var settings = new AudioDeviceSettings(((AudioDeviceChoice)microphone.SelectedItem).Id, ((AudioDeviceChoice)headphones.SelectedItem).Id, suppression.IsChecked == true);
                await context.ApplyAudio(settings); audio = settings;
            }
            catch (Exception ex) { Error(ex); }
            finally { apply.IsEnabled = true; }
        };
        panel.Children.Add(apply); return panel;
    }
    private FrameworkElement Notifications()
    {
        var panel = Section("Уведомления", "Wisp Original сопровождает вход и выход из разговора, начало трансляции, входящие сообщения и твои переключения микрофона и звука.");
        if (context.ApplyNotifications is not null)
        {
            var enabled = new CheckBox { Content = "Звуковые уведомления", IsChecked = notificationsEnabled, Margin = Ui.Insets(0, Ui.S) };
            panel.Children.Add(enabled);
            void Apply() { try { notificationsEnabled = enabled.IsChecked == true; context.ApplyNotifications(notificationsEnabled, notificationVolume); } catch (Exception ex) { Error(ex); } }
            enabled.Checked += (_, _) => Apply(); enabled.Unchecked += (_, _) => Apply();
        }
        panel.Children.Add(Note("Ошибки и предупреждения доступны в области уведомлений. Переключение темы и обычные действия интерфейса не воспроизводят системные звуки."));
        panel.Children.Add(Action("Открыть уведомления", context.ShowNotifications)); return panel;
    }
}
