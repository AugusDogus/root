using System.Text.Json;

namespace RootEngineProbe;

// Five logical remote seats in one Steam process. Real friends are never contacted.
internal sealed class SteamTransportProbe : IDisposable
{
    private readonly SteamNative api;
    private readonly SteamHost host;
    private SteamHost? botHost;
    private readonly int authorityPort;
    private readonly List<SteamGuest> guests = new();
    private readonly List<Task<JsonElement>> replies = new();
    private readonly string[] tokens;
    private readonly int blobLength;
    private int round;
    private SteamGuest? rejected;
    private Task<JsonElement>? rejectedReply;
    private int rejectionStage;
    public int CompletedResponses { get; private set; }
    public int Rejections => host.RejectedPeers + (botHost?.RejectedPeers ?? 0);
    public int Callbacks => host.ConnectionCallbacks;
    public int AcceptedConnections => host.AcceptedConnections;
    public int GuestIncomingCallbacks => guests.Sum(guest => guest.IncomingCallbacks);
    public SteamTransportProbe(SteamNative api, string file)
    {
        this.api = api;
        using var config = JsonDocument.Parse(File.ReadAllText(file));
        tokens = config.RootElement.GetProperty("tokens").Deserialize<string[]>() ?? throw new InvalidDataException("Missing test seats.");
        blobLength = config.RootElement.GetProperty("blobLength").GetInt32();
        authorityPort = config.RootElement.GetProperty("port").GetInt32();
        host = new(api, authorityPort, tokens);
        try
        {
            for (var seat = 2; seat <= 6; seat++) guests.Add(new(api, host.Invitation(seat)));
            RequestRound();
        }
        catch { Dispose(); throw; }
    }
    private void RequestRound()
    {
        replies.Clear();
        for (var index = 0; index < guests.Count; index++)
            replies.Add(guests[index].Exchange(new { op = round == 0 ? "join" : "poll", token = tokens[index + 1], after = 0 }));
    }
    public bool Tick()
    {
        host.Tick();
        botHost?.Tick();
        foreach (var guest in guests) guest.Tick();
        if (round < 2)
        {
            if (replies.Any(reply => !reply.IsCompleted)) return false;
            for (var index = 0; index < replies.Count; index++)
            {
                var response = replies[index].GetAwaiter().GetResult();
                if (!response.GetProperty("ok").GetBoolean() || response.GetProperty("seat").GetInt32() != index + 1 ||
                    response.GetProperty("blob").GetString()?.Length != blobLength)
                    throw new InvalidDataException("Steam transport returned incorrect seat data.");
                CompletedResponses++;
            }
            round++;
            if (round < 2) { RequestRound(); return false; }
            // A live seat cannot change its token to steal another seat.
            rejectedReply = guests[0].Exchange(new { op = "poll", token = tokens[2], after = 0 });
            rejected = guests[0];
            guests.RemoveAt(0);
            return false;
        }
        if (rejected is not null)
        {
            try { rejected.Tick(); }
            catch (IOException) { /* Expected rejection is checked on the request task. */ }
        }
        if (rejectedReply is not { IsCompleted: true }) return false;
        if (!rejectedReply.IsFaulted) throw new InvalidDataException("Invalid Steam seat request was accepted.");
        _ = rejectedReply.Exception;
        rejected?.Dispose();
        rejected = null;
        if (rejectionStage++ == 0)
        {
            // Tokenless strangers must not reach the private authority.
            rejected = new(api, host.Invitation(2) with { Token = new string('0', 32) });
            rejectedReply = rejected.Exchange(new { op = "join", token = new string('0', 32) });
            return false;
        }
        if (rejectionStage == 2)
        {
            rejected = new(api, host.Invitation(3));
            rejectedReply = rejected.Exchange(new { op = "join", token = tokens[2] });
            return false;
        }
        if (rejectionStage == 3)
        {
            botHost = new(api, authorityPort, tokens, new[] { 2, 3, 4, 5 });
            if (botHost.AssignInvitation(6, host.Identity)) throw new InvalidDataException("Bot seat accepted an invitation assignment.");
            try { botHost.Invitation(6); throw new InvalidDataException("Bot seat exposed an invitation."); }
            catch (ArgumentOutOfRangeException) { }
            var botInvitation = new SteamInvitation(botHost.Identity, botHost.VirtualPort, 6, tokens[5]);
            rejected = new(api, botInvitation);
            rejectedReply = rejected.Exchange(new { op = "join", token = tokens[5] });
            return false;
        }
        return host.AuthenticatedPeers == 4 && host.RejectedPeers >= 3 && botHost?.RejectedPeers == 1;
    }
    public void Dispose()
    {
        rejected?.Dispose();
        foreach (var guest in guests) guest.Dispose();
        host.Dispose();
        botHost?.Dispose();
    }
}
