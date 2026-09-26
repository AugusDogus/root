using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace RootSmokeTest;

[BepInPlugin("local.root.smoketest", "Root Smoke Test", "1.0.0")]
public sealed class Plugin : BasePlugin
{
    public override void Load()
    {
        Log.LogInfo($"SMOKE: plugin loaded; Unity={Application.unityVersion}; game={Application.version}");
        AddComponent<SmokeTestBehaviour>();
    }
}

public sealed class SmokeTestBehaviour : MonoBehaviour
{
    private static readonly ManualLogSource TestLog = BepInEx.Logging.Logger.CreateLogSource("Root Smoke Test Callback");
    private float elapsed;
    private bool observedUpdate;

    public SmokeTestBehaviour(IntPtr pointer) : base(pointer) { }

    public void Update()
    {
        if (!observedUpdate)
        {
            TestLog.LogInfo("SMOKE: injected MonoBehaviour.Update executed");
            observedUpdate = true;
        }

        elapsed += Time.unscaledDeltaTime;
        if (elapsed < 15f)
            return;

        TestLog.LogInfo($"SMOKE: PASS; active scene={SceneManager.GetActiveScene().name}; exiting test copy");
        enabled = false;
        Application.Quit();
    }
}
