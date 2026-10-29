using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
namespace Wisp.Desktop;

/// <summary>Native relative wheel commands for pixel viewers; no timer, easing backlog or per-frame virtual-list relayout.</summary>
public static class SmoothScroll
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached("Enabled",typeof(bool),typeof(SmoothScroll),new PropertyMetadata(false,Changed));
    public static bool GetEnabled(DependencyObject d)=>(bool)d.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject d,bool value)=>d.SetValue(EnabledProperty,value);
    private static void Changed(DependencyObject d,DependencyPropertyChangedEventArgs e)
    {
        if(d is not ScrollViewer view)return;
        view.PreviewMouseWheel-=Wheel;
        if((bool)e.NewValue)view.PreviewMouseWheel+=Wheel;
    }
    private static readonly DependencyProperty RemainderProperty=DependencyProperty.RegisterAttached("Remainder",typeof(double),typeof(SmoothScroll),new PropertyMetadata(0d));
    private static void Wheel(object sender,MouseWheelEventArgs e)
    {
        if(e.Handled||e.Delta==0||(Keyboard.Modifiers&ModifierKeys.Control)!=0||Mouse.Captured is not null)return;
        var view=(ScrollViewer)sender;bool horizontal=(Keyboard.Modifiers&ModifierKeys.Shift)!=0;
        ScrollViewer? nearest=null;
        for(DependencyObject? p=e.OriginalSource as DependencyObject;p is not null;p=Parent(p))
            if(p is ScrollViewer candidate&&CanMove(candidate,horizontal,e.Delta)){nearest=candidate;break;}
        if(!ReferenceEquals(nearest,view))return;
        int lines=SystemParameters.WheelScrollLines;if(lines==0)return;
        if(lines<0)
        {
            if(horizontal){if(e.Delta>0)view.PageLeft();else view.PageRight();}
            else{if(e.Delta>0)view.PageUp();else view.PageDown();}
        }
        else
        {
            double pending=(double)view.GetValue(RemainderProperty)+e.Delta/120d*Math.Max(1,lines);
            int count=(int)Math.Clamp(Math.Truncate(pending),-1000,1000);
            view.SetValue(RemainderProperty,pending-count);
            for(int i=0;i<Math.Abs(count);i++)
                if(horizontal){if(count>0)view.LineLeft();else view.LineRight();}
                else{if(count>0)view.LineUp();else view.LineDown();}
        }
        e.Handled=true;
    }
    private static bool CanMove(ScrollViewer v,bool x,int delta)=>delta>0?(x?v.HorizontalOffset:v.VerticalOffset)>0:(x?v.HorizontalOffset<v.ScrollableWidth:v.VerticalOffset<v.ScrollableHeight);
    private static DependencyObject? Parent(DependencyObject d)=>d is Visual or System.Windows.Media.Media3D.Visual3D?VisualTreeHelper.GetParent(d):LogicalTreeHelper.GetParent(d);
}
