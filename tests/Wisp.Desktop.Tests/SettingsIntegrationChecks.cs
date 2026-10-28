using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Wisp.Core;
using Wisp.Desktop;
using Xunit;

namespace Wisp.Desktop.Tests;

/// <summary>Runs inside the existing STA/Application fixture, without creating a second WPF Application.</summary>
internal static class SettingsIntegrationChecks
{
    public static void Check(Window owner,LocalStore store,UserProfile profile,Community community)
    {
        var snapshot=new CommunitySnapshot(community with{OwnerId=profile.Id},store.GetChannels(community.Id),[profile],new(){[profile.Id]="owner"});
        bool reduced=Motion.Reduce;
        try
        {
            Motion.SetReduced(true);VerifyRevokedManagement(owner,snapshot,profile.Id);
            VerifyOutOfOrderReload(owner,snapshot,profile.Id);VerifySoundGainEdit(owner,snapshot,profile.Id);
        }
        finally{Motion.SetReduced(reduced);}
    }

    private static CommunitySettingsContext Context(CommunitySnapshot snapshot,string userId,Func<Task<CommunitySnapshot>> reload,
        Func<CommunityManagement,Task>? apply=null,Action<string>? error=null,
        Func<string,Task<IReadOnlyList<MediaAsset>>>? assets=null,Action<string,float>? preview=null,Action<string,float>? previewVolume=null)=>new()
    {
        UserId=userId,Snapshot=snapshot,Online=true,Reload=reload,
        Apply=apply??(_=>Task.CompletedTask),Icon=_=>AvatarCatalog.Circle(null,96),SaveIcon=_=>Task.CompletedTask,
        Assets=assets??(_=>Task.FromResult<IReadOnlyList<MediaAsset>>(Array.Empty<MediaAsset>())),
        Upload=(_,_,_,_)=>Task.CompletedTask,Preview=preview??((_,_)=>{}),PreviewVolume=previewVolume,
        Confirm=_=>true,ReportError=error
    };

    private static void VerifyRevokedManagement(Window owner,CommunitySnapshot snapshot,string userId)
    {
        var remote=snapshot;int mutations=0;var errors=new List<string>();
        using var view=new CommunitySettingsView(Context(snapshot,userId,()=>Task.FromResult(remote),
            _=>{mutations++;return Task.CompletedTask;},errors.Add));
        var handle=ModalHost.Open(owner,"Настройки сервера",view,options:new ModalOptions(FullWindow:true));
        try
        {
            Pump(owner);
            var formerSave=Assert.Single(Descendants(view).OfType<Button>(),b=>Name(b)=="Сохранить изменения");
            remote=snapshot with{Community=snapshot.Community with{OwnerId="different-owner"},Roles=new(){[userId]="member"}};
            var reload=view.SelectSectionAsync("emoji");Wait(owner,()=>reload.IsCompleted,"revoked management reload");
            Assert.Contains(errors,message=>message.Contains("Нет права"));
            Assert.DoesNotContain(Descendants(view).OfType<Button>(),b=>Name(b) is "Загрузить эмодзи" or "Сохранить изменения");
            // Even a retained action from the previous manager page re-checks the latest authoritative role.
            formerSave.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump(owner);
            Assert.Equal(0,mutations);Assert.True(errors.Count>=2);
        }
        finally{handle.Close();Pump(owner);}
    }

    private static void VerifyOutOfOrderReload(Window owner,CommunitySnapshot snapshot,string userId)
    {
        var replies=new Queue<Task<CommunitySnapshot>>();replies.Enqueue(Task.FromResult(snapshot));
        var context=Context(snapshot,userId,()=>replies.Dequeue());
        using var view=new CommunitySettingsView(context);
        var handle=ModalHost.Open(owner,"Настройки сервера",view,options:new ModalOptions(FullWindow:true));
        var previous=SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(owner.Dispatcher));
            var oldReply=new TaskCompletionSource<CommunitySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            var fresh=snapshot with{Community=snapshot.Community with{Name="Fresh authoritative name"}};
            replies.Enqueue(oldReply.Task);replies.Enqueue(Task.FromResult(fresh));
            var oldRequest=view.SelectSectionAsync("overview");var newRequest=view.SelectSectionAsync("members");
            Wait(owner,()=>newRequest.IsCompleted,"new settings section");
            oldReply.SetResult(snapshot with{Community=snapshot.Community with{Name="Outdated response"}});
            Wait(owner,()=>oldRequest.IsCompleted,"out-of-order settings reload");
            Assert.Equal("members",view.Shell.SelectedKey);Assert.Equal(fresh,context.Snapshot);
            Assert.DoesNotContain(Descendants(view).OfType<TextBlock>(),text=>text.Text=="Outdated response");
        }
        finally{SynchronizationContext.SetSynchronizationContext(previous);handle.Close();Pump(owner);}
    }

    private static void VerifySoundGainEdit(Window owner,CommunitySnapshot snapshot,string userId)
    {
        var asset=new MediaAsset(new string('a',64),snapshot.Community.Id,"Quiet sound.wav","sound",Path.Combine(Path.GetTempPath(),"injected-preview.wav"),128,.3f);
        var commands=new List<CommunityManagement>();var preview=new List<(string Path,float Gain)>();var live=new List<(string Path,float Gain)>();var errors=new List<string>();
        using var view=new CommunitySettingsView(Context(snapshot,userId,()=>Task.FromResult(snapshot),command=>
        {
            commands.Add(command);if(command.Volume is{} gain)asset=asset with{Volume=gain};return Task.CompletedTask;
        },errors.Add,_=>Task.FromResult<IReadOnlyList<MediaAsset>>([asset]),(path,gain)=>preview.Add((path,gain)),(path,gain)=>live.Add((path,gain))),"sound");
        var handle=ModalHost.Open(owner,"Настройки сервера",view,options:new ModalOptions(FullWindow:true));
        Exception? failure=null;
        try
        {
            Pump(owner);
            owner.Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>
            {
                var form=Descendants(owner).OfType<FormDialog>().LastOrDefault();
                try
                {
                    Assert.NotNull(form);
                    var slider=Assert.Single(Descendants(form!).OfType<Slider>(),s=>Name(s)=="Громкость серверного звука");
                    Assert.InRange(slider.Value,29.999,30.001);slider.Value=75;
                    Assert.Single(Descendants(form!).OfType<Button>(),b=>Name(b)=="Прослушать").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Single(Descendants(form!).OfType<Button>(),b=>Name(b)=="Сохранить · Enter").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                catch(Exception error){failure=error;form?.Close();}
            }));
            Assert.Single(Descendants(view).OfType<Button>(),b=>Name(b)=="Изменить").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(owner);if(failure is not null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            Assert.Empty(errors);var command=Assert.Single(commands);Assert.Equal("renameAsset",command.Action);Assert.Equal("sound",command.Kind);Assert.Equal(asset.Id,command.AssetId);Assert.Equal(.75f,command.Volume);
            Assert.Contains((asset.LocalPath,.75f),preview);Assert.Contains((asset.LocalPath,.75f),live);
        }
        finally{handle.Close();Pump(owner);}
    }

    private static string Name(DependencyObject item)=>AutomationProperties.GetName(item);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var visited=new HashSet<DependencyObject>();var queue=new Queue<DependencyObject>();queue.Enqueue(root);
        while(queue.TryDequeue(out var current))
        {
            if(!visited.Add(current))continue;if(current!=root)yield return current;
            foreach(var child in LogicalTreeHelper.GetChildren(current).OfType<DependencyObject>())queue.Enqueue(child);
            if(current is Visual or System.Windows.Media.Media3D.Visual3D)for(int i=0;i<VisualTreeHelper.GetChildrenCount(current);i++)queue.Enqueue(VisualTreeHelper.GetChild(current,i));
        }
    }
    private static void Pump(Window owner)
    {
        owner.UpdateLayout();owner.Dispatcher.Invoke(DispatcherPriority.Background,new Action(()=>{}));
    }
    private static void Wait(Window owner,Func<bool> condition,string scenario)
    {
        if(condition()){Pump(owner);return;}
        var frame=new DispatcherFrame();var deadline=DateTime.UtcNow.AddSeconds(2);var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(10)};
        timer.Tick+=(_,_)=>{if(condition()||DateTime.UtcNow>=deadline){timer.Stop();frame.Continue=false;}};
        timer.Start();Dispatcher.PushFrame(frame);Pump(owner);Assert.True(condition(),scenario);
    }
}
