using dwd.core.data.composition;
using HarmonyLib;
using dwd.core.settings;
using dwd.core.settings.playerPrefs.definitions;
using tuber.canis;
using tuber.client.data;
using tuber.client.match.data;
using tuber.client.prompt;
using tuber.client.prompt.commands;
using tuber.client.menus.prompts;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace RootEngineProbe;

// Adapt Root's setup model and prefab. Root still owns its option controls,
// faction picker, Clockwork configuration and conversion to match initialization.
internal sealed class NativeOnlineSetupFlow
{
    private static NativeOnlineSetupFlow? active;
    private static bool patched;
    private readonly Action<Networking.lobby.CreateLobbyGame> configured;
    private readonly Action cancelled;
    private DisplayTuberPrompt? command;
    private ConfigureGamePrompt? prompt;
    private TMPro.TMP_Text? title;
    private ConfigureOnlineGamePromptBehaviour? view;

    public NativeOnlineSetupFlow(Action<Networking.lobby.CreateLobbyGame> configured, Action cancelled)
    { this.configured = configured; this.cancelled = cancelled; }

    public void Show()
    {
        PrivatePlaytestContent.Install();
        Install();
        for (var seat = 4; seat < 6; seat++)
        {
            var key = $"OnlineGamePlayer{seat}";
            if (PrefsManager.configDefinitionMap.ContainsKey(key + "Factions")) continue;
            PrefsManager.AddConfig(key + "Factions", IntPrefDefinition.SystemPref($"RootSixPlayer.OnlinePlayer{seat}.Faction", 4, null).Cast<ISettingDefinition>());
            PrefsManager.AddConfig(key + "Type", IntPrefDefinition.SystemPref($"RootSixPlayer.OnlinePlayer{seat}.Type", 0, null).Cast<ISettingDefinition>());
            PrefsManager.AddConfig(key + "ClockworkConfig", StringPrefDefinition.SystemPref($"RootSixPlayer.OnlinePlayer{seat}.Clockwork", "", null).Cast<ISettingDefinition>());
        }
        dwd.core.account.AccountProvider.Find().InitializeWithOfflineID(new Canis.utils.ids.AccountID(Guid.NewGuid().ToString()));
        prompt = new ConfigureGamePrompt();
        active = this;
        ExtendModel(prompt);
        command = new DisplayTuberPrompt(TuberModalScope.Menus,
            prompt.Cast<dwd.core.ui.prompt.prompts.IPrompt>(), new TuberPromptDisplayData(false, true));
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
        if (prompt?.Result is { } result)
        {
            result.Password = "";
            configured(result);
        }
        else cancelled();
    }

    private static bool Owns(ConfigureGamePrompt prompt) =>
        active?.prompt is { } current && prompt.Pointer == current.Pointer;

    private static void Install()
    {
        if (patched) return;
        var harmony = new Harmony("local.root.native-online-setup");
        harmony.Patch(AccessTools.Method(typeof(ConfigureOnlineGamePromptBehaviour), "initialize"),
            prefix: new HarmonyMethod(typeof(NativeOnlineSetupFlow), nameof(ExtendView)),
            postfix: new HarmonyMethod(typeof(NativeOnlineSetupFlow), nameof(RestoreAdditionalSeats)));
        harmony.Patch(AccessTools.Method(typeof(ConfigureOnlineGamePromptBehaviour), "OnDestroy"),
            prefix: new HarmonyMethod(typeof(NativeOnlineSetupFlow), nameof(SaveAdditionalSeats)));
        harmony.Patch(AccessTools.Method(typeof(ConfigureOnlineGamePromptBehaviour), "Event_Back"),
            prefix: new HarmonyMethod(typeof(NativeOnlineSetupFlow), nameof(Back)));
        harmony.Patch(AccessTools.Method(typeof(ConfigureOnlineGamePromptBehaviour), "Event_ResetToDefault"),
            postfix: new HarmonyMethod(typeof(NativeOnlineSetupFlow), nameof(ResetAdditionalSeats)));
        harmony.Patch(AccessTools.Method(typeof(ConfigureGameScenePromptBehaviour), "initialize"),
            prefix: new HarmonyMethod(typeof(NativeOnlineSetupFlow), nameof(ExtendScene)));
        harmony.Patch(AccessTools.Method(typeof(ConfigureGameDetailsPromptBehaviour), "initialize"),
            postfix: new HarmonyMethod(typeof(NativeOnlineSetupFlow), nameof(ConfigureDetails)));
        patched = true;
    }

    private static void RestoreAdditionalSeats(ConfigureOnlineGamePromptBehaviour __instance)
    {
        if (!Owns(__instance.Prompt)) return;
        // Root restores only the four slots declared by its original prompt.
        // Reuse its preference conversion for controller, faction and Clockwork.
        for (var seat = 4; seat < 6; seat++) __instance.SetupPlayerPrefForIndex(seat);
    }

    private static void SaveAdditionalSeats(ConfigureOnlineGamePromptBehaviour __instance)
    {
        if (!Owns(__instance.Prompt)) return;
        for (var seat = 4; seat < 6; seat++) __instance.SavePlayerPreferences(seat);
    }

    private static bool Back(ConfigureOnlineGamePromptBehaviour __instance)
    {
        if (!Owns(__instance.Prompt)) return true;
        // The stock handler also starts RunOnlineMatchesFlow and official login.
        // Our completion handler already returns to the private menu.
        __instance.Prompt.Dismiss();
        return false;
    }

    private static void ResetAdditionalSeats(ConfigureOnlineGamePromptBehaviour __instance)
    {
        if (!Owns(__instance.Prompt)) return;
        // The native reset explicitly names only the original four slots.
        __instance.Prompt.PlayerSlots[1].GetOne<PlayerSlotLockData>().PlayerTypeLocked = false;
        for (var seat = 4; seat < 6; seat++)
        {
            var slot = __instance.Prompt.PlayerSlots[seat];
            slot.GetOne<PlayerTypeData>().Type = PlayerTypeData.PlayerType.Human;
            slot.GetOne<FactionData>().Faction = new Il2CppSystem.Nullable<tuber_canis.data.Factions>(tuber_canis.data.Factions.Invalid);
            slot.GetOne<PlayerSlotLockData>().FactionLocked = true;
            slot.GetOne<PlayerSlotLockData>().PlayerTypeLocked = false;
        }
    }

    private static void ConfigureDetails(ConfigureGameDetailsPromptBehaviour __instance)
    {
        if (active is not null) NativePrivateOptions.Configure(__instance);
    }

    private static void ExtendScene(ConfigureGameScenePromptBehaviour __instance)
    {
        if (__instance.Prompt.TryCast<ConfigureGamePrompt>() is not { } prompt || !Owns(prompt)) return;
        ExtendModel(prompt);
        NativeSetupScene.Extend(__instance);
    }

    private static void ExtendModel(ConfigureGamePrompt __instance)
    {
        if (!Owns(__instance)) return;
        __instance.PlayerSlots[1].GetOne<PlayerSlotLockData>().PlayerTypeLocked = false;
        var slots = __instance.PlayerSlots.list.Cast<Il2CppSystem.Collections.Generic.ICollection<DataComposition>>();
        while (slots.Count < 6)
        {
            var faction = new FactionData(new Il2CppSystem.Nullable<tuber_canis.data.Factions>(tuber_canis.data.Factions.Invalid));
            var controller = new PlayerTypeData(PlayerTypeData.PlayerType.Human);
            var locks = new PlayerSlotLockData(true, false);
            slots.Add(new DataComposition(new DataComponent[]
            {
                new PlayerJoinSlotNameData(locks, faction, controller, false),
                locks, faction, controller, new lib.src.data.ClockworkConfigData(),
                new AllowedRandomFactionsData { AllowedFactions = __instance.PlayerSlots[0].GetOne<AllowedRandomFactionsData>().AllowedFactions.ToArray() }
            }));
        }
    }

    private static void ExtendView(ConfigureOnlineGamePromptBehaviour __instance)
    {
        if (!Owns(__instance.Prompt) || __instance.playerSlots.Length == 6) return;
        if (active is { } flow) flow.view = __instance;
        ExtendModel(__instance.Prompt);
        __instance.playerSlots = NativeSixSeatLayout.Extend(__instance.playerSlots.ToArray());
        foreach (var spacing in __instance.GetComponentsInChildren<tuber.client.menus.behaviours.PlayerSlotSpacingRenderer>(true))
            spacing.enabled = false;
    }
}
