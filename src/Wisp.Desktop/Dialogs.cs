using System.Windows;
using System.Windows.Controls;
using Wisp.Core;

namespace Wisp.Desktop;

public sealed class AuthWindow : ThemedWindow
{
    public UserProfile? Profile { get; private set; }
    public AuthWindow(LocalStore store, ServerBackend? cloud = null)
    {
        Title="Wisp · Вход";Width=560;MinWidth=400;SizeToContent=SizeToContent.Height;MaxHeight=SystemParameters.WorkArea.Height;ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterScreen;
        var panel=new StackPanel{Margin=Ui.Insets(Ui.Xxl,Ui.Xl)};Content=new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        var branding=new StackPanel{Orientation=Orientation.Horizontal,Margin=Ui.Insets(0,0,0,Ui.Xl)};
        branding.Children.Add(Brand.Image(48));var brand=Ui.Label("Wisp",28);brand.FontWeight=FontWeights.Bold;brand.Margin=Ui.Insets(Ui.M,0,0,0);branding.Children.Add(brand);panel.Children.Add(branding);
        var heading=Ui.Label("С возвращением",Ui.Heading);heading.FontWeight=FontWeights.SemiBold;panel.Children.Add(heading);
        var intro=new TextBlock{Text="Продолжи разговор со своими.",TextWrapping=TextWrapping.Wrap,Margin=Ui.Insets(0,Ui.S,0,Ui.Xl)};Ui.Secondary(intro);panel.Children.Add(intro);
        panel.Children.Add(Ui.Label(cloud is null?"Имя пользователя":"Email",Ui.Caption,true));var user=new TextBox{MaxLength=254};Ui.Name(user,cloud is null?"Имя пользователя":"Email");panel.Children.Add(user);
        var nameLabel=Ui.Label("Имя пользователя",Ui.Caption,true);var username=new TextBox{MaxLength=32};Ui.Name(username,"Имя пользователя при регистрации");panel.Children.Add(nameLabel);panel.Children.Add(username);
        panel.Children.Add(Ui.Label("Пароль · от 8 символов",Ui.Caption,true));var pass=new PasswordBox{MaxLength=128};Ui.Name(pass,"Пароль");panel.Children.Add(pass);
        var error=new TextBlock{TextWrapping=TextWrapping.Wrap,Margin=Ui.Insets(0,Ui.S),Visibility=Visibility.Collapsed};error.SetResourceReference(TextBlock.ForegroundProperty,"Danger");panel.Children.Add(error);
        var progress=new ProgressBar{IsIndeterminate=true,Height=3,Margin=Ui.Insets(0,Ui.S),Visibility=Visibility.Collapsed};panel.Children.Add(progress);
        bool registering=false,busy=false;
        Button? submit=null;Button? mode=null;
        void ChangeMode()
        {
            heading.Text=registering?"Создать аккаунт":"С возвращением";intro.Text=registering?"Имя, по которому тебя узнают в сообществах.":"Продолжи разговор со своими.";
            nameLabel.Visibility=username.Visibility=registering&&cloud is not null?Visibility.Visible:Visibility.Collapsed;
            Title=registering?"Wisp · Регистрация":"Wisp · Вход";
            if(submit is not null)submit.Content=registering?"Создать аккаунт":"Войти";
            if(mode is not null)mode.Content=registering?"Уже есть аккаунт? Войти":"Нет аккаунта? Зарегистрироваться";
            error.Visibility=Visibility.Collapsed;
        }
        async void Submit()
        {
            if(busy)return;busy=true;panel.IsEnabled=false;progress.Visibility=Visibility.Visible;error.Visibility=Visibility.Collapsed;
            try
            {
                if(registering&&pass.Password.Length<8)throw new ArgumentException("Пароль должен содержать минимум 8 символов.");
                if(cloud is not null&&registering&&!System.Text.RegularExpressions.Regex.IsMatch(username.Text,@"^[\p{L}\p{N}_\-.]{3,32}$"))throw new ArgumentException("Имя: 3–32 буквы, цифры, _, - или точка.");
                Profile=cloud is not null?await cloud.SignInAsync(user.Text.Trim(),pass.Password,registering,username.Text):registering?store.Register(user.Text.Trim(),pass.Password):store.Login(user.Text.Trim(),pass.Password);
                if(Profile is null)throw new InvalidOperationException("Неверное имя или пароль.");DialogResult=true;
            }
            catch(Exception ex){error.Text=ex.Message;error.Visibility=Visibility.Visible;}
            finally{busy=false;panel.IsEnabled=true;progress.Visibility=Visibility.Collapsed;}
        }
        submit=Buttons.Create("Войти",Submit,"Войти · Enter","arrow-right","Button.Primary");submit.IsDefault=true;submit.Margin=Ui.Insets(0,Ui.S,0,Ui.S);panel.Children.Add(submit);
        mode=Buttons.Create("Нет аккаунта? Зарегистрироваться",()=>{registering=!registering;ChangeMode();},style:"Button.Ghost");panel.Children.Add(mode);
        var footer=new TextBlock{Text=cloud is null?"Локальный аккаунт. Вход сохраняется на этом ПК.":"Сервер: "+cloud.Config.BaseUri().Authority+"\nВход сохранится на этом компьютере.",FontSize=Ui.Caption,TextWrapping=TextWrapping.Wrap,Margin=Ui.Insets(0,Ui.L,0,0)};Ui.Secondary(footer);panel.Children.Add(footer);
        ChangeMode();Loaded+=(_,_)=>user.Focus();
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
