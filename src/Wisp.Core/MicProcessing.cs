namespace Wisp.Core;

/// <summary>Local 48 kHz mono PCM processing, before Opus. One call is 960 samples (20 ms).</summary>
public interface IMicProcessor : IDisposable
{
    float Process(Span<short> frame);
    bool Enabled { get; set; }
}

/// <summary>Owned by one capture thread. Only processor Enabled properties may change from UI.</summary>
public sealed class MicProcessingChain : IDisposable
{
    public const int FrameSamples = 960;
    private readonly IMicProcessor[] processors;
    private readonly bool[] failed;
    private readonly short[] recovery = new short[FrameSamples];
    private bool disposed;

    public event Action<IMicProcessor, Exception>? ProcessorFailed;

    public MicProcessingChain(params IMicProcessor[] processors)
    {
        ArgumentNullException.ThrowIfNull(processors);
        if (processors.Any(processor => processor is null))
            throw new ArgumentException("A microphone processor cannot be null.", nameof(processors));
        this.processors = (IMicProcessor[])processors.Clone();
        failed = new bool[processors.Length];
    }

    // False means the caller must discard the frame rather than encode it.
    public bool Process(Span<short> frame, bool muted = false, bool deafened = false)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (frame.Length != FrameSamples)
            throw new ArgumentException("Expected 960 mono samples at 48 kHz.", nameof(frame));
        if (muted || deafened) return false;

        for (int index = 0; index < processors.Length; index++)
        {
            var processor = processors[index];
            if (failed[index] || !processor.Enabled) continue;
            frame.CopyTo(recovery);
            try { processor.Process(frame); }
            catch (Exception error)
            {
                // A failing stage may have changed only half a frame. Restore its entire input.
                recovery.CopyTo(frame);
                failed[index] = true;
                try { processor.Enabled = false; } catch { }
                // Notifications must never interrupt microphone capture, even if a subscriber fails.
                try { ProcessorFailed?.Invoke(processor, error); } catch { }
            }
        }
        return true;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var processor in processors)
            try { processor.Dispose(); } catch { }
    }
}
