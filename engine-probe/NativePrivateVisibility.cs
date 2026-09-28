using Canis.actions;
using Canis.attributes;
using Canis.obfuscation;
using HarmonyLib;
using tuber.canis;
using tuber_canis.data;

namespace RootEngineProbe;

internal static class NativePrivateVisibility
{
    private static bool installed;

    public static void Install()
    {
        if (installed) return;
        new Harmony("local.root.private-visibility").Patch(AccessTools.Method(typeof(TuberMatch), "IsObfuscated"),
            postfix: new HarmonyMethod(typeof(NativePrivateVisibility), nameof(UsesPrivateState)));
        installed = true;
    }

    private static void UsesPrivateState(TuberMatch __instance, ref bool __result)
    {
        // The private authority filters hidden state in every match. Native
        // reveal actions must run even when the client build disables them.
        if (__instance.messageActionFactory?.TryCast<ObfuscatedMessageActionFactory>() is not null)
            __result = true;
    }

    public static void RestoreExploredRuins(TuberMatch match)
    {
        // Older saves recorded exploration but skipped its visibility change.
        // Restore only the accounts recorded by Root as having seen this ruin.
        foreach (var pair in match.Entities)
        {
            var ruin = pair.Value;
            if (ruin.GetAttributeValue(TuberAttributes.BuildingType, BuildingTypes.None) != BuildingTypes.Ruin) continue;
            var seen = ruin.GetAttributeValue(TuberAttributes.ContentsSeenByPlayers, null);
            if (seen is null || seen.Count == 0) continue;
            var hidden = ruin.GetAttributeValue(TCAttributes.DescendantsHidden, DescendantsHidden.DeckStyle);
            if (hidden.DescendantsVisibility.TryCast<AllDescendantsHidden>() is not null)
                hidden = new DescendantsHidden { DescendantsVisibility = new AsymmetricDescendantsHidden() };
            if (hidden.DescendantsVisibility.TryCast<AsymmetricDescendantsHidden>() is not { } visibility) continue;
            foreach (var id in seen)
            {
                var player = match.GetEntity(id).TryCast<Canis.entities.PlayerEntity>();
                if (player is not null) visibility = visibility.WithOtherVisible(player.AccountID);
            }
            hidden.DescendantsVisibility = visibility;
            ruin.SetAttribute(TCAttributes.DescendantsHidden, hidden);
        }
    }
}
