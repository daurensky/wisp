using System.Buffers.Binary;

namespace Wisp.Core;

/// <summary>Bounded, ordered application fragments for an individual SCTP stream.</summary>
public sealed class RtcFragments
{
    public const int ChunkSize = 16000;
    private readonly int maximum;
    private byte[]? buffer;
    private Guid id;
    private int received;
    private DateTimeOffset started;
    public RtcFragments(int maximum) => this.maximum = maximum;
    public static IEnumerable<byte[]> Encode(byte[] bytes)
    {
        var id = Guid.NewGuid();
        for (int offset = 0; offset < bytes.Length; offset += ChunkSize)
        {
            var count = Math.Min(ChunkSize, bytes.Length - offset);
            var packet = new byte[24 + count]; id.TryWriteBytes(packet);
            BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(16), bytes.Length);
            BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(20), offset);
            bytes.AsSpan(offset, count).CopyTo(packet.AsSpan(24)); yield return packet;
        }
    }
    public byte[]? Receive(byte[] packet)
    {
        if (packet.Length is < 25 or > ChunkSize + 24) throw new InvalidDataException("Invalid RTC fragment.");
        var messageId = new Guid(packet.AsSpan(0, 16));
        int length = BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(16));
        int offset = BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(20));
        int count = packet.Length - 24;
        if (length < 1 || length > maximum || offset < 0 || offset > length - count) throw new InvalidDataException("RTC fragment exceeds limit.");
        if (buffer is not null && DateTimeOffset.UtcNow - started > TimeSpan.FromSeconds(20)) buffer = null;
        if (offset == 0) { buffer = new byte[length]; id = messageId; received = 0; started = DateTimeOffset.UtcNow; }
        if (buffer is null || messageId != id || buffer.Length != length || offset != received) { buffer = null; return null; }
        packet.AsSpan(24).CopyTo(buffer.AsSpan(offset)); received += count;
        if (received != length) return null;
        var result = buffer; buffer = null; return result;
    }
}
