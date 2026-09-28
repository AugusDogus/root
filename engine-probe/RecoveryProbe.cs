using BepInEx.Logging;
using System.Text.Json;

namespace RootEngineProbe;

internal static class RecoveryProbe
{
    public static void Run(ManualLogSource log)
    {
        var output = Environment.GetEnvironmentVariable("ROOT_LAB_OUTPUT") ?? throw new InvalidOperationException("Missing probe output");
        Directory.CreateDirectory(output);
        try
        {
            if (Environment.GetEnvironmentVariable("ROOT_LAB_RECOVERY_FIXTURE") is { } fixture)
            {
                ExploreRecoveryProbe.Run(fixture, output);
                UnityEngine.Application.Quit();
                return;
            }
            var host = new HostedMatch(true);
            if (!host.Match.IsObfuscated()) throw new InvalidOperationException("The private authority disabled native reveal actions.");
            var offer = host.GetOffer(0) ?? throw new InvalidOperationException("Missing setup offer");
            if (host.Choose(0, offer.Counter, offer.Source, offer.Targets[0]) != ChoiceResult.Accepted)
                throw new InvalidOperationException("Initial placement failed");
            var checkpoint = Path.Combine(output, "checkpoint.json");
            MatchCheckpoint.Save(host, checkpoint);
            File.WriteAllText(Path.Combine(output, "before.json"), JsonSerializer.Serialize(Enumerable.Range(0, 6).Select(host.Snapshot)));
            var restored = MatchCheckpoint.Load(checkpoint);
            File.WriteAllText(Path.Combine(output, "after.json"), JsonSerializer.Serialize(Enumerable.Range(0, 6).Select(restored.Snapshot)));
            var next = restored.GetOffer(0) ?? throw new InvalidOperationException("No restored setup offer");
            var choice = restored.Choose(0, next.Counter, next.Source, next.Targets[0]);
            if (choice != ChoiceResult.Accepted) throw new InvalidOperationException($"Restored choice failed: {choice}");
            File.WriteAllText(Path.Combine(output, "test-results.json"), JsonSerializer.Serialize(new { status = "passed", saveData = host.Match.SaveData is not null, next.Prompt }));
        }
        catch (Exception error)
        {
            log.LogError(error);
            File.WriteAllText(Path.Combine(output, "test-results.json"), JsonSerializer.Serialize(new { status = "failed", error = error.ToString() }));
            UnityEngine.Application.Quit(1);
            return;
        }
        UnityEngine.Application.Quit();
    }
}
