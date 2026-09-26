using System.Text.Json;
using RootEngineProbe;

internal static class LobbyTests
{
    public static void Run()
    {
        if (MatchLobby.CleanName("Friend\n\t") != "Friend" || MatchLobby.CleanName(new string('x', 100)).Length != 64)
            throw new Exception("Steam names exceeded roster limits");
        var lobby = new MatchLobby();
        var factions = new[] { 0, 1, 2, 3, 10, 11 };
        var setup = new MatchSetup { Factions = factions, AI = new AIDifficulty?[] { null, null, null, AIDifficulty.Hard, null, null } };
        lobby.SetName(1, "Friend");
        lobby.SetReady(1, true);
        // Root reports initially configured AI as resigned too.
        var seats = MatchLobby.Parse(JsonSerializer.SerializeToElement(lobby.Status(setup, seat => seat >= 2)));
        if (seats[1].State != "Connected" || !seats[1].Ready || !seats[1].Name.EndsWith("Friend") ||
            seats[2].State != "Resigned" || seats[3].State != "AI · Hard" || seats[4].State != "Clockwork bot") throw new Exception("Incorrect seat status");
        lobby.SetName(1, "Replacement");
        if (lobby.Status(setup, _ => false)[1].Ready) throw new Exception("Replacement inherited another player's readiness");
        seats[0] = seats[0] with { Seat = 0 };
        try { MatchLobby.Parse(JsonSerializer.SerializeToElement(seats)); throw new Exception("Invalid seat accepted"); }
        catch (InvalidDataException) { }
        try { MatchLobby.Parse(JsonSerializer.SerializeToElement(new SeatStatus[100])); throw new Exception("Unbounded roster accepted"); }
        catch (InvalidDataException) { }
    }
}
