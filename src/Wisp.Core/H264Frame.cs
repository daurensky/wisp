using System.Buffers.Binary;
namespace Wisp.Core;
public static class H264Frame
{
    public static byte[] Pack(int fps, byte[] annexB)
    {
        if (fps is not (15 or 30 or 60) || annexB.Length is 0 or > 2 * 1024 * 1024) throw new ArgumentException("Invalid video frame.");
        var result = new byte[annexB.Length + 4]; BinaryPrimitives.WriteInt32BigEndian(result, fps); annexB.CopyTo(result, 4); return result;
    }
    public static bool TryUnpack(byte[] packet, out int fps, out byte[] annexB)
    {
        fps = packet.Length >= 5 ? BinaryPrimitives.ReadInt32BigEndian(packet) : 0; annexB = Array.Empty<byte>();
        if (fps is not (15 or 30 or 60) || packet.Length > 2 * 1024 * 1024 + 4) return false;
        annexB = packet[4..]; return true;
    }
    public static bool IsKeyFrame(ReadOnlySpan<byte> data)
    {
        for (int i = 0; i + 3 < data.Length; i++)
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1 && (data[i + 3] & 31) == 5) return true;
        return false;
    }
}
