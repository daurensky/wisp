using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Wisp.Desktop;

/// <summary>One input modality per owner window, including native popup trees.</summary>
public static class PointerFocus
{
    public static readonly DependencyProperty TrackProperty = DependencyProperty.RegisterAttached("Track", typeof(bool), typeof(PointerFocus), new PropertyMetadata(false, Attach));
    public static readonly DependencyProperty KeyboardFocusProperty = DependencyProperty.RegisterAttached("KeyboardFocus", typeof(bool), typeof(PointerFocus), new PropertyMetadata(false));
    private static readonly DependencyPropertyKey IsWindowActiveKey = DependencyProperty.RegisterAttachedReadOnly("IsWindowActive", typeof(bool), typeof(PointerFocus), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.Inherits));
    public static readonly DependencyProperty IsWindowActiveProperty = IsWindowActiveKey.DependencyProperty;
    private static readonly DependencyPropertyKey PointerOverKey = DependencyProperty.RegisterAttachedReadOnly("PointerOver", typeof(bool), typeof(PointerFocus), new PropertyMetadata(false));
    public static readonly DependencyProperty PointerOverProperty = PointerOverKey.DependencyProperty;
    private static readonly ConditionalWeakTable<Control, Tracker> trackers = new();
    private static readonly ConditionalWeakTable<Window, OwnerState> owners = new();

    static PointerFocus()
    {
        // ToolTipService creates ToolTip instances for string content. Looking only
        // at Control.ToolTip misses those instances and leaves their popup alive.
        EventManager.RegisterClassHandler(typeof(ToolTip), ToolTip.OpenedEvent, new RoutedEventHandler(TipOpened));
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler(MenuOpened));
        EventManager.RegisterClassHandler(typeof(FrameworkElement), ToolTipService.ToolTipOpeningEvent, new ToolTipEventHandler(TipOpening));
    }

    public static void SetTrack(DependencyObject d, bool value) => d.SetValue(TrackProperty, value);
    public static bool GetTrack(DependencyObject d) => (bool)d.GetValue(TrackProperty);
    public static void SetKeyboardFocus(DependencyObject d, bool value) => d.SetValue(KeyboardFocusProperty, value);
    public static bool GetKeyboardFocus(DependencyObject d) => (bool)d.GetValue(KeyboardFocusProperty);
    public static bool GetIsWindowActive(DependencyObject d) => (bool)d.GetValue(IsWindowActiveProperty);
    public static bool GetPointerOver(DependencyObject d) => (bool)d.GetValue(PointerOverProperty);

    private static void Attach(DependencyObject d, DependencyPropertyChangedEventArgs args)
    {
        if(d is not Control control)return;
        if((bool)args.NewValue)trackers.GetValue(control, value=>new Tracker(value)).Attach();
        else if(trackers.TryGetValue(control,out var tracker))tracker.Detach();
    }

    internal static Window? Owner(DependencyObject? element)
    {
        var visited = new HashSet<DependencyObject>();
        while(element is not null && visited.Add(element))
        {
            if(element is Window window)return window;
            if(Window.GetWindow(element) is{} direct)return direct;
            element=element switch
            {
                ContextMenu menu=>menu.PlacementTarget,
                ToolTip tip=>tip.PlacementTarget,
                Popup popup=>popup.PlacementTarget,
                Visual=>VisualTreeHelper.GetParent(element)??LogicalTreeHelper.GetParent(element),
                _=>LogicalTreeHelper.GetParent(element)
            };
        }
        return null;
    }
    private static OwnerState? State(DependencyObject element)=>Owner(element)is{} owner?owners.GetValue(owner,value=>new OwnerState(value)):null;
    private static bool Navigation(KeyEventArgs args)
    {
        if((Keyboard.Modifiers&ModifierKeys.Alt)!=0||args.Key==Key.System)return false;
        return args.Key is Key.Tab or Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown or Key.Enter or Key.Space or Key.Apps or Key.F6 or Key.F10;
    }
    private static void TipOpening(object sender,ToolTipEventArgs args)
    {
        if(sender is FrameworkElement element && (!element.IsMouseOver || Owner(element) is{IsActive:false}))args.Handled=true;
    }
    private static void TipOpened(object sender,RoutedEventArgs args)
    {
        if(sender is not ToolTip tip||State(tip) is not{} state)return;
        state.Tips.RemoveAll(reference=>!reference.TryGetTarget(out _));state.Tips.Add(new(tip));
        if(!state.Active)tip.IsOpen=false;
    }
    private static void MenuOpened(object sender,RoutedEventArgs args)
    {
        if(sender is not ContextMenu menu||State(menu) is not{} state)return;
        state.Menus.RemoveAll(reference=>!reference.TryGetTarget(out _));state.Menus.Add(new(menu));
        if(!state.Active)menu.IsOpen=false;
    }

    private sealed class Tracker(Control control)
    {
        private OwnerState? state;
        private bool attached;
        public void Attach()
        {
            if(attached)return;attached=true;
            control.Loaded+=Loaded;control.Unloaded+=Unloaded;
            control.AddHandler(Keyboard.PreviewKeyDownEvent,new KeyEventHandler(KeyDown),true);
            control.AddHandler(Mouse.PreviewMouseDownEvent,new MouseButtonEventHandler(MouseDown),true);
            control.MouseMove+=MouseMove;control.MouseEnter+=PointerChanged;control.MouseLeave+=PointerChanged;
            control.GotKeyboardFocus+=FocusChanged;control.LostKeyboardFocus+=FocusChanged;
            if(control.IsLoaded)Bind();
        }
        public void Detach()
        {
            if(!attached)return;attached=false;Release();
            control.Loaded-=Loaded;control.Unloaded-=Unloaded;
            control.RemoveHandler(Keyboard.PreviewKeyDownEvent,new KeyEventHandler(KeyDown));
            control.RemoveHandler(Mouse.PreviewMouseDownEvent,new MouseButtonEventHandler(MouseDown));
            control.MouseMove-=MouseMove;control.MouseEnter-=PointerChanged;control.MouseLeave-=PointerChanged;
            control.GotKeyboardFocus-=FocusChanged;control.LostKeyboardFocus-=FocusChanged;
        }
        private void Loaded(object sender,RoutedEventArgs args)=>Bind();
        private void Unloaded(object sender,RoutedEventArgs args)=>Release();
        private void Bind()
        {
            var next=State(control);
            if(state!=next){Release();state=next;state?.Add(control);}
            Refresh();
        }
        private void Release(){state?.Remove(control);state=null;SetKeyboardFocus(control,false);control.SetValue(PointerOverKey,false);}
        private void KeyDown(object sender,KeyEventArgs args){state??=State(control);if(Navigation(args))state?.KeyboardInput();Refresh();}
        private void MouseDown(object sender,MouseButtonEventArgs args){state??=State(control);state?.PointerInput(true);Refresh();}
        private void MouseMove(object sender,MouseEventArgs args){state?.PointerInput(false);RefreshPointer();}
        private void PointerChanged(object sender,MouseEventArgs args){RefreshPointer();if(!control.IsMouseOver)state?.CloseTips(control);}
        private void FocusChanged(object sender,KeyboardFocusChangedEventArgs args)=>Refresh();
        private void RefreshPointer()=>control.SetValue(PointerOverKey,state is{Active:true,PointerAllowed:true}&&control.IsMouseOver);
        private void Refresh()
        {
            bool active=state?.Active??true;control.SetValue(IsWindowActiveKey,active);
            SetKeyboardFocus(control,active&&state?.KeyboardIntent==true&&control.IsKeyboardFocusWithin);
            RefreshPointer();
        }
    }

    private sealed class OwnerState
    {
        private readonly Window owner;
        private readonly List<WeakReference<Control>> controls=new();
        public readonly List<WeakReference<ToolTip>> Tips=new();
        public readonly List<WeakReference<ContextMenu>> Menus=new();
        private bool everActive;
        private Point lastPointer;
        public bool Active { get;private set; }
        public bool KeyboardIntent { get;private set; }
        public bool PointerAllowed { get;private set; }=true;
        public OwnerState(Window owner)
        {
            this.owner=owner;Active=owner.IsActive;everActive=Active;lastPointer=Mouse.GetPosition(owner);
            owner.SetValue(IsWindowActiveKey,Active);
            owner.Activated+=Activated;owner.Deactivated+=Deactivated;owner.Closed+=Closed;
            owner.AddHandler(Keyboard.PreviewKeyDownEvent,new KeyEventHandler(KeyDown),true);
            owner.AddHandler(Mouse.PreviewMouseDownEvent,new MouseButtonEventHandler(MouseDown),true);
            owner.AddHandler(Mouse.PreviewMouseMoveEvent,new MouseEventHandler(MouseMove),true);
        }
        public void Add(Control control){controls.RemoveAll(reference=>!reference.TryGetTarget(out _));controls.Add(new(control));}
        public void Remove(Control control)=>controls.RemoveAll(reference=>!reference.TryGetTarget(out var target)||target==control);
        public void KeyboardInput(){if(!Active||KeyboardIntent)return;KeyboardIntent=true;Refresh();}
        public void PointerInput(bool clicked)
        {
            if(!Active)return;
            var point=Mouse.GetPosition(owner);bool moved=point!=lastPointer;lastPointer=point;
            if(!clicked&&!moved&&!PointerAllowed)return;
            if(KeyboardIntent||!PointerAllowed){KeyboardIntent=false;PointerAllowed=true;Refresh();}
        }
        private void KeyDown(object sender,KeyEventArgs args){if(Navigation(args))KeyboardInput();}
        private void MouseDown(object sender,MouseButtonEventArgs args)=>PointerInput(true);
        private void MouseMove(object sender,MouseEventArgs args)=>PointerInput(false);
        private void Activated(object? sender,EventArgs args)
        {
            Active=true;KeyboardIntent=false;PointerAllowed=!everActive;everActive=true;lastPointer=Mouse.GetPosition(owner);Refresh();
        }
        private void Deactivated(object? sender,EventArgs args)
        {
            Active=false;KeyboardIntent=false;PointerAllowed=false;Refresh();
            foreach(var reference in Tips.ToArray())if(reference.TryGetTarget(out var tip))tip.IsOpen=false;
            foreach(var reference in Menus.ToArray())if(reference.TryGetTarget(out var menu))menu.IsOpen=false;
            Tips.Clear();Menus.Clear();
            if(Mouse.Captured is DependencyObject captured&&Owner(captured)==owner)Mouse.Capture(null);
        }
        private void Closed(object? sender,EventArgs args)
        {
            Deactivated(sender,args);controls.Clear();
            owner.Activated-=Activated;owner.Deactivated-=Deactivated;owner.Closed-=Closed;
            owner.RemoveHandler(Keyboard.PreviewKeyDownEvent,new KeyEventHandler(KeyDown));
            owner.RemoveHandler(Mouse.PreviewMouseDownEvent,new MouseButtonEventHandler(MouseDown));
            owner.RemoveHandler(Mouse.PreviewMouseMoveEvent,new MouseEventHandler(MouseMove));
        }
        public void CloseTips(Control control)
        {
            foreach(var reference in Tips.ToArray())
                if(reference.TryGetTarget(out var tip)&&Within(tip.PlacementTarget,control))tip.IsOpen=false;
            Tips.RemoveAll(reference=>!reference.TryGetTarget(out var tip)||!tip.IsOpen);
        }
        private static bool Within(DependencyObject? element,DependencyObject parent)
        {
            while(element is not null)
            {
                if(element==parent)return true;
                element=element is Visual?VisualTreeHelper.GetParent(element)??LogicalTreeHelper.GetParent(element):LogicalTreeHelper.GetParent(element);
            }
            return false;
        }
        private void Refresh()
        {
            owner.SetValue(IsWindowActiveKey,Active);
            for(int index=controls.Count-1;index>=0;index--)
            {
                if(!controls[index].TryGetTarget(out var control)){controls.RemoveAt(index);continue;}
                control.SetValue(IsWindowActiveKey,Active);
                SetKeyboardFocus(control,Active&&KeyboardIntent&&control.IsKeyboardFocusWithin);
                control.SetValue(PointerOverKey,Active&&PointerAllowed&&control.IsMouseOver);
            }
        }
    }
}
