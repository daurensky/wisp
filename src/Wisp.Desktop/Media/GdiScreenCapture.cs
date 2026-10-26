using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
namespace Wisp.Desktop.Media;

/// <summary>Reusable GDI fallback; cursor coordinates are physical pixels under PerMonitorV2.</summary>
internal sealed class GdiScreenCapture : IScreenCapture
{
    private readonly CaptureSource source;
    private readonly Bitmap bitmap;
    private readonly Graphics graphics;
    public string BackendName => "GDI";
    public GdiScreenCapture(CaptureSource source, ScreenStreamInfo info)
    {
        this.source = source;
        bitmap = new Bitmap(info.Width, info.Height, PixelFormat.Format32bppRgb);
        graphics = Graphics.FromImage(bitmap);
    }
    public CaptureState Read(byte[] pixels)
    {
        if (TryWindow(source, out var window))
        {
            if (!IsWindow(window)) throw new CaptureClosedException("Окно демонстрации закрыто.");
            if (IsIconic(window)) return CaptureState.Paused;
        }
        Capture(source, bitmap, graphics, pixels);
        return CaptureState.Active;
    }
    internal static bool TryWindow(CaptureSource source, out IntPtr window)
    {
        window = IntPtr.Zero;
        if (!source.Id.StartsWith("window:", StringComparison.Ordinal) || !long.TryParse(source.Id[7..], out var value)) return false;
        window = new IntPtr(value); return true;
    }
    internal static bool IsMinimized(CaptureSource source) => TryWindow(source, out var window) && IsIconic(window);
    public void Dispose() { graphics.Dispose(); bitmap.Dispose(); }
    public static IReadOnlyList<CaptureSource> GetSources()
    {
        var result = new List<CaptureSource>();
        var screens = System.Windows.Forms.Screen.AllScreens;
        for (var i = 0; i < screens.Length; i++)
        {
            var screen = screens[i];
            result.Add(new CaptureSource("monitor:" + screen.DeviceName,
                $"Монитор {i + 1} · {screen.Bounds.Width}×{screen.Bounds.Height}" + (screen.Primary ? " · основной" : "")));
        }
        if (screens.Length > 1) result.Add(new CaptureSource("desktop", "Весь рабочий стол · все мониторы"));
        var ownProcess = Environment.ProcessId;
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) || IsIconic(window)) return true;
            GetWindowThreadProcessId(window, out var pid);
            if (pid == ownProcess) return true;
            var length = GetWindowTextLength(window);
            if (length <= 0) return true;
            var title = new StringBuilder(Math.Min(length + 1, 512));
            GetWindowText(window, title, title.Capacity);
            if (title.Length > 0 && GetWindowRect(window, out var rect) && rect.Right > rect.Left && rect.Bottom > rect.Top)
                result.Add(new CaptureSource("window:" + window.ToInt64(), "Приложение · " + title));
            return true;
        }, IntPtr.Zero);
        return result;
    }

    internal static (Rectangle Bounds, IntPtr Window) Resolve(CaptureSource source)
    {
        if (source.Id == "desktop") return (System.Windows.Forms.SystemInformation.VirtualScreen, IntPtr.Zero);
        if (source.Id.StartsWith("monitor:", StringComparison.Ordinal))
        {
            var device = source.Id[8..];
            var screen = System.Windows.Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == device)
                ?? throw new InvalidOperationException("Монитор отключён.");
            return (screen.Bounds, IntPtr.Zero);
        }
        if (source.Id.StartsWith("window:", StringComparison.Ordinal) && long.TryParse(source.Id[7..], out var handle))
        {
            var window = new IntPtr(handle);
            if (!IsWindow(window) || !GetWindowRect(window, out var rect))
                throw new CaptureClosedException("Окно демонстрации закрыто.");
            return (Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom), window);
        }
        throw new ArgumentException("Неизвестный источник демонстрации.", nameof(source));
    }

    private static void Capture(CaptureSource source, Bitmap bitmap, Graphics graphics, byte[] pixels)
    {
        var (bounds, window) = Resolve(source);
        if (bounds.Width <= 0 || bounds.Height <= 0) throw new InvalidOperationException("Пустая область захвата.");
        var scale = Math.Min((double)bitmap.Width / bounds.Width, (double)bitmap.Height / bounds.Height);
        int width = Math.Max(1, (int)(bounds.Width * scale)), height = Math.Max(1, (int)(bounds.Height * scale));
        graphics.Clear(Color.Black); var targetDc = graphics.GetHdc(); var sourceDc = GetWindowDC(window);
        try
        {
            if (sourceDc == IntPtr.Zero) throw new InvalidOperationException("Не удалось захватить окно.");
            SetStretchBltMode(targetDc, 3);
            if (!StretchBlt(targetDc, (bitmap.Width - width) / 2, (bitmap.Height - height) / 2, width, height, sourceDc, window == IntPtr.Zero ? bounds.X : 0, window == IntPtr.Zero ? bounds.Y : 0, bounds.Width, bounds.Height, 0x00CC0020)) throw new InvalidOperationException("Windows отказала в захвате изображения.");
            DrawCursor(targetDc, bounds, bitmap.Width, bitmap.Height, width, height);
        }
        finally { if (sourceDc != IntPtr.Zero) ReleaseDC(window, sourceDc); graphics.ReleaseHdc(targetDc); }
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try { for (int y = 0; y < bitmap.Height; y++) Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * bitmap.Width * 4, bitmap.Width * 4); } finally { bitmap.UnlockBits(data); }
    }

    private static void DrawCursor(IntPtr dc, Rectangle bounds, int canvasWidth, int canvasHeight, int width, int height)
    {
        var cursor = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        if (!GetCursorInfo(ref cursor) || (cursor.Flags & 1) == 0 || !bounds.Contains(cursor.Position)) return;
        if (!GetIconInfo(cursor.Handle, out var icon)) return;
        try
        {
            double scaleX = (double)width / bounds.Width, scaleY = (double)height / bounds.Height;
            int x = (canvasWidth - width) / 2 + (int)((cursor.Position.X - bounds.X - icon.HotspotX) * scaleX);
            int y = (canvasHeight - height) / 2 + (int)((cursor.Position.Y - bounds.Y - icon.HotspotY) * scaleY);
            DrawIconEx(dc, x, y, cursor.Handle, Math.Max(1, (int)(GetSystemMetrics(13) * scaleX)), Math.Max(1, (int)(GetSystemMetrics(14) * scaleY)), 0, IntPtr.Zero, 3);
        }
        finally { if (icon.Mask != IntPtr.Zero) DeleteObject(icon.Mask); if (icon.Color != IntPtr.Zero) DeleteObject(icon.Color); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct CursorInfo { public int Size, Flags; public IntPtr Handle; public Point Position; }
    [StructLayout(LayoutKind.Sequential)] private struct IconInfo { [MarshalAs(UnmanagedType.Bool)] public bool Icon; public uint HotspotX, HotspotY; public IntPtr Mask, Color; }
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CursorInfo info);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr icon, out IconInfo info);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int width, int height, uint step, IntPtr brush, uint flags);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    private delegate bool EnumWindowCallback(IntPtr window, IntPtr parameter);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool StretchBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, int sourceWidth, int sourceHeight, uint rasterOperation);
}
