using System.Text.Json;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using Canis.json;
using Canis.utils.ids;
using HarmonyLib;
using Networking.game;
using Networking.lobby;
using tuber.client.data;
using tuber.client.menus;
using tuber.client.menus.prompts;
using tuber.client.prompt;
using tuber.client.prompt.commands;
using UnityEngine;
using Object = UnityEngine.Object;

namespace RootEngineProbe;

// Display the stock waiting room directly. Its outgoing lobby messages go to
// our authenticated seat connection; the official lobby flow is never run.
internal sealed class NativeLobbyFlow : IDisposable
{
    private static NativeLobbyFlow? active;
    private static bool patched;
    private readonly Action<string, Dictionary<string, string>?> send;
    private readonly Action leave;
    private readonly Action<GameObject> attachChat;
    private WaitingForPlayersPrompt? prompt;
    private DisplayTuberPrompt? command;
    private GameID? gameId;
    private TuberLobbyGameProvider? provider;
    private bool host;
    private bool canStart;
    private bool joined;
    private enum JoinResult { Pending, Joined, Rejected }
    private sealed class JoinAttempt { public JoinResult Result; }
    private JoinAttempt? joining;
    private string? previous;

    public NativeLobbyFlow(Action<string, Dictionary<string, string>?> send, Action leave, Action<GameObject> attachChat)
    { this.send = send; this.leave = leave; this.attachChat = attachChat; }

    public void Apply(JsonElement response)
    {
        Install();
        host = response.GetProperty("seat").GetInt32() == 0;
        canStart = response.GetProperty("canStart").GetBoolean();
        var json = response.GetProperty("pendingGame").GetRawText();
        if (json == previous) return;
        previous = json;
        if (prompt is null)
        {
            NativeLobbyData.UseIdentity(new AccountID(response.GetProperty("account").GetString()));
            dwd.core.account.AccountProvider.Find().Initialize(new dwd.core.account.SerializableAccount
            { AccountID = new AccountID(response.GetProperty("account").GetString()), Username = "Private player", Attributes = new() });
        }
        var metadata = JSON.Deserialize<DWDPendingGameMetadata>(json);
        joined = metadata.AlreadyInGame;
        if (joined && joining is { } attempt) attempt.Result = JoinResult.Joined;
        provider ??= Object.FindObjectOfType<TuberLobbyGameProvider>()
            ?? throw new InvalidOperationException("Root's lobby screen is unavailable. Return to the menu and try again.");
        if (!provider.Initialized)
            provider.Initialize(new Il2CppSystem.Collections.Generic.Dictionary<GameID, dwd.core.data.composition.DataComposition>()
                .Cast<Il2CppSystem.Collections.Generic.IDictionary<GameID, dwd.core.data.composition.DataComposition>>());
        provider.createOrUpdateDataCompositionFor(metadata);
        if (prompt is not null) return;
        gameId = metadata.GameID;
        prompt = new WaitingForPlayersPrompt(provider.All[gameId]);
        active = this;
        command = new DisplayTuberPrompt(TuberModalScope.Menus,
            prompt.Cast<dwd.core.ui.prompt.prompts.IPrompt>(), new TuberPromptDisplayData(false, true));
        dwd.core.commands.CommandExecutor.Get().Execute(command);
    }

    public void Dispose()
    {
        RejectJoin();
        prompt?.Dismiss();
        if (provider != null && gameId is not null) provider.writableAll.Remove(gameId);
        if (active == this) active = null;
        NativeLobbyData.UseIdentity(null);
        prompt = null;
        command = null;
        previous = null;
    }

    public void RejectJoin()
    {
        if (joining is { } attempt) attempt.Result = JoinResult.Rejected;
    }

    private static bool Owns(WaitingForPlayersPromptBehaviour view) => active?.prompt is { } current && view.Prompt.Pointer == current.Pointer;
    private static void Install()
    {
        if (patched) return;
        var harmony = new Harmony("local.root.private-lobby");
        harmony.Patch(AccessTools.Method(typeof(WaitingForPlayersPromptBehaviour), "initialize"),
            prefix: new HarmonyMethod(typeof(NativeLobbyFlow), nameof(ExtendView)),
            postfix: new HarmonyMethod(typeof(NativeLobbyFlow), nameof(FinishView)));
        harmony.Patch(AccessTools.Method(typeof(lib.src.menus.GameLobbySessionProvider), "Init"),
            prefix: new HarmonyMethod(typeof(NativeLobbyFlow), nameof(UsePrivateSession)));
        harmony.Patch(AccessTools.Method(typeof(tuber.client.menus.commands.JoinExistingGame), "execute"),
            prefix: new HarmonyMethod(typeof(NativeLobbyFlow), nameof(JoinPrivateRoom)));
        harmony.Patch(AccessTools.Method(typeof(ConfigureGameScenePromptBehaviour), "initialize"),
            prefix: new HarmonyMethod(typeof(NativeLobbyFlow), nameof(ExtendScene)));
        harmony.Patch(AccessTools.Method(typeof(WaitingForPlayersPromptBehaviour), "Update"),
            postfix: new HarmonyMethod(typeof(NativeLobbyFlow), nameof(UpdateButtons)));
        harmony.Patch(AccessTools.Method(typeof(WaitingForPlayersPromptBehaviour), "Event_StartEarly"),
            prefix: new HarmonyMethod(typeof(NativeLobbyFlow), nameof(Start)));
        harmony.Patch(AccessTools.Method(typeof(WaitingForPlayersPromptBehaviour), "Event_Back"),
            prefix: new HarmonyMethod(typeof(NativeLobbyFlow), nameof(Leave)));
        harmony.Patch(AccessTools.Method(typeof(WaitingForPlayersPromptBehaviour), "Event_LeaveGame"),
            prefix: new HarmonyMethod(typeof(NativeLobbyFlow), nameof(ChangeFaction)));
        harmony.Patch(AccessTools.Method(typeof(OnlinePlaySessionProvider), "Write"),
            prefix: new HarmonyMethod(typeof(NativeLobbyFlow), nameof(Route)));
        patched = true;
    }

    private static void ExtendScene(ConfigureGameScenePromptBehaviour __instance)
    {
        if (active?.prompt is not { } prompt || __instance.Prompt.Pointer != prompt.Pointer) return;
        NativeSetupScene.Extend(__instance);
    }
    private static void ExtendView(WaitingForPlayersPromptBehaviour __instance)
    {
        if (!Owns(__instance)) return;
        __instance.playerSlots = NativeSixSeatLayout.Extend(__instance.playerSlots.ToArray());
    }
    private static void FinishView(WaitingForPlayersPromptBehaviour __instance)
    {
        if (!Owns(__instance)) return;
        active?.attachChat(__instance.gameObject);
    }
    private static bool UsePrivateSession(lib.src.menus.GameLobbySessionProvider __instance, GameID __0,
        ref Il2CppSystem.Collections.IEnumerator __result)
    {
        if (active?.gameId?.ToString() != __0.ToString()) return true;
        // The stock waiting room opens a separate official socket directly,
        // bypassing Write. Our seat transport already owns this room.
        __instance.gameID = __0;
        __result = new Il2CppSystem.Collections.ArrayList().GetEnumerator();
        return false;
    }
    private static bool JoinPrivateRoom(tuber.client.menus.commands.JoinExistingGame __instance,
        ref Il2CppSystem.Collections.IEnumerator __result)
    {
        if (active is not { } flow || __instance.gameID.ToString() != flow.gameId?.ToString()) return true;
        __result = flow.Join(__instance).WrapToIl2Cpp();
        return false;
    }
    private System.Collections.IEnumerator Join(tuber.client.menus.commands.JoinExistingGame command)
    {
        // The native button already validates and packs the faction choice.
        // Replace only its official request/spinner/timeout with our acknowledgement.
        if (joining is not null) yield break;
        if (joined) { command.GameJoined = true; yield break; }
        var attempt = new JoinAttempt();
        joining = attempt;
        send("lobby-join", CopyMetadata(command.metadata));
        while (active == this && attempt.Result == JoinResult.Pending) yield return null;
        command.GameJoined = attempt.Result == JoinResult.Joined;
        if (joining == attempt) joining = null;
    }
    private static void UpdateButtons(WaitingForPlayersPromptBehaviour __instance)
    {
        if (!Owns(__instance) || active is not { } flow) return;
        __instance.startEarlyButton.gameObject.SetActive(flow.host && flow.joined);
        __instance.startEarlyButton.interactable = flow.canStart;
        foreach (var label in __instance.startEarlyButton.GetComponentsInChildren<TMPro.TMP_Text>(true)) label.text = "Start Game";
    }
    private static bool Start(WaitingForPlayersPromptBehaviour __instance)
    {
        if (!Owns(__instance) || active is not { } flow) return true;
        if (flow.host && flow.canStart) flow.send("lobby-start", null);
        return false;
    }
    private static bool Leave(WaitingForPlayersPromptBehaviour __instance)
    {
        if (!Owns(__instance) || active is not { } flow) return true;
        flow.leave();
        return false;
    }
    private static bool ChangeFaction(WaitingForPlayersPromptBehaviour __instance)
    {
        if (!Owns(__instance) || active is not { } flow) return true;
        flow.send("lobby-leave", null);
        return false;
    }
    private static bool Route(Il2CppSystem.Object __0)
    {
        if (active is not { } flow) return true;
        if (__0.TryCast<UpdateLobbyGamePlayerMetadata>() is { } update && update.GameID.ToString() == flow.gameId?.ToString())
            flow.send("lobby-metadata", CopyMetadata(update.Metadata));
        else if (__0.TryCast<JoinLobbyGame>() is { } join && join.GameID.ToString() == flow.gameId?.ToString())
            flow.send("lobby-join", CopyMetadata(join.Metadata));
        // A private waiting room must never submit to the official session.
        return false;
    }
    private static Dictionary<string, string> CopyMetadata(Il2CppSystem.Collections.Generic.Dictionary<string, string> source)
    {
        var result = new Dictionary<string, string>();
        foreach (var pair in source) result.Add(pair.Key, pair.Value);
        return result;
    }
}
