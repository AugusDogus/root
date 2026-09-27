using Networking.lobby;
using tuber.canis;
using tuber_canis.data;

namespace RootEngineProbe;

internal static class NativeOnlineConfiguration
{
    // Online setup splits settings between MatchInitData and Options. The
    // private authority must project the latter before configuring the engine.
    // Keep the native initialization for settings already serialized there.
    public static void Apply(CreateLobbyGame request, TuberMatchInitData init)
    {
        foreach (var option in request.Options) init.AddOption(option.Key, option.Value);
        init.AddOption("matchType", request.MatchType);
        if (request.Options.TryGetValue("ChosenMap", out var map)) init.ChosenMap = ParseEnum<MapLayout>("ChosenMap", map);
        if (request.Options.TryGetValue("ChosenDeck", out var deck)) init.ChosenDeck = ParseEnum<DeckOptions>("ChosenDeck", deck);
        if (request.Options.TryGetValue("AdvancedSetup", out var advanced)) init.AdvancedSetup = ParseBool("AdvancedSetup", advanced);
        if (request.Options.TryGetValue("CooperativeMode", out var cooperative)) init.CooperativeMode = ParseBool("CooperativeMode", cooperative);
        // Native option consumers use lowercase boolean strings.
        foreach (var key in new[] { "RandomSuits", "RandomFactions" })
            if (request.Options.TryGetValue(key, out var value)) init.AddOption(key, ParseBool(key, value) ? "true" : "false");
    }

    private static T ParseEnum<T>(string key, string value) where T : struct, Enum =>
        Enum.TryParse<T>(value, out var parsed) && Enum.IsDefined(parsed)
            ? parsed : throw InvalidOption(key);

    private static bool ParseBool(string key, string value) =>
        bool.TryParse(value, out var parsed) ? parsed : throw InvalidOption(key);

    private static InvalidDataException InvalidOption(string key) =>
        new($"Root returned an invalid {key} setting. Return to setup and choose that setting again.");
}
