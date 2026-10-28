using System.Windows.Media.Imaging;
using Wisp.Desktop.Media;

namespace Wisp.Desktop;

/// <summary>Shares encoded images and frozen covers, never a mutable GIF animator.</summary>
internal static class AvatarImageCache
{
    private const long MaximumEncodedBytes = 16 * 1024 * 1024;
    private const long MaximumCoverBytes = 16 * 1024 * 1024;
    private static readonly object gate = new();
    private static readonly Dictionary<string, (byte[] Value, long Used)> encoded = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, (BitmapSource Value, long Used)> covers = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Task<BitmapSource>> pending = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim coverWorkers = new(2, 2);
    private static long clock, encodedBytes, coverBytes;

    private static string Key(string path)
    {
        var file = new FileInfo(path);
        return file.FullName + "\0" + file.LastWriteTimeUtc.Ticks + ":" + file.Length;
    }

    internal static byte[] Encoded(string path)
    {
        string key = Key(path);
        lock (gate)
        {
            if (encoded.TryGetValue(key, out var item)) { encoded[key] = (item.Value, ++clock); return item.Value; }
        }
        if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidDataException("Изображение: не более 16 МБ.");
        var bytes = File.ReadAllBytes(path);
        lock (gate)
        {
            if (encoded.TryGetValue(key, out var existing)) return existing.Value;
            encoded[key] = (bytes, ++clock); encodedBytes += bytes.Length;
            while (encodedBytes > MaximumEncodedBytes && encoded.Count > 1)
            {
                var oldest = encoded.MinBy(pair => pair.Value.Used);
                encodedBytes -= oldest.Value.Value.Length; encoded.Remove(oldest.Key);
            }
        }
        return bytes;
    }

    internal static BitmapSource First(string path, int width)
    {
        // This inexpensive first paint can be an uncomposited GIF subframe. Keep it
        // separate from CoverAsync, which renders the logical canvas correctly.
        string key = Key(path) + "\0preview:0:" + width;
        lock (gate) if (covers.TryGetValue(key, out var found)) { covers[key] = (found.Value, ++clock); return found.Value; }
        using var input = new MemoryStream(Encoded(path), false);
        var frame = new BitmapImage(); frame.BeginInit(); frame.CacheOption = BitmapCacheOption.OnLoad;
        frame.DecodePixelWidth = width; frame.StreamSource = input; frame.EndInit(); frame.Freeze();
        AddCover(key, frame); return frame;
    }

    internal static Task<BitmapSource> CoverAsync(string path, int index, int width)
    {
        string key = Key(path) + "\0cover:" + index + ":" + width;
        lock (gate)
        {
            if (covers.TryGetValue(key, out var found)) { covers[key] = (found.Value, ++clock); return Task.FromResult(found.Value); }
            if (pending.TryGetValue(key, out var current)) return current;
            var task = Task.Run(async () =>
            {
                await coverWorkers.WaitAsync().ConfigureAwait(false);
                try
                {
                    var frame = ImageEditingService.LoadCover(path, index, width);
                    AddCover(key, frame); return frame;
                }
                finally { coverWorkers.Release(); lock (gate) pending.Remove(key); }
            });
            pending[key] = task; return task;
        }
    }

    private static void AddCover(string key, BitmapSource frame)
    {
        lock (gate)
        {
            if (covers.ContainsKey(key)) return;
            covers[key] = (frame, ++clock); coverBytes += (long)frame.PixelWidth * frame.PixelHeight * 4;
            while (coverBytes > MaximumCoverBytes && covers.Count > 1)
            {
                var oldest = covers.MinBy(pair => pair.Value.Used);
                coverBytes -= (long)oldest.Value.Value.PixelWidth * oldest.Value.Value.PixelHeight * 4; covers.Remove(oldest.Key);
            }
        }
    }
}
