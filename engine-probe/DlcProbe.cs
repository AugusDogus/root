using System.Text.Json;
using BepInEx.Logging;
using Canis.json;
using tuber_canis.data;

namespace RootEngineProbe;

internal static class DlcProbe
{
    public static void Run(ManualLogSource log)
    {
        var directory = Path.GetFullPath(Path.Combine(BepInEx.Paths.GameRootPath, "..", "results", "dlc"));
        Directory.CreateDirectory(directory);
        try
        {
            var init = HostedMatch.CreateInitialization(true);
            var report = new
            {
                factions = Enum.GetValues<Factions>().Where(lib.data.FactionUtils.ReleasedPlayerFactions.Contains).Select(value => new { id = (int)value, name = value.ToString() }).ToArray(),
                hirelings = Enum.GetValues<Factions>().Where(lib.data.FactionUtils.ReleasedHirelings.Contains).Select(value => new { id = (int)value, name = value.ToString() }).ToArray(),
                exclusions = Enum.GetValues<Factions>().Where(lib.data.FactionUtils.mutuallyExclusiveFactions.ContainsKey).Select(value => new { faction = (int)value, excludes = lib.data.FactionUtils.mutuallyExclusiveFactions[value].Select(other => (int)other).ToArray() }).ToArray(),
                initialization = JsonSerializer.Deserialize<JsonElement>(JSON.ToJSON(init, false))
            };
            File.WriteAllText(Path.Combine(directory, "catalog.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            log.LogInfo("DLC catalog captured");
        }
        catch (Exception error) { log.LogError(error); UnityEngine.Application.Quit(1); return; }
        UnityEngine.Application.Quit();
    }
}
