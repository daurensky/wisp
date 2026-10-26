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
    public void Dispose() => (decoder as IDisposable)?.Dispose();
}
