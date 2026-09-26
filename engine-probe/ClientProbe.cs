using BepInEx;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.SceneManagement;
using System.Text.Json;
using Canis.utils.ids;
using tuber.client.match.behaviours;
using tuber.client.match.selection;

namespace RootEngineProbe;

public sealed class ClientProbe : MonoBehaviour
{
    private static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("Client Probe");
    private float elapsed;
    private int captures;
    private bool launched;
    private PrivateClient? client;
    private bool failed;
    private bool finished;
    private bool submitted;
    private bool configuredGraphics;
    private int? submittedCounter;
    private string? submittedPrompt;
    private string? submittedTarget;
    private bool cardConfirmed;
    private bool fastForwarded;
    private SteamGameBridge? steamBridge;
    private readonly System.Diagnostics.Stopwatch steamClock = new();

    public ClientProbe(IntPtr pointer) : base(pointer) { }
    public void Start() => steamClock.Start();

    public void Update()
    {
        if (finished) return;
        AudioListener.volume = 0;
        if (configuredGraphics == false)
        {
            configuredGraphics = true;
            QualitySettings.SetQualityLevel(0, true);
            QualitySettings.shadows = ShadowQuality.Disable;
            QualitySettings.antiAliasing = 0;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 20;
        }
        elapsed += Time.unscaledDeltaTime;
        if (Environment.GetEnvironmentVariable("ROOT_LAB_MODE") == "steam-client-probe")
        {
            elapsed = (float)steamClock.Elapsed.TotalSeconds;
            if (!Steamworks.SteamClient.IsValid || !Steamworks.SteamClient.IsLoggedOn ||
                tuber.client.utils.TuberPrefs.SeenFirstTimeStrategicViewTutorialPrompt is null)
            {
                if (elapsed < 120) return;
                Log.LogError("Steam client or Root preferences did not initialize within 120 seconds.");
                failed = true;
                launched = true;
            }
        }
        if (elapsed >= 25 && launched == false)
        {
            launched = true;
            try
            {
                var tutorial = tuber.client.utils.TuberPrefs.SeenFirstTimeStrategicViewTutorialPrompt
                    ?? throw new InvalidOperationException("Root tutorial preferences have not initialized.");
                var file = Path.GetFullPath(Path.Combine(BepInEx.Paths.GameRootPath, "..", "connection.json"));
                if (Environment.GetEnvironmentVariable("ROOT_LAB_MODE") == "steam-client-probe")
                {
                    steamBridge = new(Path.Combine(Path.GetDirectoryName(file) ?? throw new InvalidDataException("Missing lab path."), "steam-config.json"));
                    client = steamBridge.Client;
                }
                else client = new PrivateClient(file);
                tutorial.SetWithoutSave(true);
            }
            catch (Exception error)
            {
                Log.LogError($"Board launch failed: {error}");
                failed = true;
            }
        }
        if (client is not null && failed == false)
        {
            try { steamBridge?.Tick(); client.Update(elapsed); }
            catch (Exception error) { failed = true; Log.LogError($"Private client failed: {error}"); }
        }
        if (elapsed > 45 && client?.ReceivedMessages > 100 && fastForwarded == false)
        {
            var replay = UnityEngine.Object.FindObjectOfType<tuber.client.match.ui.SkipReplayButton>(true);
            if (replay is not null)
            {
                replay.Event_FastForward();
                fastForwarded = true;
                Log.LogInfo("Skipped earlier setup replay through the native replay control");
            }
        }
        if (elapsed > 45 && submitted == false && client?.Offer is { Targets.Length: > 0 } offer)
        {
            var responder = UnityEngine.Object.FindObjectOfType<TuberSelectionResponder>();
            var target = new EntityID(offer.Targets[0]);
            if (offer.Prompt.EndsWith(".ChooseAmbushCards") &&
                responder?.SelectionCommand?.TryCast<TuberSelectCardPromptEntityListSelectionCommand>() is { } cardCommand &&
                cardCommand.prompt is { } prompt && prompt.Choices.Count > 0)
            {
                // Card dialogs use temporary presentation entities. Feed the
                // displayed card to the native control, which maps it back.
                submitted = cardCommand.SubmitSelection(prompt.Choices[0].EntityID);
                if (submitted)
                {
                    submittedCounter = offer.Counter;
                    submittedPrompt = offer.Prompt;
                    submittedTarget = target.ToString();
                    Log.LogInfo("Selected the presented ambush card through its native control");
                }
            }
            else if (offer.Prompt.EndsWith(".ChooseAmbushCards") == false &&
                responder is not null && responder.HasSelection && responder.ValidateSelection(target))
            {
                submitted = responder.AttemptSubmitSelection(target);
                if (submitted)
                {
                    submittedCounter = offer.Counter;
                    submittedPrompt = offer.Prompt;
                    submittedTarget = target.ToString();
                }
                Log.LogInfo($"Native UI selection submission: {submitted}");
            }
        }
        if (elapsed > 50 && submittedPrompt?.EndsWith(".ChooseAmbushCards") == true && cardConfirmed == false)
        {
            var prompt = UnityEngine.Object.FindObjectOfType<tuber.client.match.prompt.behaviours.SelectCardPromptBehaviour>();
            if (prompt is not null)
            {
                prompt.Event_ConfirmSelection();
                cardConfirmed = true;
                Log.LogInfo("Confirmed the ambush using the native card dialog");
            }
        }
        if (elapsed >= (captures + 1) * 15)
        {
            captures++;
            var output = Path.GetFullPath(Path.Combine(BepInEx.Paths.GameRootPath, "..", "results", "client-probe"));
            Directory.CreateDirectory(output);
            var lines = new List<string> { $"Scene={SceneManager.GetActiveScene().name}; frame={Time.frameCount}" };
            foreach (var transform in UnityEngine.Object.FindObjectsOfType<Transform>())
            {
                var components = string.Join(",", transform.GetComponents<Component>().Select(c => c.GetIl2CppType().FullName));
                lines.Add($"{transform.name}: {components}");
            }
            File.WriteAllLines(Path.Combine(output, $"scene-{captures}.txt"), lines);
            ScreenCapture.CaptureScreenshot(Path.Combine(output, $"screen-{captures}.png"));
            var consumer = UnityEngine.Object.FindObjectOfType<TuberMatchMessageConsumer>();
            Log.LogInfo($"Captured frame {Time.frameCount}, scene {SceneManager.GetActiveScene().name}, private messages {client?.ReceivedMessages}, queued {consumer?.messageQueue?.Count}");
        }
        if (elapsed >= 140 || failed)
        {
            var entities = UnityEngine.Object.FindObjectOfType<TuberEntitiesProvider>()?.TuberEntities;
            var passed = failed == false && entities?.allPlayers.Count == 6 && client?.AcceptedChoices == 1 &&
                submittedCounter is int previous && (client.Offer is null || client.Offer.Counter != previous);
            var output = Path.GetFullPath(Path.Combine(BepInEx.Paths.GameRootPath, "..", "results", "client-probe"));
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "test-results.json"), JsonSerializer.Serialize(new
            {
                status = passed ? "passed" : "failed", scene = SceneManager.GetActiveScene().name,
                players = entities?.allPlayers.Count, submitted, received = client?.ReceivedMessages,
                accepted = client?.AcceptedChoices, prompt = client?.Offer?.Prompt, counter = client?.Offer?.Counter,
                submittedCounter, submittedPrompt, submittedTarget,
                localAccount = entities?.GetActiveLocalPlayerID()?.ToString()
            }, new JsonSerializerOptions { WriteIndented = true }));
            finished = true;
            steamBridge?.Dispose();
            steamBridge = null;
            Application.Quit(passed ? 0 : 1);
        }
    }
    public void OnDestroy() => steamBridge?.Dispose();
}
