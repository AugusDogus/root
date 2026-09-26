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
    }
}
