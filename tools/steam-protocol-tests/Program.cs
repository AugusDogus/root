using System.Buffers.Binary;
using RootEngineProbe;

void Check(bool condition, string detail) { if (!condition) throw new Exception(detail); }
void Reject(Action action, string detail)
{
    try { action(); }
    catch (InvalidDataException) { return; }
    throw new Exception(detail);
}
MatchSetupTests.Run();
SeatHistoryTests.Run();
RecoveryTests.Run();
LobbyTests.Run();
var invite = new SteamInvitation(0x0110000100000001, 501, 6, new string('a', 32));
Check(SteamInvitation.TryParse(invite.Encode(), out var decoded) && decoded == invite, "Invitation round trip");
foreach (var text in new[] { invite.Encode() + "\n", invite.Encode().Replace("22238765", "0"),
    invite.Encode().Replace("root6:2:", "root6:1:"),
    invite.Encode().Replace(":501:", ":0:"), invite.Encode().Replace(":6:", ":1:"),
    invite.Encode().Replace(invite.Host.ToString(), "1"), invite.Encode() + ":extra", "ordinary-root-invite" })
    Check(!SteamInvitation.TryParse(text, out _), "Malformed invitation accepted");
foreach (var length in new[] { 1, SteamFrames.Chunk, SteamFrames.Chunk + 1, SteamFrames.MaxResponse })
{
    var payload = Enumerable.Range(0, length).Select(index => (byte)(index % 251)).ToArray();
    var reader = new SteamFrames.Reader(SteamFrames.MaxResponse);
    for (var offset = 0; offset < length; offset += SteamFrames.Chunk)
    {
        var completed = reader.Push(SteamFrames.Encode(9, payload, offset));
        if (offset + SteamFrames.Chunk < length) Check(completed is null, "Early completion");
        else Check(completed is { Id: 9 } value && value.Body.SequenceEqual(payload), "Payload corruption");
    }
}
var body = new byte[SteamFrames.Chunk + 1];
var first = SteamFrames.Encode(1, body, 0);
var last = SteamFrames.Encode(1, body, SteamFrames.Chunk);
var reordered = new SteamFrames.Reader(SteamFrames.MaxResponse);
Reject(() => reordered.Push(last), "Gap accepted");
var duplicate = new SteamFrames.Reader(SteamFrames.MaxResponse);
duplicate.Push(first);
Reject(() => duplicate.Push(first), "Duplicate accepted");
var mixed = new SteamFrames.Reader(SteamFrames.MaxResponse);
mixed.Push(first);
Reject(() => mixed.Push(SteamFrames.Encode(2, body, SteamFrames.Chunk)), "Interleaved response accepted");
var tooLarge = (byte[])first.Clone();
BinaryPrimitives.WriteInt32LittleEndian(tooLarge.AsSpan(8), int.MaxValue);
Reject(() => new SteamFrames.Reader(SteamFrames.MaxRequest).Push(tooLarge), "Oversized allocation accepted");
Reject(() => new SteamFrames.Reader(SteamFrames.MaxRequest).Push(new byte[16]), "Empty frame accepted");
Console.WriteLine("PASS: DLC settings, versioned invitations, bounded history, recovery without move replay, and 4 MiB framing");
