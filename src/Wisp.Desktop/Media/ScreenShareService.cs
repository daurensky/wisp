using System.Diagnostics;
using Wisp.Core;
namespace Wisp.Desktop.Media;

public sealed record CaptureSource(string Id, string Name);

/// <summary>Single encoder/capture worker, shared by all viewers. No capture or conversion on the UI thread.</summary>
public sealed class ScreenShareService : IDisposable
{
    private readonly object gate = new();
    private CancellationTokenSource? cancellation;
    private int keyRequested, desiredBitrate, desiredFps;
    private VideoRateController? adaptation;
    public event Action<byte[]>? FrameReady;
    public event Action<string>? Error;
    public event Action<string>? Status;
    public event Action<bool>? PauseChanged;
    public bool IsSharing { get { lock (gate) return cancellation is not null; } }
    public ScreenStreamInfo? CurrentInfo { get; private set; }
    public static IReadOnlyList<CaptureSource> GetSources() => GdiScreenCapture.GetSources();
    public static ScreenStreamInfo Describe(CaptureSource source, int height, int fps, int bitrate)
    {
        var bounds = GdiScreenCapture.Resolve(source).Bounds;
        var scale = Math.Min(1d, Math.Min(height * 16d / 9 / bounds.Width, (double)height / bounds.Height));
        return new ScreenStreamInfo(Math.Max(2, (int)(bounds.Width * scale) / 2 * 2), Math.Max(2, (int)(bounds.Height * scale) / 2 * 2), fps, bitrate);
    }
    public void RequestKeyFrame() => Interlocked.Exchange(ref keyRequested, 1);
    public void ObserveNetwork(string peer, VideoNetworkReport report)
    {
        lock (gate)
        {
            if (adaptation?.Observe(peer, report) != true) return;
            Volatile.Write(ref desiredBitrate, adaptation.Bitrate);
            Volatile.Write(ref desiredFps, adaptation.FramesPerSecond);
        }
    }
    public void Start(CaptureSource source, ScreenStreamInfo info, bool borderless = false, bool borderlessGranted = false)
    {
        Stop();
        if (!info.IsValid) throw new ArgumentException("Некорректный режим демонстрации.");
        GdiScreenCapture.Resolve(source);
        lock (gate)
        {
            CurrentInfo = info;
            adaptation = new VideoRateController(info.Bitrate, info.FramesPerSecond);
            desiredBitrate = info.Bitrate; desiredFps = info.FramesPerSecond; keyRequested = 1;
            var lifetime = new CancellationTokenSource(); cancellation = lifetime;
            _ = Task.Factory.StartNew(() => CaptureLoop(source, info, lifetime, borderless, borderlessGranted), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
    }
    private void CaptureLoop(CaptureSource source, ScreenStreamInfo info, CancellationTokenSource lifetime, bool borderless, bool borderlessGranted)
    {
        var token = lifetime.Token;
        Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
        try
        {
            using var encoder = new NativeVideoEncoder(info);
            using var capture = new ResilientScreenCapture(source, info, text => Status?.Invoke(text), borderless, borderlessGranted);
            if (token.IsCancellationRequested) return;
            Status?.Invoke("Захват: " + capture.BackendName + "; " + encoder.RateControlStatus);
            var pixels = new byte[info.Width * info.Height * 4];
            var clock = Stopwatch.StartNew();
            bool paused = false;
            int bitrate = info.Bitrate, fps = info.FramesPerSecond;
            while (!token.IsCancellationRequested)
            {
                long started = clock.ElapsedTicks;
                var state = capture.Read(pixels);
                if (token.IsCancellationRequested) break;
                if (state == CaptureState.Waiting) { token.WaitHandle.WaitOne(5); continue; }
                bool nextPaused = state == CaptureState.Paused;
                if (paused != nextPaused) { paused = nextPaused; PauseChanged?.Invoke(paused); RequestKeyFrame(); }
                int nextBitrate = Volatile.Read(ref desiredBitrate), nextFps = Volatile.Read(ref desiredFps);
                if (nextBitrate != bitrate || nextFps != fps)
                {
                    bool accepted = encoder.TrySetBitrate(nextBitrate);
                    bitrate = nextBitrate; fps = nextFps;
                    if (!accepted) fps = Math.Min(fps, 15);
                    Status?.Invoke(accepted ? $"Сеть: поток снижен до {bitrate / 1000000d:0.0} Мбит/с × число зрителей; {fps} FPS." : "Кодировщик не принял изменение битрейта: частота снижена до 15 FPS.");
                }
                if (Interlocked.Exchange(ref keyRequested, 0) != 0) encoder.RequestKeyFrame();
                var encoded = encoder.Encode(pixels, clock.Elapsed);
                if (encoded is not null && !token.IsCancellationRequested) FrameReady?.Invoke(H264Frame.Pack(info.FramesPerSecond, encoded));
                double spent = (clock.ElapsedTicks - started) * 1000d / Stopwatch.Frequency;
                double delay = (paused ? 1000 : 1000d / fps) - spent;
                if (delay > 0) token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(delay));
            }
        }
        catch (Exception ex) when (!token.IsCancellationRequested) { Error?.Invoke("Демонстрация остановлена: " + ex.Message); }
        finally
        {
            lock (gate) if (ReferenceEquals(cancellation, lifetime)) { cancellation = null; CurrentInfo = null; adaptation = null; }
            lifetime.Dispose();
        }
    }
    public void Stop()
    {
        lock (gate) { cancellation?.Cancel(); cancellation = null; CurrentInfo = null; adaptation = null; }
    }
    public void Dispose() => Stop();
}
