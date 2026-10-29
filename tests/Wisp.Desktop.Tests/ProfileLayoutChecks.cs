using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Wisp.Core;
using Wisp.Desktop;
using Xunit;

namespace Wisp.Desktop.Tests;

/// <summary>Runs in ThemeTests' existing STA/Application, against real measured profile controls.</summary>
internal static class ProfileLayoutChecks
{
    public static void Check(Window owner,UserProfile profile,string gif)
    {
        bool wasLight=ThemeManager.IsLight,reduced=Motion.Reduce;
        var sample=profile with{About="First line\nSecond line",Status="dnd",AvatarPath=gif,BannerPath=gif};
        try
        {
            Motion.SetReduced(true);
            foreach(bool light in new[]{false,true})
            {
                if(ThemeManager.IsLight!=light)ThemeManager.Toggle();
                VerifyView(owner,sample,gif,light);
                VerifyEditor(owner,sample,gif,light);
            }
        }
        finally
        {
            if(ThemeManager.IsLight!=wasLight)ThemeManager.Toggle();
            Motion.SetReduced(reduced);
        }
    }

    private static void VerifyView(Window owner,UserProfile profile,string gif,bool light)
    {
        var view=new ProfileView(profile,gif,gif);
        var handle=ModalHost.Open(owner,"",new ScrollViewer{Content=view,VerticalScrollBarVisibility=ScrollBarVisibility.Auto},ProfileLayout.CardWidth,new ModalOptions(Headerless:true));
        try
        {
            Pump(owner);AssertBanner(view);
            var text=Assert.Single(Descendants(view).OfType<TextBlock>(),item=>item.Text==profile.About);
            Assert.InRange(text.TranslatePoint(new Point(),view).X,ProfileLayout.Inset-.1,ProfileLayout.Inset+.1);
            Assert.DoesNotContain(Descendants(view).OfType<Button>(),button=>Name(button) is "Изменить аватар" or "Изменить баннер");
            Save(owner,light?"profile-20x7-light":"profile-20x7-dark");
        }
        finally{handle.Close();Pump(owner);}
    }

    private static void VerifyEditor(Window owner,UserProfile profile,string gif,bool light)
    {
        UserProfile? accepted=null;
        var editor=new ProfileEditor(profile,gif,gif,value=>{accepted=value;return Task.FromResult(value);},initialEditing:true);
        var handle=ModalHost.Open(owner,"Редактирование профиля",new ScrollViewer{Content=editor,VerticalScrollBarVisibility=ScrollBarVisibility.Auto},ProfileLayout.CardWidth);
        try
        {
            Pump(owner);AssertBanner(editor);
            Assert.Empty(Descendants(editor).OfType<ComboBox>());
            Assert.DoesNotContain(Descendants(editor).OfType<TextBlock>(),text=>text.Text=="Статус");
            var about=Assert.Single(Descendants(editor).OfType<TextBox>(),field=>Name(field)=="О себе");
            Assert.Equal(VerticalAlignment.Top,about.VerticalContentAlignment);
            Assert.Equal(HorizontalAlignment.Left,about.HorizontalContentAlignment);
            Assert.Equal(TextAlignment.Left,about.TextAlignment);Assert.True(about.AcceptsReturn);
            Assert.Equal(TextWrapping.Wrap,about.TextWrapping);Assert.Equal(ScrollBarVisibility.Auto,about.VerticalScrollBarVisibility);
            Assert.Equal(Ui.Metric("Height.Multiline"),about.ActualHeight);
            Assert.True(about.Padding.Left>0&&about.Padding.Top>0);
            Assert.InRange(about.TranslatePoint(new Point(),editor).X,ProfileLayout.Inset-.1,ProfileLayout.Inset+.1);
            var host=Assert.IsType<ScrollViewer>(about.Template.FindName("PART_ContentHost",about));
            Assert.Equal(VerticalAlignment.Top,host.VerticalContentAlignment);
            Assert.Equal(HorizontalAlignment.Left,host.HorizontalContentAlignment);
            about.CaretIndex=0;Pump(owner);
            Assert.InRange(about.GetRectFromCharacterIndex(0).Y,0,about.Padding.Top+2);
            about.Text=string.Join("\n",Enumerable.Range(0,90).Select(i=>"Line "+i+": profile text"));Pump(owner);
            double height=about.ActualHeight;
            about.ScrollToEnd();Pump(owner);
            Assert.True(about.VerticalOffset>0);Assert.Equal(height,about.ActualHeight);
            about.ScrollToHome();about.Text="Saved about\nSecond line";Pump(owner);
            Save(owner,light?"profile-editor-20x7-light":"profile-editor-20x7-dark");
            var save=Assert.Single(Descendants(editor).OfType<Button>(),button=>Name(button)=="Сохранить");
            Assert.InRange(save.TranslatePoint(new Point(),editor).X,ProfileLayout.Inset-.1,ProfileLayout.Inset+.1);
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump(owner);
            Assert.NotNull(accepted);Assert.Equal("dnd",accepted.Status);Assert.Equal("Saved about\nSecond line",accepted.About);
            Assert.False(editor.IsEditing);AssertBanner(editor);
            Assert.Contains(Descendants(editor).OfType<TextBlock>(),text=>text.Text=="Не беспокоить");
        }
        finally{handle.Close();Pump(owner);}
    }

    private static void AssertBanner(FrameworkElement root)
    {
        var frame=Assert.Single(Descendants(root).OfType<AspectRatioFrame>());
        Assert.Equal(ProfileLayout.BannerRatio,frame.Ratio);
        Assert.Equal(400,frame.ActualWidth);Assert.Equal(140,frame.ActualHeight);
        Assert.InRange(frame.TranslatePoint(new Point(),root).X,ProfileLayout.Inset-.1,ProfileLayout.Inset+.1);
        var border=Assert.IsType<Border>(Descendants(frame).OfType<Border>().FirstOrDefault());
        Assert.True(border.ClipToBounds);
    }

    private static string Name(DependencyObject element)=>AutomationProperties.GetName(element);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach(var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {yield return child;foreach(var nested in Descendants(child))yield return nested;}
    }
    private static void Pump(Window owner)
    {
        owner.UpdateLayout();var frame=new DispatcherFrame();
        owner.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>frame.Continue=false));
        Dispatcher.PushFrame(frame);owner.UpdateLayout();
    }
    private static void Save(Window owner,string name)
    {
        if(Environment.GetEnvironmentVariable("GITHUB_WORKSPACE")is not{Length:>0}workspace)return;
        var directory=Path.Combine(workspace,"artifacts","test-results","ui-snapshots");Directory.CreateDirectory(directory);
        var bitmap=new RenderTargetBitmap((int)Math.Ceiling(owner.ActualWidth),(int)Math.Ceiling(owner.ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render(owner);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output=File.Create(Path.Combine(directory,name+".png"));encoder.Save(output);
    }
}
