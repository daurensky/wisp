using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Wisp.Desktop;

/// <summary>A single-window video surface, dockable picture-in-picture and monitor fullscreen.</summary>
public sealed class ScreenViewer
{
    private readonly Window owner;
    private readonly Grid layer;
    private readonly Border surface;
    public Image Target { get; } = new() { Stretch = Stretch.Uniform };
    private readonly Button mini, full, mute;
    private readonly TextBlock caption;
    private readonly string title;
    private WriteableBitmap? bitmap;
    private bool closed, fullscreen, muted;
    private (WindowStyle Style, ResizeMode Resize, WindowState State, Rect Bounds, bool Topmost, double MinWidth, double MinHeight) previous;
    public bool IsMini { get; private set; }
    public bool IsFullscreen => fullscreen;
    public event Action? Closed;
    public event Action<bool>? MuteChanged;
    public ScreenViewer(Window owner, string title)
    {
        this.owner = owner; this.title = title; layer = ModalHost.Surface(owner);
        var dock = new DockPanel(); var bar = new DockPanel { LastChildFill = true, Margin = Ui.Insets(Ui.S) };
        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        ToggleIconButton Tool(string normal, string active, string tooltip, Action action)
        {
            var button = new ToggleIconButton { IconOff = normal, IconOn = active, ToolTipOff = tooltip, ToolTipOn = tooltip };
            button.Click += (_, _) => action(); tools.Children.Add(button); return button;
        }
        mini = Tool("arrow-down-right", "arrow-up-left", "Уменьшить справа снизу / развернуть во всё окно", ToggleMini);
        full = Tool("maximize", "minimize", "На весь экран · F11", ToggleFullscreen);
        mute = Tool("volume-2", "volume-x", "Выключить / включить звук демонстрации", () => { muted = !muted; ((ToggleIconButton)mute!).IsChecked = muted; MuteChanged?.Invoke(muted); });
        tools.Children.Add(Buttons.Tool("x", "Закрыть просмотр", Close)); DockPanel.SetDock(tools, Dock.Right); bar.Children.Add(tools);
        caption = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        bar.Children.Add(caption);
        DockPanel.SetDock(bar, Dock.Top); dock.Children.Add(bar); dock.Children.Add(Target);
        surface = new Border { BorderThickness = new Thickness(Ui.Metric("Size.Hairline")), Child = dock, Focusable = true };
        surface.SetResourceReference(Border.BackgroundProperty, "VideoBackground");
        surface.SetResourceReference(Border.BorderBrushProperty, "Card");
        surface.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; if (fullscreen) ToggleFullscreen(); else if (!IsMini) ToggleMini(); } else if (e.Key == Key.F11) { e.Handled = true; ToggleFullscreen(); } };
        Target.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) { e.Handled = true; ToggleFullscreen(); } };
        layer.Children.Insert(Math.Min(1, layer.Children.Count), surface); surface.Focus();
    }
    public void ToggleMini()
    {
        if (fullscreen) ToggleFullscreen(); IsMini = !IsMini;
        surface.Width = IsMini ? Ui.Metric("Width.PictureInPicture") : double.NaN; surface.Height = IsMini ? Ui.Metric("Height.PictureInPicture") : double.NaN;
        surface.HorizontalAlignment = IsMini ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        surface.VerticalAlignment = IsMini ? VerticalAlignment.Bottom : VerticalAlignment.Stretch;
        surface.Margin = IsMini ? Ui.Insets(Ui.L) : Ui.Insets(0); surface.CornerRadius = IsMini ? Ui.Radius("Radius.Dialog") : new CornerRadius(0);
        ((ToggleIconButton)mini).IsChecked = IsMini;
    }
    public void ToggleFullscreen()
    {
        if (fullscreen)
        {
            fullscreen = false; owner.WindowStyle = previous.Style; owner.ResizeMode = previous.Resize; owner.Topmost = previous.Topmost;
            owner.WindowState = WindowState.Normal; owner.MinWidth = previous.MinWidth; owner.MinHeight = previous.MinHeight; owner.Left = previous.Bounds.Left; owner.Top = previous.Bounds.Top; owner.Width = previous.Bounds.Width; owner.Height = previous.Bounds.Height; owner.WindowState = previous.State;
            if (owner is ThemedWindow restored) restored.IsImmersive = false;
            ((ToggleIconButton)full).IsChecked = false; return;
        }
        if (IsMini) ToggleMini();
        previous = (owner.WindowStyle, owner.ResizeMode, owner.WindowState, owner.WindowState == WindowState.Normal ? new Rect(owner.Left, owner.Top, owner.Width, owner.Height) : owner.RestoreBounds, owner.Topmost, owner.MinWidth, owner.MinHeight);
        var handle = new WindowInteropHelper(owner).Handle; var bounds = System.Windows.Forms.Screen.FromHandle(handle).Bounds;
        owner.MinWidth = 0; owner.MinHeight = 0; owner.WindowState = WindowState.Normal; owner.WindowStyle = WindowStyle.None; owner.ResizeMode = ResizeMode.NoResize; owner.Topmost = true;
        if (owner is ThemedWindow immersive) immersive.IsImmersive = true;
        SetWindowPos(handle, new IntPtr(-1), bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0x0040); fullscreen = true; ((ToggleIconButton)full).IsChecked = true; surface.Focus();
    }
    public void SetPaused(bool paused) => caption.Text = title + (paused ? " · окно свёрнуто" : "");
    public void Present(byte[] bgra, int width, int height)
    {
        if (closed || bgra.Length < width * height * 4) return;
        if (bitmap is null || bitmap.PixelWidth != width || bitmap.PixelHeight != height) { bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null); Target.Source = bitmap; }
        bitmap.WritePixels(new Int32Rect(0, 0, width, height), bgra, width * 4, 0);
    }
    public void Close() { if (closed) return; if (fullscreen) ToggleFullscreen(); closed = true; layer.Children.Remove(surface); Target.Source = null; bitmap = null; Closed?.Invoke(); }
    public void Hide() { if (fullscreen) ToggleFullscreen(); surface.Visibility = Visibility.Collapsed; }
    public void Show() { if (closed) return; surface.Visibility = Visibility.Visible; layer.Children.Remove(surface); layer.Children.Add(surface); }
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
