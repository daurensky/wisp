using System.Windows;
using System.Windows.Controls;
using Wisp.Core;

namespace Wisp.Desktop;

public sealed class AuthWindow : ThemedWindow
{
    public UserProfile? Profile { get; private set; }
    public AuthWindow(LocalStore store, FirebaseBackend? cloud = null)
    {
        Title = "Wisp · Вход"; Width = Ui.Metric("Width.DialogMedium"); SizeToContent = SizeToContent.Height; MaxHeight = SystemParameters.WorkArea.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var panel = new StackPanel { Margin = Ui.Insets(Ui.Xxl, Ui.Xl) }; Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(new TextBlock { Text = "Рады видеть тебя", FontSize = Ui.Heading, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "Место для своих.", Margin = Ui.Insets(0, Ui.Xs, 0, Ui.Xl) });
        panel.Children.Add(Ui.Label(cloud is null ? "Имя пользователя" : "Email", Ui.Caption, true)); var user = new TextBox(); panel.Children.Add(user);
        panel.Children.Add(Ui.Label("Пароль · от 8 символов", Ui.Caption, true)); var pass = new PasswordBox(); panel.Children.Add(pass);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = Ui.Insets(0, Ui.S) }; error.SetResourceReference(TextBlock.ForegroundProperty, "Danger"); panel.Children.Add(error); Ui.Name(user, cloud is null ? "Имя пользователя" : "Email"); Ui.Name(pass, "Пароль");
        TextBox? username = null;
        if (cloud is not null) { panel.Children.Add(Ui.Label("Имя пользователя (при регистрации)", Ui.Caption, true)); username = new TextBox { MaxLength = 32 }; panel.Children.Add(username); }
        async void Submit(bool register)
        {
            try { panel.IsEnabled = false;
                if (register && pass.Password.Length < 8) throw new ArgumentException("Пароль должен содержать минимум 8 символов.");
                if (cloud is not null && register && !System.Text.RegularExpressions.Regex.IsMatch(username!.Text, @"^[\p{L}\p{N}_\-.]{3,32}$")) throw new ArgumentException("Имя: 3–32 буквы, цифры, _, - или точка.");
                Profile = cloud is not null ? await cloud.SignInAsync(user.Text.Trim(), pass.Password, register, username?.Text ?? "user") : register ? store.Register(user.Text.Trim(), pass.Password) : store.Login(user.Text.Trim(), pass.Password); if (Profile is null) throw new InvalidOperationException("Неверное имя или пароль."); DialogResult = true; }
            catch (Exception ex) { error.Text = ex.Message; } finally { panel.IsEnabled = true; }
        }
        var login = Buttons.Create("Войти", () => Submit(false), "Войти · Enter", "arrow-right", "Button.Primary"); login.IsDefault = true; login.Margin = Ui.Insets(0, 0, 0, Ui.S); panel.Children.Add(login);
        var signup = Buttons.Create("Создать аккаунт", () => Submit(true), "Создать аккаунт", "plus"); signup.Margin = Ui.Insets(0); panel.Children.Add(signup); Loaded += (_, _) => user.Focus();
        panel.Children.Add(new TextBlock { Text = cloud is null ? "Локальный аккаунт. Вход сохраняется на этом ПК на 90 дней." : "Аккаунт хранится в Firebase. Вход будет сохранён на этом ПК.", FontSize = Ui.Caption, Opacity = .6, TextWrapping = TextWrapping.Wrap, Margin = Ui.Insets(0, Ui.L, 0, 0) });
    }
}

public sealed class FormDialog : ContentControl
{
    private readonly StackPanel panel = new() { Margin = Ui.Insets(Ui.Xl, 0, Ui.Xl, Ui.L) };
    private readonly StackPanel actions = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = Ui.Insets(Ui.Xl, Ui.L) };
    private readonly Border footer;
    private readonly double dialogWidth;
    private ModalHandle? handle;
    private Button? accept;
    private TextBox? firstField;
    private bool? result;
    public Window OwnerWindow { get; }
    public string Title { get; }
    public bool? DialogResult { get => result; set { result = value; Close(); } }
    public FormDialog(Window owner, string title, double width = 0)
    {
        OwnerWindow = owner; Title = title;
        dialogWidth = width > 0 ? Math.Min(width, Ui.Metric("Width.DialogLarge")) : Ui.Metric("Width.DialogMedium");
        var dock = new DockPanel();
        footer = new Border { Child = actions, BorderThickness = new Thickness(0, Ui.Metric("Size.Hairline"), 0, 0), Visibility = Visibility.Collapsed };
        footer.SetResourceReference(Border.BorderBrushProperty, "Border");
        DockPanel.SetDock(footer, Dock.Bottom); dock.Children.Add(footer);
        dock.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }); Content = dock;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter && e.OriginalSource is not Button && e.OriginalSource is not ComboBox && (e.OriginalSource is not TextBox field || !field.AcceptsReturn) && accept is not null)
            { e.Handled = true; accept.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
        };
    }
    public bool? ShowDialog()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        handle = ModalHost.Open(OwnerWindow, Title, this, dialogWidth);
        if (firstField is not null) Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() => firstField.Focus()));
        handle.Closed += () => frame.Continue = false;
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        return result;
    }
    public void Close() => handle?.Close();
    public void Note(string text)
    {
        var label = new TextBlock { Text = text, FontSize = Ui.Caption, TextWrapping = TextWrapping.Wrap, Margin = Ui.Insets(0, 0, 0, Ui.M) };
        Ui.Secondary(label); panel.Children.Add(label);
    }
    public void Add(UIElement element) => panel.Children.Add(element);
    public TextBox Field(string label, string value = "", bool multiline = false)
    {
        panel.Children.Add(Ui.Label(label, Ui.Caption, true));
        var field = new TextBox { Text = value, AcceptsReturn = multiline, MinHeight = multiline ? Ui.Metric("Height.Multiline") : Ui.ControlHeight, TextWrapping = TextWrapping.Wrap, VerticalContentAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center };
        Ui.Name(field, label); panel.Children.Add(field); firstField ??= field; return field;
    }
    public CheckBox Check(string label, bool value = false) { var c = new CheckBox { Content = label, IsChecked = value }; panel.Children.Add(c); return c; }
    public Button Action(string label, Action action)
    {
        var b = Buttons.Create(label, () => { try { action(); } catch (Exception ex) { if (OwnerWindow is MainWindow main) main.ReportNotification(ex.Message, true); else Note(ex.Message); } });
        b.Margin = Ui.Insets(0, 0, 0, Ui.S);
        if (label.Contains("Удалить", StringComparison.OrdinalIgnoreCase)) Ui.Style(b, "Button.Danger");
        panel.Children.Add(b); return b;
    }
    public void Accept(string label = "Сохранить")
        => PrimaryAction(label, () => DialogResult = true);
    public void PrimaryAction(string label, Action action)
    {
        footer.Visibility = Visibility.Visible;
        if (accept is not null) actions.Children.Remove(accept);
        if (actions.Children.Count == 0 && label is not "Закрыть" and not "Готово")
            actions.Children.Add(Buttons.Create("Отмена", Close, "Отмена · Esc", "x", "Button.Ghost"));
        accept = Buttons.Create(label, () => { try { action(); } catch (Exception ex) { if (OwnerWindow is MainWindow main) main.ReportNotification(ex.Message, true); else Note(ex.Message); } }, label + " · Enter", Buttons.ForLabel(label), "Button.Primary");
        actions.Children.Add(accept);
    }
    public void FooterAction(string label, Action action)
    {
        footer.Visibility = Visibility.Visible;
        var button = Buttons.Create(label, () => { try { action(); } catch (Exception ex) { if (OwnerWindow is MainWindow main) main.ReportNotification(ex.Message, true); else Note(ex.Message); } });
        actions.Children.Insert(0, button);
    }
}
