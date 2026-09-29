using BepInEx.Logging;
using Canis.json;
using System.Text.Json;

namespace RootEngineProbe;

internal static class SetupProbe
{
    public static void Run(ManualLogSource log)
    {
        var output = Path.GetFullPath(Path.Combine(BepInEx.Paths.GameRootPath, "..", "results", "setup"));
        Directory.CreateDirectory(output);
        var traceFile = Path.Combine(output, "trace.txt");
        File.WriteAllText(traceFile, "");
        var completedRound = false;
        var startedRound = false;
        var turnSeats = new HashSet<int>();
        var accepted = 0;
        try
        {
            var host = new HostedMatch(Environment.GetEnvironmentVariable("ROOT_LAB_PLAYERS") == "6");
            for (var step = 0; step < 100; step++)
            {
                var advanced = false;
                for (var seat = 0; seat < host.PlayerCount; seat++)
                {
                    var counter = host.Thread.GetCounterForPlayerEntity(host.Match.Players[seat]);
                    if (host.Thread.HasPendingResponse(counter) == false) continue;
                    var selection = host.Thread.GetPlayerPendingResponse(counter).Item2.Selection;
                    if (seat == 0 && selection.Prompt.ID.EndsWith(".BuyRiverfolkServices"))
                    {
                        if (startedRound && turnSeats.Count == host.PlayerCount)
                        {
                            completedRound = true;
                            break;
                        }
                        startedRound = true;
                    }
                    if (startedRound) turnSeats.Add(seat + 1);
                    File.AppendAllText(traceFile, $"PENDING step {step} seat {seat + 1}: {JSON.ToJSON(selection, false)}\n");
                    var offer = host.GetOffer(seat);
                    var result = selection.Prompt.ID.EndsWith(".NoActions") ? host.Pass(seat, counter)
                        : offer is { Targets.Length: > 0 }
                        ? host.Choose(seat, counter, offer.Source, offer.Targets[0])
                        : host.Pass(seat, counter);
                    if (result == ChoiceResult.UnsupportedSelection)
                    {
                        var integer = selection.TryCast<Networking.selection.messages.IntChoiceRequired>();
                        result = host.ChooseCustom(seat, counter, integer?.min ?? 0);
                    }
                    if (result == ChoiceResult.UnsupportedSelection)
                        result = host.SetPrices(seat, counter, 1, 1, 1);
                    if (result == ChoiceResult.UnsupportedSelection &&
                        selection.TryCast<Networking.selection.messages.SelectionWithTargetsRequired>() is { } required)
                    {
                        foreach (var pair in required.TargetMap)
                        {
                            var selected = pair.Value.Where(target => target.Selected).ToArray();
                            if (selected.Length != 1 || selected[0].TryCast<Networking.selection.targetinformation.EntityListTargetInformation>()
                                is not { NumberToSelect: 1, ValidTargets.Length: > 0 } target) continue;
                            result = host.Choose(seat, counter, pair.Key.ToString(), target.ValidTargets[0].ToString());
                            break;
                        }
                    }
                    File.AppendAllText(traceFile, $"RESULT: {result}\n");
                    if (result == ChoiceResult.Accepted) { accepted++; advanced = true; break; }
                }
                if (completedRound || advanced == false) break;
            }
            log.LogInfo("SETUP: trace complete");
        }
        catch (Exception error) { log.LogError($"SETUP FAILED: {error}"); }
        File.WriteAllText(Path.Combine(output, "test-results.json"), JsonSerializer.Serialize(new
        {
            status = completedRound ? "passed" : "failed", completedRound, acceptedDecisions = accepted,
            turnSeats = turnSeats.OrderBy(seat => seat).ToArray()
        }, new JsonSerializerOptions { WriteIndented = true }));
        UnityEngine.Application.Quit(completedRound ? 0 : 1);
    }
}
