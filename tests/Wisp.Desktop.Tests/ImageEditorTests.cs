using System.IO;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wisp.Desktop;
using Wisp.Desktop.Media;
using Xunit;

namespace Wisp.Desktop.Tests;

public sealed class ImageEditorTests
{
    // Independently encoded with Pillow: offset subframes and disposal 1, 2, 3.
    private const string DisposalGif = "R0lGODlhCAAGAIEAAAAAAP8AAAAAAAAAACH/C05FVFNDQVBFMi4wAwECAAAh+QQFAwAAACwAAAAACAAGAAAIFQABCBwoMIBBggAMBkCoEKHDhwMDAgAh+QQJBwAAACwAAAEABAADAIEAAAAA/wAAAAAAAAAICwABBBgokODAAAEBACH5BA0LAAAALAIAAQADAAMAgQAAAAAA/wAAAAAAAAgHAAMIHDgwIAAh+QQFEwAAACwCAAEABAADAIEAAAD//wAAAAAAAAAICwABBBgokODAAAEBADs=";

    [Fact]
    public void GifCompositorRestoresPreviousCanvasAndBackgroundWithOriginalTiming()
    {
        var gif = new ImageEditingGifDocument(Convert.FromBase64String(DisposalGif));
        Assert.Equal(4, gif.FrameCount); Assert.Equal(2, gif.LoopCount);
        Assert.Equal(new[] { 3, 7, 11, 19 }, gif.Delays); Assert.True(gif.HasTransparency);
        var expected = new[]
        {
            "........RRR.....RRR.....RRR.....................",
            "........RGGG....RGGG....RGGG....................",
            "..........BBB.....BBB.....BBB...................",
            "...........YYY.....YYY.....YYY.................."
        };
        var actual = gif.RenderFrames().ToArray();
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i].Select(Color).ToArray(), actual[i]);
        Assert.Equal(actual[2], gif.RenderFrame(2));
        Assert.Throws<ArgumentOutOfRangeException>(() => gif.RenderFrame(4));
    }

    [Fact]
    public void GifWriterPreservesPaletteTransitionsLzwGrowthAndOpaqueDeltaFrames()
    {
        const int width = 160, height = 128;
        var random = new Random(7123);
        uint[] palette = Enumerable.Range(0, 255).Select(i => 0xff000000u | (uint)i << 16 | (uint)(254 - i) << 8 | (uint)(i * 67 % 256)).ToArray();
        var first = Enumerable.Range(0, width * height).Select(_ => palette[random.Next(palette.Length)]).ToArray();
        var second = (uint[])first.Clone(); second[^1] = palette[17];
        var third = (uint[])second.Clone(); third[0] = palette[29];
        var original = new[] { first, second, third };
        using var output = new MemoryStream();
        ImageEditingGifCodec.Write(output, width, height, original.Select((pixels, i) => new ImageEditingGifOutputFrame(pixels, i * 13 + 1)), 0, true);
        var reloaded = new ImageEditingGifDocument(output.ToArray());
        Assert.Equal(0, reloaded.LoopCount); Assert.False(reloaded.HasTransparency);
        Assert.Equal(new[] { 1, 14, 27 }, reloaded.Delays);
        var frames = reloaded.RenderFrames().ToArray();
        for (int i = 0; i < original.Length; i++) Assert.Equal(original[i], frames[i]);
    }

    [Fact]
    public void InterlacedGifRowsUseAllFourPassesAndTruncatedFramesAreRejected()
    {
        byte[] fixture = Convert.FromBase64String("R0lGODdhEAAQAIEAAAAAAP8AAAD/AAAA/ywAAAAAEAAQAEAIVAABCAQQoGAAAQgFDFg4YCBBgwkVMnRo8GBChg0HVoyIkeNEjRAvfhS4UeRCiiERYkRZsaDHkwNXgnSZsKTKkQBkkkwpwKZEmAJ1PqSJ0KdQoT4DAgA7");
        var gif = new ImageEditingGifDocument(fixture);
        uint[] colors = [0xff000000, 0xffff0000, 0xff00ff00, 0xff0000ff];
        Assert.Equal(Enumerable.Range(0, 256).Select(i => colors[(i % 16 / 4 + i / 16) % 4]).ToArray(), gif.RenderFrame(0));
        Assert.Throws<InvalidDataException>(() => new ImageEditingGifDocument(fixture[..^6]));
    }

    [Fact]
    public void AnimatedCropKeepsEveryCompositedFrameCoverDelaysAndOriginalBytes() => Sta(() =>
    {
        WithSource(Convert.FromBase64String(DisposalGif), source =>
        {
            byte[] original = SHA256.HashData(File.ReadAllBytes(source));
            var document = new ImageEditingDocument(source);
            using var edited = ImageEditingService.Save(document, new Rect(1, 1, 4, 4), ImageEditKind.UserAvatar, 2);
            Assert.Equal(4, edited.Width); Assert.Equal(4, edited.Height); Assert.Equal(4, edited.FrameCount); Assert.Equal(2, edited.CoverFrame);
            Assert.EndsWith(".gif", edited.Path); Assert.InRange(new FileInfo(edited.Path).Length, 1, ImageEditingService.MaximumBytes);
            var output = new ImageEditingGifDocument(File.ReadAllBytes(edited.Path));
            Assert.Equal(2, output.LoopCount); Assert.Equal(document.Delays, output.Delays);
            var expected = new ImageEditingGifDocument(File.ReadAllBytes(source)).RenderFrames().Select(frame => Enumerable.Range(0, 4).SelectMany(y => frame.Skip((y + 1) * 8 + 1).Take(4)).ToArray()).ToArray();
            var actual = output.RenderFrames().ToArray();
            for (int i = 0; i < expected.Length; i++) Assert.Equal(expected[i], actual[i]);
            var cover = ImageEditingService.LoadCover(edited.Path, edited.CoverFrame, 4);
            Assert.True(cover.IsFrozen); Assert.Equal(expected[2], Pixels(cover));
            Assert.Equal(original, SHA256.HashData(File.ReadAllBytes(source)));
            string outputPath = edited.Path, directory = Path.GetDirectoryName(outputPath)!;
            edited.Dispose(); Assert.False(File.Exists(outputPath)); Assert.False(Directory.Exists(directory));
        });
    });

    [Fact]
    public void StaticAvatarAndBannerHaveExactProportionsAndLeaveSourceUntouched() => Sta(() =>
    {
        var original = Enumerable.Range(0, 48 * 27).Select(i => 0xff000000u | (uint)(i % 48 * 5) << 16 | (uint)(i / 48 * 9) << 8 | 19u).ToArray();
        WithSource(Png(original, 48, 27), source =>
        {
            var document = new ImageEditingDocument(source);
            using var banner = ImageEditingService.Save(document, new Rect(0, 0, 48, 27), ImageEditKind.ProfileBanner, 0);
            using var avatar = ImageEditingService.Save(document, new Rect(10, 5, 12, 12), ImageEditKind.UserAvatar, 0);
            Assert.Equal(48, banner.Width); Assert.Equal(27, banner.Height); Assert.Equal(1, banner.FrameCount); Assert.Equal(0, banner.CoverFrame);
            Assert.Equal(original, Pixels(new ImageEditingDocument(banner.Path).GetFrame(0)));
            Assert.Equal(12, avatar.Width); Assert.Equal(12, avatar.Height);
            Assert.Equal(Enumerable.Range(0, 12).SelectMany(y => original.Skip((y + 5) * 48 + 10).Take(12)).ToArray(), Pixels(new ImageEditingDocument(avatar.Path).GetFrame(0)));
            Assert.Equal(original, Pixels(new ImageEditingDocument(source).GetFrame(0)));
            var tiny = new ImageEditingDocument(avatar.Path);
            using var zoomed = ImageEditingService.Save(tiny, new Rect(5.75, 5.75, .5, .5), ImageEditKind.ServerIcon, 0);
            Assert.Equal(1, zoomed.Width); Assert.Equal(1, zoomed.Height);
        });
    });

    [Fact]
    public void CropRejectsInvalidBoundsFramesAndOversizedInputsAndHonorsCancellation() => Sta(() =>
    {
        WithSource(Convert.FromBase64String(DisposalGif), source =>
        {
            var document = new ImageEditingDocument(source);
            foreach (var crop in new[] { Rect.Empty, new Rect(-1, 0, 4, 4), new Rect(7, 0, 4, 4), new Rect(0, 0, 4, 2), new Rect(double.NaN, 0, 4, 4) })
                Assert.Throws<ArgumentException>(() => ImageEditingService.Save(document, crop, ImageEditKind.UserAvatar, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => ImageEditingService.Save(document, new Rect(0, 0, 4, 4), ImageEditKind.UserAvatar, 4));
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => ImageEditingService.Save(document, new Rect(0, 0, 4, 4), ImageEditKind.UserAvatar, 0, cancellation.Token));
            Assert.Throws<OperationCanceledException>(() => document.GetFrame(0, 16, cancellation.Token));
        });
        WithSource(new byte[ImageEditingService.MaximumBytes + 1], source => Assert.Throws<InvalidDataException>(() => new ImageEditingDocument(source)));
        WithSource(Png(new uint[2049], 2049, 1), source => Assert.Throws<InvalidDataException>(() => new ImageEditingDocument(source)));
        using var stream = new MemoryStream();
        Assert.Throws<InvalidDataException>(() => ImageEditingGifCodec.Write(stream, 2, 2, new[] { new ImageEditingGifOutputFrame(new uint[4], 1) }, 0, false, maximumBytes: 10));
    });

    [Fact]
    public void EditorDraftStateClampsZoomAndDoesNotAllowSavingBeforeDecode()
    {
        var editor = new ImageEditorViewModel(ImageEditKind.ProfileBanner);
        Assert.True(editor.Busy); Assert.False(editor.CanEdit); Assert.False(editor.CanSave); Assert.False(editor.Animated);
        editor.Zoom = 500; Assert.Equal(8, editor.Zoom); editor.Zoom = .01; Assert.Equal(1, editor.Zoom);
        editor.CoverFrame = 700; Assert.Equal(0, editor.CoverFrame);
    }

    private static uint Color(char value) => value switch { 'R' => 0xffff0000, 'G' => 0xff00ff00, 'B' => 0xff0000ff, 'Y' => 0xffffff00, _ => 0 };
    private static uint[] Pixels(BitmapSource bitmap)
    {
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var pixels = new uint[converted.PixelWidth * converted.PixelHeight]; converted.CopyPixels(pixels, converted.PixelWidth * 4, 0); return pixels;
    }
    private static byte[] Png(uint[] pixels, int width, int height)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }
    private static void WithSource(byte[] encoded, Action<string> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "wisp-image-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        string source = Path.Combine(root, "original.image"); File.WriteAllBytes(source, encoded);
        try { action(source); } finally { Directory.Delete(root, true); }
    }
    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Image editor operation timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
