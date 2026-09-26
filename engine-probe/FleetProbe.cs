using BepInEx.Logging;
using Canis.utils.ids;
using System.Text.Json;
using tuber.client.match.behaviours;
using tuber.client.match.selection;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace RootEngineProbe;

// Test-only observer. A local marker lets the harness request one native move
// after every client has rendered the initial six-seat state.
public sealed class FleetProbe : MonoBehaviour
{
    private static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("Fleet Probe");
    private readonly string output = Path.GetFullPath(Path.Combine(BepInEx.Paths.GameRootPath, "..", "results", "client-probe"));
    private PrivateClient? client;
    private float elapsed;
    private float nextReport;
    private bool submitted;
    private string? error;

    public FleetProbe(IntPtr pointer) : base(pointer) { }

    public void Start()
    {
        Directory.CreateDirectory(output);
        QualitySettings.SetQualityLevel(0, true);
        QualitySettings.shadows = ShadowQuality.Disable;
        QualitySettings.antiAliasing = 0;
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = 5;
    }

    public void Update()
    {
        AudioListener.volume = 0;
        elapsed += Time.unscaledDeltaTime;
        try
        {
            if (elapsed >= 25 && error is null)
            {
                if (client is null)
                {
                    client = new PrivateClient(Path.GetFullPath(Path.Combine(BepInEx.Paths.GameRootPath, "..", "connection.json")));
                    tuber.client.utils.TuberPrefs.SeenFirstTimeStrategicViewTutorialPrompt.SetWithoutSave(true);
                }
                client.Update(elapsed);
                if (submitted == false && File.Exists(Path.Combine(output, "submit")) && client.Offer is { Targets.Length: > 0 } offer)
                {
                    var responder = UnityEngine.Object.FindObjectOfType<TuberSelectionResponder>();
                    var target = new EntityID(offer.Targets[0]);
                    if (responder is not null && responder.HasSelection && responder.ValidateSelection(target))
                        submitted = responder.AttemptSubmitSelection(target);
                }
            }
        }
        catch (Exception failure)
        {
            error = failure.ToString();
            client?.Stop();
            Log.LogError(error);
        }
        if (elapsed >= nextReport)
        {
            nextReport = elapsed + 2;
            var entities = UnityEngine.Object.FindObjectOfType<TuberEntitiesProvider>()?.TuberEntities;
            var consumer = UnityEngine.Object.FindObjectOfType<TuberMatchMessageConsumer>();
            var json = JsonSerializer.Serialize(new
            {
                error, scene = SceneManager.GetActiveScene().name, players = entities?.allPlayers.Count,
                account = entities?.GetActiveLocalPlayerID()?.ToString(), cursor = client?.Cursor,
                received = client?.ReceivedMessages, accepted = client?.AcceptedChoices,
                queued = consumer?.messageQueue?.Count, submitted
            });
            var report = Path.Combine(output, "fleet-state.json");
            File.WriteAllText(report + ".tmp", json);
            File.Move(report + ".tmp", report, true);
        }
        var capture = Path.Combine(output, "capture");
        if (File.Exists(capture))
        {
            File.Delete(capture);
            ScreenCapture.CaptureScreenshot(Path.Combine(output, "fleet.png"));
        }
        if (error is not null || File.Exists(Path.Combine(output, "finish")) || elapsed > 900)
            Application.Quit(error is null ? 0 : 1);
    }
}
