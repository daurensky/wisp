using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wisp.Core;
using Wisp.Desktop.Media;

namespace Wisp.Desktop;

public enum SettingsSection { Account, Profile, Appearance, Audio, Notifications, Connection, ScreenShare }

/// <summary>Existing application operations, injected without coupling settings to a voice session or navigation.</summary>
public sealed class SettingsContext
{
    public required UserProfile Profile { get; init; }
    public Func<UserProfile>? CurrentProfile { get; init; }
    public Func<UserProfile, string?>? ResolveAvatar { get; init; }
    public string? AvatarPath { get; init; }
    public string? BannerPath { get; init; }
    public required AudioDeviceSettings Audio { get; init; }
    public bool NoiseSuppressionAvailable { get; init; }
    public required Func<AudioDeviceSettings, Task> ApplyAudio { get; init; }
    public required Action EditProfile { get; init; }
    public required Action ShowNotifications { get; init; }
    public required Action ConnectionDetails { get; init; }
    public Action? ServerSettings { get; init; }
    public required Action ShareScreen { get; init; }
    public bool CanShareScreen { get; init; }
    public Action? SignOut { get; init; }
    public string ServerAddress { get; init; } = "";
    public Func<string>? ConnectionStatus { get; init; }
    public bool NotificationsEnabled { get; init; } = true;
    public float NotificationVolume { get; init; } = .8f;
    public Action<bool, float>? ApplyNotifications { get; init; }
    public Func<IReadOnlyList<AudioDeviceChoice>>? InputDevices { get; init; }
    public Func<IReadOnlyList<AudioDeviceChoice>>? OutputDevices { get; init; }
}

/// <summary>One native settings surface, with a sidebar and actual controls for existing Wisp settings.</summary>
public sealed class SettingsView : UserControl
{
    private readonly SettingsContext context;
    private readonly ContentControl page = new();
    private readonly TextBlock feedback = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly Dictionary<SettingsSection, Button> navigation = new();
    private AudioDeviceSettings audio;
    private bool notificationsEnabled;
    private float notificationVolume;
    public SettingsSection SelectedSection { get; private set; }

    public SettingsView(SettingsContext context)
    {
        this.context = context; audio = context.Audio;
        notificationsEnabled = context.NotificationsEnabled; notificationVolume = float.IsFinite(context.NotificationVolume) ? Math.Clamp(context.NotificationVolume, 0, 1) : .8f;
        MinHeight = 320; MaxHeight = 520;
        var root = new Grid(); root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) }); root.ColumnDefinitions.Add(new ColumnDefinition());
        var sidebar = new DockPanel { Margin = Ui.Insets(Ui.M) };
        var identity = new StackPanel { Margin = Ui.Insets(Ui.S, Ui.L, Ui.S, Ui.S) };
        identity.Children.Add(AvatarCatalog.Circle(context.AvatarPath, 40));
        identity.Children.Add(Ui.Label(context.Profile.Username, Ui.Body));
        var appName = Ui.Label("WISP · НАСТРОЙКИ", Ui.Caption, true); appName.Margin = Ui.Insets(0, Ui.Xs, 0, 0); identity.Children.Add(appName);
        DockPanel.SetDock(identity, Dock.Bottom); sidebar.Children.Add(identity);
        var links = new StackPanel();
        Add(SettingsSection.Account, "Учётная запись", "users");
        Add(SettingsSection.Profile, "Профиль", "pencil");
        Add(SettingsSection.Appearance, "Внешний вид", "sun");
        Add(SettingsSection.Audio, "Голос и звук", "headphones");
        Add(SettingsSection.Notifications, "Уведомления", "bell");
        Add(SettingsSection.Connection, "Подключение", "circle-dot");
        Add(SettingsSection.ScreenShare, "Демонстрация", "monitor");
        void Add(SettingsSection section, string label, string icon)
        {
            var button = Buttons.Create(label, () => SelectSection(section), icon: icon, style: "Button.Row");
            button.HorizontalContentAlignment = HorizontalAlignment.Left; button.Margin = Ui.Insets(0, Ui.Xs);
            button.Height = Ui.RowHeight; navigation.Add(section, button); links.Children.Add(button);
        }
        sidebar.Children.Add(new ScrollViewer { Content = links, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        root.Children.Add(Ui.Panel(sidebar));
        var right = new DockPanel { Margin = Ui.Insets(Ui.Xl, Ui.L, Ui.Xl, Ui.Xl) };
        feedback.SetResourceReference(TextBlock.ForegroundProperty, "Danger"); feedback.Margin = Ui.Insets(0, Ui.M, 0, 0); DockPanel.SetDock(feedback, Dock.Bottom); right.Children.Add(feedback);
        right.Children.Add(new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Grid.SetColumn(right, 1); root.Children.Add(right); Content = root; SelectSection(SettingsSection.Account);
    }

    public void SelectSection(SettingsSection section)
    {
        SelectedSection = section; feedback.Visibility = Visibility.Collapsed;
        foreach (var entry in navigation) entry.Value.SetResourceReference(Button.BackgroundProperty, entry.Key == section ? "Selected" : "Surface");
        try
        {
            page.Content = section switch
            {
                SettingsSection.Account => Account(), SettingsSection.Profile => Profile(),
                SettingsSection.Appearance => Appearance(), SettingsSection.Audio => Audio(),
                SettingsSection.Notifications => Notifications(), SettingsSection.Connection => Connection(),
                SettingsSection.ScreenShare => ScreenShare(), _ => throw new ArgumentOutOfRangeException(nameof(section))
            };
            Motion.Enter(page);
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
    private void Error(Exception exception) { feedback.Text = exception.Message; feedback.Visibility = Visibility.Visible; DiagnosticLog.Write("settings", exception); }

    private FrameworkElement Account()
    {
        var profile = context.CurrentProfile?.Invoke() ?? context.Profile;
        var panel = Section("Учётная запись", "Параметры твоего аккаунта Wisp.");
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(AvatarCatalog.Circle(context.ResolveAvatar is not null ? context.ResolveAvatar(profile) : profile.AvatarPath ?? context.AvatarPath, 56));
        var name = Ui.Label(profile.Username, Ui.Subtitle); name.FontWeight = FontWeights.SemiBold; name.Margin = Ui.Insets(Ui.L, 0, 0, 0); row.Children.Add(name); panel.Children.Add(row);
        panel.Children.Add(Action("Редактировать профиль", EditProfile, "Button.Primary"));
        if (context.SignOut is not null) panel.Children.Add(Action("Выйти из аккаунта", context.SignOut, "Button.Danger"));
        return panel;
    }
    private FrameworkElement Profile()
    {
        var profile = context.CurrentProfile?.Invoke() ?? context.Profile;
        return new ProfileView(profile, context.ResolveAvatar is not null ? context.ResolveAvatar(profile) : profile.AvatarPath ?? context.AvatarPath, context.CurrentProfile is not null ? profile.BannerPath : profile.BannerPath ?? context.BannerPath, EditProfile);
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
            panel.Children.Add(enabled); panel.Children.Add(Ui.Label("Громкость уведомлений", Ui.Caption, true));
            var volume = new Slider { Minimum = 0, Maximum = 100, Value = notificationVolume * 100, Margin = Ui.Insets(0, Ui.S, 0, Ui.L), IsEnabled = notificationsEnabled }; Ui.Name(volume, "Громкость уведомлений"); panel.Children.Add(volume);
            void Apply() { try { notificationsEnabled = enabled.IsChecked == true; notificationVolume = (float)(volume.Value / 100); volume.IsEnabled = notificationsEnabled; context.ApplyNotifications(notificationsEnabled, notificationVolume); } catch (Exception ex) { Error(ex); } }
            enabled.Checked += (_, _) => Apply(); enabled.Unchecked += (_, _) => Apply();
            // Persist once at the end of interaction rather than on each drag tick.
            volume.PreviewMouseLeftButtonUp += (_, _) => Apply(); volume.LostKeyboardFocus += (_, _) => Apply();
        }
        panel.Children.Add(Note("Ошибки и предупреждения доступны в области уведомлений. Переключение темы и обычные действия интерфейса не воспроизводят системные звуки."));
        panel.Children.Add(Action("Открыть уведомления", context.ShowNotifications)); return panel;
    }
    private FrameworkElement Connection()
    {
        var panel = Section("Подключение", "Состояние сервера и текущего голосового соединения.");
        var status = context.ConnectionStatus?.Invoke(); if (!string.IsNullOrWhiteSpace(status)) { var label = Ui.Label(status); label.TextWrapping = TextWrapping.Wrap; panel.Children.Add(label); }
        if (!string.IsNullOrWhiteSpace(context.ServerAddress)) panel.Children.Add(Note(context.ServerAddress));
        panel.Children.Add(Action("Сведения о подключении", context.ConnectionDetails));
        if (context.ServerSettings is not null) panel.Children.Add(Action("Параметры сервера Wisp", context.ServerSettings)); return panel;
    }
    private FrameworkElement ScreenShare()
    {
        var panel = Section("Демонстрация экрана", "Выбор приложения или монитора, разрешение, частота кадров и звук доступны при запуске демонстрации.");
        var button = Action("Выбрать источник и качество", context.ShareScreen, "Button.Primary"); button.IsEnabled = context.CanShareScreen; panel.Children.Add(button);
        if (!context.CanShareScreen) panel.Children.Add(Note("Подключись к голосовому каналу, чтобы начать демонстрацию.")); return panel;
    }
}
