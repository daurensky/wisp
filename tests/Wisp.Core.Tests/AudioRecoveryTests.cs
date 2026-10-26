using Concentus;
using Concentus.Enums;
using Wisp.Core;
using Xunit;
namespace Wisp.Core.Tests;

public sealed class AudioRecoveryTests
{
    [Fact]
    public void SequenceRolloverDropsLateDuplicatesAndBoundsRecoveryWork()
    {
        var order = new AudioSequenceTracker();
        Assert.True(order.Accept(65534, out int missing)); Assert.Equal(0, missing);
        Assert.True(order.Accept(0, out missing)); Assert.Equal(1, missing);
        Assert.False(order.Accept(65535, out _)); Assert.False(order.Accept(0, out _));
        Assert.True(order.Accept(40, out missing)); Assert.Equal(3, missing);
    }
    [Theory]
    [InlineData(1)] [InlineData(2)]
    public void MissingFramesUseFecOrPlcBeforeDecodingTheCurrentFrame(int channels)
    {
        var encoder = OpusCodecFactory.CreateEncoder(48000, channels, OpusApplication.OPUS_APPLICATION_VOIP);
        using var decoder = new OpusFrameDecoder(channels);
        try
        {
            encoder.Bitrate = 32000; encoder.UseInbandFEC = true; encoder.PacketLossPercent = 20;
            var pcm = new short[960 * channels]; var packet = new byte[1275]; var output = new short[960 * channels * 4];
            int pending = 0, recovered = 0;
            for (int frame = 0; frame < 300; frame++)
            {
                for (int i = 0; i < pcm.Length; i++) pcm[i] = (short)(4000 * Math.Sin((frame * 960 + i / channels) * Math.PI * 660 / 48000));
                int length = encoder.Encode(pcm, 960, packet, packet.Length);
                if (frame % 19 == 3 || frame % 19 == 4 || frame % 11 == 1) { pending = Math.Min(3, pending + 1); continue; }
                Assert.True(decoder.TryDecodeWithRecovery(packet.AsSpan(0, length), pending, output, out int samples));
                Assert.Equal(960 * (pending + 1), samples); recovered += pending; pending = 0;
            }
            Assert.True(recovered > 30); Assert.True(decoder.FecAttempts > 0); Assert.True(decoder.ConcealedFrames > 0);
            Assert.Equal(0, decoder.DroppedPackets);
            Assert.Contains(output, sample => sample != 0);
        }
        finally { (encoder as IDisposable)?.Dispose(); }
    }
    [Fact]
    public void ReceiverOnlyLossMetadataNeverChangesTheSerializedWireProtocol()
    {
        var message = new WireMessage("audio", "alice", "voice", [1, 2, 3]) { MissingAudioFrames = 3 };
        Assert.DoesNotContain("MissingAudioFrames", System.Text.Json.JsonSerializer.Serialize(message));
    }
    [Fact]
    public void JitterBufferPrimesThenTrimsLatencyWithoutErasingAllAudio()
    {
        var buffer = new PcmJitterBuffer();
        var frame = Enumerable.Repeat((short)8000, 960).ToArray(); var output = new float[960];
        buffer.Write(frame); buffer.Read(output); Assert.All(output, sample => Assert.Equal(0, sample));
        buffer.Write(frame); buffer.Read(output); Assert.Contains(output, sample => sample > 0);
        for (int i = 0; i < 14; i++) buffer.Write(frame);
        buffer.Read(output);
        Assert.True(buffer.TrimmedSamples > 0); Assert.True(buffer.BufferedSamples > 0);
        Assert.Contains(output, sample => sample > 0);
        for (int i = 0; i < 20; i++) buffer.Read(output);
        Assert.InRange(buffer.TargetMilliseconds, 40, 80);
    }
}
