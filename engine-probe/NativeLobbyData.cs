using dwd.core.data.composition;
using lib.src.data;
using tuber.client.data;
using tuber.client.match.data;
using tuber_canis.data;
using Canis.utils.ids;
using HarmonyLib;
using Networking.game;
using tuber.canis;
using tuber.canis.data.matchinitdata;

namespace RootEngineProbe;

internal static class NativeLobbyData
{
    private static AccountID? identity;
    private static bool patched;
    private static bool projecting;
    private static (FactionData Faction, Factions[] Allowed)? selection;
    public static void UseIdentity(AccountID? account)
    {
        identity = account;
        selection = null;
        if (patched) return;
        var harmony = new Harmony("local.root.lobby-data");
        harmony.Patch(AccessTools.Method(typeof(boardgames.utils.AccountUtils), "GetLoggedInID"),
            prefix: new HarmonyMethod(typeof(NativeLobbyData), nameof(PrivateIdentity)));
        harmony.Patch(AccessTools.Method(typeof(PendingGameData), "UpdateWith"),
            prefix: new HarmonyMethod(typeof(NativeLobbyData), nameof(PrepareUpdate)),
            postfix: new HarmonyMethod(typeof(NativeLobbyData), nameof(ExtendPrivateModel)));
        patched = true;
    }
    private static bool PrivateIdentity(ref AccountID __result)
    {
        if (identity is not { } account) return true;
        __result = account;
        return false;
    }
    private static void PrepareUpdate(PendingGameData __instance)
    {
        if (identity is null || projecting) return;
        // Clone natively: the generated Nullable<enum> getter cannot safely
        // round-trip faction values through managed code in this game build.
        foreach (var slot in __instance.playerData)
            if (slot.GetOne<PendingGamePlayerAccountData>().PlayerAccountID?.ToString() == identity.ToString())
                selection = (slot.GetOne<FactionData>().Clone().Cast<FactionData>(), slot.GetOne<AllowedRandomFactionsData>().AllowedFactions.ToArray());
        while (__instance.playerData.Count > 4) __instance.playerData.RemoveAt(__instance.playerData.Count - 1);
    }
    private static void ExtendPrivateModel(PendingGameData __instance, DWDPendingGameMetadata __0)
    {
        if (identity is null || projecting) return;
        // Root's updater has a fixed four-slot loop. Let it interpret a second
        // native metadata projection for the final two slots, including all
        // native faction locks, local-player state and Clockwork settings.
        var humans = Math.Max(0, __0.MaximumPlayerSessionCount - 4);
        var tail = new DWDPendingGameMetadata
        {
            GameID = __0.GameID, CreatorID = __0.CreatorID, Name = __0.Name,
            MaximumPlayerSessionCount = humans, AIPlayerCount = 2 - humans,
            CurrentPlayerSessionCount = Math.Max(0, __0.CurrentPlayerSessionCount - 4),
            AlreadyInGame = __0.AlreadyInGame, HasPassword = __0.HasPassword, MatchType = __0.MatchType,
            MatchInitData = __0.MatchInitData, Options = new(), PlayersInGame = new()
        };
        var bots = new Il2CppSystem.Collections.Generic.List<TuberPlayerMatchInitData>();
        foreach (var option in __0.Options)
            if (option.Key.StartsWith("AIPlayerMetadata.", StringComparison.Ordinal))
                PlayerMatchInitDataPacker.UnpackPlayerMatchInitData(ref bots, option.Key, option.Value);
            else tail.Options[option.Key] = option.Value;
        var remaining = new Il2CppSystem.Collections.Generic.List<TuberPlayerMatchInitData>();
        for (var index = Math.Max(0, 4 - __0.MaximumPlayerSessionCount); index < bots.Count; index++) remaining.Add(bots[index]);
        for (var index = 0; index < remaining.Count; index++)
            PlayerMatchInitDataPacker.PackPlayerMatchInitDatum($"AIPlayerMetadata.{index}", remaining[index], tail.Options);
        for (var index = 4; index < __0.PlayersInGame.Count; index++) tail.PlayersInGame.Add(__0.PlayersInGame[index]);
        projecting = true;
        try
        {
            var extension = new PendingGameData(tail);
            for (var index = 0; index < 2; index++) __instance.playerData.Add(extension.PlayerData[index]);
        }
        finally { projecting = false; }
        var localJoined = __0.PlayersInGame.ToArray().Any(player => player.accountID.ToString() == identity.ToString());
        for (var index = __0.PlayersInGame.Count; index < __0.MaximumPlayerSessionCount; index++)
        {
            var slot = __instance.playerData[index];
            var localChoice = !localJoined && index == __0.PlayersInGame.Count;
            var account = slot.GetOne<PendingGamePlayerAccountData>();
            account.PlayerAccountID = localChoice ? identity : null;
            if (!localChoice) account.PlayerName = "Open seat";
            slot.GetOne<PlayerSlotLockData>().FactionLocked = !localChoice;
            if (localChoice && selection is { } selected)
            {
                slot.GetOne<FactionData>().factionAttribute = selected.Faction.factionAttribute;
                slot.GetOne<AllowedRandomFactionsData>().AllowedFactions = selected.Allowed;
            }
        }
    }
}
