using System.Windows;
using Wisp.Core;

namespace Wisp.Desktop;
public partial class App : System.Windows.Application
{
    private LocalStore? store;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            store = new LocalStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wisp"));
            var auth = new AuthWindow(store);
            if (auth.ShowDialog() != true || auth.Profile is null) { Shutdown(); return; }
            var window = new MainWindow(store, auth.Profile);
            MainWindow = window;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            window.Show();
        }
        catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message, "Wisp — ошибка запуска"); Shutdown(1); }
    }
    protected override void OnExit(ExitEventArgs e) { store?.Dispose(); base.OnExit(e); }
}
