using System.Runtime.InteropServices;
using Vortice.MediaFoundation;
using static Vortice.MediaFoundation.MediaFactory;

namespace Wisp.Desktop.Media;

public sealed record ScreenStreamInfo(int Width, int Height, int FramesPerSecond, int Bitrate, string SessionId = "", long StartedAtUnixMs = 0)
{
    public bool IsValid => Width is >= 2 and <= 1920 && Height is >= 2 and <= 1080 && Width % 2 == 0 && Height % 2 == 0 && FramesPerSecond is 15 or 30 or 60 && Bitrate is >= 500000 and <= 8000000 && SessionId is not null && SessionId.Length <= 64;
}

/// <summary>Windows' native H.264 MFT and native color conversion. No JPEG or managed video encoder.</summary>
public sealed class NativeVideoEncoder : IDisposable
{
    private readonly MfTransform converter, encoder;
    private readonly ScreenStreamInfo info;
    private long index;
    private long lastKeyTime = -30000000;
    private int keyRequested;
    private int bitrate;
    public string RateControlStatus { get; private set; } = "";
    private byte[] header = Array.Empty<byte>();
    public NativeVideoEncoder(ScreenStreamInfo info)
    {
        if (!info.IsValid) throw new ArgumentException("Недопустимый режим демонстрации.");
        this.info = info; bitrate = info.Bitrate;
        converter = MfTransform.Color(info.Width, info.Height, VideoFormatGuids.Rgb32, VideoFormatGuids.NV12, info.FramesPerSecond);
        try
        {
            encoder = new MfTransform(new Guid("6ca50344-051a-4ded-9779-a43305165e35"));
            using var output = MfTransform.VideoType(info, VideoFormatGuids.H264);
            output.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)info.Bitrate).CheckError();
            output.Set(MediaTypeAttributeKeys.Mpeg2Profile, 66u).CheckError(); // Baseline: no B-frame reorder delay.
            encoder.Transform.SetOutputType(0, output, 0);
            using var input = MfTransform.VideoType(info, VideoFormatGuids.NV12);
            encoder.Transform.SetInputType(0, input, 0);
            encoder.TryCodec(new Guid("9d3ecd55-89e8-490a-970a-0c9548d5a56e"), true);
            encoder.TryCodec(new Guid("95f31b26-95a4-41aa-9303-246a7fc6eef1"), (uint)(info.FramesPerSecond * 3));
            bool cbr = encoder.TryCodec(new Guid("1c0608e9-370c-4710-8a58-cb6181c42423"), 0u);
            bool buffer = encoder.TryCodec(new Guid("0db96574-b6a4-4c8b-8106-3773de0310cd"), (uint)(info.Bitrate / 2));
            RateControlStatus = (cbr ? "CBR" : "AvgBitrate (CBR не принят)") + (buffer ? "; буфер 0,5 с" : "; буфер по умолчанию");
            encoder.Begin();
        }
        catch { Dispose(); throw; }
    }
    public void RequestKeyFrame() => Interlocked.Exchange(ref keyRequested, 1);
    public bool TrySetBitrate(int value)
    {
        if (value is < 500000 or > 8000000) return false;
        if (!encoder.TryCodec(new Guid("f7222374-2144-4815-b550-a37f8e12ee52"), (uint)value)) return false;
        bitrate = value;
        return true;
    }
    public byte[]? Encode(byte[] bgra, TimeSpan? presentationTime = null)
    {
        if (bgra.Length != info.Width * info.Height * 4) throw new ArgumentException("Неверный размер кадра.");
        var duration = 10000000L / info.FramesPerSecond;
        var time = presentationTime?.Ticks ?? index * duration;
        index++;
        bool forceKey = Interlocked.Exchange(ref keyRequested, 0) != 0;
        if (time - lastKeyTime >= TimeSpan.FromSeconds(3).Ticks || forceKey)
        {
            encoder.TryCodec(new Guid("398c1b98-8353-475a-9ef2-8f265d260345"), 1u);
            lastKeyTime = time;
        }
        var nv12 = converter.Convert(bgra, time, duration); if (nv12 is null) return null;
        var frame = encoder.Convert(nv12, time, duration); if (frame is null) return null;
        if (header.Length == 0) { try { using var type = encoder.Transform.GetOutputCurrentType(0); header = type.GetBlob(MediaTypeAttributeKeys.MpegSequenceHeader); } catch { } }
        // Repeat parameter sets so a late join or a lost frame recovers at the next IDR.
        return header.Length == 0 ? frame : header.Concat(frame).ToArray();
    }
    public void Dispose() { converter?.Dispose(); encoder?.Dispose(); }
}

public sealed class NativeVideoDecoder : IDisposable
{
    private readonly MfTransform decoder, converter;
    private readonly ScreenStreamInfo info;
    private int codedWidth, codedHeight, stride;
    private long index;
    public NativeVideoDecoder(ScreenStreamInfo info)
    {
        if (!info.IsValid) throw new ArgumentException("Недопустимый видеопоток."); this.info = info;
        codedWidth = stride = info.Width; codedHeight = info.Height;
        decoder = new MfTransform(new Guid("62ce7e72-4c71-4d20-b15d-452831a87d9d"));
        try
        {
            converter = MfTransform.Color(info.Width, info.Height, VideoFormatGuids.NV12, VideoFormatGuids.Rgb32, info.FramesPerSecond);
            using var input = MfTransform.VideoType(info, VideoFormatGuids.H264); decoder.Transform.SetInputType(0, input, 0);
            SetOutput(); decoder.Begin();
        }
        catch { Dispose(); throw; }
    }
    private void SetOutput()
    {
        // H.264 pads 1080 lines to 1088. Use the decoder's actual media type, then crop its NV12 planes.
        for (int index = 0; index < 32; index++)
        {
            using var candidate = decoder.Transform.GetOutputAvailableType(0, index);
            if (candidate.GetGUID(MediaTypeAttributeKeys.Subtype) != VideoFormatGuids.NV12) continue;
            var size = candidate.GetUInt64(MediaTypeAttributeKeys.FrameSize);
            int width = (int)(size >> 32), height = (int)(size & uint.MaxValue);
            if (width < info.Width || height < info.Height || width > 1936 || height > 1088) throw new InvalidDataException("Размер видеопотока не совпадает с объявленным.");
            int pitch = width; try { pitch = checked((int)candidate.GetUInt32(MediaTypeAttributeKeys.DefaultStride)); } catch { }
            if (pitch < width || pitch > 4096) throw new InvalidDataException("Некорректный шаг видеобуфера.");
            decoder.Transform.SetOutputType(0, candidate, 0); codedWidth = width; codedHeight = height; stride = pitch; return;
        }
        throw new InvalidOperationException("Windows не предоставила формат NV12 для демонстрации.");
    }
    public byte[]? Decode(byte[] h264)
    {
        var duration = 10000000L / info.FramesPerSecond; var time = index++ * duration;
        var nv12 = decoder.Convert(h264, time, duration, SetOutput); if (nv12 is null) return null;
        if (stride != info.Width || codedWidth != info.Width || codedHeight != info.Height)
        {
            int yLength = checked(stride * codedHeight);
            if (nv12.Length < yLength + stride * (codedHeight / 2)) throw new InvalidDataException("Видеобуфер короче объявленного размера.");
            var cropped = new byte[info.Width * info.Height * 3 / 2];
            for (int row = 0; row < info.Height; row++) Buffer.BlockCopy(nv12, row * stride, cropped, row * info.Width, info.Width);
            for (int row = 0; row < info.Height / 2; row++) Buffer.BlockCopy(nv12, yLength + row * stride, cropped, info.Width * info.Height + row * info.Width, info.Width);
            nv12 = cropped;
        }
        return converter.Convert(nv12, time, duration);
    }
    public void Dispose() { decoder?.Dispose(); converter?.Dispose(); }
}

internal sealed class MfTransform : IDisposable
{
    private static readonly Lazy<bool> startup = new(() => { MFStartup().CheckError(); return true; });
    public IMFTransform Transform { get; }
    private object? codecObject;
    private readonly bool comInitialized;
    private bool disposed;
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
    public bool TryCodec(Guid key, object value)
    {
        try
        {
            codecObject ??= Marshal.GetObjectForIUnknown(Transform.NativePointer);
            return codecObject is ICodecApi codec && codec.SetValue(ref key, ref value) >= 0;
        }
        catch { return false; }
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
    public void Dispose() { if (disposed) return; disposed = true; if (codecObject is not null && Marshal.IsComObject(codecObject)) Marshal.ReleaseComObject(codecObject); codecObject = null; Transform.Dispose(); if (comInitialized) CoUninitialize(); }
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
