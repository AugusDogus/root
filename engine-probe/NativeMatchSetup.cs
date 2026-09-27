using tuber.canis;
using tuber.canis.data.matchinitdata;
using tuber_canis.data;

namespace RootEngineProbe;

internal static class NativeMatchSetup
{
    private const string AISeatKey = "rootSixPlayer.aiSeat";

    public static void RecordController(TuberPlayerMatchInitData player)
    {
        if (!player.isHuman && !MatchSetup.IsClockwork((int)player.Faction)) player.metadata[AISeatKey] = "true";
        else player.metadata.Remove(AISeatKey);
    }

    // Native HasResigned also returns true for seats created as ordinary AI.
    // Keep original seat ownership in saved initialization metadata. Older
    // saves have no marker and used humans for every non-Clockwork faction.
    public static bool IsHumanSeat(TuberPlayerMatchInitData player) =>
        !MatchSetup.IsClockwork((int)player.Faction) && !player.metadata.ContainsKey(AISeatKey);

    public static string? Validate(MatchSetup setup, bool pendingLobby = false)
    {
        if (setup.Validate(pendingLobby) is { } error) return error;
        var factions = setup.Factions.Concat(setup.Hirelings).Select(id => (Factions)id).ToArray();
        foreach (var faction in factions)
        {
            if (faction == Factions.Invalid && (setup.FactionDraft || pendingLobby)) continue;
            var released = setup.Hirelings.Contains((int)faction) ? lib.data.FactionUtils.ReleasedHirelings
                : MatchSetup.IsClockwork((int)faction) ? lib.data.FactionUtils.ClockworkFactions : lib.data.FactionUtils.ReleasedPlayerFactions;
            if (!released.Contains(faction)) return $"{faction} is not available in this version of Root.";
            if (lib.data.FactionUtils.mutuallyExclusiveFactions.TryGetValue(faction, out var exclusions) && factions.Any(exclusions.Contains))
                return $"{faction} conflicts with another selected faction or hireling. Choose a different combination.";
        }
        return null;
    }

    public static TuberPlayerMatchInitData Player(string name, Canis.utils.ids.AccountID account, int seat, MatchSetup setup)
    {
        var faction = (Factions)setup.Factions[seat];
        var player = setup.IsHumanSeat(seat) ? new TuberPlayerMatchInitData(name, account)
            : new TuberPlayerMatchInitData((int)(setup.AI[seat] ?? AIDifficulty.Hard), account, name);
        if (setup.AI[seat] is not null) player.metadata[AISeatKey] = "true";
        player.Faction = faction;
        player.PlayerStartingOrder = seat;
        player.StartingCharacter = (VagabondCharacters)setup.Characters[seat];
        if (MatchSetup.IsClockwork((int)faction))
        {
            player.ClockworkConfig = new()
            {
                difficulty = lib.src.data.ClockworkConfigData.TranslateClockworkLeveltoArchID(new Il2CppSystem.Nullable<Factions>(faction), (RootbotDifficulty)setup.BotDifficulty),
                traits = new(),
                character = faction == Factions.Vagabot ? tuber.canis.archetypes.VagabondArchetypes.VagabotTinkerArchetype.archID : null
            };
            NativeClockwork.Apply(player.ClockworkConfig, (int)faction, setup.BotTraits[seat], setup.VagabotCharacter);
        }
        return player;
    }

    public static void Apply(TuberMatchInitData init, MatchSetup setup)
    {
        if (Validate(setup) is { } error) throw new InvalidDataException(error);
        init.ChosenMap = (MapLayout)setup.Map;
        init.ChosenDeck = (DeckOptions)setup.Deck;
        init.AdvancedSetup = setup.AdvancedSetup;
        init.AdsetDisableDraft = setup.AdvancedSetup && !setup.FactionDraft;
        init.CooperativeMode = setup.CooperativeMode;
        init.EnableBluff = setup.EnableBluff;
        init.Hirelings = setup.Hirelings.Length != 0;
        init.DisableRandomHirelings = true;
        init.HirelingsList = new();
        foreach (var id in setup.Hirelings)
            init.HirelingsList.Add(new HirelingMatchInitData { Faction = (Factions)id, Side = HirelingSide.Demoted });
        init.NumLandmarks = setup.Landmarks.Length;
        init.LandmarksList = new();
        foreach (var id in setup.Landmarks) init.LandmarksList.Add(new LandmarksMatchInitData { Landmark = (Landmarks)id });
    }

    public static MatchSetup Read(TuberMatchInitData init, IReadOnlyList<TuberPlayerMatchInitData> seats) => new()
    {
        Factions = seats.Select(player => (int)player.Faction).ToArray(),
        // Resigned humans retain their original seats for watching and reconnecting.
        AI = seats.Select(player => IsHumanSeat(player) || MatchSetup.IsClockwork((int)player.Faction)
            ? null : (AIDifficulty?)player.aiLevel).ToArray(),
        Characters = seats.Select(player => (int)player.StartingCharacter).ToArray(),
        Map = (int)init.ChosenMap, Deck = (int)init.ChosenDeck, AdvancedSetup = init.AdvancedSetup,
        FactionDraft = init.AdvancedSetup && !init.AdsetDisableDraft,
        CooperativeMode = init.CooperativeMode, EnableBluff = init.EnableBluff,
        BotDifficulty = ReadBotDifficulty(init),
        BotTraits = seats.Select(NativeClockwork.ReadTraits).ToArray(),
        VagabotCharacter = NativeClockwork.ReadCharacter(seats),
        Landmarks = init.LandmarksList?.ToArray().Select(item => (int)item.Landmark)
            .Where(id => !(init.ChosenMap == MapLayout.Lake && id == 1 || init.ChosenMap == MapLayout.Mountain && id == 0)).ToArray() ?? Array.Empty<int>(),
        Hirelings = init.HirelingsList?.ToArray().Select(item => (int)item.Faction).ToArray() ?? Array.Empty<int>()
    };

    private static int ReadBotDifficulty(TuberMatchInitData init)
    {
        for (var seat = 0; seat < init.TuberPlayers.Count; seat++)
        {
            var player = init.TuberPlayers[seat];
            if (!MatchSetup.IsClockwork((int)player.Faction) || player.ClockworkConfig?.difficulty is not { } difficulty) continue;
            for (var level = 0; level < 4; level++)
                if (lib.src.data.ClockworkConfigData.TranslateClockworkLeveltoArchID(new Il2CppSystem.Nullable<Factions>(player.Faction), (RootbotDifficulty)level).ToString() == difficulty.ToString())
                    return level;
        }
        return 1;
    }
}
