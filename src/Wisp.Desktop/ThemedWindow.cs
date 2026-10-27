using System.Windows;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Shell;
using System.Windows.Media;
using System.Runtime.InteropServices;

namespace Wisp.Desktop;

/// <summary>Explicitly applies the shared style to every derived application window.</summary>
public class ThemedWindow : Window
{
    private readonly WindowChrome chrome;
    private HwndSource? source;
    private bool immersive;
    public static readonly DependencyProperty TitleBarVisibilityProperty = DependencyProperty.Register(nameof(TitleBarVisibility), typeof(Visibility), typeof(ThemedWindow), new PropertyMetadata(Visibility.Visible));
    public Visibility TitleBarVisibility { get => (Visibility)GetValue(TitleBarVisibilityProperty); private set => SetValue(TitleBarVisibilityProperty, value); }
    public bool IsImmersive
    {
        get => immersive;
        set
        {
            immersive = value;
            TitleBarVisibility = value ? Visibility.Collapsed : Visibility.Visible;
            WindowChrome.SetWindowChrome(this, value ? null : chrome);
        }
    }
    public ThemedWindow()
    {
        // WPF implicit Window styles do not match custom Window subclasses.
        SetResourceReference(StyleProperty, "WispWindowStyle");
        chrome = new WindowChrome
        {
            CaptionHeight = Ui.Metric("Size.TitleBar"), ResizeBorderThickness = SystemParameters.WindowResizeBorderThickness,
            GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false
        };
        WindowChrome.SetWindowChrome(this, chrome);
        SourceInitialized += (_, _) =>
        {
            source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            source?.AddHook(WindowMessage);
            ApplyTitleBar();
        };
        ThemeManager.Changed += ApplyTitleBar;
        Deactivated += (_, _) => StopAnimations(this);
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) StopAnimations(this); UpdateMaximizeIcon(); };
        Motion.Changed += StopOwnAnimations;
        Closed += (_, _) => { ThemeManager.Changed -= ApplyTitleBar; Motion.Changed -= StopOwnAnimations; source?.RemoveHook(WindowMessage); };
    }
    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (GetTemplateChild("PART_Minimize") is Button minimize)
        {
            minimize.Click += (_, _) => SystemCommands.MinimizeWindow(this);
            minimize.Visibility = ResizeMode == ResizeMode.NoResize ? Visibility.Collapsed : Visibility.Visible;
        }
        if (GetTemplateChild("PART_Maximize") is Button maximize)
        {
            maximize.Click += (_, _) => { if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this); else SystemCommands.MaximizeWindow(this); };
            maximize.Visibility = ResizeMode is ResizeMode.NoResize or ResizeMode.CanMinimize ? Visibility.Collapsed : Visibility.Visible;
        }
        if (GetTemplateChild("PART_Close") is Button close) close.Click += (_, _) => Close();
        UpdateMaximizeIcon();
    }
    private void UpdateMaximizeIcon()
    {
        if (GetTemplateChild("PART_Maximize") is not Button button) return;
        button.Content = new Icon { Kind = WindowState == WindowState.Maximized ? "panels-top-left" : "square", Width = Ui.Metric("Size.InlineIcon"), Height = Ui.Metric("Size.InlineIcon") };
        button.ToolTip = WindowState == WindowState.Maximized ? "Восстановить" : "Развернуть";
        Ui.Name(button, (string)button.ToolTip);
    }
    private void StopOwnAnimations() => StopAnimations(this);
    private static void StopAnimations(DependencyObject element)
    {
        var children = Enumerable.Range(0, VisualTreeHelper.GetChildrenCount(element)).Select(index => VisualTreeHelper.GetChild(element, index)).ToArray();
        if (element is FrameworkElement view) Motion.Stop(view);
        foreach (var child in children) StopAnimations(child);
    }
    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Physical monitor coordinates, including negative origins and per-monitor DPI.
        if (message == 0x0024 && !immersive)
        {
            IntPtr monitor = MonitorFromWindow(hwnd, 2);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
            {
                var limits = Marshal.PtrToStructure<MinMaxInfo>(lParam);
                limits.MaxPosition = new NativePoint(info.Work.Left - info.Monitor.Left, info.Work.Top - info.Monitor.Top);
                limits.MaxSize = new NativePoint(info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top);
                Marshal.StructureToPtr(limits, lParam, false);
            }
        }
        return IntPtr.Zero;
    }
    private void ApplyTitleBar()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        int dark = ThemeManager.IsLight ? 0 : 1;
        if (DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int)) != 0)
            DwmSetWindowAttribute(handle, 19, ref dark, sizeof(int));
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint(int x, int y) { public int X = x, Y = y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo { public NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
}
