using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Wisp.Core;
using Wisp.Desktop;
using Xunit;

namespace Wisp.Desktop.Tests;

/// <summary>Native mouse movement proves the settings hit area survives crossing the channel boundary.</summary>
internal static class ChannelRowChecks
{
    public static void Check(Window owner)
    {
        Assert.True(GetCursorPos(out var originalPointer));
        bool light=ThemeManager.IsLight;
        var voice=new Channel("voice-hit-check","community","Голосовой канал",true);
        var text=new Channel("text-hit-check","community","Текстовый канал");
        int joins=0,edits=0,chats=0;
        var voiceRow=new ChannelRow(voice,2,false,true,()=>joins++,()=>edits++,()=>chats++);
        var textRow=new ChannelRow(text,0,true,true,()=>joins++,()=>edits++,()=>chats++);
        var memberRow=new ChannelRow(voice with{Id="member-channel"},2,false,false,()=>joins++,()=>edits++,()=>chats++);
        var legacy=LegacyRow(out var legacyMain,out var legacyGear);
        var content=new StackPanel{Margin=new Thickness(20)};
        content.Children.Add(voiceRow);content.Children.Add(textRow);content.Children.Add(memberRow);content.Children.Add(legacy);
        var probe=new ThemedWindow{Title="Channel hit areas",Content=content,Width=400,Height=350,WindowStartupLocation=WindowStartupLocation.CenterScreen};
        try
        {
            probe.Show();probe.Activate();Pump(probe);Keyboard.ClearFocus();
            foreach(bool useLight in new[]{false,true})
            {
                if(ThemeManager.IsLight!=useLight)ThemeManager.Toggle();Pump(probe);
                Move(probe,new Point(10,probe.ActualHeight-20));
                Assert.Equal(Visibility.Hidden,voiceRow.SettingsButton.Visibility);
                Assert.Equal(Visibility.Visible,textRow.SettingsButton.Visibility);
                Assert.Equal(Visibility.Collapsed,memberRow.SettingsButton.Visibility);
                Assert.Equal(Visibility.Collapsed,textRow.ChatButton.Visibility);
                Assert.False(HasButtonAncestor(voiceRow.SettingsButton));Assert.False(HasButtonAncestor(voiceRow.MainButton));

                // Recreate the previous layout literally: transparent WPF Buttons have
                // non-hit-testable margins and the parent DockPanel had no background.
                Move(probe,Center(legacyMain,probe));Wait(probe,()=>legacyGear.IsVisible,"legacy gear appears");
                var legacyGap=legacyGear.TranslatePoint(new Point(-2,legacyGear.ActualHeight/2),probe);
                Move(probe,legacyGap);Wait(probe,()=>!legacy.IsMouseOver,"legacy sibling margin is outside row hit area");
                Assert.Equal(Visibility.Hidden,legacyGear.Visibility);
                UiCheckEvidence.Write("legacy_channel_hover_gap_"+(useLight?"light":"dark"),true);

                Move(probe,Center(voiceRow.MainButton,probe));Wait(probe,()=>voiceRow.SettingsButton.IsVisible,"voice gear appears");
                var stableWidth=voiceRow.MainButton.ActualWidth;
                var newGap=voiceRow.SettingsButton.TranslatePoint(new Point(-2,voiceRow.SettingsButton.ActualHeight/2),probe);
                Move(probe,newGap);Assert.True(voiceRow.IsMouseOver);Assert.True(voiceRow.SettingsButton.IsVisible);
                Move(probe,Center(voiceRow.SettingsButton,probe));Wait(probe,()=>voiceRow.SettingsButton.IsMouseOver,"gear receives pointer");
                var oldButton=voiceRow.SettingsButton;
                voiceRow.Update(voice with{Name="Обновлённый канал"},4,false,true,()=>joins++,()=>edits++,()=>chats++);
                Pump(probe);Assert.Same(oldButton,voiceRow.SettingsButton);Assert.True(voiceRow.SettingsButton.IsMouseOver);
                Assert.True(voiceRow.SettingsButton.IsVisible);Assert.Equal(stableWidth,voiceRow.MainButton.ActualWidth);
                int before=edits;Click();Wait(probe,()=>edits==before+1,"gear click opens settings only");Assert.Equal(0,joins);Assert.Equal(0,chats);

                var chrome=Assert.IsType<Border>(textRow.FindName("RowChrome"));
                Assert.Same(Application.Current.FindResource("Selected"),chrome.Background);
                Move(probe,Center(textRow.SettingsButton,probe));Assert.Same(Application.Current.FindResource("Selected"),chrome.Background);
                before=edits;Click();Wait(probe,()=>edits==before+1,"text gear click");Assert.Equal(0,joins);
                Move(probe,Center(voiceRow.ChatButton,probe));Click();Wait(probe,()=>chats==(useLight?2:1),"voice text chat action");Assert.Equal(0,joins);
                UiCheckEvidence.Write("native_channel_gear_click_and_update_"+(useLight?"light":"dark"),true);
            }
        }
        finally
        {
            probe.Close();if(ThemeManager.IsLight!=light)ThemeManager.Toggle();
            SetCursorPos(originalPointer.X,originalPointer.Y);owner.Activate();Pump(owner);
        }
    }

    private static DockPanel LegacyRow(out Button main,out Button gear)
    {
        var row=new DockPanel{Margin=new Thickness(4,10,4,0)};
        main=Buttons.Create("Legacy channel",()=>{},style:"Button.Row");
        gear=Buttons.Tool("settings","Legacy settings",()=>{});
        var style=new Style(typeof(Button),(Style)Application.Current.FindResource("Button.Icon"));
        style.Setters.Add(new Setter(UIElement.VisibilityProperty,Visibility.Hidden));
        var trigger=new DataTrigger{Binding=new Binding(nameof(UIElement.IsMouseOver)){Source=row},Value=true};
        trigger.Setters.Add(new Setter(UIElement.VisibilityProperty,Visibility.Visible));style.Triggers.Add(trigger);gear.Style=style;
        DockPanel.SetDock(gear,Dock.Right);row.Children.Add(gear);row.Children.Add(main);return row;
    }
    private static bool HasButtonAncestor(DependencyObject child)
    {
        for(var parent=VisualTreeHelper.GetParent(child);parent is not null;parent=VisualTreeHelper.GetParent(parent))if(parent is Button)return true;
        return false;
    }
    private static Point Center(FrameworkElement target,Window owner)=>target.TranslatePoint(new Point(target.ActualWidth/2,target.ActualHeight/2),owner);
    private static void Move(Window owner,Point local)
    {
        var screen=owner.PointToScreen(local);Assert.True(SetCursorPos((int)Math.Round(screen.X),(int)Math.Round(screen.Y)));
        Mouse.Synchronize();Pump(owner);
    }
    private static void Click(){mouse_event(0x0002,0,0,0,UIntPtr.Zero);mouse_event(0x0004,0,0,0,UIntPtr.Zero);}
    private static void Pump(Window window){window.UpdateLayout();window.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);window.UpdateLayout();}
    private static void Wait(Window window,Func<bool> ready,string name)
    {
        var watch=System.Diagnostics.Stopwatch.StartNew();
        while(!ready()&&watch.Elapsed<TimeSpan.FromSeconds(3)){Pump(window);Thread.Sleep(5);}
        Assert.True(ready(),name);
    }
    [StructLayout(LayoutKind.Sequential)]private struct NativePoint{public int X,Y;}
    [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool SetCursorPos(int x,int y);
    [DllImport("user32.dll")]private static extern void mouse_event(uint flags,uint x,uint y,uint data,UIntPtr extra);
}
