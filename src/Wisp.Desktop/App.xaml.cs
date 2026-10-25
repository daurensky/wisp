using System.Windows;
using Wisp.Core;

namespace Wisp.Desktop;
public partial class App : System.Windows.Application
{
    private LocalStore? store;
    private FirebaseBackend? cloud;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            store = new LocalStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wisp"));
            ThemeManager.Initialize(store.RootDirectory);
            cloud = FirebaseBackend.Load(store.RootDirectory);
            var saved = SessionVault.Read(store.RootDirectory);
            UserProfile? profile = null;
            if (cloud is not null && saved?.StartsWith("firebase:") == true)
            {
                try { profile = await cloud.RestoreAsync(saved[9..]); } catch (Exception ex) { System.Windows.MessageBox.Show("Не удалось восстановить Firebase-сессию: " + ex.Message); }
            }
            else if (cloud is null && saved is not null && !saved.StartsWith("firebase:")) profile = store.RestoreSession(saved);
            if (profile is null)
            {
                var auth = new AuthWindow(store, cloud);
                if (auth.ShowDialog() != true || auth.Profile is null) { Shutdown(); return; }
                profile = auth.Profile;
                SessionVault.Save(store.RootDirectory, cloud is null ? store.CreateSession(profile.Id) : "firebase:" + cloud.RefreshToken);
            }
            store.SaveProfile(profile);
            if (cloud is not null)
            {
                SessionVault.Save(store.RootDirectory, "firebase:" + cloud.RefreshToken);
                foreach (var snapshot in await cloud.CommunitiesAsync()) { store.SaveSnapshot(snapshot); store.UpsertMember(snapshot.Community.Id, profile); }
            }
            var window = new MainWindow(store, profile, cloud);
            MainWindow = window;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            window.Show();
        }
        catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message, "Wisp — ошибка запуска"); Shutdown(1); }
    }
    protected override void OnExit(ExitEventArgs e) { cloud?.Dispose(); store?.Dispose(); base.OnExit(e); }
}
