using System.Diagnostics;

namespace RootEngineProbe;

internal sealed class SteamChannel
{
    private readonly SteamNative api;
    private readonly uint connection;
    private readonly SteamFrames.Reader reader;
    private byte[]? sending;
    private int offset;
    private uint sendingId;
    private readonly Stopwatch deadline = Stopwatch.StartNew();
    public bool Sending => sending is not null;
    public bool TimedOut => deadline.Elapsed > TimeSpan.FromSeconds(30);
    public SteamChannel(SteamNative api, uint connection, int receiveLimit)
    { this.api = api; this.connection = connection; reader = new(receiveLimit); }
    public void ResetDeadline() => deadline.Restart();
    public void Send(uint id, byte[] body)
    {
        if (sending is not null) throw new InvalidOperationException("A Steam response is already pending.");
        if (body.Length is < 1 or > SteamFrames.MaxResponse) throw new InvalidDataException("Steam response exceeds 4 MiB.");
        sendingId = id;
        sending = body;
        offset = 0;
    }
    public void Flush()
    {
        for (var step = 0; step < 8 && sending is { } body; step++)
        {
            var result = api.SendReliable(connection, SteamFrames.Encode(sendingId, body, offset));
            if (result == 25) return; // SDK send buffer full, no bytes accepted.
            if (result != 1) throw new IOException($"Steam send failed with result {result}. Rejoin before retrying a move.");
            offset += Math.Min(SteamFrames.Chunk, body.Length - offset);
            if (offset == body.Length) sending = null;
        }
    }
    public (uint Id, byte[] Body)? Receive()
    {
        for (var step = 0; step < 8; step++)
        {
            var frame = api.ReceiveOne(connection);
            if (frame is null) break;
            if (reader.Push(frame) is { } message) return message;
        }
        return null;
    }
}
