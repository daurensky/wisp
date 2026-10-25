using System.Windows;
using System.Windows.Controls;
using Wisp.Core;

namespace Wisp.Desktop;

public sealed class AuthWindow : Window
{
    public UserProfile? Profile { get; private set; }
    public AuthWindow(LocalStore store)
    {
        Title = "Wisp · Вход"; Width = 440; Height = 530; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var panel = new StackPanel { Margin = new Thickness(36) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = "wisp", FontSize = 44, FontWeight = FontWeights.Bold });
        panel.Children.Add(new TextBlock { Text = "Место для своих.", Margin = new Thickness(0, 4, 0, 24) });
        panel.Children.Add(new TextBlock { Text = "Имя пользователя" }); var user = new TextBox(); panel.Children.Add(user);
        panel.Children.Add(new TextBlock { Text = "Пароль · от 8 символов" }); var pass = new PasswordBox(); panel.Children.Add(pass);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) }; panel.Children.Add(error);
        void Submit(bool register)
        {
            try { Profile = register ? store.Register(user.Text.Trim(), pass.Password) : store.Login(user.Text.Trim(), pass.Password); if (Profile is null) throw new InvalidOperationException("Неверное имя или пароль."); DialogResult = true; }
            catch (Exception ex) { error.Text = ex.Message; }
        }
        var login = new Button { Content = "Войти", IsDefault = true }; login.Click += (_, _) => Submit(false); panel.Children.Add(login);
        var signup = new Button { Content = "Создать аккаунт на этом компьютере" }; signup.Click += (_, _) => Submit(true); panel.Children.Add(signup);
        panel.Children.Add(new TextBlock { Text = "Аккаунт и пароль хранятся только на этом ПК.\nОбщего каталога пользователей и восстановления пароля нет.", FontSize = 11, Opacity = .6, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 20, 0, 0) });
    }
}

public sealed class FormDialog : Window
{
    private readonly StackPanel panel = new() { Margin = new Thickness(24) };
    public FormDialog(Window owner, string title, double width = 460)
    {
        Owner = owner; Title = title; Width = width; SizeToContent = SizeToContent.Height; MaxHeight = 800; WindowStartupLocation = WindowStartupLocation.CenterOwner; ResizeMode = ResizeMode.NoResize;
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 23, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 18) });
    }
    public void Note(string text) => panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = .7, Margin = new Thickness(0, 8, 0, 8) });
    public void Add(UIElement element) => panel.Children.Add(element);
    public TextBox Field(string label, string value = "", bool multiline = false)
    {
        panel.Children.Add(new TextBlock { Text = label }); var field = new TextBox { Text = value, AcceptsReturn = multiline, MinHeight = multiline ? 80 : 0, TextWrapping = TextWrapping.Wrap }; panel.Children.Add(field); return field;
    }
    public CheckBox Check(string label, bool value = false) { var c = new CheckBox { Content = label, IsChecked = value }; panel.Children.Add(c); return c; }
    public Button Action(string label, Action action) { var b = new Button { Content = label }; b.Click += (_, _) => { try { action(); } catch (Exception ex) { System.Windows.MessageBox.Show(this, ex.Message, "Wisp"); } }; panel.Children.Add(b); return b; }
    public void Accept(string label = "Сохранить") => Action(label, () => DialogResult = true);
}
