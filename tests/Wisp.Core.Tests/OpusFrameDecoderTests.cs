using Concentus;
using Concentus.Enums;
using Xunit;
namespace Wisp.Core.Tests;
public sealed class OpusFrameDecoderTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void SpeechSilenceBitrateChangesAndLostPacketsDecodeWithoutPacketErrors(int channels)
    {
        var encoder = OpusCodecFactory.CreateEncoder(48000, channels, OpusApplication.OPUS_APPLICATION_VOIP);
        using var decoder = new OpusFrameDecoder(channels);
        try
        {
            if (OperatingSystem.IsWindows()) Assert.Equal("NativeOpusEncoder", encoder.GetType().Name);
            encoder.UseInbandFEC = true; encoder.PacketLossPercent = 10; encoder.UseDTX = true;
            var pcm = new short[960 * channels]; var output = new short[960 * channels]; var packet = new byte[1275]; int decoded = 0;
            for (int frame = 0; frame < 300; frame++)
            {
                encoder.Bitrate = frame < 100 ? 16000 : frame < 200 ? 32000 : 96000;
                for (int i = 0; i < pcm.Length; i++) pcm[i] = frame % 60 < 20 ? (short)0 : (short)(Math.Sin(2 * Math.PI * (frame * 960 + i / channels) * 330 / 48000) * 4000);
                int length = encoder.Encode(pcm, 960, packet, packet.Length); if (frame % 13 == 0) continue;
                Assert.True(decoder.TryDecode(packet.AsSpan(0, length), output, out int samples), $"Frame {frame}, {length} bytes"); Assert.Equal(960, samples); decoded++;
            }
            Assert.True(decoded > 250); Assert.Equal(0, decoder.DroppedPackets);
            Assert.False(decoder.TryDecode(ReadOnlySpan<byte>.Empty, output, out _));
            Assert.False(decoder.TryDecode(new byte[1276], output, out _));
            int valid = encoder.Encode(pcm, 960, packet, packet.Length); Assert.True(decoder.TryDecode(packet.AsSpan(0, valid), output, out _));
        }
        finally { (encoder as IDisposable)?.Dispose(); }
    }
}
