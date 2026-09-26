using BepInEx.Logging;

namespace RootEngineProbe;

internal static class HostProbe
{
    public static void Run(ManualLogSource log)
    {
        var output = Path.GetFullPath(Path.Combine(BepInEx.Paths.GameRootPath, "..", "results", "host"));
        Directory.CreateDirectory(output);
        var sixPlayers = Environment.GetEnvironmentVariable("ROOT_LAB_PLAYERS") == "6";
        using var report = new StreamWriter(Path.Combine(output, sixPlayers ? "host-6.txt" : "host-4.txt")) { AutoFlush = true };
        void Record(string text)
        {
            report.WriteLine(text);
            log.LogInfo($"HOST: {text}");
        }

        try
        {
            var host = new HostedMatch(sixPlayers);
            Record($"Started: players={host.Match.Players.Count}; entities={host.Match.Entities.Count}; gameType={host.Match.GetGameType()}");
            var offer = host.GetOffer(0);
            if (offer is null || offer.Prompt.EndsWith("MarquiseDeCatSetup.ChooseStarting") == false || offer.Targets.Length == 0)
                throw new InvalidOperationException("Expected a keep-placement offer with legal targets.");
            var result = host.Choose(0, offer.Counter, offer.Source, offer.Targets[0]);
            var next = host.GetOffer(0);
            if (result != ChoiceResult.Accepted || next is null || next.Counter == offer.Counter)
                throw new InvalidOperationException($"Keep placement did not advance to a new offer: {result}.");
            Record($"Accepted keep placement; next counter={next.Counter}; next prompt={next.Prompt}; entities={host.Match.Entities.Count}");
            for (var i = 0; i < host.PlayerCount; i++)
                File.WriteAllLines(Path.Combine(output, $"messages-{host.PlayerCount}-seat-{i + 1}.jsonl"), host.Messages[i]);
            Record("PASS");
        }
        catch (Exception error)
        {
            Record($"FAILED: {error}");
            UnityEngine.Application.Quit(1);
            return;
        }
        UnityEngine.Application.Quit();
    }
}
