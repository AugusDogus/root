using System.Diagnostics;
using System.Text.Json;
using RootEngineProbe;

internal static class PrivateSessionTests
{
    public static void Run()
    {
        static void Check(bool value, string detail) { if (!value) throw new Exception(detail); }
        static void Reject(Action action)
        {
            try { action(); }
            catch (Exception error) when (error is IOException or InvalidDataException) { return; }
            throw new Exception("Invalid private session input accepted");
        }
        var directory = Path.Combine(Path.GetTempPath(), "root-session-" + Guid.NewGuid().ToString("N"));
        var environment = new Dictionary<string, string?>
        {
            ["DOORSTOP_DISABLE"] = "TRUE", ["DOORSTOP_INITIALIZED"] = "TRUE", ["BEPINEX_GAME_ASSEMBLY_PATH"] = "client",
            ["IL2CPP_INTEROP_DATABASES_LOCATION"] = "client", ["ROOT_LAB_MODE"] = "steam-menu", ["SteamAppId"] = "965580", ["WINEPREFIX"] = "owned-prefix"
        };
        PrivateSession.ClearInheritedRuntime(environment);
        Check(environment.Count == 2 && environment["SteamAppId"] == "965580" && environment["WINEPREFIX"] == "owned-prefix",
            "Child host inherited the parent's loader state or lost its Steam/Proton context");
        Directory.CreateDirectory(Path.Combine(directory, "saves"));
        try
        {
            var save = Path.Combine(directory, "saves", "match.json");
            File.WriteAllText(save, "preserve this checkpoint");
            Check(PrivateSession.SavePath(directory, "match.json") == save, "Saved match path changed");
            Check(Path.GetDirectoryName(PrivateSession.SavePath(directory, null)) == Path.Combine(directory, "saves"), "New save escaped data directory");
            foreach (var name in new[] { "../match.json", "..\\match.json", save, "missing.json", "", "match.txt" })
                Reject(() => PrivateSession.SavePath(directory, name));
            var session = new PrivateSession(Path.Combine(directory, "client"));
            Reject(() => session.StartHost("match.json", new MatchSetup(), null).GetAwaiter().GetResult());
            session.Stop().GetAwaiter().GetResult();
            session.Stop().GetAwaiter().GetResult();
            Check(File.ReadAllText(save) == "preserve this checkpoint", "Failed host start changed save");
            var tokens = Enumerable.Range(1, 6).Select(value => value.ToString("x32")).ToArray();
            var text = JsonSerializer.Serialize(new { port = 32000, tokens, controlToken = new string('a', 32), setup = new { }, pending = true });
            Check(AuthorityEndpoint.Parse(text).Pending, "Pending lobby marker lost");
            Reject(() => AuthorityEndpoint.Parse(text.Replace(tokens[1], tokens[0])));
            Reject(() => AuthorityEndpoint.Parse(text.Replace("32000", "0")));
            Reject(() => AuthorityEndpoint.Parse(text.Replace(new string('a', 32), new string('z', 32))));
        }
        finally { Directory.Delete(directory, true); }
        var priorId = Environment.GetEnvironmentVariable("ROOT_LAB_PARENT_PID");
        var priorStart = Environment.GetEnvironmentVariable("ROOT_LAB_PARENT_START");
        try
        {
            using var current = Process.GetCurrentProcess();
            Environment.SetEnvironmentVariable("ROOT_LAB_PARENT_PID", current.Id.ToString());
            Environment.SetEnvironmentVariable("ROOT_LAB_PARENT_START", current.StartTime.ToUniversalTime().Ticks.ToString());
            using var live = new AuthorityOwner();
            Check(live.Alive, "Current game owner rejected");
            Environment.SetEnvironmentVariable("ROOT_LAB_PARENT_START", "1");
            using var recycled = new AuthorityOwner();
            Check(!recycled.Alive, "Recycled owner PID accepted");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ROOT_LAB_PARENT_PID", priorId);
            Environment.SetEnvironmentVariable("ROOT_LAB_PARENT_START", priorStart);
        }
        Console.WriteLine("PASS: mod-owned session boundaries, save preservation, owner identity, and cleanup after failed startup");
    }
}
