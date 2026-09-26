using BepInEx.Logging;
using UnityEngine;
using HarmonyLib;

namespace RootEngineProbe;

// Yield between requests so the native engine's async continuations can run.
public sealed class AuthorityBehaviour : MonoBehaviour
{
    private static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("Private Authority");
    private IEnumerator<object?>? server;
    public AuthorityBehaviour(IntPtr pointer) : base(pointer) { }
    public static void Install()
    {
        // The rules process has no client session. Root's normal batch-mode
        // startup tears it down; keep Unity's update loop without that bootstrap.
        new Harmony("local.root.authority").Patch(AccessTools.Method(typeof(tuber.client.TuberGameInit), "Start"),
            prefix: new HarmonyMethod(typeof(AuthorityBehaviour), nameof(SkipClientStartup)));
    }
    private static bool SkipClientStartup() => false;
    public void Start()
    {
        UnityEngine.Object.DontDestroyOnLoad(gameObject);
        Application.targetFrameRate = 60;
        server = LoopbackProbe.Run(Log, routine => StartCoroutine(routine));
    }
    public void Update()
    {
        if (server is null) return;
        try { if (server.MoveNext()) return; }
        catch (Exception error) { Log.LogError($"SERVER FAILED: {error}"); Application.Quit(1); }
        server.Dispose(); server = null;
    }
    public void OnDestroy() { server?.Dispose(); server = null; }
}
