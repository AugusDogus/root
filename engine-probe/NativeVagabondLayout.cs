using HarmonyLib;
using tuber.client.match.voodoo;
using UnityEngine;

namespace RootEngineProbe;

internal static class NativeVagabondLayout
{
    public static void Install(Harmony harmony) => harmony.Patch(
        AccessTools.Method(typeof(VagabondSatchelLayout), "Start"),
        postfix: new HarmonyMethod(typeof(NativeVagabondLayout), nameof(PositionSatchel)));

    private static void PositionSatchel(VagabondSatchelLayout __instance)
    {
        if (PrivateClient.Active is null || __instance.transform.TryCast<RectTransform>() is not { } satchel ||
            satchel.parent?.TryCast<RectTransform>() is not { } drawer || drawer.name != "DrawerContainer") return;
        // The stock satchel expands above the drawer into the sixth player's
        // panel. Keep its native contents and animation beside the drawer.
        satchel.anchoredPosition = new Vector2(drawer.rect.width + 12, drawer.rect.height + 12);
    }
}
