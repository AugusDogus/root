using System.Text.Json;
using RootEngineProbe;

internal static class MatchSetupTests
{
    public static void Run()
    {
        void Valid(MatchSetup setup)
        {
            if (setup.Validate() is { } error) throw new Exception(error);
            if (MatchSetup.Parse(JsonSerializer.Serialize(setup)).Validate() is not null) throw new Exception("Settings round trip failed");
        }
        void Invalid(MatchSetup setup)
        {
            if (setup.Validate() is null) throw new Exception("Invalid settings were accepted");
        }
        Valid(new());
        Valid(new() { Factions = new[] { 4, 4, 4, 4, 4, 4 }, AdvancedSetup = true, FactionDraft = true });
        Invalid(new() { Factions = new[] { 4, 4, 4, 4, 4, 4 } });
        var pending = new MatchSetup { Factions = new[] { 0, 4, 2, 3, 6, 7 } };
        if (MatchSetup.Parse(JsonSerializer.Serialize(pending), pendingLobby: true).FactionDraft)
            throw new Exception("An open lobby seat must not enable faction drafting");
        if (new MatchSetup { Factions = new[] { 0, 0, 4, 4, 4, 4 } }.Validate(pendingLobby: true) is null)
            throw new Exception("An online lobby accepted duplicate chosen factions");
        Invalid(new() { FactionDraft = true });
        Invalid(new() { Factions = new[] { 0, 0, 4, 4, 4, 4 }, AdvancedSetup = true, FactionDraft = true });
        var ai = new MatchSetup { AI = new AIDifficulty?[] { null, null, AIDifficulty.Easy, AIDifficulty.Medium, AIDifficulty.Hard, AIDifficulty.Medium } };
        Valid(ai);
        if (!MatchSetup.Parse(JsonSerializer.Serialize(ai)).AI.SequenceEqual(ai.AI))
            throw new Exception("AI difficulties changed in settings round trip");
        if (!ai.IsHumanSeat(1) || ai.IsHumanSeat(2)) throw new Exception("AI seats were offered to human players");
        Invalid(new() { AI = new AIDifficulty?[] { AIDifficulty.Easy, null, null, null, null, null } });
        Invalid(new() { AI = Array.Empty<AIDifficulty?>() });
        Invalid(new() { AI = new AIDifficulty?[] { null, (AIDifficulty)9, null, null, null, null } });
        Invalid(new() { Factions = new[] { 0, 11, 2, 3, 6, 7 }, AI = new AIDifficulty?[] { null, AIDifficulty.Medium, null, null, null, null } });
        if (!MatchSetup.Parse("{}").IsHumanSeat(5)) throw new Exception("Older settings lost their human seats");
        Valid(new() { Factions = new[] { 14, 15, 8, 9, 6, 7 }, Map = 2, Deck = 1 });
        Valid(new() { Factions = new[] { 14, 15, 10, 11, 12, 13 }, BotDifficulty = 3 });
        Valid(new() { Factions = new[] { 0, 1, 3, 5, 8, 9 }, Characters = new[] { 0, 0, 7, 9, 0, 0 } });
        Invalid(new() { Factions = new[] { 0, 0, 2, 3, 6, 7 } });
        Invalid(new() { Factions = new[] { 10, 15, 8, 9, 6, 7 } });
        Invalid(new() { Factions = new[] { 0, 1, 2, 5, 6, 7 } });
        Invalid(new() { Characters = new[] { 7, 0, 0, 0, 0, 0 } });
        Invalid(new() { Factions = new[] { 0, 1, 3, 5, 8, 9 }, Characters = new[] { 0, 0, 7, 7, 0, 0 } });
        Invalid(new() { Hirelings = new[] { 20, 21 } });
        Invalid(new() { Hirelings = new[] { 20, 20, 21 } });
        Invalid(new() { Map = 3, Landmarks = new[] { 0 } });
        Invalid(new() { Map = 2, Landmarks = new[] { 1 } });
        Invalid(new() { Landmarks = new[] { 2, 3, 4 } });
        Invalid(new() { Map = 4 });
        Invalid(new() { Deck = 2 });
        Valid(new() { Factions = new[] { 14, 15, 10, 11, 12, 13 }, VagabotCharacter = 3,
            BotTraits = new[] { Array.Empty<int>(), Array.Empty<int>(), new[] { 0, 1, 2, 3 }, new[] { 0 }, new[] { 1 }, new[] { 2 } } });
        Invalid(new() { VagabotCharacter = 4 });
        Invalid(new() { BotTraits = new[] { new[] { 0 }, Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>() } });
    }
}
