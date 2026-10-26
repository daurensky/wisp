using System.Runtime.InteropServices;
using Concentus;
using Concentus.Enums;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace Wisp.Desktop.Media;

/// <summary>Windows process loopback: selected app tree, or the desktop excluding Wisp (prevents call echo).</summary>
public sealed class ShareAudioCapture : IDisposable
{
    private CancellationTokenSource? stop;
    public event Action<byte[]>? PacketReady;
    public event Action<string>? Error;
    public static bool Supported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348);
    public async Task StartAsync(CaptureSource source)
    {
        Stop(); if (!Supported) throw new PlatformNotSupportedException("Звук демонстрации с исключением Wisp требует Windows 11 или Windows Server 2022. Видео продолжит работать без звука.");
        var cancellation = new CancellationTokenSource(); stop = cancellation; var token = cancellation.Token;
        uint process = (uint)Environment.ProcessId; bool exclude = true;
        if (source.Id.StartsWith("window:") && long.TryParse(source.Id[7..], out var handle)) { GetWindowThreadProcessId(new IntPtr(handle), out process); exclude = false; }
        AudioClient client;
        try { client = await ActivateAsync(process, exclude); } catch { if (ReferenceEquals(stop, cancellation)) stop = null; cancellation.Dispose(); throw; }
        if (token.IsCancellationRequested) { client.Dispose(); cancellation.Dispose(); return; }
        _ = Task.Factory.StartNew(() =>
        {
            int com = CoInitializeEx(IntPtr.Zero, 0);
            try
            {
                using (client)
                using (var ready = new AutoResetEvent(false))
                {
                    client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback | AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality, 200000, 0, new WaveFormat(48000, 16, 2), Guid.Empty);
                    client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle());
                    var capture = client.AudioCaptureClient;
                    var encoder = OpusCodecFactory.CreateEncoder(48000, 2, OpusApplication.OPUS_APPLICATION_AUDIO);
                    try
                    {
                        encoder.Bitrate = 96000; encoder.Complexity = 5; encoder.UseVBR = true;
                        var pcm = new short[1920]; int used = 0; var packet = new byte[1275]; client.Start();
                        while (!token.IsCancellationRequested)
                        {
                            WaitHandle.WaitAny(new[] { ready, token.WaitHandle }, 100); if (token.IsCancellationRequested) break;
                            while (capture.GetNextPacketSize() > 0 && !token.IsCancellationRequested)
                            {
                                var pointer = capture.GetBuffer(out int frames, out var flags); int count = frames * 2;
                                try
                                {
                                    for (int offset = 0; offset < count;)
                                    {
                                        int take = Math.Min(pcm.Length - used, count - offset);
                                        if ((flags & AudioClientBufferFlags.Silent) != 0) Array.Clear(pcm, used, take); else Marshal.Copy(pointer + offset * 2, pcm, used, take);
                                        offset += take; used += take;
                                        if (used != pcm.Length) continue; used = 0;
                                        var length = encoder.Encode(pcm, 960, packet, packet.Length); if (length > 0) PacketReady?.Invoke(packet.AsSpan(0, length).ToArray());
                                    }
                                }
                                finally { capture.ReleaseBuffer(frames); }
                            }
                        }
                        client.Stop();
                    }
                    finally { (encoder as IDisposable)?.Dispose(); }
                }
            }
            catch (Exception ex) when (!token.IsCancellationRequested) { Error?.Invoke("Звук демонстрации остановлен: " + ex.Message); }
            finally { if (com >= 0) CoUninitialize(); if (ReferenceEquals(stop, cancellation)) stop = null; cancellation.Dispose(); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }
    public void Stop() { var previous = Interlocked.Exchange(ref stop, null); try { previous?.Cancel(); } catch (ObjectDisposedException) { } }
    public void Dispose() => Stop();
    private static async Task<AudioClient> ActivateAsync(uint process, bool exclude)
    {
        var data = Marshal.AllocHGlobal(12); var variant = Marshal.AllocHGlobal(24);
        Marshal.WriteInt32(data, 0, 1); Marshal.WriteInt32(data, 4, unchecked((int)process)); Marshal.WriteInt32(data, 8, exclude ? 1 : 0);
        for (int i = 0; i < 24; i += 4) Marshal.WriteInt32(variant, i, 0);
        Marshal.WriteInt16(variant, 0, 65); Marshal.WriteInt32(variant, 8, 12); Marshal.WriteIntPtr(variant, 16, data);
        var handler = new ActivationHandler(); IActivationOperation? operation = null;
        try
        {
            var iid = typeof(IAudioClient).GUID;
            Marshal.ThrowExceptionForHR(ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ref iid, variant, handler, out operation));
            var audio = await handler.Ready.Task.WaitAsync(TimeSpan.FromSeconds(8)); return new AudioClient(audio);
        }
        finally
        {
            // The native request owns its copied activation parameters after ActivateAudioInterfaceAsync returns.
            Marshal.FreeHGlobal(variant); Marshal.FreeHGlobal(data); GC.KeepAlive(operation); GC.KeepAlive(handler);
        }
    }
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class ActivationHandler : IActivationHandler, IAgileObject
    {
        public readonly TaskCompletionSource<IAudioClient> Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ActivateCompleted(IActivationOperation operation)
        {
            IntPtr pointer = IntPtr.Zero;
            try { Marshal.ThrowExceptionForHR(operation.GetActivateResult(out int result, out pointer)); Marshal.ThrowExceptionForHR(result); Ready.TrySetResult((IAudioClient)Marshal.GetObjectForIUnknown(pointer)); }
            catch (Exception ex) { Ready.TrySetException(ex); } finally { if (pointer != IntPtr.Zero) Marshal.Release(pointer); }
            return 0;
        }
    }
    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivationHandler { [PreserveSig] int ActivateCompleted(IActivationOperation operation); }
    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivationOperation { [PreserveSig] int GetActivateResult(out int result, out IntPtr activated); }
    [ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface IAgileObject { }
    [DllImport("Mmdevapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern int ActivateAudioInterfaceAsync(string path, ref Guid iid, IntPtr parameters, IActivationHandler handler, out IActivationOperation operation);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint mode);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
}
