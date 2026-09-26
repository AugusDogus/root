namespace RootEngineProbe;

// Uses the production adapter. Self-connection does not establish remote SDR routing.
internal sealed class SteamP2pProbe : IDisposable
{
    private readonly SteamNative api;
    private readonly SteamSockets sockets;
    private readonly uint listener;
    private readonly uint outgoing;
    private uint incoming;
    private bool sent;
    private readonly byte[] payload = System.Text.Encoding.UTF8.GetBytes("root-private-p2p-probe-v1");
    public int CallbackCount { get; private set; }

    public SteamP2pProbe(SteamNative api)
    {
        this.api = api;
        sockets = new SteamSockets(api);
        try
        {
            var port = Random.Shared.Next(100, 1000);
            listener = sockets.ListenOn(port);
            outgoing = sockets.ConnectTo(sockets.Self, port);
        }
        catch { sockets.Dispose(); throw; }
    }

    public bool Tick()
    {
        foreach (var (connection, info) in sockets.Changes())
        {
            CallbackCount++;
            if (info.ListenSocket == listener && info.State == 1)
            {
                if (info.Remote.Type != 16 || info.Remote.SteamId != sockets.Self || incoming != 0)
                { sockets.Close(connection); continue; }
                sockets.AcceptPeer(connection);
                incoming = connection;
            }
        }
        var current = sockets.State(outgoing);
        if (current.State is 4 or 5) throw new IOException($"Steam P2P self-test closed: state {current.State}, reason {current.EndReason}.");
        if (current.State != 3 || incoming == 0) return false;
        if (!sent)
        {
            if (api.SendReliable(outgoing, payload) != 1) throw new IOException("Steam P2P test send failed.");
            sent = true;
        }
        var received = api.ReceiveOne(incoming);
        if (received is null) return false;
        if (!received.SequenceEqual(payload)) throw new InvalidDataException("Steam P2P test payload did not match.");
        return true;
    }

    public void Dispose() => sockets.Dispose();
}
