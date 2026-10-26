using Concentus;
using Concentus.Structs;

namespace Wisp.Core;

/// <summary>Wisp sends 48kHz/20ms frames. Reject malformed packets before touching decoder state.</summary>
public sealed class OpusFrameDecoder : IDisposable
{
    public const int SamplesPerChannel = 960;
    private readonly IOpusDecoder decoder;
    private readonly int channels;
    public long DroppedPackets { get; private set; }
    public OpusFrameDecoder(int channels = 1) { this.channels = channels; decoder = OpusCodecFactory.CreateDecoder(48000, channels); }
    public bool TryDecode(ReadOnlySpan<byte> packet, Span<short> output, out int samples)
    {
        samples = 0;
        try
        {
            if (packet.Length is < 1 or > 1275 || output.Length < SamplesPerChannel * channels || OpusPacketInfo.GetNumSamples(packet, 48000) != SamplesPerChannel) { DroppedPackets++; return false; }
            samples = decoder.Decode(packet, output[..(SamplesPerChannel * channels)], SamplesPerChannel, false);
            return samples == SamplesPerChannel;
        }
        catch (Exception ex) when (ex is ArgumentException or Concentus.OpusException or IndexOutOfRangeException)
        {
            DroppedPackets++; decoder.ResetState(); return false;
        }
    }
    public long FecAttempts { get; private set; }
    public long ConcealedFrames { get; private set; }
    public bool TryDecodeWithRecovery(ReadOnlySpan<byte> packet, int missingFrames, Span<short> output, out int samplesPerChannel)
    {
        samplesPerChannel = 0;
        int missing = Math.Clamp(missingFrames, 0, 3);
        int frameSize = SamplesPerChannel * channels;
        if (packet.Length is < 1 or > 1275 || output.Length < frameSize * (missing + 1)) { DroppedPackets++; return false; }
        try
        {
            if (OpusPacketInfo.GetNumSamples(packet, 48000) != SamplesPerChannel) { DroppedPackets++; return false; }
            for (int i = 0; i < missing; i++)
            {
                // A single gap may be recovered from the following packet's LBRR. Opus falls back to PLC
                // when that packet has no FEC; count attempts rather than claiming FEC was present.
                bool fec = missing == 1;
                var source = fec ? packet : ReadOnlySpan<byte>.Empty;
                int restored = decoder.Decode(source, output.Slice(i * frameSize, frameSize), SamplesPerChannel, fec);
                if (restored != SamplesPerChannel) return false;
                if (fec) FecAttempts++; else ConcealedFrames++;
            }
            int decoded = decoder.Decode(packet, output.Slice(missing * frameSize, frameSize), SamplesPerChannel, false);
            if (decoded != SamplesPerChannel) return false;
            samplesPerChannel = SamplesPerChannel * (missing + 1);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or Concentus.OpusException or IndexOutOfRangeException)
        {
            DroppedPackets++; decoder.ResetState(); return false;
        }
    }
    public void Dispose() => (decoder as IDisposable)?.Dispose();
}
