using System.Windows;
using Wisp.Core;
namespace Wisp.Desktop;
public partial class App:System.Windows.Application
{
    private LocalStore? store;
    private ServerBackend? cloud;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);ShutdownMode=ShutdownMode.OnExplicitShutdown;DiagnosticLog.WriteStartup();
        DispatcherUnhandledException+=(_,args)=>DiagnosticLog.Write("dispatcher",args.Exception);
        AppDomain.CurrentDomain.UnhandledException+=(_,args)=>{if(args.ExceptionObject is Exception ex)DiagnosticLog.Write("unhandled",ex);};
        TaskScheduler.UnobservedTaskException+=(_,args)=>DiagnosticLog.Write("background-task",args.Exception);
        try
        {
            var root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Wisp");Directory.CreateDirectory(root);ThemeManager.Initialize(root);
            ServerConfig? config=null;try{config=ServerConfig.Load(root);if(config is not null)_=config.BaseUri();}catch(Exception){config=null;}
            if(config is null||e.Args.Contains("--setup"))
            {
                var setup=new ServerSetupWindow(root,config);if(setup.ShowDialog()!=true||setup.Config is null){Shutdown();return;}config=setup.Config;
            }
            // The self-hosted database starts clean; old caches remain untouched in the old directory.
            store=new LocalStore(config.DataDirectory(root));cloud=new ServerBackend(config);
            cloud.SessionChanged+=token=>SessionVault.Save(store.RootDirectory,token);
            var saved=SessionVault.Read(store.RootDirectory);UserProfile? profile=null;string? warning=null;
            if(saved is not null)try{profile=await cloud.RestoreAsync(saved);}catch(Exception ex){warning="Не удалось восстановить вход: "+ex.Message;}
            if(profile is null)
            {
                var login=new AuthWindow(store,cloud);if(login.ShowDialog()!=true||login.Profile is null){Shutdown();return;}profile=login.Profile;
            }
            SessionVault.Save(store.RootDirectory,cloud.RefreshToken!);
            var cached=store.GetCommunities().SelectMany(c=>store.GetMembers(c.Id)).FirstOrDefault(p=>p.Id==profile.Id);
            if(cached is not null)profile=profile with{AvatarPath=profile.AvatarRevision==cached.AvatarRevision?cached.AvatarPath:null,BannerPath=profile.BannerAssetId==cached.BannerAssetId?cached.BannerPath:null};
            store.SaveProfile(profile);
            foreach(var snapshot in await cloud.CommunitiesAsync()){store.SaveSnapshot(snapshot);store.UpsertMember(snapshot.Community.Id,profile);}
            var window=new MainWindow(store,profile,cloud);MainWindow=window;ShutdownMode=ShutdownMode.OnMainWindowClose;window.Show();
            if(warning is not null)window.ReportNotification(warning,true);
        }
        catch(Exception ex){System.Windows.MessageBox.Show(ex.Message+"\nНастроить другой сервер: Wisp.exe --setup","Wisp — ошибка запуска");Shutdown(1);}
    }
    protected override void OnExit(ExitEventArgs e){cloud?.Dispose();store?.Dispose();base.OnExit(e);}
}
