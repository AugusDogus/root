using Canis.json;
using Canis.utils.ids;
using System.Text.Json;
using tuber.canis;
using tuber.canis.data.matchinitdata;

namespace RootEngineProbe;

internal static class NativeClientConfiguration
{
    // Construct a fresh initialization from public fields. Never clone the
    // authority's initialization: it can contain a save, hands and a random seed.
    public static JsonElement Create(HostedMatch host)
    {
        var source = host.Match.TuberMatchInitData;
        var init = new TuberMatchInitData(new GameID(source.gameID.ToString()))
        {
            ChosenMap = source.ChosenMap, ChosenDeck = source.ChosenDeck,
            AdvancedSetup = source.AdvancedSetup, AdsetDisableDraft = source.AdsetDisableDraft,
            EnableBluff = source.EnableBluff, CooperativeMode = source.CooperativeMode,
            Hirelings = source.Hirelings, HirelingsList = source.HirelingsList,
            DisableRandomHirelings = source.DisableRandomHirelings,
            NumLandmarks = source.NumLandmarks, LandmarksList = source.LandmarksList,
            BannedLandmarksList = source.BannedLandmarksList,
            AllowedExpansions = source.AllowedExpansions,
            DoNotShufflePlayers = true
        };
        init.AddOption("matchType", source.ValueForOption("matchType", "Live"));
        init.AddOption("Timers", source.ValueForOption("Timers", "0"));
        for (var seat = 0; seat < host.PlayerCount; seat++)
        {
            var player = host.SeatInitialization[seat];
            var publicPlayer = new TuberPlayerMatchInitData(MatchLobby.CleanName(player.name), player.accountID)
            {
                Faction = host.SeatPlayers[seat].Faction,
                PlayerStartingOrder = player.PlayerStartingOrder,
                StartingCharacter = player.StartingCharacter,
                ClockworkConfig = player.ClockworkConfig,
                isHuman = host.IsHumanSeat(seat), aiLevel = player.aiLevel
            };
            NativeMatchSetup.RecordController(publicPlayer);
            init.AddTuberPlayer(publicPlayer);
        }
        return JsonSerializer.Deserialize<JsonElement>(JSON.ToJSON(init, false));
    }

    public static TuberMatchInitData Parse(JsonElement json, JsonElement roster)
    {
        var init = JSON.Deserialize<TuberMatchInitData>(json.GetRawText());
        if (init.TuberPlayers?.Count != 6 || init.gameState is not null || init.saveData is not null || roster.GetArrayLength() != 6)
            throw new InvalidDataException("The host returned an invalid public game configuration. Ask the host to update the mod.");
        for (var seat = 0; seat < 6; seat++)
            if (init.TuberPlayers[seat].accountID.ToString() != roster[seat].GetProperty("account").GetString())
                throw new InvalidDataException("The host's game configuration does not match its seats. Rejoin the match.");
        return init;
    }
}
