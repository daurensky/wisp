using System.Runtime.ExceptionServices;
using Wisp.Core;
using Wisp.Desktop.Media;
using Xunit;
namespace Wisp.Desktop.Tests;
public sealed class VideoCodecTests
{
    [Fact]
    public void NativeH264EncodesAndDecodesBgraWithoutJpegOrUiThread()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var info = new ScreenStreamInfo(320, 192, 60, 2000000);
                using var encoder = new NativeVideoEncoder(info); using var decoder = new NativeVideoDecoder(info);
                var pixels = new byte[info.Width * info.Height * 4];
                for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 24; pixels[i + 1] = 100; pixels[i + 2] = 210; pixels[i + 3] = 255; }
                int encoded = 0, decoded = 0; bool key = false;
                for (int frame = 0; frame < 10; frame++)
                {
                    var video = encoder.Encode(pixels); if (video is null) continue; encoded++; key |= H264Frame.IsKeyFrame(video);
                    var output = decoder.Decode(video); if (output is null) continue; decoded++;
                    Assert.True(output.Length >= pixels.Length);
                    Assert.InRange(output[4 * (100 * info.Width + 100) + 2], 175, 240);
                }
                Assert.True(key, "The encoder must send an IDR for a new viewer."); Assert.True(encoded > 0); Assert.True(decoded > 0);
            }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.MTA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
