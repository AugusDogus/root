using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Unity.IL2CPP;

namespace RootEngineProbe;

[BepInPlugin("local.root.engineprobe", "Root Engine Probe", "0.7.4")]
public sealed class Plugin : BasePlugin
{
    public override void Load()
    {
        if (Environment.GetEnvironmentVariable("ROOT_LAB_MODE") == "chat-probe")
        {
            AuthorityBehaviour.Install();
            AddComponent<NativeChatProbe>();
            return;
        }
        if (Environment.GetEnvironmentVariable("ROOT_LAB_MODE") == "timer-probe")
        {
            AuthorityBehaviour.Install();
            AddComponent<NativeTurnTimerProbe>();
            return;
        }
        if (Environment.GetEnvironmentVariable("ROOT_LAB_MODE") == "online-setup-probe")
        {
            AddComponent<NativeOnlineSetupProbe>();
            return;
        }
        if (Environment.GetEnvironmentVariable("ROOT_LAB_MODE") == "steam-menu-test")
        {
            AddComponent<SteamSessionBehaviour>();
            AddComponent<NativeOnlineSetupProbe>();
            return;
        }
        if (Environment.GetEnvironmentVariable("ROOT_LAB_MODE") == "steam-online-test")
        {
            AddComponent<SteamSessionBehaviour>();
            AddComponent<NativeOnlineSetupProbe>();
            return;
        }
        if (Environment.GetEnvironmentVariable("ROOT_LAB_MODE") == "native-setup-probe")
        {
            AddComponent<NativeSetupProbe>();
            return;
        }
        if (Environment.GetEnvironmentVariable("ROOT_LAB_MODE") == "dlc-probe")
        {
            DlcProbe.Run(Log);
            return;
        }
        if (Environment.GetEnvironmentVariable("ROOT_LAB_MODE") is "steam-host" or "steam-client" or "steam-wait" or "steam-menu")
        {
            AddComponent<SteamSessionBehaviour>();
            return;
        }
        if (Environment.GetEnvironmentVariable("ROOT_LAB_MODE") == "steam-probe")
        {
            AddComponent<SteamworksProbe>();
            return;
        }
        if (Environment.GetEnvironmentVariable("ROOT_LAB_MODE") == "bootstrap")
        {
            Log.LogInfo("Private launcher bindings ready");
            UnityEngine.Application.Quit();
            return;
        }
        if (Environment.GetEnvironmentVariable("ROOT_LAB_MODE") == "recovery-tests")
        {
            RecoveryProbe.Run(Log);
            return;
        }
        if (Environment.GetEnvironmentVariable("ROOT_LAB_MODE") == "selection-tests")
        {
            SelectionValidationProbe.Run(Log);
            return;
        }
        if (Environment.GetEnvironmentVariable("ROOT_LAB_MODE") == "client")
        {
            AddComponent<PrivateClientBehaviour>();
            return;
        }
        if (Environment.GetEnvironmentVariable("ROOT_LAB_MODE") == "setup")
        {
            SetupProbe.Run(Log);
            return;
        }
        if (Environment.GetEnvironmentVariable("ROOT_LAB_MODE") is "client-probe" or "steam-client-probe")
        {
            if (Environment.GetEnvironmentVariable("ROOT_LAB_FLEET_TEST") == "1")
                AddComponent<FleetProbe>();
            else
                AddComponent<ClientProbe>();
            return;
        }
        if (Environment.GetEnvironmentVariable("ROOT_LAB_MODE") is "server" or "lobby-data-tests")
        {
            AuthorityBehaviour.Install();
            AddComponent<AuthorityBehaviour>();
            return;
        }
        if (Environment.GetEnvironmentVariable("ROOT_LAB_MODE") == "host")
        {
            HostProbe.Run(Log);
            return;
        }
        var output = Path.GetFullPath(Path.Combine(BepInEx.Paths.GameRootPath, "..", "results", "inspect"));
        Directory.CreateDirectory(output);
        var report = new StringBuilder();
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().OrderBy(a => a.FullName))
        {
            if (assembly.GetName().Name is not ("Assembly-CSharp" or "Canis" or "core-canis" or "tuber-canis" or "tuber-client" or "boardgames" or "Networking" or "Utils" or "Attributes" or "DwdJson" or "core-commands" or "platform" or "platformCore"))
                continue;
            foreach (var type in assembly.GetTypes().Where(IsRelevant).OrderBy(t => t.FullName))
            {
                report.AppendLine($"TYPE {type.FullName} : {type.BaseType}");
                const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
                foreach (var field in type.GetFields(flags))
                    report.AppendLine($"  FIELD {field}");
                foreach (var property in type.GetProperties(flags))
                    report.AppendLine($"  PROPERTY {property}");
                foreach (var constructor in type.GetConstructors(flags))
                    report.AppendLine($"  CTOR {constructor}");
                foreach (var method in type.GetMethods(flags).Where(m => m.IsSpecialName == false))
                    report.AppendLine($"  METHOD {method}");
                if (type.IsEnum)
                    foreach (var value in Enum.GetValues(type))
                        report.AppendLine($"  ENUM {value}={Convert.ToInt64(value)}");
                report.AppendLine();
            }
        }
        File.WriteAllText(Path.Combine(output, "api.txt"), report.ToString());
        NativeMethods.Write(output);
        Log.LogInfo($"PROBE: API inventory written to {output}");
        UnityEngine.Application.Quit();
    }

    private static bool IsRelevant(Type type)
    {
        var name = type.FullName ?? "";
        return name is "tuber.canis.TuberMatch" or "Canis.Match" or "Canis.MatchInitData" or "Canis.PlayerMatchInitData"
            || name.StartsWith("tuber.canis.data.matchinitdata.")
            || name.StartsWith("tuber_canis.data.")
            || name.StartsWith("tuber.data.")
            || name.StartsWith("Canis.messageRouters.")
            || name.StartsWith("Canis.matchThread.")
            || name.StartsWith("Canis.messages.sequence.")
            || name.StartsWith("Networking.selection.messages.")
            || name.StartsWith("Networking.selection.targetresponse.")
            || name.StartsWith("Networking.selection.targetinformation.")
            || name.StartsWith("Canis.json.")
            || name is "dwd.core.data.ReflectionTypeInitializer"
            || name is "Canis.data.PendingSelection" or "Canis.messages.SerializedGameState" or "Canis.SaveData"
            || name.Contains("MatchInitData")
            || name.Contains("MessageActionFactory")
            || type.Name is "AccountID" or "GameID" or "MatchType" or "Faction" or "AILevel"
            || name is "Canis.boardgames.CanisMatch" or "tuber.client.match.commands.PlayOfflineMatch"
            || name.StartsWith("Networking.game.")
            || name.StartsWith("tuber.client.match.")
            || name.StartsWith("Canis.boardgames.")
            || name.StartsWith("boardgames.match.")
            || name.StartsWith("dwd.core.match.")
            || name.StartsWith("dwd.core.commands.")
            || name.StartsWith("dwd.core.account.")
            || name.StartsWith("dwd.core.session.")
            || name is "tuber.client.TuberGameInit" or "GameManagers"
            || name.StartsWith("dwd.core.data.composition.")
            || name.StartsWith("dwd.core.platform.websocket.")
            || name.StartsWith("tuber.client.menus.commands.")
            || name.StartsWith("lotus.");
    }
}
