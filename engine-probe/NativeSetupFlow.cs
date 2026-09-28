using dwd.core.data.composition;
using HarmonyLib;
using dwd.core.settings;
using dwd.core.settings.playerPrefs.definitions;
using tuber.canis;
using tuber.client.data;
using tuber.client.match.data;
using tuber.client.menus.commands;
using tuber.client.menus.prompts;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace RootEngineProbe;

// Adapt Root's setup model and prefab. Root still owns its option controls,
// faction picker, Clockwork configuration and conversion to match initialization.
internal sealed class NativeSetupFlow
{
    private static NativeSetupFlow? active;
    private static bool patched;
    private readonly Action<TuberMatchInitData> configured;
    private readonly Action cancelled;
    private RunConfigureOfflineMatchFlow? command;
    private SerializedMatchComponent? data;
    private TMPro.TMP_Text? title;
    private ConfigureOfflineGamePromptBehaviour? view;

    public NativeSetupFlow(Action<TuberMatchInitData> configured, Action cancelled)
    { this.configured = configured; this.cancelled = cancelled; }

    public void Show()
    {
        Install();
        for (var seat = 4; seat < 6; seat++)
        {
            var key = $"PassAndPlayGamePlayer{seat}";
            if (PrefsManager.configDefinitionMap.ContainsKey(key + "Factions")) continue;
            PrefsManager.AddConfig(key + "Factions", IntPrefDefinition.SystemPref($"RootSixPlayer.Player{seat}.Faction", seat + 2, null).Cast<ISettingDefinition>());
            PrefsManager.AddConfig(key + "Type", IntPrefDefinition.SystemPref($"RootSixPlayer.Player{seat}.Type", 0, null).Cast<ISettingDefinition>());
            PrefsManager.AddConfig(key + "ClockworkConfig", StringPrefDefinition.SystemPref($"RootSixPlayer.Player{seat}.Clockwork", "", null).Cast<ISettingDefinition>());
        }
        // Keep the catalog's ordinary four-player scenario unchanged.
        var init = Canis.json.JSON.Deserialize<TuberMatchInitData>(Canis.json.JSON.ToJSON(
            lib.src.menus.AllTuberOfflineMatches.GetMatch("PassAndPlay"), false));
        init.clientMatchOptions.MinPlayers = 6;
        init.clientMatchOptions.MaxPlayers = 6;
        init.clientMatchOptions.MinHumanPlayers = 1;
        init.clientMatchOptions.MaxHumanPlayers = 6;
        while (init.clientMatchOptions.DefaultPlayerNames.Count < 6)
            init.clientMatchOptions.DefaultPlayerNames.Add($"Player {init.clientMatchOptions.DefaultPlayerNames.Count + 1}");
        data = new SerializedMatchComponent(init);
        active = this;
        command = new RunConfigureOfflineMatchFlow(data);
        dwd.core.commands.CommandExecutor.Get().Execute(command);
    }

    public void Update()
    {
        if (view != null && title == null)
            title = view.GetComponentsInChildren<TMPro.TMP_Text>(true).FirstOrDefault(item => item.name == "HeaderText");
        if (title != null && title.text != "Six Player") title.text = "Six Player";
        if (command is not { Completed: true } completed) return;
        command = null;
        title = null;
        view = null;
        active = null;
        if (completed.ConfiguredResult is { } result) configured(result);
        else cancelled();
    }

    private static bool Owns(ConfigureOfflineGamePrompt prompt) =>
        active?.data is { } current && prompt.MatchData.Pointer == current.Pointer;

    private static void Install()
    {
        if (patched) return;
        var harmony = new Harmony("local.root.native-setup");
        harmony.Patch(AccessTools.Method(typeof(ConfigureOfflineGamePromptBehaviour), "initialize"),
            prefix: new HarmonyMethod(typeof(NativeSetupFlow), nameof(ExtendView)));
        harmony.Patch(AccessTools.Method(typeof(ConfigureGameScenePromptBehaviour), "initialize"),
            prefix: new HarmonyMethod(typeof(NativeSetupFlow), nameof(ExtendScene)));
        patched = true;
    }

    private static void ExtendScene(ConfigureGameScenePromptBehaviour __instance)
    {
        if (__instance.Prompt.TryCast<ConfigureOfflineGamePrompt>() is not { } prompt || !Owns(prompt)) return;
        ExtendModel(prompt);
        NativeSetupScene.Extend(__instance);
    }

    private static void ExtendModel(ConfigureOfflineGamePrompt __instance)
    {
        if (!Owns(__instance)) return;
        var slots = __instance.PlayerSlots.list.Cast<Il2CppSystem.Collections.Generic.ICollection<DataComposition>>();
        while (slots.Count < 6)
        {
            var faction = new FactionData(new Il2CppSystem.Nullable<tuber_canis.data.Factions>());
            var controller = new PlayerTypeData(PlayerTypeData.PlayerType.Human);
            var locks = new PlayerSlotLockData(false, false);
            slots.Add(new DataComposition(new DataComponent[]
            {
                new OfflineMatchSlotNameData($"Player {slots.Count + 1}", locks, faction, controller, true),
                locks, faction, controller, new lib.src.data.ClockworkConfigData(),
                new AllowedRandomFactionsData { AllowedFactions = __instance.PlayerSlots[0].GetOne<AllowedRandomFactionsData>().AllowedFactions.ToArray() }
            }));
        }
    }

    private static void ExtendView(ConfigureOfflineGamePromptBehaviour __instance)
    {
        if (!Owns(__instance.Prompt) || __instance.playerSlots.Length == 6) return;
        if (active is { } flow) flow.view = __instance;
        ExtendModel(__instance.Prompt);
        __instance.playerSlots = NativeSixSeatLayout.Extend(__instance.playerSlots.ToArray());
        foreach (var spacing in __instance.GetComponentsInChildren<tuber.client.menus.behaviours.PlayerSlotSpacingRenderer>(true))
            spacing.enabled = false;
    }
}
