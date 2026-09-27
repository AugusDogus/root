using System.Text.Json;

namespace RootEngineProbe;

// Values match Root's native Easy / Medium / Hard difficulty levels.
internal enum AIDifficulty { Easy = 0, Medium = 1, Hard = 2 }

// Public match settings only. Never send a native checkpoint or private hands to clients.
internal sealed record MatchSetup
{
    public int[] Factions { get; init; } = { 0, 1, 2, 3, 6, 7 };
    public int[] Characters { get; init; } = new int[6];
    // A null entry leaves the faction under its normal controller: a human
    // for standard factions, or scripted automation for Clockwork factions.
    public AIDifficulty?[] AI { get; init; } = new AIDifficulty?[6];
    public bool IsHumanSeat(int seat) => !IsClockwork(Factions[seat]) && AI[seat] is null;
    public int Map { get; init; }
    public int Deck { get; init; }
    public int[] Landmarks { get; init; } = Array.Empty<int>();
    public int[] Hirelings { get; init; } = Array.Empty<int>();
    public bool AdvancedSetup { get; init; }
    public bool FactionDraft { get; init; }
    public bool CooperativeMode { get; init; }
    public bool EnableBluff { get; init; }
    public int BotDifficulty { get; init; } = 1;
    public int[][] BotTraits { get; init; } = Enumerable.Range(0, 6).Select(_ => Array.Empty<int>()).ToArray();
    public int VagabotCharacter { get; init; } = 1;
    public static readonly string[][] TraitNames =
    {
        new[] { "Blitz", "Fortified", "Hospitals", "Iron Will" },
        new[] { "Nobility", "Relentless", "Swoop", "War Tax" },
        new[] { "Informants", "Popularity", "Veterans", "Wildfire" },
        new[] { "Adventurer", "Berserker", "Helper", "Marksman" }
    };

    public static readonly (int Id, string Name)[] PlayerFactions =
    {
        (0, "Marquise de Cat"), (1, "Eyrie Dynasties"), (2, "Woodland Alliance"),
        (3, "Vagabond"), (5, "Second Vagabond"), (6, "Lizard Cult"),
        (7, "Riverfolk Company"), (8, "Underground Duchy"), (9, "Corvid Conspiracy"),
        (14, "Lord of the Hundreds"), (15, "Keepers in Iron"),
        (10, "Mechanical Marquise"), (11, "Electric Eyrie"), (12, "Automated Alliance"), (13, "Vagabot")
    };
    public static readonly string[] Maps = { "Autumn", "Winter", "Lake", "Mountain" };
    public static readonly string[] Decks = { "Standard", "Exiles & Partisans" };
    public static readonly string[] Vagabonds = { "Choose in game", "Tinker", "Thief", "Ranger", "Vagrant", "Arbiter", "Scoundrel", "Harrier", "Ronin", "Adventurer" };
    public static readonly string[] LandmarkNames = { "Tower", "Ferry", "Elder Treetop", "Lost City", "Black Market", "Legendary Forge" };
    public static readonly (int Id, string Name)[] HirelingNames =
    {
        (16, "Forest Patrol"), (17, "Last Dynasty"), (18, "Spring Uprising"), (19, "The Exile"),
        (20, "Popular Band"), (21, "Vault Keepers"), (22, "Flame Bearers"), (23, "Highway Bandits"),
        (24, "Warm Sun Prophets"), (25, "Riverfolk Flotilla"), (26, "Furious Protector"),
        (27, "Corvid Spies"), (28, "Sunward Expedition")
    };
    public static string FactionName(int id) => id == 4 ? "Choosing faction" : PlayerFactions.FirstOrDefault(item => item.Id == id).Name ?? $"Faction {id}";
    public static bool IsClockwork(int id) => id is >= 10 and <= 13;
    public static readonly string[] BotDifficulties = { "Easy", "Normal", "Challenging", "Nightmare" };

    public string? Validate(bool pendingLobby = false)
    {
        if (FactionDraft && !AdvancedSetup) return "Faction drafting requires advanced setup.";
        if (Factions is null || Factions.Length != 6 ||
            Factions.Where(id => id != 4).Distinct().Count() != Factions.Count(id => id != 4) ||
            Factions.Any(id => !(id == 4 && (FactionDraft || pendingLobby)) && !PlayerFactions.Any(item => item.Id == id)))
            return "Choose six different playable factions.";
        if (AI is null || AI.Length != 6 || AI.Any(level => level is { } value && !Enum.IsDefined(typeof(AIDifficulty), value)))
            return "Choose Human or Easy, Medium, or Hard AI for each seat.";
        if (Enumerable.Range(0, 6).Any(seat => IsClockwork(Factions[seat]) && AI[seat] is not null))
            return "Clockwork factions use their own difficulty and traits. Choose a normal faction for ordinary AI.";
        if (!IsHumanSeat(0)) return "The host must play a human faction. Use the other seats for AI or Clockwork.";
        if (BotDifficulty is < 0 or > 3) return "Choose a valid Clockwork difficulty.";
        if (VagabotCharacter is < 1 or > 3) return "Vagabot can use Tinker, Thief, or Ranger.";
        if (BotTraits is null || BotTraits.Length != 6 || BotTraits.Any(traits => traits is null || traits.Length > 4 || traits.Any(id => id is < 0 or > 3) || traits.Distinct().Count() != traits.Length))
            return "Choose up to four different traits for each Clockwork bot.";
        if (Enumerable.Range(0, 6).Any(seat => !IsClockwork(Factions[seat]) && BotTraits[seat].Length > 0))
            return "Only Clockwork bots can use Clockwork traits.";
        if (Factions.Contains(5) && !Factions.Contains(3) && !((FactionDraft || pendingLobby) && Factions.Contains(4))) return "A second Vagabond needs a first Vagabond.";
        if (Characters is null || Characters.Length != 6 || Characters.Any(id => id is < 0 or > 9))
            return "Choose a valid character for each Vagabond.";
        for (var seat = 0; seat < 6; seat++)
            if (Factions[seat] is not (3 or 5) && Characters[seat] != 0)
                return "Only Vagabonds can choose a Vagabond character.";
        var chosenCharacters = Characters.Where(id => id != 0).ToArray();
        if (chosenCharacters.Distinct().Count() != chosenCharacters.Length) return "The two Vagabonds must use different characters.";
        if (Map is < 0 or > 3 || Deck is < 0 or > 1) return "Choose a valid map and deck.";
        if (Landmarks is null || Landmarks.Length > 2 || Landmarks.Any(id => id is < 0 or > 5) || Landmarks.Distinct().Count() != Landmarks.Length)
            return "Choose up to two different landmarks.";
        if (Map == 2 && Landmarks.Contains(1) || Map == 3 && Landmarks.Contains(0))
            return "This map already includes that landmark.";
        if (Hirelings is null || Hirelings.Length is not (0 or 3) || Hirelings.Any(id => id is < 16 or > 28) || Hirelings.Distinct().Count() != Hirelings.Length)
            return "Use no hirelings, or choose exactly three different hirelings.";
        return null;
    }

    public static MatchSetup Parse(string json, bool pendingLobby = false)
    {
        var setup = JsonSerializer.Deserialize<MatchSetup>(json) ?? throw new InvalidDataException("Match settings are missing.");
        if (setup.Validate(pendingLobby) is { } error) throw new InvalidDataException(error);
        return setup;
    }
}
