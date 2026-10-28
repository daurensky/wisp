using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Wisp.Core;
using DrawingIcon = System.Drawing.Icon;

namespace Wisp.Desktop;

/// <summary>Small cached tray artwork; independent of the window/taskbar branding.</summary>
public static class VoiceTrayIcons
{
    public static IReadOnlyList<int> Sizes { get; } = Array.AsReadOnly(new[] { 16, 20, 24, 32 });

    public static DrawingIcon Create(VoiceTrayState state, int preferredSize = 0)
    {
        if (state == VoiceTrayState.Disconnected) return Brand.TrayIcon();
        using var stream = new MemoryStream(Encode(state), writable: false);
        int size = preferredSize > 0 ? preferredSize : System.Windows.Forms.SystemInformation.SmallIconSize.Width;
        using var original = new DrawingIcon(stream, new Size(size, size));
        // Clone owns its native icon; callers can release the ICO stream now.
        return (DrawingIcon)original.Clone();
    }

    public static byte[] Encode(VoiceTrayState state)
    {
        if (state == VoiceTrayState.Disconnected) throw new ArgumentException("Use the existing Wisp icon outside voice.", nameof(state));
        var images = new List<byte[]>();
        foreach (int size in Sizes)
        {
            using var bitmap = Render(state, size);
            using var image = new MemoryStream();
            bitmap.Save(image, ImageFormat.Png); images.Add(image.ToArray());
        }
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)Sizes.Count);
        int offset = 6 + 16 * Sizes.Count;
        for (int i = 0; i < Sizes.Count; i++)
        {
            writer.Write((byte)Sizes[i]); writer.Write((byte)Sizes[i]); writer.Write((byte)0); writer.Write((byte)0);
            writer.Write((ushort)1); writer.Write((ushort)32); writer.Write(images[i].Length); writer.Write(offset);
            offset += images[i].Length;
        }
        foreach (var image in images) writer.Write(image);
        return output.ToArray();
    }

    public static Bitmap Render(VoiceTrayState state, int size)
    {
        if (!Sizes.Contains(size)) throw new ArgumentOutOfRangeException(nameof(size));
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.Transparent); graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.ScaleTransform(size / 32f, size / 32f);
            switch (state)
            {
                case VoiceTrayState.Silent: Orb(graphics, Color.FromArgb(0x17, 0x4E, 0x9C)); break;
                case VoiceTrayState.Speaking: Orb(graphics, Color.FromArgb(0x21, 0x85, 0xFF)); break;
                case VoiceTrayState.Error: Orb(graphics, Color.FromArgb(0xF0, 0x44, 0x59)); break;
                case VoiceTrayState.Muted: Microphone(graphics); Strike(graphics); break;
                case VoiceTrayState.Deafened: Headphones(graphics); Strike(graphics); break;
                default: throw new ArgumentOutOfRangeException(nameof(state));
            }
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    private static Color Shade(Color color, float scale) => Color.FromArgb(
        (int)Math.Clamp(color.R * scale, 0, 255), (int)Math.Clamp(color.G * scale, 0, 255), (int)Math.Clamp(color.B * scale, 0, 255));

    private static void Orb(Graphics graphics, Color color)
    {
        var bounds = new RectangleF(2, 2, 28, 28);
        using var gradient = new LinearGradientBrush(bounds, color, color, 48f);
        gradient.InterpolationColors = new ColorBlend
        {
            Colors = new[] { Shade(color, 1.4f), color, Shade(color, .55f) },
            Positions = new[] { 0f, .48f, 1f }
        };
        graphics.FillEllipse(gradient, bounds);
        using var edge = new Pen(Color.FromArgb(95, Shade(color, 1.6f)), .7f);
        graphics.DrawEllipse(edge, bounds);
        // One small upper-left glint; no text or microphone inside the sphere.
        using var glint = new SolidBrush(Color.FromArgb(210, 234, 247, 255));
        graphics.FillEllipse(glint, 8, 6, 5.5f, 3.2f);
    }

    private static Pen GlyphPen(float width) => new(Color.FromArgb(0x21, 0x85, 0xFF), width)
    { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };

    private static void Microphone(Graphics graphics)
    {
        using var blue = new SolidBrush(Color.FromArgb(0x21, 0x85, 0xFF));
        using var capsule = RoundedRectangle(new RectangleF(12, 3, 8, 16), 4);
        graphics.FillPath(blue, capsule);
        using var pen = GlyphPen(2.5f);
        graphics.DrawArc(pen, 8, 10, 16, 15, 0, 180);
        graphics.DrawLine(pen, 16, 25, 16, 29); graphics.DrawLine(pen, 11, 29, 21, 29);
    }

    private static void Headphones(Graphics graphics)
    {
        using var pen = GlyphPen(3);
        graphics.DrawArc(pen, 6, 4, 20, 24, 180, 180);
        using var blue = new SolidBrush(Color.FromArgb(0x21, 0x85, 0xFF));
        using var left = RoundedRectangle(new RectangleF(4, 15, 6, 12), 2.5f);
        using var right = RoundedRectangle(new RectangleF(22, 15, 6, 12), 2.5f);
        graphics.FillPath(blue, left); graphics.FillPath(blue, right);
    }

    private static void Strike(Graphics graphics)
    {
        // Remove the narrow path under the slash so the glyph reads at 16 px
        // on either taskbar theme without painting a solid background tile.
        graphics.CompositingMode = CompositingMode.SourceCopy;
        using var clear = new Pen(Color.Transparent, 6) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        graphics.DrawLine(clear, 5, 4, 27, 28);
        graphics.CompositingMode = CompositingMode.SourceOver;
        using var pen = GlyphPen(2.7f); graphics.DrawLine(pen, 5, 4, 27, 28);
    }

    private static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        float diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure(); return path;
    }
}
