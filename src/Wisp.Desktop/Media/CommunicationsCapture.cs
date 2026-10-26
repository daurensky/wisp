using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace Wisp.Desktop.Media;

/// <summary>Shared event-driven WASAPI capture, Communications category, Windows conversion to 48k/16-bit/mono.</summary>
internal sealed class CommunicationsCapture(string endpointId, Action<string> status) : IWaveIn
{
    private readonly CancellationTokenSource stop = new();
    private Thread? worker;
    public event EventHandler<WaveInEventArgs>? DataAvailable;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;
    public WaveFormat WaveFormat
    {
        get => new(48000, 16, 1);
        set { if (value.SampleRate != 48000 || value.BitsPerSample != 16 || value.Channels != 1) throw new ArgumentException("Capture format is 48kHz/16-bit/mono."); }
    }
    public void StartRecording()
    {
        if (worker is not null) return;
        worker = new Thread(Capture) { IsBackground = true, Name = "Wisp microphone" };
        worker.SetApartmentState(ApartmentState.MTA); worker.Start();
    }
    private void Capture()
    {
        int com = CoInitializeEx(IntPtr.Zero, 0);
        Exception? failure = null;
        try
        {
            using var client = OpenEndpoint(endpointId, status);
            using var ready = new AutoResetEvent(false);
            client.Initialize(AudioClientShareMode.Shared,
                AudioClientStreamFlags.EventCallback | AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality,
                400000, 0, WaveFormat, Guid.Empty);
            client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle());
            var capture = client.AudioCaptureClient;
            var bytes = new byte[Math.Max(960 * 2, client.BufferSize * 2)];
            client.Start();
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    WaitHandle.WaitAny([ready, stop.Token.WaitHandle], 100);
                    while (!stop.IsCancellationRequested && capture.GetNextPacketSize() > 0)
                    {
                        var pointer = capture.GetBuffer(out int frames, out var flags);
                        try
                        {
                            int count = checked(frames * 2);
                            if (count > bytes.Length) throw new InvalidDataException("Unexpected WASAPI capture size.");
                            if ((flags & AudioClientBufferFlags.Silent) != 0) Array.Clear(bytes, 0, count);
                            else Marshal.Copy(pointer, bytes, 0, count);
                            if (count > 0) DataAvailable?.Invoke(this, new WaveInEventArgs(bytes, count));
                        }
                        finally { capture.ReleaseBuffer(frames); }
                    }
                }
            }
            finally { client.Stop(); }
        }
        catch (Exception ex) when (!stop.IsCancellationRequested) { failure = ex; }
        finally
        {
            if (com >= 0) CoUninitialize();
            RecordingStopped?.Invoke(this, new StoppedEventArgs(failure));
        }
    }
    private static AudioClient OpenEndpoint(string id, Action<string> status)
    {
        // IMMDevice::Activate accepts endpoint IDs directly. AudioClient.ActivateAsync expects a device
        // interface path; passing MMDevice.ID to that API would fail for some endpoints.
        var type = Type.GetTypeFromCLSID(new Guid("bcde0395-e52f-467c-8e3d-c4579291692e"), true)!;
        var enumerator = (IEndpointEnumerator)Activator.CreateInstance(type)!;
        IEndpoint? endpoint = null;
        object? audio = null;
        try
        {
            Marshal.ThrowExceptionForHR(enumerator.GetDevice(id, out endpoint));
            var iid = typeof(IAudioClient2).GUID;
            Marshal.ThrowExceptionForHR(endpoint.Activate(ref iid, 23, IntPtr.Zero, out audio));
            var properties = new AudioClientProperties
            {
                cbSize = (uint)Marshal.SizeOf<AudioClientProperties>(),
                eCategory = AudioStreamCategory.Communications
            };
            var memory = Marshal.AllocHGlobal(Marshal.SizeOf<AudioClientProperties>());
            try
            {
                Marshal.StructureToPtr(properties, memory, false);
                try { ((IAudioClient2)audio).SetClientProperties(memory); }
                catch (COMException) { status("Драйвер не принял категорию Communications; используется обычный WASAPI. Системная обработка голоса не гарантируется."); }
            }
            finally { Marshal.FreeHGlobal(memory); }
            var result = new AudioClient((IAudioClient)audio);
            audio = null; // Ownership transfers to NAudio.AudioClient.Dispose.
            return result;
        }
        finally
        {
            if (audio is not null) Marshal.ReleaseComObject(audio);
            if (endpoint is not null) Marshal.ReleaseComObject(endpoint);
            Marshal.ReleaseComObject(enumerator);
        }
    }
    public void StopRecording() { stop.Cancel(); if (worker is not null && Thread.CurrentThread != worker) worker.Join(TimeSpan.FromSeconds(2)); }
    public void Dispose() { StopRecording(); if (worker?.IsAlive != true) stop.Dispose(); }
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint mode);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [ComImport, Guid("a95664d2-9614-4f35-a746-de8db63617e6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEndpointEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint state, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IntPtr device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IEndpoint device);
    }
    [ComImport, Guid("d666063f-1587-4e43-81f1-b948e807363f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEndpoint
    {
        [PreserveSig] int Activate(ref Guid iid, int context, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }
}
