using Canis.json;
using HarmonyLib;
using tuber.client.menus.prompts;
using tuber.client.prompt;
using tuber.client.prompt.commands;
using UnityEngine;
using Object = UnityEngine.Object;

namespace RootEngineProbe;

// Isolated inspection of the online prompt, without the lobby submission flow.
public sealed class NativeOnlineSetupProbe : MonoBehaviour
{
    private NativeOnlineSetupFlow? flow;
    private bool failed;
    private bool captured;
    private static string Output => Path.GetFullPath(Path.Combine(BepInEx.Paths.GameRootPath, "..", "results", "online-setup-probe"));
    public NativeOnlineSetupProbe(IntPtr pointer) : base(pointer) { }
    public void Start()
    {
        Directory.CreateDirectory(Output);
        QualitySettings.SetQualityLevel(0, true);
        Application.targetFrameRate = 15;
        var harmony = new Harmony("local.root.online-setup-probe");
        harmony.Patch(AccessTools.Method(typeof(tuber.client.menus.OnlinePlaySessionProvider), "Write"),
            prefix: new HarmonyMethod(typeof(NativeOnlineSetupProbe), nameof(BlockLobbyWrite)) { priority = Priority.Last });
        harmony.Patch(AccessTools.Method(typeof(lib.src.menus.GameLobbySessionProvider), "Init"),
            prefix: new HarmonyMethod(typeof(NativeOnlineSetupProbe), nameof(BlockLobbySocket)) { priority = Priority.Last });
        harmony.Patch(AccessTools.Method(typeof(tuber.client.menus.commands.RunOnlineMatchesFlow), "execute"),
            prefix: new HarmonyMethod(typeof(NativeOnlineSetupProbe), nameof(BlockOnlineFlow)));
        if (Environment.GetEnvironmentVariable("ROOT_LAB_MODE") == "steam-online-test" &&
            File.Exists(Path.Combine(Output, "owned-content.fixture")))
        {
            harmony.Patch(AccessTools.Method(typeof(dwd.iap.store.IAPStoreBehaviour), "UserOwnsProduct"),
                prefix: new HarmonyMethod(typeof(NativeOnlineSetupProbe), nameof(FixtureOwnership)));
            File.WriteAllText(Path.Combine(Output, "fixture-ownership-active"), "Fixture ownership active for the isolated UI test only.");
        }
    }
    // Isolated UI-test dependency, never installed by the friends' launcher.
    // This does not test purchases or change Steam's product ownership.
    private static bool FixtureOwnership(Canis.utils.ids.ArchetypeID __0, ref bool __result)
    {
        var product = lib.data.TuberProductUtils.ProductForArchId(__0);
        if (product is not (tuber_canis.data.TuberProducts.BaseGame or tuber_canis.data.TuberProducts.ClockworkFactions or
            tuber_canis.data.TuberProducts.RiverfolkExpansion or tuber_canis.data.TuberProducts.UnderworldExpansion or
            tuber_canis.data.TuberProducts.ExilesAndPartisans)) return true;
        __result = true;
        return false;
    }
    private static bool BlockLobbyWrite(Il2CppSystem.Object __0, bool __runOriginal)
    {
        if (__runOriginal) File.WriteAllText(Path.Combine(Output, "blocked-lobby-write.txt"), $"Unrouted native lobby message {__0.GetIl2CppType().FullName}. Submission was blocked.");
        return false;
    }
    private static bool BlockLobbySocket(bool __runOriginal, ref Il2CppSystem.Collections.IEnumerator __result)
    {
        if (__runOriginal) File.WriteAllText(Path.Combine(Output, "blocked-lobby-write.txt"), "The native waiting room tried to open an official lobby socket. Connection was blocked.");
        __result = new Il2CppSystem.Collections.ArrayList().GetEnumerator();
        return false;
    }
    private static bool BlockOnlineFlow(ref Il2CppSystem.Collections.IEnumerator __result)
    {
        File.WriteAllText(Path.Combine(Output, "blocked-online-flow.txt"), "Private navigation tried to start the official online menu. The test blocked it.");
        __result = new Il2CppSystem.Collections.ArrayList().GetEnumerator();
        return false;
    }
    public void Update()
    {
        AudioListener.volume = 0;
        if (failed) return;
        try
        {
            NativeCommunicationProbe.Update(Output);
            NativeUiAuditProbe.Update(Output);
            File.WriteAllText(Path.Combine(Output, "scene-state.json"), System.Text.Json.JsonSerializer.Serialize(
                Object.FindObjectsOfType<ConfigureGamePlayerSlot>().Select(figure => new
                {
                    figure.name,
                    models = figure.GetComponentsInChildren<dwd.core.prefabs.implementations.byflavor.PrefabByFlavorMetadata>()
                        .Where(model => model.GetComponentsInChildren<Renderer>().Any(renderer => renderer.isVisible))
                        .Select(model => model.name).ToArray()
                }).ToArray()));
            if (flow is null && Environment.GetEnvironmentVariable("ROOT_LAB_MODE") == "online-setup-probe")
            {
                if (Object.FindObjectOfType<LandingPromptBehaviour>() == null) return;
                flow = new NativeOnlineSetupFlow(result =>
                {
                    File.WriteAllText(Path.Combine(Output, "configured.json"), JSON.ToJSON(result, false));
                    failed = true;
                }, () => { File.WriteAllText(Path.Combine(Output, "configured.json"), "null"); failed = true; });
                flow.Show();
            }
            flow?.Update();
            if (Object.FindObjectOfType<ConfigureOnlineGamePromptBehaviour>() is { } view)
            {
                if (!captured)
                {
                    File.WriteAllText(Path.Combine(Output, "view.txt"), $"Prefab slots: {view.playerSlots.Length}\n" + string.Join("\n", view.GetComponentsInChildren<Transform>(true).Select(item => item.name)));
                    File.WriteAllText(Path.Combine(Output, "options.json"), System.Text.Json.JsonSerializer.Serialize(view.gameOptions.Select(option =>
                        new { option.name, option.value, choices = option.possibleChoices.Select(choice => choice.choiceName).ToArray() }).ToArray()));
                    File.WriteAllText(Path.Combine(Output, "pref-prefix.txt"), view.PlayerPrefPrefix);
                    File.WriteAllText(Path.Combine(Output, "model.txt"), string.Join("\n", Enumerable.Range(0, view.Prompt.PlayerSlots.Count).Select(index => view.Prompt.PlayerSlots[index]).Select(slot =>
                        string.Join("; ", slot.components.ToArray().Select(item => item.GetIl2CppType().FullName + ": " + item.ToString())))));
                    captured = true;
                }
                var figures = Object.FindObjectsOfType<ConfigureGamePlayerSlot>();
                File.WriteAllText(Path.Combine(Output, "visual.json"), System.Text.Json.JsonSerializer.Serialize(new
                {
                    slots = view.playerSlots.Length,
                    visibleFigures = figures.Count(figure => figure.GetComponentsInChildren<Renderer>().Any(renderer => renderer.isVisible)),
                    title = view.GetComponentsInChildren<TMPro.TMP_Text>(true).FirstOrDefault(item => item.name == "HeaderText")?.text
                }));
                var commandFile = Path.Combine(Output, "command.txt");
                if (File.Exists(commandFile))
                {
                    var command = File.ReadAllText(commandFile).Trim();
                    if (command.StartsWith("{", StringComparison.Ordinal))
                    {
                        using var settings = System.Text.Json.JsonDocument.Parse(command);
                        if (settings.RootElement.TryGetProperty("options", out var options))
                        {
                            foreach (var option in options.EnumerateObject()) view.SelectOptionByValue(option.Name, option.Value.GetInt32());
                            view.Event_ConfirmAllSettingsChanges();
                        }
                        if (settings.RootElement.TryGetProperty("controllers", out var controllers))
                            for (var index = 0; index < controllers.GetArrayLength(); index++)
                                view.Prompt.PlayerSlots[index].GetOne<tuber.client.data.PlayerTypeData>().Type = (tuber.client.data.PlayerTypeData.PlayerType)controllers[index].GetInt32();
                        if (settings.RootElement.TryGetProperty("factions", out var factions))
                            for (var index = 0; index < factions.GetArrayLength(); index++)
                            {
                                view.Prompt.PlayerSlots[index].GetOne<tuber.client.match.data.FactionData>().Faction = new Il2CppSystem.Nullable<tuber_canis.data.Factions>((tuber_canis.data.Factions)factions[index].GetInt32());
                                if (MatchSetup.IsClockwork(factions[index].GetInt32()))
                                    view.Prompt.PlayerSlots[index].GetOne<lib.src.data.ClockworkConfigData>().Reset(new Il2CppSystem.Nullable<tuber_canis.data.Factions>((tuber_canis.data.Factions)factions[index].GetInt32()));
                            }
                    }
                    if (command == "cycle-second-seat")
                    {
                        var slot = view.Prompt.PlayerSlots[1];
                        view.Event_Increment(slot);
                        File.WriteAllText(Path.Combine(Output, "second-controller.txt"), slot.GetOne<tuber.client.data.PlayerTypeData>().Type.ToString());
                    }
                    if (command == "settings") view.GetComponentInChildren<tuber.client.menus.behaviours.ConfigureGameDetailsPromptButton>().Event_Open();
                    if (command == "confirm-settings") Object.FindObjectOfType<ConfigureGameDetailsPromptBehaviour>()?.Confirm();
                    if (command == "create") view.Event_CreateGame();
                    if (command == "back") view.Event_Back();
                    if (command == "reset") view.Event_ResetToDefault();
                    // Deletion acknowledges completion, not merely receipt.
                    File.Delete(commandFile);
                }
            }
            if (Object.FindObjectOfType<ConfigureGameDetailsPromptBehaviour>() is { } settingsView)
                File.WriteAllText(Path.Combine(Output, "settings-view.txt"), string.Join("\n", settingsView.GetComponentsInChildren<Transform>(true).Select(item =>
                    item.parent.name + "/" + item.name + ": " + string.Join(",", item.GetComponents<Component>().Select(c => c.GetIl2CppType().Name)))));
            var cooperativePrompt = Path.Combine(Output, "coop-ready");
            if (Object.FindObjectOfType<tuber.client.match.prompt.behaviours.CoopOptionPromptBehaviour>() != null)
                File.WriteAllText(cooperativePrompt, "ready");
            else File.Delete(cooperativePrompt);
            if (Object.FindObjectOfType<WaitingForPlayersPromptBehaviour>() is { } waiting)
            {
                File.WriteAllText(Path.Combine(Output, "lobby-view.txt"), $"Slots: {waiting.playerSlots.Length}\n" +
                    string.Join("\n", waiting.GetComponentsInChildren<Transform>(true).Select(item => item.name)));
                File.WriteAllText(Path.Combine(Output, "lobby-state.json"), System.Text.Json.JsonSerializer.Serialize(new
                {
                    joinVisible = waiting.joinButton.gameObject.activeInHierarchy,
                    startVisible = waiting.startEarlyButton.gameObject.activeInHierarchy,
                    joinEnabled = waiting.joinButton.interactable,
                    local = Enumerable.Range(0, waiting.Prompt.PlayerSlots.Count).Select(index => waiting.Prompt.PlayerSlots[index])
                        .Where(slot => slot.GetOne<lib.src.data.PendingGamePlayerAccountData>().PlayerAccountID?.ToString() == boardgames.utils.AccountUtils.GetLoggedInID()?.ToString())
                        .Select(slot => new { filled = slot.GetOne<tuber.client.data.PlayerSlotLockData>().SlotFilled, faction = slot.GetOne<tuber.client.match.data.FactionData>().ToString() }).ToArray()
                }));
                var commandFile = Path.Combine(Output, "lobby-command.txt");
                if (File.Exists(commandFile))
                {
                    var command = File.ReadAllText(commandFile).Trim();
                    File.Delete(commandFile);
                    if (command == "leave") waiting.Event_LeaveGame();
                    if (command == "join") waiting.Event_JoinGame();
                    if (command == "start") waiting.Event_StartEarly();
                    if (command == "increment")
                    {
                        var id = boardgames.utils.AccountUtils.GetLoggedInID().ToString();
                        var local = Enumerable.Range(0, waiting.Prompt.PlayerSlots.Count).Select(index => waiting.Prompt.PlayerSlots[index])
                            .Single(slot => slot.GetOne<lib.src.data.PendingGamePlayerAccountData>().PlayerAccountID?.ToString() == id);
                        waiting.Event_Increment(local);
                    }
                }
            }

        }
        catch (Exception error)
        { File.WriteAllText(Path.Combine(Output, "error.txt"), error.ToString()); failed = true; }
    }
}
