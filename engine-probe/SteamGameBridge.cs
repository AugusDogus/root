using System.Text.Json;

namespace RootEngineProbe;

// Native board regression fixture. Production host/guest classes use a real Steam
// self-connection and a separate native rules process. No desktop or friend actions.
internal sealed class SteamGameBridge : IDisposable
{
    private readonly SteamNative api;
    private readonly SteamHost host;
    private readonly RecoveringConnection guest;
    private SteamGuest? transport;
    private readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
    public bool Interrupted { get; private set; }
    public bool Recovered { get; private set; }
    private bool sawReconnecting;
    public PrivateClient Client { get; }
    public SteamGameBridge(string configFile)
    {
        using var config = JsonDocument.Parse(File.ReadAllText(configFile));
        var tokens = config.RootElement.GetProperty("tokens").Deserialize<string[]>() ?? throw new InvalidDataException("Missing test seats.");
        api = new();
        try { host = new(api, config.RootElement.GetProperty("port").GetInt32(), tokens); }
        catch { api.Dispose(); throw; }
        try { guest = new(() => transport = new SteamGuest(api, host.Invitation(6)), () => clock.Elapsed.TotalSeconds, tokens[5]); }
        catch { host.Dispose(); api.Dispose(); throw; }
        Client = new(tokens[5], guest.Exchange);
    }
    public void Interrupt()
    {
        if (Interrupted) return;
        Interrupted = true;
        transport?.Dispose();
    }
    public void Tick()
    {
        host.Tick(); guest.Tick();
        if (guest.Reconnecting) { sawReconnecting = true; Client.DiscardQueuedMoves(); }
        else if (sawReconnecting) Recovered = true;
    }
    public void Dispose() { Client.Stop(); guest.Dispose(); host.Dispose(); api.Dispose(); }
}
