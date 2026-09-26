using System.Buffers.Binary;

namespace RootEngineProbe;

// A reliable ordered Steam connection carries one request/response at a time.
// Bound allocations before copying and reject interleaving, gaps and stale frames.
internal static class SteamFrames
{
    public const int Chunk = 48 * 1024;
    public const int MaxRequest = 64 * 1024;
    public const int MaxResponse = 4 * 1024 * 1024;
    private const uint Magic = 0x31533652; // R6S1
    public static byte[] Encode(uint id, byte[] body, int offset)
    {
        if (id == 0 || body.Length is < 1 or > MaxResponse || offset < 0 || offset >= body.Length || offset % Chunk != 0)
            throw new ArgumentException("Invalid Steam frame source.");
        var count = Math.Min(Chunk, body.Length - offset);
        var frame = new byte[16 + count];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), id);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(8), body.Length);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(12), offset);
        body.AsSpan(offset, count).CopyTo(frame.AsSpan(16));
        return frame;
    }

    internal sealed class Reader
    {
        private readonly int maximum;
        private byte[]? body;
        private int offset;
        private uint id;
        public Reader(int maximum) => this.maximum = maximum;
        public (uint Id, byte[] Body)? Push(byte[] frame)
        {
            if (frame.Length is < 17 or > Chunk + 16 || BinaryPrimitives.ReadUInt32LittleEndian(frame) != Magic)
                throw new InvalidDataException("Invalid Steam frame header.");
            var incomingId = BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(4));
            var length = BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(8));
            var position = BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(12));
            if (incomingId == 0 || length < 1 || length > maximum || position != offset ||
                frame.Length - 16 != Math.Min(Chunk, length - offset) ||
                (body is not null && (incomingId != id || body.Length != length)))
                throw new InvalidDataException("Steam frame size, sequence, or request ID is invalid.");
            body ??= new byte[length];
            id = incomingId;
            frame.AsSpan(16).CopyTo(body.AsSpan(offset));
            offset += frame.Length - 16;
            if (offset != length) return null;
            var completed = (id, body);
            body = null;
            offset = 0;
            return completed;
        }
    }
}
