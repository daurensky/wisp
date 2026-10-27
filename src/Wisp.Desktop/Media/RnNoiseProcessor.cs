using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Wisp.Core;

namespace Wisp.Desktop.Media;

/// <summary>RNNoise 0.2, default model, two 10 ms blocks per Wisp microphone frame.</summary>
public sealed class RnNoiseProcessor : IMicProcessor
{
    private const int BlockSamples = 480;
    private static readonly Lazy<NativeApi> Library = new(() => new NativeApi());
    private readonly NativeApi api;
    private readonly StateHandle state;
    private readonly float[] input = new float[BlockSamples];
    private readonly float[] output = new float[BlockSamples];
    private int configuration = 1; // Low bit: enabled; other bits: transition generation.
    private int appliedConfiguration = 1;
    private long lastFrame;
    private bool disposed;

    // Read/write from UI without touching native state. Capture owns Process and Dispose.
    public bool Enabled
    {
        get => (Volatile.Read(ref configuration) & 1) != 0;
        set
        {
            int observed, next;
            do
            {
                observed = Volatile.Read(ref configuration);
                if (((observed & 1) != 0) == value) return;
                next = unchecked((observed + 2) & ~1) | (value ? 1 : 0);
            } while (Interlocked.CompareExchange(ref configuration, next, observed) != observed);
        }
    }

    public RnNoiseProcessor()
    {
        api = Library.Value;
        if (api.FrameSize() != BlockSamples)
            throw new NotSupportedException("RNNoise должен обрабатывать 480 отсчётов.");
        state = new StateHandle(api);
        if (state.IsInvalid)
        {
            state.Dispose();
            throw new InvalidOperationException("RNNoise не смог создать состояние обработки.");
        }
    }

    public void RequestReset() => Interlocked.Add(ref configuration, 2);

    public unsafe float Process(Span<short> frame)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (frame.Length != MicProcessingChain.FrameSamples)
            throw new ArgumentException("Expected 960 mono samples at 48 kHz.", nameof(frame));
        int desired = Volatile.Read(ref configuration);
        if ((desired & 1) == 0) return -1;

        long now = Stopwatch.GetTimestamp();
        if (desired != appliedConfiguration ||
            (lastFrame != 0 && Stopwatch.GetElapsedTime(lastFrame, now) >= TimeSpan.FromSeconds(3)))
        {
            // rnnoise_init(state, NULL) resets the default model in place, without allocation.
            if (api.Init(state.DangerousGetHandle(), IntPtr.Zero) != 0)
                throw new InvalidOperationException("RNNoise не смог сбросить состояние.");
            appliedConfiguration = desired;
        }
        lastFrame = now;

        float probability = 0;
        fixed (float* source = input)
        fixed (float* destination = output)
        {
            for (int offset = 0; offset < frame.Length; offset += BlockSamples)
            {
                for (int i = 0; i < BlockSamples; i++) input[i] = frame[offset + i];
                // rnnoise_demo.c uses PCM16 scale, not normalized -1..1 floats.
                probability += api.Process(state.DangerousGetHandle(), destination, source);
                for (int i = 0; i < BlockSamples; i++)
                {
                    float sample = output[i];
                    if (!float.IsFinite(sample)) throw new InvalidDataException("RNNoise вернул некорректный отсчёт.");
                    frame[offset + i] = (short)Math.Clamp(MathF.Round(sample), short.MinValue, short.MaxValue);
                }
            }
        }
        // No speech gate: quiet speech and consonant tails are left to the model.
        return Math.Clamp(probability * .5f, 0, 1);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        state.Dispose();
    }

    private sealed class StateHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private readonly NativeApi api;
        public StateHandle(NativeApi api) : base(true)
        {
            this.api = api;
            SetHandle(api.Create(IntPtr.Zero));
        }
        protected override bool ReleaseHandle() { api.Destroy(handle); return true; }
    }

    // Delegates use the exact C ABI from include/rnnoise.h in the pinned v0.2 release.
    private sealed unsafe class NativeApi
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int FrameSizeCall();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr CreateCall(IntPtr model);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int InitCall(IntPtr state, IntPtr model);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void DestroyCall(IntPtr state);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate float ProcessCall(IntPtr state, float* output, float* input);
        internal readonly FrameSizeCall FrameSize;
        internal readonly CreateCall Create;
        internal readonly InitCall Init;
        internal readonly DestroyCall Destroy;
        internal readonly ProcessCall Process;

        public NativeApi()
        {
            if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
                throw new PlatformNotSupportedException("RNNoise для Wisp требует Windows x64.");
            string path = Path.Combine(AppContext.BaseDirectory, "rnnoise.dll");
            // Absolute application path only. Dependent system DLLs cannot come from CWD/PATH.
            IntPtr module = NativeLibrary.Load(path, typeof(RnNoiseProcessor).Assembly,
                DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.System32);
            try
            {
                FrameSize = Export<FrameSizeCall>(module, "rnnoise_get_frame_size");
                Create = Export<CreateCall>(module, "rnnoise_create");
                Init = Export<InitCall>(module, "rnnoise_init");
                Destroy = Export<DestroyCall>(module, "rnnoise_destroy");
                Process = Export<ProcessCall>(module, "rnnoise_process_frame");
            }
            catch { NativeLibrary.Free(module); throw; }
            // Successful module is process-lived: SafeHandle finalizers may still call its exports.
        }

        private static T Export<T>(IntPtr module, string name) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(module, name));
    }
}
