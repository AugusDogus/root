using Canis.utils.ids;
using HarmonyLib;
using tuber_canis.data;

namespace RootEngineProbe;

// Private demo content comes from the installed game. This changes local menu
// availability only, never Steam purchases, account records, or the Steam copy.
internal static class PrivatePlaytestContent
{
    private static bool installed;
    internal static readonly TuberProducts[] Products =
    {
        TuberProducts.ClockworkFactions, TuberProducts.WinterMap, TuberProducts.RiverfolkExpansion,
        TuberProducts.VagabondPack, TuberProducts.UnderworldExpansion, TuberProducts.MaraudersExpansion,
        TuberProducts.ExilesAndPartisans, TuberProducts.TheMiniExpansionPack, TuberProducts.HirelingsAndLandmarksPack
    };

    public static void Install()
    {
        if (installed) return;
        var harmony = new Harmony("local.root.private-playtest-content");
        harmony.Patch(AccessTools.Method(typeof(TuberIAPUtilities), nameof(TuberIAPUtilities.UserOwnsProduct)),
            prefix: new HarmonyMethod(typeof(PrivatePlaytestContent), nameof(GameplayContent)));
        harmony.Patch(AccessTools.Method(typeof(dwd.iap.store.IAPStoreBehaviour), "UserOwnsProduct", new[] { typeof(ArchetypeID) }),
            prefix: new HarmonyMethod(typeof(PrivatePlaytestContent), nameof(MenuContent)));
        installed = true;
    }

    private static bool GameplayContent(TuberProducts __0, ref bool __result)
    {
        if (!Products.Contains(__0)) return true;
        __result = true;
        return false;
    }

    private static bool MenuContent(ArchetypeID __0, ref bool __result) =>
        GameplayContent(lib.data.TuberProductUtils.ProductForArchId(__0), ref __result);
}
