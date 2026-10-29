using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Wisp.Desktop.Media;

public sealed class ImageEditingDocument
{
    private readonly BitmapSource? still;
    internal ImageEditingGifDocument? Gif { get; }
    internal byte[] Encoded { get; }
    public int Width { get; }
    public int Height { get; }
    public int FrameCount => Gif?.FrameCount ?? 1;
    public IReadOnlyList<int> Delays => Gif?.Delays ?? new[] { 0 };
    public bool Animated => FrameCount > 1;

    public ImageEditingDocument(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length is <= 0 or > ImageEditingService.MaximumBytes) throw new InvalidDataException("Изображение должно быть не больше 4 МиБ.");
        Encoded = File.ReadAllBytes(path);
        if (Encoded.AsSpan().StartsWith("GIF87a"u8) || Encoded.AsSpan().StartsWith("GIF89a"u8))
        {
            Gif = new ImageEditingGifDocument(Encoded); Width = Gif.Width; Height = Gif.Height; return;
        }
        bool png = Encoded.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        bool jpeg = Encoded.Length >= 3 && Encoded[0] == 255 && Encoded[1] == 216 && Encoded[2] == 255;
        if (!png && !jpeg) throw new InvalidDataException("Поддерживаются PNG, JPEG и GIF.");
        using var stream = new MemoryStream(Encoded, false);
        // Inspect dimensions before WIC decodes the full raster. A small,
        // highly compressed PNG/JPEG must not allocate an enormous canvas.
        var metadata = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        Width = metadata.Frames[0].PixelWidth; Height = metadata.Frames[0].PixelHeight;
        if (Width is < 1 or > 2048 || Height is < 1 or > 2048) throw new InvalidDataException("Изображение: максимум 2048×2048.");
        stream.Position = 0;
        var source = new BitmapImage(); source.BeginInit(); source.CacheOption = BitmapCacheOption.OnLoad; source.StreamSource = stream; source.EndInit(); source.Freeze();
        still = source;
    }

    public BitmapSource GetFrame(int frame, int decodeWidth = 1024, CancellationToken cancellation = default)
    {
        if (frame < 0 || frame >= FrameCount) throw new ArgumentOutOfRangeException(nameof(frame));
        if (decodeWidth < 1) throw new ArgumentOutOfRangeException(nameof(decodeWidth));
        cancellation.ThrowIfCancellationRequested();
        var bitmap = Gif is null ? still! : ImageEditingService.Bitmap(Gif.RenderFrame(frame, cancellation), Width, Height);
        if (bitmap.PixelWidth <= decodeWidth) return bitmap;
        var reduced = new TransformedBitmap(bitmap, new ScaleTransform((double)decodeWidth / bitmap.PixelWidth, (double)decodeWidth / bitmap.PixelWidth)); reduced.Freeze(); return reduced;
    }

    internal IEnumerable<uint[]> Pixels(CancellationToken cancellation)
    {
        if (Gif is not null) { foreach (var frame in Gif.RenderFrames(cancellation)) yield return frame; yield break; }
        var converted = new FormatConvertedBitmap(still!, PixelFormats.Bgra32, null, 0); converted.Freeze();
        var pixels = new uint[Width * Height]; converted.CopyPixels(pixels, Width * 4, 0); yield return pixels;
    }
}

public static class ImageEditingService
{
    public const int MaximumBytes = 4 * 1024 * 1024;

    /// <summary>The same compositor is used by the editor and paused avatar/banner covers.</summary>
    public static BitmapSource LoadCover(string path, int frameIndex, int decodeWidth)
    {
        var document = new ImageEditingDocument(path);
        return document.GetFrame(Math.Clamp(frameIndex, 0, document.FrameCount - 1), Math.Max(1, decodeWidth));
    }

    public static EditedImage Save(ImageEditingDocument document, Rect crop, ImageEditKind kind, int coverFrame, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        bool banner = kind == ImageEditKind.ProfileBanner;
        double ratio = banner ? ProfileLayout.BannerRatio : 1;
        if (!double.IsFinite(crop.X) || !double.IsFinite(crop.Y) || !double.IsFinite(crop.Width) || !double.IsFinite(crop.Height) ||
            crop.Width <= 0 || crop.Height <= 0 || crop.X < -.001 || crop.Y < -.001 || crop.Right > document.Width + .001 || crop.Bottom > document.Height + .001 || Math.Abs(crop.Width / crop.Height - ratio) > .0001)
            throw new ArgumentException("Область обрезки находится вне изображения или имеет неверные пропорции.", nameof(crop));
        if (coverFrame < 0 || coverFrame >= document.FrameCount) throw new ArgumentOutOfRangeException(nameof(coverFrame));
        cancellation.ThrowIfCancellationRequested();
        int width = banner ? ProfileLayout.BannerWidth : Math.Clamp((int)Math.Round(crop.Width), 1, 512);
        var directory = Path.Combine(Path.GetTempPath(), "wisp-image-editor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, document.Animated ? "edited.gif" : "edited.png");
        try
        {
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                int height = banner ? ProfileLayout.BannerHeight : width;
                try
                {
                    using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
                    if (document.Animated)
                    {
                        var delays = document.Delays;
                        IEnumerable<ImageEditingGifOutputFrame> CroppedFrames()
                        {
                            int index = 0;
                            foreach (var pixels in document.Pixels(cancellation))
                                yield return new(CropAndScale(pixels, document.Width, document.Height, crop, width, height, cancellation), delays[index++]);
                        }
                        ImageEditingGifCodec.Write(output, width, height, CroppedFrames(), document.Gif!.LoopCount, !document.Gif.HasTransparency, cancellation, MaximumBytes);
                    }
                    else
                    {
                        var pixels = document.Pixels(cancellation).Single();
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(Bitmap(CropAndScale(pixels, document.Width, document.Height, crop, width, height, cancellation), width, height))); encoder.Save(output);
                    }
                }
                catch (InvalidDataException error) when (!banner && error.Message.Contains("превышает допустимый размер", StringComparison.Ordinal) && width > 64)
                {
                    width = Math.Max(64, width * 3 / 4); continue;
                }
                cancellation.ThrowIfCancellationRequested();
                long size = new FileInfo(path).Length;
                if (size > MaximumBytes) throw new InvalidDataException("После обработки изображение больше 4 МиБ. Выберите меньшую область.");
                // Large frame expansion is avoided by retrying a smaller animated canvas.
                // All frames remain present; there is no hidden conversion to a still image.
                if (document.Animated && size > Math.Max(512 * 1024, document.Encoded.Length * 3L) && (banner || width > 64))
                {
                    // A banner has an exact 400×140 output contract. Keep animation
                    // and dimensions; report growth rather than silently shrinking it.
                    if(banner)throw new InvalidDataException("После обрезки GIF-баннер стал слишком большим. Выберите менее детализированную область или другой файл.");
                    width = Math.Max(64, width * 3 / 4); continue;
                }
                return new EditedImage(path, document.Animated ? coverFrame : 0, width, height, document.FrameCount);
            }
        }
        catch { if (Directory.Exists(directory)) Directory.Delete(directory, true); throw; }
    }

    internal static BitmapSource Bitmap(uint[] pixels, int width, int height)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4); bitmap.Freeze(); return bitmap;
    }

    private static uint[] CropAndScale(uint[] source, int sourceWidth, int sourceHeight, Rect crop, int width, int height, CancellationToken cancellation)
    {
        var result = new uint[checked(width * height)];
        for (int y = 0; y < height; y++)
        {
            cancellation.ThrowIfCancellationRequested();
            double sy = Math.Clamp(crop.Y + (y + .5) * crop.Height / height - .5, 0, sourceHeight - 1d);
            int y0 = (int)sy, y1 = Math.Min(sourceHeight - 1, y0 + 1); double fy = sy - y0;
            for (int x = 0; x < width; x++)
            {
                double sx = Math.Clamp(crop.X + (x + .5) * crop.Width / width - .5, 0, sourceWidth - 1d);
                int x0 = (int)sx, x1 = Math.Min(sourceWidth - 1, x0 + 1); double fx = sx - x0;
                uint a = source[y0 * sourceWidth + x0], b = source[y0 * sourceWidth + x1], c = source[y1 * sourceWidth + x0], d = source[y1 * sourceWidth + x1];
                double wa = (1 - fx) * (1 - fy), wb = fx * (1 - fy), wc = (1 - fx) * fy, wd = fx * fy;
                double alpha = (a >> 24) * wa + (b >> 24) * wb + (c >> 24) * wc + (d >> 24) * wd;
                if (alpha < .5) continue;
                uint Channel(int shift) => (uint)Math.Clamp(Math.Round((((a >> shift & 255) * (a >> 24) * wa) + ((b >> shift & 255) * (b >> 24) * wb) + ((c >> shift & 255) * (c >> 24) * wc) + ((d >> shift & 255) * (d >> 24) * wd)) / alpha), 0, 255);
                result[y * width + x] = (uint)Math.Clamp(Math.Round(alpha), 0, 255) << 24 | Channel(16) << 16 | Channel(8) << 8 | Channel(0);
            }
        }
        return result;
    }
}
