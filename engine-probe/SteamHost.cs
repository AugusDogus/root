using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace RootEngineProbe;

// Graphical host owns Steam, while the separate native authority stays on loopback.
internal sealed class SteamHost : IDisposable
{
    private sealed class Peer
    {
        public readonly ulong Identity;
        public readonly SteamChannel Channel;
        public int? Seat;
        public uint LastId;
        public Task<byte[]>? Pending;
        public Peer(ulong identity, SteamChannel channel) { Identity = identity; Channel = channel; }
    }
    private readonly SteamNative api;
    private readonly SteamSockets sockets;
    private readonly uint listener;
    private readonly int authorityPort;
    private readonly string[] tokens;
    private readonly string[] authorityTokens;
    private readonly Func<ulong, string>? names;
    private readonly HashSet<int> humanSeats;
    private readonly Dictionary<uint, Peer> peers = new();
    private readonly Dictionary<int, ulong> seatOwners = new();
    private readonly List<(int? Seat, Task<byte[]> Task)> retiring = new();
    public ulong? Owner(int seat) => seatOwners.TryGetValue(seat - 1, out var owner) ? owner : null;
    public bool Connected(int seat) => peers.Values.Any(peer => peer.Seat == seat - 1);
    public bool ReleaseSeat(int seat)
    {
        if (!humanSeats.Contains(seat) || Connected(seat) || retiring.Any(item => item.Seat == seat - 1 && !item.Task.IsCompleted)) return false;
        seatOwners.Remove(seat - 1);
        tokens[seat - 1] = Guid.NewGuid().ToString("N");
        return true;
    }
    public int VirtualPort { get; }
    public ulong Identity => sockets.Self;
    public int AuthenticatedPeers => peers.Values.Count(peer => peer.Seat is not null);
    public int RejectedPeers { get; private set; }
    public int ConnectionCallbacks { get; private set; }
    public int AcceptedConnections { get; private set; }
    public SteamHost(SteamNative api, int port, string[] tokens, int[]? humanSeats = null, Func<ulong, string>? names = null)
    {
        if (port is < 1 or > 65535 || tokens.Length != 6 || tokens.Distinct().Count() != 6 ||
            tokens.Any(token => !System.Text.RegularExpressions.Regex.IsMatch(token, "\\A[0-9a-f]{32}\\z")))
            throw new InvalidDataException("Invalid private Steam host configuration.");
        this.api = api;
        authorityPort = port;
        this.tokens = tokens.ToArray();
        authorityTokens = tokens.ToArray();
        this.names = names;
        this.humanSeats = (humanSeats ?? Enumerable.Range(2, 5).ToArray()).ToHashSet();
        sockets = new(api);
        VirtualPort = Random.Shared.Next(100, 1000);
        try { listener = sockets.ListenOn(VirtualPort); }
        catch { sockets.Dispose(); throw; }
    }
    public SteamInvitation Invitation(int seat)
    {
        if (seat is < 2 or > 6 || !humanSeats.Contains(seat)) throw new ArgumentOutOfRangeException(nameof(seat));
        return new(Identity, VirtualPort, seat, tokens[seat - 1]);
    }
    public bool AssignInvitation(int seat, ulong recipient)
    {
        if (seat is < 2 or > 6 || recipient == 0 || !humanSeats.Contains(seat)) return false;
        if (seatOwners.TryGetValue(seat - 1, out var owner) && owner != recipient) return false;
        seatOwners[seat - 1] = recipient;
        return true;
    }
    public void Tick()
    {
        retiring.RemoveAll(item => item.Task.IsCompleted);
        foreach (var (connection, info) in sockets.Changes())
        {
            ConnectionCallbacks++;
            if (info.ListenSocket == listener && info.State == 1 && !peers.ContainsKey(connection))
            {
                if (info.Remote.Type != 16 || info.Remote.SteamId == 0 || peers.Count >= 5)
                { Reject(connection); continue; }
                sockets.AcceptPeer(connection);
                AcceptedConnections++;
                peers.Add(connection, new(info.Remote.SteamId, new(api, connection, SteamFrames.MaxRequest)));
            }
            if (info.State is 4 or 5) Drop(connection);
        }
        foreach (var (connection, peer) in peers.ToArray())
        {
            try { TickPeer(peer); }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException or InvalidOperationException or FormatException or OverflowException)
            { Reject(connection); }
        }
    }
    private void TickPeer(Peer peer)
    {
        if (peer.Channel.TimedOut && (peer.Seat is null || peer.Pending is not null || peer.Channel.Sending))
            throw new IOException("Private Steam peer timed out.");
        if (peer.Pending is { IsCompleted: true } task)
        {
            peer.Channel.Send(peer.LastId, task.GetAwaiter().GetResult());
            peer.Pending = null;
        }
        peer.Channel.Flush();
        if (peer.Channel.Receive() is not { } message) return;
        if (peer.Pending is not null || peer.Channel.Sending || message.Id <= peer.LastId)
            throw new InvalidDataException("Overlapping or repeated Steam request.");
        using var document = JsonDocument.Parse(message.Body);
        var request = document.RootElement;
        if (request.ValueKind != JsonValueKind.Object ||
            !request.TryGetProperty("token", out var token) || token.ValueKind != JsonValueKind.String ||
            !request.TryGetProperty("op", out var op) || op.ValueKind != JsonValueKind.String || op.GetString() == "shutdown")
            throw new InvalidDataException("Invalid private Steam request.");
        var seat = Array.IndexOf(tokens, token.GetString());
        if (seat is < 1 or > 5 || !humanSeats.Contains(seat + 1) || (peer.Seat is { } assigned && assigned != seat) ||
            (seatOwners.TryGetValue(seat, out var owner) && owner != peer.Identity) ||
            peers.Values.Any(other => other != peer && other.Seat == seat) ||
            (peer.Seat is null && op.GetString() != "join"))
            throw new InvalidDataException("Steam seat authentication failed.");
        peer.Seat = seat;
        seatOwners[seat] = peer.Identity;
        peer.LastId = message.Id;
        peer.Channel.ResetDeadline();
        var forwarded = request.Deserialize<Dictionary<string, JsonElement>>() ?? throw new InvalidDataException("Invalid request.");
        forwarded["token"] = JsonSerializer.SerializeToElement(authorityTokens[seat]);
        if (op.GetString() == "join" && names is not null)
            forwarded["name"] = JsonSerializer.SerializeToElement(MatchLobby.CleanName(names(peer.Identity)));
        var body = JsonSerializer.SerializeToUtf8Bytes(forwarded);
        peer.Pending = Task.Run(() => Exchange(body));
    }
    private byte[] Exchange(byte[] request)
    {
        try
        {
            using var client = new TcpClient();
            client.ConnectAsync("127.0.0.1", authorityPort).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            client.ReceiveTimeout = 5000;
            client.SendTimeout = 5000;
            using var stream = client.GetStream();
            stream.Write(request);
            stream.WriteByte((byte)'\n');
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            while (output.Length <= SteamFrames.MaxResponse)
            {
                var read = stream.Read(buffer);
                if (read == 0) throw new IOException("Private authority closed its response.");
                var end = Array.IndexOf(buffer, (byte)'\n', 0, read);
                output.Write(buffer, 0, end < 0 ? read : end);
                if (output.Length > SteamFrames.MaxResponse) break;
                if (end >= 0) return output.ToArray();
            }
            throw new InvalidDataException("Native response exceeds 4 MiB.");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or SocketException or TimeoutException)
        {
            return Encoding.UTF8.GetBytes("{\"ok\":false,\"error\":\"Native host unavailable or response incomplete. Rejoin to check whether the move was accepted.\"}");
        }
    }
    private void Reject(uint connection) { RejectedPeers++; Drop(connection); }
    private void Drop(uint connection)
    {
        if (peers.Remove(connection, out var peer) && peer.Pending is { } task) retiring.Add((peer.Seat, task));
        sockets.Close(connection);
    }
    public void Dispose() { sockets.Dispose(); peers.Clear(); }
}
