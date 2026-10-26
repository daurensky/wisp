using System.Threading.Channels;
using Wisp.Core;
namespace Wisp.Desktop.Media;

/// <summary>Decode in order off the UI thread. When overloaded, wait for an IDR instead of queuing latency.</summary>
public sealed class ScreenVideoReceiver : IDisposable
{
    private readonly Channel<(byte[] Data, bool Reset)> queue = System.Threading.Channels.Channel.CreateBounded<(byte[], bool)>(4);
    private readonly CancellationTokenSource stop = new();
    private readonly object gate = new();
    private bool waitingForKey = true, closed;
    public ScreenStreamInfo Info { get; }
    public ScreenVideoReceiver(ScreenStreamInfo info, Action<byte[]> ready, Action<string> error)
    {
        Info = info;
        _ = Task.Factory.StartNew(() =>
        {
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
            NativeVideoDecoder? decoder = null;
            try
            {
                while (queue.Reader.WaitToReadAsync(stop.Token).AsTask().GetAwaiter().GetResult())
                {
                    if (!queue.Reader.TryRead(out var frame)) continue;
                    try
                    {
                        if (frame.Reset) { decoder?.Dispose(); decoder = null; decoder = new NativeVideoDecoder(info); }
                        var pixels = decoder?.Decode(frame.Data); if (pixels is not null && !stop.IsCancellationRequested) ready(pixels);
                    }
                    catch (Exception) when (decoder is not null) { decoder.Dispose(); decoder = null; lock (gate) { waitingForKey = true; while (queue.Reader.TryRead(out _)) { } } }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!stop.IsCancellationRequested) error("Не удалось открыть демонстрацию: " + ex.Message); }
            finally { decoder?.Dispose(); lock (gate) { closed = true; queue.Writer.TryComplete(); while (queue.Reader.TryRead(out _)) { } } stop.Dispose(); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }
    public void Enqueue(byte[] h264)
    {
        lock (gate)
        {
            if (closed || h264.Length > 2 * 1024 * 1024) return;
            bool key = H264Frame.IsKeyFrame(h264);
            if (waitingForKey && !key) return;
            if (queue.Writer.TryWrite((h264, waitingForKey))) { waitingForKey = false; return; }
            waitingForKey = true; while (queue.Reader.TryRead(out _)) { }
            if (key && queue.Writer.TryWrite((h264, true))) waitingForKey = false;
        }
    }
    public void Dispose() { lock (gate) { if (closed) return; closed = true; try { stop.Cancel(); } catch (ObjectDisposedException) { } queue.Writer.TryComplete(); while (queue.Reader.TryRead(out _)) { } } }
}
