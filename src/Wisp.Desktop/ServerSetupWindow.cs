using System.Windows;
using System.Windows.Controls;
using Wisp.Core;
namespace Wisp.Desktop;
public sealed class ServerSetupWindow: ThemedWindow
{
    public ServerConfig? Config{get;private set;}
    public ServerSetupWindow(string root,ServerConfig? current=null)
    {
        Title="Wisp · подключение к серверу";Width=560;SizeToContent=SizeToContent.Height;WindowStartupLocation=WindowStartupLocation.CenterScreen;
        var panel=new StackPanel{Margin=Ui.Insets(Ui.Xl)};Content=panel;
        panel.Children.Add(Brand.Image(64));panel.Children.Add(Ui.Label("Твой сервер Wisp",Ui.Heading));
        panel.Children.Add(new TextBlock{Text="Вставь адрес и публичный SHA-256 сертификата из установщика VPS.",TextWrapping=TextWrapping.Wrap,Margin=Ui.Insets(0,Ui.M)});
        panel.Children.Add(Ui.Label("HTTPS адрес"));var url=new TextBox{Text=current?.Url??"https://89.126.195.20:8443"};panel.Children.Add(url);
        panel.Children.Add(Ui.Label("SHA-256 сертификата"));var pin=new TextBox{Text=current?.CertificateSha256??"",TextWrapping=TextWrapping.Wrap};panel.Children.Add(pin);
        var error=new TextBlock{TextWrapping=TextWrapping.Wrap};error.SetResourceReference(TextBlock.ForegroundProperty,"Danger");panel.Children.Add(error);
        var button=Buttons.Create("Продолжить",()=>
        {
            try{var cfg=new ServerConfig(url.Text.Trim(),pin.Text.Replace(":","").Replace(" ","").Trim());cfg.Save(root);Config=cfg;DialogResult=true;}
            catch(Exception ex){error.Text=ex.Message;}
        },style:"Button.Primary");button.IsDefault=true;panel.Children.Add(button);
    }
}
