using Canis.json;
using Canis.utils.ids;
using tuber.canis;

namespace RootEngineProbe;

// This configuration travels only from the local setup screen to the authority.
// Guest connections receive public settings and their own obfuscated snapshot.
internal static class NativeHostConfiguration
{
    public const string SeatKey = "rootSixPlayer.seat";

    public static TuberMatchInitData Parse(string json)
    {
        dwd.core.data.ReflectionTypeInitializer.Initialize();
        var init = JSON.Deserialize<TuberMatchInitData>(json);
        if (init.TuberPlayers?.Count != 6 || init.gameState is not null || init.saveData is not null)
            throw new InvalidDataException("Root did not return a new six-player game. Return to setup and try again.");
        if (!init.TuberPlayers[0].isHuman)
            throw new InvalidDataException("The host needs a human seat. Choose Human for the first player.");
        init.gameID = new GameID(Guid.NewGuid().ToString());
        init.AddOption("matchType", "Live");
        for (var seat = 0; seat < 6; seat++)
        {
            var player = init.TuberPlayers[seat];
            player.accountID = new AccountID(Guid.NewGuid().ToString());
            player.metadata[SeatKey] = seat.ToString(System.Globalization.CultureInfo.InvariantCulture);
            NativeMatchSetup.RecordController(player);
        }
        return init;
    }
}
