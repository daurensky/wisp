using System.Windows;
using System.Windows.Controls;
using Wisp.Core;
namespace Wisp.Desktop;

/// <summary>First-run endpoint setup, with the same field, card and typography resources as sign-in.</summary>
public sealed class ServerSetupWindow:ThemedWindow
{
    public ServerConfig? Config{get;private set;}
    public ServerSetupWindow(string root,ServerConfig? current=null)
    {
        Title="Wisp · Твой сервер";Width=560;MinWidth=400;SizeToContent=SizeToContent.Height;MaxHeight=SystemParameters.WorkArea.Height;ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterScreen;
        var panel=new StackPanel{Margin=Ui.Insets(Ui.Xxl,Ui.Xl)};
        Content=new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        var branding=new StackPanel{Orientation=Orientation.Horizontal,Margin=Ui.Insets(0,0,0,Ui.Xl)};
        branding.Children.Add(Brand.Image(48));var brand=Ui.Label("Wisp",28);brand.FontWeight=FontWeights.Bold;brand.Margin=Ui.Insets(Ui.M,0,0,0);branding.Children.Add(brand);panel.Children.Add(branding);
        var title=Ui.Label("Подключись к своему серверу",Ui.Heading);title.FontWeight=FontWeights.SemiBold;panel.Children.Add(title);
        var intro=new TextBlock{Text="Адрес и отпечаток сертификата можно получить у владельца Wisp-сервера.",TextWrapping=TextWrapping.Wrap,Margin=Ui.Insets(0,Ui.S,0,Ui.Xl)};Ui.Secondary(intro);panel.Children.Add(intro);
        panel.Children.Add(Ui.Label("HTTPS адрес",Ui.Caption,true));var url=new TextBox{Text=current?.Url??"https://89.126.195.20:8443",MaxLength=2048};Ui.Name(url,"HTTPS адрес сервера");panel.Children.Add(url);
        panel.Children.Add(Ui.Label("SHA-256 сертификата",Ui.Caption,true));var pin=new TextBox{Text=current?.CertificateSha256??"",TextWrapping=TextWrapping.Wrap,MinHeight=Ui.ControlHeight*2,MaxLength=191};Ui.Name(pin,"Публичный SHA-256 сертификата");panel.Children.Add(pin);
        var note=new TextBlock{Text="Отпечаток состоит из 64 символов. Он проверяет, что ты подключаешься к нужному серверу.",TextWrapping=TextWrapping.Wrap,FontSize=Ui.Caption,Margin=Ui.Insets(0,0,0,Ui.L)};Ui.Secondary(note);panel.Children.Add(note);
        var error=new TextBlock{TextWrapping=TextWrapping.Wrap,Visibility=Visibility.Collapsed,Margin=Ui.Insets(0,Ui.S)};error.SetResourceReference(TextBlock.ForegroundProperty,"Danger");panel.Children.Add(error);
        var button=Buttons.Create("Продолжить ко входу",()=>
        {
            try
            {
                var fingerprint=string.Concat(pin.Text.Where(c=>!char.IsWhiteSpace(c)&&c!=':'));
                var cfg=new ServerConfig(url.Text.Trim(),fingerprint);cfg.Save(root);Config=cfg;DialogResult=true;
            }
            catch(Exception ex){error.Text=ex.Message;error.Visibility=Visibility.Visible;}
        },"Сохранить адрес и перейти ко входу","arrow-right","Button.Primary");button.IsDefault=true;button.Margin=Ui.Insets(0,Ui.S,0,0);panel.Children.Add(button);
        Loaded+=(_,_)=>url.Focus();
    }
}
