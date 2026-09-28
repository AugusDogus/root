using System.Text.Json;
using Networking.selection.messages;
using Networking.selection.targetinformation;

namespace RootEngineProbe;

// Run against a private checkpoint copy, without ticking its historical timers.
internal static class ExploreRecoveryProbe
{
    public static void Run(string fixture, string output)
    {
        var host = MatchCheckpoint.Load(fixture);
        try
        {
            var seat = Enumerable.Range(0, host.PlayerCount).Single(index =>
            {
                var counter = host.Thread.GetCounterForPlayerEntity(host.SeatPlayers[index]);
                return host.Thread.HasPendingResponse(counter) &&
                    host.Thread.GetPlayerPendingResponse(counter).Item2.Selection.TryCast<SelectionWithTargetsRequired>()?.TargetType == "ExploreItemChoice";
            });
            var counter = host.Thread.GetCounterForPlayerEntity(host.SeatPlayers[seat]);
            var selection = host.Thread.GetPlayerPendingResponse(counter).Item2.Selection.Cast<SelectionWithTargetsRequired>();
            var targets = new List<string>();
            foreach (var pair in selection.TargetMap[selection.SourceID][0].Cast<KnapsackEntityListTargetInformation>().ValidTargets)
                targets.Add(pair.Key.ToString());
            if (targets.Count != 2) throw new InvalidOperationException("Expected a two-item Explore checkpoint.");
            var visible = Enumerable.Range(0, host.PlayerCount).Select(index =>
            {
                using var snapshot = JsonDocument.Parse(host.Snapshot(index)[0]);
                var entities = snapshot.RootElement.GetProperty("value").GetProperty("msg").GetProperty("value").GetProperty("entities");
                return targets.Count(id => Contains(entities, id));
            }).ToArray();
            File.WriteAllText(Path.Combine(output, "explore-visibility.json"), JsonSerializer.Serialize(new { seat, visible, obfuscated = host.Match.IsObfuscated() }));
            if (visible[seat] != targets.Count) throw new InvalidOperationException("The explorer's snapshot is missing ruin items.");
            if (visible.Where((_, index) => index != seat).Any(count => count != 0))
                throw new InvalidOperationException("Unexplored ruin items leaked to another seat.");
            var choice = host.ChooseTargets(seat, counter, selection.SourceID.ToString(),
                new TargetChoice[] { new TargetChoice.Entities(new[] { targets[0] }) });
            if (choice != ChoiceResult.Accepted) throw new InvalidOperationException($"Explore choice failed: {choice}");
            File.WriteAllText(Path.Combine(output, "test-results.json"), JsonSerializer.Serialize(new { status = "passed", explore = true }));
        }
        finally { host.Timers.Stop(); }
    }

    private static bool Contains(JsonElement entity, string id) =>
        entity.GetProperty("entityID").GetString() == id ||
        entity.TryGetProperty("children", out var children) && children.EnumerateArray().Any(child => Contains(child, id));
}
