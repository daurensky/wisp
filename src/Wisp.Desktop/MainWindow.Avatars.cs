using System.Windows;
using System.Windows.Controls;
using Wisp.Core;

namespace Wisp.Desktop;

public sealed partial class MainWindow
{
    // Weak hosts let visible/recycled message avatars refresh without clearing chat,
    // changing scroll/selection, or retaining old virtualized message trees.
    private readonly List<(string AuthorId,WeakReference<ContentControl> Host)> messageAvatarViews=new();

    private FrameworkElement CreateMessageAvatar(string authorId,UserProfile? author,FrameworkElement scope)
    {
        double size=Ui.Metric("Size.Avatar");
        var host=new ContentControl{Name="ChatAvatar",Width=size,Height=size,HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Top};
        UpdateMessageAvatar(host,author,scope);
        messageAvatarViews.RemoveAll(entry=>!entry.Host.TryGetTarget(out _));
        messageAvatarViews.Add((authorId,new(host)));return host;
    }

    private void UpdateMessageAvatar(ContentControl host,UserProfile? person,FrameworkElement? scope=null)
    {
        var path=person is null?null:avatars.Resolve(person,store);
        string key=person?.AvatarRevision+":"+(person?.AvatarCoverFrame??0)+":"+path;
        if(Equals(host.Tag,key))return;
        host.Tag=key;
        host.Content=AvatarCatalog.Circle(path,Ui.Metric("Size.Avatar"),GifPlaybackMode.Always,scope??host,person?.AvatarCoverFrame??0);
    }

    private void RefreshMessageAvatars(IReadOnlyDictionary<string,UserProfile> people)
    {
        for(int index=messageAvatarViews.Count-1;index>=0;index--)
        {
            var entry=messageAvatarViews[index];
            if(!entry.Host.TryGetTarget(out var host)){messageAvatarViews.RemoveAt(index);continue;}
            if(people.TryGetValue(entry.AuthorId,out var person))UpdateMessageAvatar(host,person);
        }
    }
}
