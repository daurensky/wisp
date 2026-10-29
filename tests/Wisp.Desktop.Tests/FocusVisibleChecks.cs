using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Wisp.Desktop;
using Xunit;

namespace Wisp.Desktop.Tests;

/// <summary>Runs in the existing real STA Application, with actual WPF focus transfer and native popup trees.</summary>
internal static class FocusVisibleChecks
{
    public static void Check(Window owner)
    {
        bool reduced=Motion.Reduce;Motion.SetReduced(true);
        var navigation=Buttons.Create("Navigation",()=>{},style:"Button.Row");
        var settings=Buttons.Tool("settings","Settings",()=>{});
        var voice=new ToggleIconButton{IconOff="mic",IconOn="mic-off",ToolTipOff="Mute",ToolTipOn="Unmute"};
        KeyboardNavigation.SetTabIndex(navigation,0);KeyboardNavigation.SetTabIndex(settings,1);KeyboardNavigation.SetTabIndex(voice,2);
        var content=new StackPanel{Margin=new Thickness(24),Children={navigation,settings,voice}};
        KeyboardNavigation.SetTabNavigation(content,KeyboardNavigationMode.Cycle);
        var probe=new ThemedWindow{Title="Focus-visible lifecycle",Width=400,Height=320,Owner=owner,Content=content};
        var other=new ThemedWindow{Title="Activation transfer",Width=220,Height=140,Owner=probe,Content=new Button{Content="Another active window"}};
        var tip=new ToolTip{PlacementTarget=settings,Content="Settings help"};settings.ToolTip=tip;
        var gain=new Slider{Minimum=0,Maximum=100,Value=50,Width=180};
        var action=new MenuItem{Header="Settings action"};
        var volume=new MenuItem{Header=gain,StaysOpenOnClick=true};
        var menu=new ContextMenu{PlacementTarget=settings,Items={action,volume}};
        try
        {
            probe.Show();probe.Activate();PumpUntil(probe,()=>probe.IsActive,"probe activation");
            navigation.Focus();PointerDown(navigation);Pump(probe);
            Assert.True(navigation.IsKeyboardFocused);Assert.False(PointerFocus.GetKeyboardFocus(navigation));Assert.Equal(0,Ring(navigation).Opacity);
            // The old handler marked *any* preview key as keyboard focus. Alt
            // must not turn a pointer-focused button into a keyboard outline.
            RaiseKey(navigation,Key.LeftAlt);Pump(probe);Assert.False(PointerFocus.GetKeyboardFocus(navigation));
            RaiseKey(navigation,Key.Tab);
            Assert.True(navigation.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)));Pump(probe);
            Assert.True(settings.IsKeyboardFocused);Assert.True(PointerFocus.GetKeyboardFocus(settings));Assert.Equal(1,Ring(settings).Opacity);
            Assert.True(settings.Focusable);Assert.True(voice.Focusable);

            tip.IsOpen=true;PumpUntil(probe,()=>tip.IsOpen,"native tooltip opens");
            other.Show();other.Activate();PumpUntil(probe,()=>other.IsActive&&!probe.IsActive,"window deactivation");
            Assert.False(PointerFocus.GetKeyboardFocus(settings));Assert.False(PointerFocus.GetPointerOver(settings));Assert.False(PointerFocus.GetIsWindowActive(settings));Assert.Equal(0,Ring(settings).Opacity);Assert.False(tip.IsOpen);
            other.Hide();probe.Activate();PumpUntil(probe,()=>probe.IsActive,"window activation return");
            Assert.False(PointerFocus.GetKeyboardFocus(settings));Assert.Equal(0,Ring(settings).Opacity);Assert.False(tip.IsOpen);
            RaiseKey(settings,Key.Tab);Assert.True(settings.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)));Pump(probe);
            Assert.True(voice.IsKeyboardFocused);Assert.True(PointerFocus.GetKeyboardFocus(voice));Assert.Equal(1,Ring(voice).Opacity);

            // A pointer gesture after keyboard navigation clears the cue, while
            // losing activation cancels the pressed transform even without MouseUp.
            PointerDown(voice);voice.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left){RoutedEvent=UIElement.PreviewMouseLeftButtonDownEvent});Pump(probe);
            Assert.False(PointerFocus.GetKeyboardFocus(voice));
            var root=Assert.IsType<Grid>(voice.Template.FindName("Root",voice));var scale=Assert.IsType<ScaleTransform>(root.RenderTransform);
            Assert.Equal(.94,scale.ScaleX);
            other.Show();other.Activate();PumpUntil(probe,()=>other.IsActive,"pressed activation transfer");
            Assert.Equal(1,scale.ScaleX);Assert.Equal(1,scale.ScaleY);
            Assert.Equal(0,Assert.IsType<Border>(voice.Template.FindName("HoverSurface",voice)).Opacity);
            other.Hide();probe.Activate();PumpUntil(probe,()=>probe.IsActive,"return before popup check");
            Assert.False(PointerFocus.GetKeyboardFocus(voice));

            PointerDown(settings);menu.IsOpen=true;PumpUntil(probe,()=>menu.IsOpen&&action.IsLoaded,"native context menu opens");
            action.Focus();RaiseKey(action,Key.Tab);gain.Focus();Pump(probe);
            Assert.True(gain.IsKeyboardFocused);Assert.True(PointerFocus.GetKeyboardFocus(gain));Assert.True(PointerFocus.GetIsWindowActive(gain));
            Assert.Equal(1,Assert.IsType<Border>(gain.Template.FindName("FocusRing",gain)).Opacity);
            // Arrow navigation still reaches the actual native slider control.
            double before=gain.Value;gain.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(gain),Environment.TickCount,Key.Right){RoutedEvent=Keyboard.KeyDownEvent});Pump(probe);
            Assert.True(gain.Value>before);
            other.Show();other.Activate();PumpUntil(probe,()=>other.IsActive,"popup owner deactivation");
            Assert.False(menu.IsOpen);Assert.False(PointerFocus.GetKeyboardFocus(gain));
            UiCheckEvidence.Write("focus_mouse_tab_alt_deactivation_popup_and_pressed_cleanup",true);
        }
        finally
        {
            tip.IsOpen=false;menu.IsOpen=false;other.Close();probe.Close();Motion.SetReduced(reduced);owner.Activate();Pump(owner);
        }
    }
    private static Border Ring(Button button)=>Assert.IsType<Border>(button.Template.FindName("FocusRing",button));
    private static void RaiseKey(UIElement target,Key key)=>target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(target),Environment.TickCount,key){RoutedEvent=Keyboard.PreviewKeyDownEvent});
    private static void PointerDown(UIElement target)=>target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left){RoutedEvent=Mouse.PreviewMouseDownEvent});
    private static void Pump(Window window){window.UpdateLayout();window.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);window.UpdateLayout();}
    private static void PumpUntil(Window owner,Func<bool> condition,string operation)
    {
        var timer=Stopwatch.StartNew();while(!condition()&&timer.Elapsed<TimeSpan.FromSeconds(3)){Pump(owner);Thread.Sleep(4);}
        Assert.True(condition(),"Focus-visible check timed out: "+operation);
    }
}
