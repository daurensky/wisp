using System.Runtime.InteropServices;
using Vortice.MediaFoundation;
using static Vortice.MediaFoundation.MediaFactory;

namespace Wisp.Desktop.Media;

public sealed record ScreenStreamInfo(int Width, int Height, int FramesPerSecond, int Bitrate, string SessionId = "")
{
    public bool IsValid => Width is >= 2 and <= 1920 && Height is >= 2 and <= 1080 && Width % 2 == 0 && Height % 2 == 0 && FramesPerSecond is 15 or 30 or 60 && Bitrate is >= 500000 and <= 8000000;
}

/// <summary>Windows' native H.264 MFT and native color conversion. No JPEG or managed video encoder.</summary>
public sealed class NativeVideoEncoder : IDisposable
{
    private readonly MfTransform converter, encoder;
    private readonly ScreenStreamInfo info;
    private long index;
    private byte[] header = Array.Empty<byte>();
    public NativeVideoEncoder(ScreenStreamInfo info)
    {
        if (!info.IsValid) throw new ArgumentException("Недопустимый режим демонстрации.");
        this.info = info;
        converter = MfTransform.Color(info.Width, info.Height, VideoFormatGuids.Rgb32, VideoFormatGuids.NV12, info.FramesPerSecond);
        encoder = new MfTransform(new Guid("6ca50344-051a-4ded-9779-a43305165e35"));
        try
        {
            using var output = MfTransform.VideoType(info, VideoFormatGuids.H264);
            output.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)info.Bitrate).CheckError();
            output.Set(MediaTypeAttributeKeys.Mpeg2Profile, 66u).CheckError(); // Baseline: no B-frame reorder delay.
            encoder.Transform.SetOutputType(0, output, 0);
            using var input = MfTransform.VideoType(info, VideoFormatGuids.NV12);
            encoder.Transform.SetInputType(0, input, 0);
            encoder.TryCodec(new Guid("9d3ecd55-89e8-490a-970a-0c9548d5a56e"), true);
            encoder.TryCodec(new Guid("95f31b26-95a4-41aa-9303-246a7fc6eef1"), (uint)info.FramesPerSecond);
            encoder.Begin();
        }
        catch { Dispose(); throw; }
    }
    public byte[]? Encode(byte[] bgra)
    {
        if (bgra.Length != info.Width * info.Height * 4) throw new ArgumentException("Неверный размер кадра.");
        var duration = 10000000L / info.FramesPerSecond; var time = index * duration;
        if (index++ % info.FramesPerSecond == 0) encoder.TryCodec(new Guid("398c1b98-8353-475a-9ef2-8f265d260345"), true);
        var nv12 = converter.Convert(bgra, time, duration); if (nv12 is null) return null;
        var frame = encoder.Convert(nv12, time, duration); if (frame is null) return null;
        if (header.Length == 0) { try { using var type = encoder.Transform.GetOutputCurrentType(0); header = type.GetBlob(MediaTypeAttributeKeys.MpegSequenceHeader); } catch { } }
        // Repeat parameter sets so a late join or a lost frame recovers at the next IDR.
        return header.Length == 0 ? frame : header.Concat(frame).ToArray();
    }
    public void Dispose() { converter.Dispose(); encoder.Dispose(); }
}

public sealed class NativeVideoDecoder : IDisposable
{
    private readonly MfTransform decoder, converter;
    private readonly ScreenStreamInfo info;
    private long index;
    public NativeVideoDecoder(ScreenStreamInfo info)
    {
        if (!info.IsValid) throw new ArgumentException("Недопустимый видеопоток."); this.info = info;
        decoder = new MfTransform(new Guid("62ce7e72-4c71-4d20-b15d-452831a87d9d"));
        converter = MfTransform.Color(info.Width, info.Height, VideoFormatGuids.NV12, VideoFormatGuids.Rgb32, info.FramesPerSecond);
        try
        {
            using var input = MfTransform.VideoType(info, VideoFormatGuids.H264); decoder.Transform.SetInputType(0, input, 0);
            SetOutput(); decoder.Begin();
        }
        catch { Dispose(); throw; }
    }
    private void SetOutput()
    {
        using var output = MfTransform.VideoType(info, VideoFormatGuids.NV12); decoder.Transform.SetOutputType(0, output, 0);
    }
    public byte[]? Decode(byte[] h264)
    {
        var duration = 10000000L / info.FramesPerSecond; var time = index++ * duration;
        var nv12 = decoder.Convert(h264, time, duration, SetOutput); return nv12 is null ? null : converter.Convert(nv12, time, duration);
    }
    public void Dispose() { decoder.Dispose(); converter.Dispose(); }
}

internal sealed class MfTransform : IDisposable
{
    private static readonly Lazy<bool> startup = new(() => { MFStartup().CheckError(); return true; });
    public IMFTransform Transform { get; }
    private object? codecObject;
    private readonly bool comInitialized;
    public MfTransform(Guid clsid)
    {
        int com = CoInitializeEx(IntPtr.Zero, 0); comInitialized = com >= 0;
        if (com < 0 && com != unchecked((int)0x80010106)) Marshal.ThrowExceptionForHR(com);
        _ = startup.Value;
        var iid = typeof(IMFTransform).GUID;
        Marshal.ThrowExceptionForHR(CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out var pointer));
        Transform = new IMFTransform(pointer);
        try { using var attrs = Transform.Attributes; attrs.Set(new Guid("9c27891a-ed7a-40e1-88e8-b22727a024ee"), true); } catch { }
    }
    public static MfTransform Color(int width, int height, Guid inputFormat, Guid outputFormat, int fps)
    {
        var transform = new MfTransform(new Guid("98230571-0087-4204-b020-3282538e57d3"));
        try
        {
            var info = new ScreenStreamInfo(width, height, fps, 4000000);
            using var input = VideoType(info, inputFormat); using var output = VideoType(info, outputFormat);
            transform.Transform.SetInputType(0, input, 0); transform.Transform.SetOutputType(0, output, 0); transform.Begin(); return transform;
        }
        catch { transform.Dispose(); throw; }
    }
    public static IMFMediaType VideoType(ScreenStreamInfo info, Guid format)
    {
        var type = MFCreateMediaType(); type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError(); type.Set(MediaTypeAttributeKeys.Subtype, format).CheckError();
        type.Set(MediaTypeAttributeKeys.FrameSize, ((ulong)info.Width << 32) | (uint)info.Height).CheckError();
        type.Set(MediaTypeAttributeKeys.FrameRate, ((ulong)info.FramesPerSecond << 32) | 1).CheckError();
        type.Set(MediaTypeAttributeKeys.PixelAspectRatio, (1UL << 32) | 1).CheckError(); type.Set(MediaTypeAttributeKeys.InterlaceMode, 2u).CheckError();
        return type;
    }
    public void Begin() { Transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, 0); Transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, 0); }
    public void TryCodec(Guid key, object value)
    {
        try { codecObject ??= Marshal.GetObjectForIUnknown(Transform.NativePointer); if (codecObject is ICodecApi codec) codec.SetValue(ref key, ref value); } catch { }
    }
    public byte[]? Convert(byte[] bytes, long time, long duration, Action? streamChange = null)
    {
        using var sample = MFCreateSample(); using var buffer = MFCreateMemoryBuffer(bytes.Length);
        buffer.Lock(out var ptr, out _, out _); try { Marshal.Copy(bytes, 0, ptr, bytes.Length); } finally { buffer.Unlock(); }
        buffer.CurrentLength = bytes.Length; sample.AddBuffer(buffer); sample.SampleTime = time; sample.SampleDuration = duration;
        Transform.ProcessInput(0, sample, 0);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var info = Transform.GetOutputStreamInfo(0);
            IMFSample? supplied = null; IMFMediaBuffer? suppliedBuffer = null;
            var output = new OutputDataBuffer { StreamID = 0 };
            try
            {
                if ((info.Flags & 0x100) == 0)
                {
                    supplied = MFCreateSample(); suppliedBuffer = MFCreateMemoryBuffer(Math.Max(1, info.Size)); supplied.AddBuffer(suppliedBuffer); output.Sample = supplied;
                }
                var result = Transform.ProcessOutput(ProcessOutputFlags.None, 1, ref output, out _);
                if (result.Code == unchecked((int)0xC00D6D72)) return null; // MF_E_TRANSFORM_NEED_MORE_INPUT
                if (result.Code == unchecked((int)0xC00D6D61) && streamChange is not null) { streamChange(); continue; }
                result.CheckError(); if (output.Sample is null || output.Sample.BufferCount == 0 || (output.Status & 0x300) == 0x300) return null;
                using var contiguous = output.Sample.ConvertToContiguousBuffer(); contiguous.Lock(out var source, out _, out var length);
                try { var data = new byte[length]; Marshal.Copy(source, data, 0, length); return data; } finally { contiguous.Unlock(); }
            }
            finally
            {
                output.Events?.Dispose();
                if (output.Sample is not null && !ReferenceEquals(supplied, output.Sample))
                {
                    // SharpGen unmarshals a new managed wrapper around a caller-owned sample.
                    // Matching pointers represent one COM reference, not two independently owned samples.
                    if (supplied is not null && output.Sample.NativePointer == supplied.NativePointer) GC.SuppressFinalize(output.Sample);
                    else output.Sample.Dispose();
                }
                supplied?.Dispose(); suppliedBuffer?.Dispose();
            }
        }
        return null;
    }
    public void Dispose() { if (codecObject is not null && Marshal.IsComObject(codecObject)) Marshal.ReleaseComObject(codecObject); codecObject = null; Transform.Dispose(); if (comInitialized) CoUninitialize(); }
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint mode);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IntPtr instance);
    [ComImport, Guid("901DB4C7-31CE-41a2-85DC-8FA0BF41B8DA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICodecApi
    {
        [PreserveSig] int IsSupported(ref Guid key); [PreserveSig] int IsModifiable(ref Guid key);
        [PreserveSig] int GetParameterRange(ref Guid key, out object min, out object max, out object step);
        [PreserveSig] int GetParameterValues(ref Guid key, out IntPtr values, out uint count);
        [PreserveSig] int GetDefaultValue(ref Guid key, [MarshalAs(UnmanagedType.Struct)] out object value);
        [PreserveSig] int GetValue(ref Guid key, [MarshalAs(UnmanagedType.Struct)] out object value);
        [PreserveSig] int SetValue(ref Guid key, [MarshalAs(UnmanagedType.Struct)] ref object value);
    }
}
