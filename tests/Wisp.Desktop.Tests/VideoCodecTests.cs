using System.Runtime.ExceptionServices;
using Wisp.Core;
using Wisp.Desktop.Media;
using Xunit;
namespace Wisp.Desktop.Tests;
public sealed class VideoCodecTests(Xunit.Abstractions.ITestOutputHelper log)
{
    [Theory]
    [InlineData(320, 192, 200)]
    [InlineData(1920, 1080, 4)]
    public void NativeH264EncodesAndDecodesBgraWithoutJpegOrUiThread(int width, int height, int frames)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var info = new ScreenStreamInfo(width, height, 60, 4000000);
                using var encoder = new NativeVideoEncoder(info); using var decoder = new NativeVideoDecoder(info);
                var pixels = new byte[info.Width * info.Height * 4];
                for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 24; pixels[i + 1] = 100; pixels[i + 2] = 210; pixels[i + 3] = 255; }
                int encoded = 0, decoded = 0; int keyFrames = 0;
                for (int frame = 0; frame < frames; frame++)
                {
                    if (frame == 30) encoder.RequestKeyFrame();
                    var video = encoder.Encode(pixels); if (video is null) continue; encoded++; if (H264Frame.IsKeyFrame(video)) keyFrames++;
                    var output = decoder.Decode(video); if (output is null) continue; decoded++;
                    Assert.True(output.Length >= pixels.Length);
                    Assert.InRange(output[4 * (100 * info.Width + 100) + 2], 175, 240);
                }
                Assert.True(keyFrames >= (frames > 60 ? 3 : 1), "New and late viewers need periodic IDR frames."); Assert.True(encoded > 0); Assert.True(decoded > 0);
            }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.MTA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
    [Fact]
    public void LiveBitrateChangeReducesMeasuredOutputAndRequestedIdrArrivesImmediately()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var encoder = new NativeVideoEncoder(new(320, 192, 30, 4000000));
                var pixels = new byte[320 * 192 * 4];
                var noise = new Random(42);
                long high = 0, low = 0;
                for (int frame = 0; frame < 300; frame++)
                {
                    noise.NextBytes(pixels);
                    if (frame == 120) Assert.True(encoder.TrySetBitrate(1000000), "Software MFT must accept live mean bitrate changes.");
                    if (frame == 200) encoder.RequestKeyFrame();
                    var packet = encoder.Encode(pixels);
                    if (packet is null) continue;
                    if (frame is >= 30 and < 120) high += packet.Length;
                    if (frame is >= 180 and < 270) low += packet.Length;
                    if (frame == 200) Assert.True(H264Frame.IsKeyFrame(packet));
                }
                log.WriteLine($"{encoder.RateControlStatus}; 3s high={high} bytes; 3s low={low} bytes; ratio={(double)low / high:0.000}");
                Assert.True(high > 0 && low > 0 && low < high * .8, "Measured stream bitrate must fall after live codec reconfiguration.");
            }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.MTA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(60)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
