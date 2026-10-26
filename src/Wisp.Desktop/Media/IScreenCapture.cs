namespace Wisp.Desktop.Media;

public enum CaptureState { Active, Waiting, Paused }
public sealed class CaptureClosedException(string message) : InvalidOperationException(message);
public interface IScreenCapture : IDisposable
{
    string BackendName { get; }
    CaptureState Read(byte[] bgra);
}

internal sealed class ResilientScreenCapture : IScreenCapture
{
    private readonly CaptureSource source;
    private readonly ScreenStreamInfo info;
    private readonly Action<string> status;
    private IScreenCapture capture;
    public string BackendName => capture.BackendName;
    public ResilientScreenCapture(CaptureSource source, ScreenStreamInfo info, Action<string> status)
    {
        this.source = source; this.info = info; this.status = status;
        try { capture = new WindowsGraphicsScreenCapture(source, info); }
        catch (Exception ex) when (ex is not CaptureClosedException)
        {
            capture = new GdiScreenCapture(source, info);
            status("Захват GDI: Windows Graphics Capture недоступен. " + ex.Message);
        }
    }
    public CaptureState Read(byte[] pixels)
    {
        try { return capture.Read(pixels); }
        catch (Exception ex) when (capture is WindowsGraphicsScreenCapture && ex is not CaptureClosedException)
        {
            capture.Dispose(); capture = new GdiScreenCapture(source, info);
            status("Захват переключён на GDI. " + ex.Message);
            return capture.Read(pixels);
        }
    }
    public void Dispose() => capture.Dispose();
}
