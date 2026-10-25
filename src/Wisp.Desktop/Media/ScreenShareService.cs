using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace Wisp.Desktop.Media;

public sealed record CaptureSource(string Id, string Name);

/// <summary>
/// Conservative native GDI capture: at most 1280x720 JPEG frames, five per second.
/// Window capture requires a visible, non-minimized window. Protected/GPU-only
/// surfaces can return black; this is not Windows Graphics Capture.
/// </summary>
public sealed class ScreenShareService : IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _cancellation;
    private Task? _loop;
    public event Action<byte[]>? FrameReady;
    public event Action<string>? Error;
    public bool IsSharing { get { lock (_gate) return _cancellation is not null; } }

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

    public void Start(CaptureSource source)
    {
        Stop();
        // Resolve before allocating a worker, so invalid choices are reported immediately.
        Resolve(source);
        lock (_gate)
        {
            var cancellation = new CancellationTokenSource();
            _cancellation = cancellation;
            var token = cancellation.Token;
            _loop = Task.Run(async () =>
            {
                try
                {
                    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
                    do
                    {
                        token.ThrowIfCancellationRequested();
                        var frame = Capture(source);
                        if (!token.IsCancellationRequested) FrameReady?.Invoke(frame);
                    } while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (Exception ex) { Error?.Invoke("Демонстрация остановлена: " + ex.Message); }
                finally
                {
                    lock (_gate)
                    {
                        if (ReferenceEquals(_cancellation, cancellation)) _cancellation = null;
                    }
                    cancellation.Dispose();
                }
            });
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _cancellation?.Cancel();
            _cancellation = null;
            // The worker releases its own GDI handles and cancellation source.
            _loop = null;
        }
    }

    private static (Rectangle Bounds, IntPtr Window) Resolve(CaptureSource source)
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
            if (!IsWindow(window) || IsIconic(window) || !GetWindowRect(window, out var rect))
                throw new InvalidOperationException("Окно закрыто или свёрнуто.");
            return (Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom), window);
        }
        throw new ArgumentException("Неизвестный источник демонстрации.", nameof(source));
    }

    private static byte[] Capture(CaptureSource source)
    {
        var (bounds, window) = Resolve(source);
        if (bounds.Width <= 0 || bounds.Height <= 0) throw new InvalidOperationException("Пустая область захвата.");
        var scale = Math.Min(1d, Math.Min(1280d / bounds.Width, 720d / bounds.Height));
        var width = Math.Max(1, (int)(bounds.Width * scale));
        var height = Math.Max(1, (int)(bounds.Height * scale));
        using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            var targetDc = graphics.GetHdc();
            var sourceDc = GetWindowDC(window);
            try
            {
                if (sourceDc == IntPtr.Zero) throw new InvalidOperationException("Не удалось захватить окно.");
                SetStretchBltMode(targetDc, 3); // COLORONCOLOR: cheaper than HALFTONE at five fps.
                var x = window == IntPtr.Zero ? bounds.X : 0;
                var y = window == IntPtr.Zero ? bounds.Y : 0;
                if (!StretchBlt(targetDc, 0, 0, width, height, sourceDc, x, y, bounds.Width, bounds.Height, 0x00CC0020))
                    throw new InvalidOperationException("Windows отказала в захвате изображения.");
            }
            finally
            {
                if (sourceDc != IntPtr.Zero) ReleaseDC(window, sourceDc);
                graphics.ReleaseHdc(targetDc);
            }
        }
        using var output = new MemoryStream();
        using var settings = new EncoderParameters(1);
        settings.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 55L);
        var jpeg = ImageCodecInfo.GetImageEncoders().First(x => x.FormatID == ImageFormat.Jpeg.Guid);
        bitmap.Save(output, jpeg, settings);
        return output.ToArray();
    }

    public void Dispose() => Stop();

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
