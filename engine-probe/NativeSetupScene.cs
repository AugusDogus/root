using dwd.core.prefabs.implementations.byflavor;
using HarmonyLib;
using tuber.client.menus.prompts;
using tuber.client.menus.behaviours;
using UnityEngine;
using Object = UnityEngine.Object;

namespace RootEngineProbe;

// The animated faction figures live in a scene shared by setup and the lobby.
// Keep its six slots until Unity unloads the scene, including between prompts.
internal static class NativeSetupScene
{
    private static bool patched;

    public static void Extend(ConfigureGameScenePromptBehaviour view)
    {
        if (!patched)
        {
            new Harmony("local.root.six-player-figures").Patch(
                AccessTools.Method(typeof(ConfigureGamePlayerSlot), "dataChanged"),
                postfix: new HarmonyMethod(typeof(NativeSetupScene), nameof(RefreshFigure)));
            patched = true;
        }
        if (view.playerSlots.Length == 6) return;
        var original = view.playerSlots.ToArray();
        var positions = original.Select(slot => slot.transform.localPosition).ToArray();
        var scale = original[0].transform.localScale * 0.7f;
        foreach (var spacing in view.GetComponentsInChildren<PlayerSlotSpacingRenderer>(true)) spacing.enabled = false;
        var slots = original.ToList();
        var template = original[^1];
        while (slots.Count < 6)
        {
            var clone = Object.Instantiate(template.gameObject, template.transform.parent);
            // The template can already have a spawned character. Unity copies
            // that child, but not the subscriber's runtime instance reference.
            foreach (var model in clone.GetComponentsInChildren<PrefabByFlavorMetadata>(true))
            {
                model.gameObject.SetActive(false);
                Object.Destroy(model.gameObject);
            }
            foreach (var figure in clone.GetComponentsInChildren<ConfigureGamePlayerSlot>(true))
            {
                figure.currentPrefab = null;
                figure.instance = null;
            }
            slots.Add(clone.GetComponent<SubscriptionProvider>());
        }
        view.playerSlots = slots.ToArray();
        var margin = (positions[^1] - positions[0]) * 0.05f;
        for (var index = 0; index < 6; index++)
        {
            slots[index].transform.localPosition = Vector3.Lerp(positions[0] - margin, positions[^1] + margin, index / 5f);
            slots[index].transform.localScale = scale;
        }
    }

    private static void RefreshFigure(ConfigureGamePlayerSlot __instance)
    {
        // New empty-seat data can have version zero, just like the native
        // cache after rebinding. Force the first model lookup for all six slots.
        if (__instance.GetComponentInParent<ConfigureGameScenePromptBehaviour>()?.playerSlots.Length == 6)
            __instance.cachedFactionVersion = ulong.MaxValue;
    }
}
