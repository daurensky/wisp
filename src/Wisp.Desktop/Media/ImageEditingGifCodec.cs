using System.Text;

namespace Wisp.Desktop.Media;

/// <summary>A bounded GIF reader/compositor. It retains compressed frames, not hundreds of decoded canvases.</summary>
public sealed class ImageEditingGifDocument
{
    internal sealed record Frame(int Left, int Top, int Width, int Height, bool Interlaced, int Delay, int Disposal, int Transparent, uint[] Palette, int MinimumCodeSize, byte[] Data);
    private readonly List<Frame> frames = new();
    private readonly uint background;
    public int Width { get; }
    public int Height { get; }
    public int FrameCount => frames.Count;
    public int? LoopCount { get; private set; }
    public bool HasTransparency => frames.Any(frame => frame.Transparent >= 0);
    public IReadOnlyList<int> Delays => frames.Select(frame => frame.Delay).ToArray();

    public ImageEditingGifDocument(byte[] encoded)
    {
        if (encoded.Length is < 13 or > 4 * 1024 * 1024) throw new InvalidDataException("GIF: допустимый размер от 13 байт до 4 МиБ.");
        using var reader = new BinaryReader(new MemoryStream(encoded, false));
        var header = Encoding.ASCII.GetString(reader.ReadBytes(6));
        if (header is not ("GIF87a" or "GIF89a")) throw new InvalidDataException("Некорректный GIF.");
        Width = reader.ReadUInt16(); Height = reader.ReadUInt16();
        if (Width is < 1 or > 2048 || Height is < 1 or > 2048) throw new InvalidDataException("Изображение: максимум 2048×2048.");
        int packed = reader.ReadByte(), backgroundIndex = reader.ReadByte(); _ = reader.ReadByte();
        var global = (packed & 128) != 0 ? ReadPalette(reader, 2 << (packed & 7)) : Array.Empty<uint>();
        background = backgroundIndex < global.Length ? global[backgroundIndex] : 0;
        int delay = 0, disposal = 0, transparent = -1;
        bool trailer = false;
        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            int marker = reader.ReadByte();
            if (marker == 0x3b) { trailer = true; break; }
            if (marker == 0x21)
            {
                int label = reader.ReadByte();
                if (label == 0xf9)
                {
                    if (reader.ReadByte() != 4) throw new InvalidDataException("Некорректный блок GIF.");
                    int flags = reader.ReadByte(); delay = reader.ReadUInt16();
                    int index = reader.ReadByte(); transparent = (flags & 1) != 0 ? index : -1; disposal = (flags >> 2) & 7;
                    if (disposal > 3 || reader.ReadByte() != 0) throw new InvalidDataException("Неизвестный режим кадра GIF.");
                }
                else if (label == 0xff)
                {
                    int count = reader.ReadByte(); var application = ReadExactly(reader, count);
                    var data = ReadBlocks(reader);
                    if ((Encoding.ASCII.GetString(application) is "NETSCAPE2.0" or "ANIMEXTS1.0") && data.Length >= 3 && data[0] == 1)
                        LoopCount = data[1] | data[2] << 8;
                }
                else { _ = ReadBlocks(reader); if (label == 1) { delay = disposal = 0; transparent = -1; } }
                continue;
            }
            if (marker != 0x2c) throw new InvalidDataException("Некорректная последовательность GIF.");
            int left = reader.ReadUInt16(), top = reader.ReadUInt16(), width = reader.ReadUInt16(), height = reader.ReadUInt16();
            if (width < 1 || height < 1 || left + width > Width || top + height > Height) throw new InvalidDataException("Кадр находится вне изображения.");
            int flagsImage = reader.ReadByte();
            var palette = (flagsImage & 128) != 0 ? ReadPalette(reader, 2 << (flagsImage & 7)) : global;
            if (palette.Length == 0) throw new InvalidDataException("В GIF отсутствует палитра.");
            int minimum = reader.ReadByte();
            if (minimum is < 2 or > 8) throw new InvalidDataException("Некорректное сжатие GIF.");
            frames.Add(new(left, top, width, height, (flagsImage & 64) != 0, delay, disposal, transparent, palette, minimum, ReadBlocks(reader)));
            if (frames.Count > 500) throw new InvalidDataException("GIF: максимум 500 кадров.");
            delay = disposal = 0; transparent = -1;
        }
        if (!trailer || frames.Count == 0) throw new InvalidDataException("GIF не завершён или не содержит кадров.");
    }

    private static byte[] ReadExactly(BinaryReader reader, int length)
    {
        var bytes = reader.ReadBytes(length); if (bytes.Length != length) throw new InvalidDataException("Файл GIF обрезан."); return bytes;
    }
    private static uint[] ReadPalette(BinaryReader reader, int length)
    {
        var result = new uint[length];
        for (int i = 0; i < length; i++) { uint r = reader.ReadByte(), g = reader.ReadByte(), b = reader.ReadByte(); result[i] = 0xff000000 | r << 16 | g << 8 | b; }
        return result;
    }
    private static byte[] ReadBlocks(BinaryReader reader)
    {
        using var result = new MemoryStream();
        int count; while ((count = reader.ReadByte()) != 0) result.Write(ReadExactly(reader, count));
        return result.ToArray();
    }

    public IEnumerable<uint[]> RenderFrames(CancellationToken cancellation = default)
    {
        var canvas = new uint[checked(Width * Height)];
        uint clear = HasTransparency ? 0 : background; if (clear != 0) Array.Fill(canvas, clear);
        Frame? previous = null; uint[]? restore = null;
        foreach (var frame in frames)
        {
            cancellation.ThrowIfCancellationRequested();
            if (previous is { Disposal: 2 })
                for (int y = previous.Top; y < previous.Top + previous.Height; y++) Array.Fill(canvas, clear, y * Width + previous.Left, previous.Width);
            else if (previous is { Disposal: 3 } && restore is not null) Array.Copy(restore, canvas, canvas.Length);
            restore = frame.Disposal == 3 ? (uint[])canvas.Clone() : null;
            var indices = DecodeIndices(frame, cancellation);
            int row = 0;
            void DrawRow(int y)
            {
                cancellation.ThrowIfCancellationRequested();
                for (int x = 0; x < frame.Width; x++)
                {
                    int color = indices[row * frame.Width + x];
                    if (color == frame.Transparent) continue;
                    if (color >= frame.Palette.Length) throw new InvalidDataException("Индекс цвета вне палитры GIF.");
                    canvas[(y + frame.Top) * Width + frame.Left + x] = frame.Palette[color];
                }
                row++;
            }
            if (frame.Interlaced)
            {
                int[] starts = [0, 4, 2, 1], steps = [8, 8, 4, 2];
                for (int pass = 0; pass < 4; pass++) for (int y = starts[pass]; y < frame.Height; y += steps[pass]) DrawRow(y);
            }
            else for (int y = 0; y < frame.Height; y++) DrawRow(y);
            previous = frame;
            yield return (uint[])canvas.Clone();
        }
    }

    public uint[] RenderFrame(int index, CancellationToken cancellation = default)
    {
        if (index < 0 || index >= FrameCount) throw new ArgumentOutOfRangeException(nameof(index));
        int current = 0; foreach (var canvas in RenderFrames(cancellation)) { if (current++ == index) return canvas; }
        throw new InvalidDataException("Кадр GIF не найден.");
    }

    private static byte[] DecodeIndices(Frame frame, CancellationToken cancellation)
    {
        var output = new byte[checked(frame.Width * frame.Height)];
        var prefix = new ushort[4096]; var suffix = new byte[4096]; var stack = new byte[4097];
        int clear = 1 << frame.MinimumCodeSize, end = clear + 1, next = end + 1, size = frame.MinimumCodeSize + 1;
        int bit = 0, offset = 0, old = -1; byte first = 0; bool ended = false;
        while (bit + size <= frame.Data.Length * 8)
        {
            cancellation.ThrowIfCancellationRequested();
            int value = 0; for (int i = 0; i < size; i++) { value |= ((frame.Data[bit >> 3] >> (bit & 7)) & 1) << i; bit++; }
            if (value == clear) { size = frame.MinimumCodeSize + 1; next = end + 1; old = -1; continue; }
            if (value == end) { ended = true; break; }
            if (value > next || value >= 4096) throw new InvalidDataException("Повреждён словарь GIF.");
            if (old < 0)
            {
                if (value >= clear || offset == output.Length) throw new InvalidDataException("Повреждён первый код GIF.");
                output[offset++] = first = (byte)value; old = value; continue;
            }
            int code = value, count = 0;
            if (code == next) { stack[count++] = first; code = old; }
            while (code >= clear)
            {
                if (code >= next || count >= 4096) throw new InvalidDataException("Циклический словарь GIF.");
                stack[count++] = suffix[code]; code = prefix[code];
            }
            stack[count++] = first = (byte)code;
            if (offset + count > output.Length) throw new InvalidDataException("Кадр GIF содержит лишние пиксели.");
            while (count > 0) output[offset++] = stack[--count];
            if (next < 4096)
            {
                prefix[next] = (ushort)old; suffix[next++] = first;
                if (next == 1 << size && size < 12) size++;
            }
            old = value;
        }
        if (!ended || offset != output.Length) throw new InvalidDataException("Кадр GIF обрезан.");
        return output;
    }
}

public sealed record ImageEditingGifOutputFrame(uint[] Pixels, int Delay);

/// <summary>Writes real animated GIFs with per-frame palettes and original centisecond timing/loop count.</summary>
public static class ImageEditingGifCodec
{
    public static void Write(Stream stream, int width, int height, IEnumerable<ImageEditingGifOutputFrame> frames, int? loopCount, bool optimizeOpaque, CancellationToken cancellation = default, long maximumBytes = 4 * 1024 * 1024)
    {
        if (width is < 1 or > 2048 || height is < 1 or > 2048) throw new ArgumentOutOfRangeException(nameof(width));
        using var writer = new BinaryWriter(stream, Encoding.ASCII, true);
        writer.Write("GIF89a"u8); writer.Write((ushort)width); writer.Write((ushort)height); writer.Write((byte)0x70); writer.Write((byte)0); writer.Write((byte)0);
        if (loopCount is { } loops)
        {
            writer.Write(new byte[] { 0x21, 0xff, 11 }); writer.Write("NETSCAPE2.0"u8);
            writer.Write(new byte[] { 3, 1, (byte)loops, (byte)(loops >> 8), 0 });
        }
        uint[]? previous = null; int frameCount = 0;
        foreach (var frame in frames)
        {
            cancellation.ThrowIfCancellationRequested();
            if (frame.Pixels.Length != width * height || ++frameCount > 500) throw new InvalidDataException("Некорректный кадр GIF.");
            bool transparent = frame.Pixels.Any(pixel => pixel >> 24 < 128);
            if (optimizeOpaque && transparent) throw new InvalidDataException("Нельзя удалить прозрачность GIF.");
            int left = 0, top = 0, right = width, bottom = height;
            if (optimizeOpaque && previous is not null)
            {
                left = width; top = height; right = bottom = 0;
                for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) if (frame.Pixels[y * width + x] != previous[y * width + x]) { left = Math.Min(left, x); right = Math.Max(right, x + 1); top = Math.Min(top, y); bottom = Math.Max(bottom, y + 1); }
                if (left >= right) { left = top = 0; right = bottom = 1; }
            }
            int fw = right - left, fh = bottom - top;
            var pixels = new uint[fw * fh]; for (int y = 0; y < fh; y++) Array.Copy(frame.Pixels, (top + y) * width + left, pixels, y * fw, fw);
            var (palette, indices) = Quantize(pixels, cancellation);
            writer.Write(new byte[] { 0x21, 0xf9, 4, (byte)(((optimizeOpaque ? 1 : 2) << 2) | (transparent ? 1 : 0)) });
            writer.Write((ushort)Math.Clamp(frame.Delay, 0, ushort.MaxValue)); writer.Write((byte)0); writer.Write((byte)0);
            writer.Write((byte)0x2c); writer.Write((ushort)left); writer.Write((ushort)top); writer.Write((ushort)fw); writer.Write((ushort)fh); writer.Write((byte)0x87);
            foreach (uint color in palette) { writer.Write((byte)(color >> 16)); writer.Write((byte)(color >> 8)); writer.Write((byte)color); }
            writer.Write((byte)8);
            var compressed = EncodeIndices(indices, cancellation);
            for (int offset = 0; offset < compressed.Length; offset += 255) { int count = Math.Min(255, compressed.Length - offset); writer.Write((byte)count); writer.Write(compressed, offset, count); }
            writer.Write((byte)0); previous = frame.Pixels;
            if (stream.Position > maximumBytes) throw new InvalidDataException("После обработки GIF превышает допустимый размер.");
        }
        if (frameCount == 0) throw new InvalidDataException("В GIF нет кадров.");
        writer.Write((byte)0x3b); writer.Flush();
        if (stream.Position > maximumBytes) throw new InvalidDataException("После обработки GIF превышает допустимый размер.");
    }

    private static byte[] EncodeIndices(byte[] pixels, CancellationToken cancellation)
    {
        using var output = new MemoryStream();
        int current = 0, bits = 0, size = 9, next = 258;
        void Code(int code) { current |= code << bits; bits += size; while (bits >= 8) { output.WriteByte((byte)current); current >>= 8; bits -= 8; } }
        var dictionary = new Dictionary<int, int>(); Code(256);
        int prefix = pixels[0];
        for (int i = 1; i < pixels.Length; i++)
        {
            if ((i & 4095) == 0) cancellation.ThrowIfCancellationRequested();
            int value = pixels[i], key = prefix << 8 | value;
            if (dictionary.TryGetValue(key, out int match)) { prefix = match; continue; }
            Code(prefix);
            if (next < 4096)
            {
                dictionary[key] = next++;
                if (next > 1 << size && size < 12) size++;
            }
            else { Code(256); dictionary.Clear(); next = 258; size = 9; }
            prefix = value;
        }
        Code(prefix);
        if (next == 1 << size && size < 12) size++;
        Code(257); if (bits > 0) output.WriteByte((byte)current); return output.ToArray();
    }

    private sealed record Bin(int Index, int Count, long Red, long Green, long Blue)
    {
        public int R => (int)(Red / Count); public int G => (int)(Green / Count); public int B => (int)(Blue / Count);
    }
    private static (uint[] Palette, byte[] Indices) Quantize(uint[] pixels, CancellationToken cancellation)
    {
        var exact = new Dictionary<uint, byte>(); bool simple = true;
        foreach (uint pixel in pixels)
        {
            if (pixel >> 24 < 128) continue;
            uint color = pixel & 0xffffff;
            if (exact.ContainsKey(color)) continue;
            if (exact.Count == 255) { simple = false; break; }
            exact[color] = (byte)(exact.Count + 1);
        }
        var palette = new uint[256]; var indices = new byte[pixels.Length];
        if (simple)
        {
            foreach (var pair in exact) palette[pair.Value] = pair.Key;
            for (int i = 0; i < pixels.Length; i++) indices[i] = pixels[i] >> 24 < 128 ? (byte)0 : exact[pixels[i] & 0xffffff];
            return (palette, indices);
        }
        var count = new int[32768]; var red = new long[32768]; var green = new long[32768]; var blue = new long[32768];
        static int Index(uint color) => (int)(((color >> 19) & 31) << 10 | ((color >> 11) & 31) << 5 | (color >> 3) & 31);
        foreach (uint pixel in pixels)
        {
            if (pixel >> 24 < 128) continue; int key = Index(pixel); count[key]++; red[key] += pixel >> 16 & 255; green[key] += pixel >> 8 & 255; blue[key] += pixel & 255;
        }
        var initial = new List<Bin>(); for (int i = 0; i < count.Length; i++) if (count[i] > 0) initial.Add(new(i, count[i], red[i], green[i], blue[i]));
        var boxes = new List<List<Bin>> { initial };
        while (boxes.Count < 255)
        {
            cancellation.ThrowIfCancellationRequested();
            var target = boxes.Where(box => box.Count > 1).OrderByDescending(box => (long)box.Sum(bin => bin.Count) * Math.Max(box.Max(bin => bin.R) - box.Min(bin => bin.R), Math.Max(box.Max(bin => bin.G) - box.Min(bin => bin.G), box.Max(bin => bin.B) - box.Min(bin => bin.B)))).FirstOrDefault();
            if (target is null) break;
            int r = target.Max(bin => bin.R) - target.Min(bin => bin.R), g = target.Max(bin => bin.G) - target.Min(bin => bin.G), b = target.Max(bin => bin.B) - target.Min(bin => bin.B);
            target.Sort((x, y) => r >= g && r >= b ? x.R.CompareTo(y.R) : g >= b ? x.G.CompareTo(y.G) : x.B.CompareTo(y.B));
            int half = target.Sum(bin => bin.Count) / 2, cumulative = 0, cut = 1;
            for (int i = 0; i < target.Count - 1; i++) { cumulative += target[i].Count; cut = i + 1; if (cumulative >= half) break; }
            boxes.Remove(target); boxes.Add(target.GetRange(0, cut)); boxes.Add(target.GetRange(cut, target.Count - cut));
        }
        var mapping = new byte[32768];
        for (int i = 0; i < boxes.Count; i++)
        {
            var box = boxes[i]; long weight = box.Sum(bin => (long)bin.Count);
            palette[i + 1] = (uint)(box.Sum(bin => bin.Red) / weight) << 16 | (uint)(box.Sum(bin => bin.Green) / weight) << 8 | (uint)(box.Sum(bin => bin.Blue) / weight);
            foreach (var bin in box) mapping[bin.Index] = (byte)(i + 1);
        }
        for (int i = 0; i < pixels.Length; i++) indices[i] = pixels[i] >> 24 < 128 ? (byte)0 : mapping[Index(pixels[i])];
        return (palette, indices);
    }
}
