using BepInEx.Logging;
using UnityEngine;

namespace RootEngineProbe;

// Manual-play mode. The separate ClientProbe contains all automated moves.
public sealed class PrivateClientBehaviour : MonoBehaviour
{
    private static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("Private Match");
    private PrivateClient? client;
    private float elapsed;
    private string? errorMessage;

    public PrivateClientBehaviour(IntPtr pointer) : base(pointer) { }

    public void Start()
    {
        if (Environment.GetEnvironmentVariable("ROOT_FRIENDS_LAUNCHER") == "1")
        {
            Application.targetFrameRate = 60;
            return;
        }
        QualitySettings.SetQualityLevel(0, true);
        QualitySettings.shadows = ShadowQuality.Disable;
        QualitySettings.antiAliasing = 0;
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = 20;
    }

    public void Update()
    {
        if (Environment.GetEnvironmentVariable("ROOT_FRIENDS_LAUNCHER") != "1")
            AudioListener.volume = 0;
        elapsed += Time.unscaledDeltaTime;
        if (elapsed < 25 || errorMessage is not null) return;
        try
        {
            if (client is null)
            {
                var file = Path.GetFullPath(Path.Combine(BepInEx.Paths.GameRootPath, "..", "connection.json"));
                client = new PrivateClient(file);
            }
            client.Update(elapsed);
        }
        catch (Exception error)
        {
            client?.Stop();
            Log.LogError($"Private connection stopped: {error}");
            errorMessage = "Private connection stopped. Check the host and restart this client. The host keeps the match while it is running.";
        }
    }

    public void OnGUI()
    {
        if (errorMessage is not null)
            GUI.Box(new Rect(20, 20, 620, 70), errorMessage);
    }
}
