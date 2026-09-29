using System.Text.Json;
using BepInEx.Logging;
using Canis.utils.ids;
using Networking.selection.targetinformation;
using dwd.core.match.selection;

namespace RootEngineProbe;

// Compare host validation with the game's native selection control, without a match.
internal static class SelectionValidationProbe
{
    public static void Run(ManualLogSource log)
    {
        var output = Path.Combine(BepInEx.Paths.GameRootPath, "..", "results", "selection-tests");
        Directory.CreateDirectory(output);
        var results = new List<object>();
        var failures = new List<string>();
        try
        {
            NumericChoiceProbe.Run(results, failures);
            var ids = Enumerable.Range(0, 3).Select(_ => new EntityID(Guid.NewGuid().ToString())).ToArray();
            foreach (var forced in new[] { false, true })
            foreach (var minimum in new[] { -1, 0, 1, 2, 4 })
            foreach (var maximum in new[] { 0, 1, 2, 4 })
            foreach (var offered in new[] { Array.Empty<EntityID>(), ids.Take(1).ToArray(), ids, new[] { ids[0], ids[0], ids[1] } })
            for (var count = 0; count <= offered.Length; count++)
            {
                var information = new EntityListTargetInformation
                { ValidTargets = offered, NumberToSelect = maximum, MinimumToSelect = minimum, Forced = forced };
                var following = new Il2CppSystem.Collections.Generic.List<TargetInformation>();
                var node = new EntityListTargetNode(null, information, false, null, null,
                    following.Cast<Il2CppSystem.Collections.Generic.IEnumerable<TargetInformation>>(), "optional-target-probe");
                var available = true;
                foreach (var id in offered.Take(count))
                {
                    if (node.AvailableSelections.IndexOf(id) < 0) { available = false; break; }
                    node.Select(id);
                }
                var actual = new TargetChoice.Entities(offered.Take(count).Select(id => id.ToString()).ToArray()).Validate(information);
                var nativeAccepts = available && node.satisfied;
                if ((actual == ChoiceResult.Accepted) != nativeAccepts)
                    failures.Add($"Entity choice forced={forced} minimum={minimum} maximum={maximum} offered={offered.Length} count={count}: native={nativeAccepts}, host={actual}");
                results.Add(new { kind = "entities", forced, minimum, maximum, offered = offered.Length, count, nativeAccepts, ours = actual.ToString() });
            }
            var attributed = new CustomChoiceWithAttributesTargetInformation
            {
                Choices = new[] { new Canis.attributes.SerializableAttributes(), new Canis.attributes.SerializableAttributes() },
                NumberToSelect = 1, MinimumToSelect = 1, Forced = true
            };
            for (var index = -1; index <= 2; index++)
            {
                var expected = index is 0 or 1;
                if (expected)
                {
                    var following = new Il2CppSystem.Collections.Generic.List<TargetInformation>();
                    var node = new CustomChoiceWithAttributesTargetInfoNode(null, attributed, false, null, null,
                        following.Cast<Il2CppSystem.Collections.Generic.IEnumerable<TargetInformation>>(), "probe");
                    node.Select(index);
                    if (!node.satisfied) failures.Add($"Native attributed choice {index} was not satisfied");
                }
                if ((new TargetChoice.Number(index).Validate(attributed) == ChoiceResult.Accepted) != expected)
                    failures.Add($"Attributed choice {index} violated bounds");
            }
            var groupSets = new[]
            {
                new[] {
                    new EntityGroupingTargetInformation.Grouping { Items = new[] { ids[0], ids[1] } },
                    new EntityGroupingTargetInformation.Grouping { Items = new[] { ids[2] } }
                },
                new[] {
                    new EntityGroupingTargetInformation.Grouping { Items = new[] { ids[0], ids[1] } },
                    new EntityGroupingTargetInformation.Grouping { Items = new[] { ids[0], ids[2] } },
                    new EntityGroupingTargetInformation.Grouping { Items = new[] { ids[1], ids[2] } }
                }
            };
            foreach (var groups in groupSets)
            foreach (var forced in new[] { false, true })
            foreach (var minimum in new[] { 0, 1, 2 })
            foreach (var maximum in new[] { 1, 2 })
            for (var mask = 0; mask < 1 << groups.Length; mask++)
            {
                var information = new EntityGroupingTargetInformation { ValidTargets = groups, NumberToSelect = maximum, MinimumToSelect = minimum, Forced = forced };
                var following = new Il2CppSystem.Collections.Generic.List<TargetInformation>();
                var node = new tc.selection.EntityGroupingTargetNode(null, information, false, null, null,
                    following.Cast<Il2CppSystem.Collections.Generic.IEnumerable<TargetInformation>>(), "probe");
                var available = true;
                for (var index = 0; index < groups.Length; index++)
                    if ((mask & (1 << index)) != 0)
                    {
                        if (!Enumerable.Range(0, node.Available.Count).Any(i => node.Available[i].Pointer == groups[index].Pointer)) { available = false; break; }
                        node.Select(groups[index]);
                    }
                var chosen = groups.Where((_, index) => (mask & (1 << index)) != 0).SelectMany(group => group.Items.Select(id => id.ToString())).ToArray();
                var actual = new TargetChoice.Entities(chosen).Validate(information);
                if ((actual == ChoiceResult.Accepted) != (available && node.satisfied)) failures.Add($"Grouping forced={forced} minimum={minimum} maximum={maximum} groups={groups.Length} mask={mask}: native={available && node.satisfied}, host={actual}");
                results.Add(new { kind = "groups", forced, minimum, maximum, groups = groups.Length, mask, nativeAccepts = available && node.satisfied, ours = actual.ToString() });
                if (new TargetChoice.Entities(new[] { ids[0].ToString() }).Validate(information) != ChoiceResult.InvalidTarget)
                    failures.Add("Grouping accepted a partial group");
            }
            var recruit = new EntityListTargetInformation
            {
                ValidTargets = new[] { ids[0], ids[0], ids[1] }, NumberToSelect = 2, MinimumToSelect = 2, Forced = true
            };
            foreach (var (indexes, expected) in new[]
            {
                (new[] { 0, 0 }, ChoiceResult.Accepted), (new[] { 0, 1 }, ChoiceResult.Accepted),
                (new[] { 1, 1 }, ChoiceResult.InvalidTarget), (new[] { 0, 0, 0 }, ChoiceResult.InvalidTarget),
                (new[] { 0 }, ChoiceResult.InvalidTarget), (new[] { 0, 2 }, ChoiceResult.InvalidTarget)
            })
            {
                var actual = new TargetChoice.Entities(indexes.Select(index => ids[index].ToString()).ToArray()).Validate(recruit);
                if (actual != expected) failures.Add($"Recruit [{string.Join(',', indexes)}]: expected {expected}, got {actual}");
            }
            foreach (var mode in new[] { KnapsackSelectionMode.AsMuchAsPossibleButNoMoreThan, KnapsackSelectionMode.AsLittleAsPossibleButNoLessThan })
            foreach (var forced in new[] { false, true })
            for (var mask = 0; mask < 8; mask++)
            {
                var weights = new Il2CppSystem.Collections.Generic.Dictionary<EntityID, int>();
                for (var index = 0; index < ids.Length; index++) weights.Add(ids[index], index + 2);
                var information = new KnapsackEntityListTargetInformation
                {
                    ValidTargets = weights, TargetWeight = 5, Forced = forced, knapsackSelectionMode = mode
                };
                var following = new Il2CppSystem.Collections.Generic.List<TargetInformation>();
                var node = new KnapsackEntityListTargetNode(null, information, false, null, null,
                    following.Cast<Il2CppSystem.Collections.Generic.IEnumerable<TargetInformation>>(), "probe");
                var chosen = ids.Where((_, index) => (mask & (1 << index)) != 0).ToArray();
                var available = true;
                foreach (var id in chosen)
                {
                    if (node.AvailableSelections.IndexOf(id) < 0) { available = false; break; }
                    node.Select(id);
                }
                var nativeAccepts = available && node.satisfied;
                var ours = new TargetChoice.Entities(chosen.Select(id => id.ToString()).ToArray()).Validate(information);
                if ((ours == ChoiceResult.Accepted) != nativeAccepts)
                    failures.Add($"Weighted {mode} forced={forced} mask={mask}: native={nativeAccepts}, host={ours}");
                results.Add(new { mode = mode.ToString(), forced, mask, nativeAccepts, ours = ours.ToString(),
                    nativeWeight = node.CurrentlySelectedWeight(), nativeCount = node.selectedEnts.Count,
                    remaining = node.unselectedEnts.Count, node.minAvailableWeight, node.maxAvailableWeight });
            }
            var weightedTargets = new Il2CppSystem.Collections.Generic.Dictionary<EntityID, int>();
            weightedTargets.Add(ids[0], 2);
            weightedTargets.Add(ids[1], 3);
            weightedTargets.Add(ids[2], 4);
            var weighted = new KnapsackEntityListTargetInformation
            {
                ValidTargets = weightedTargets, TargetWeight = 5, Forced = true,
                knapsackSelectionMode = KnapsackSelectionMode.AsMuchAsPossibleButNoMoreThan
            };
            foreach (var (indexes, expected) in new[]
            {
                (new[] { 0, 1 }, ChoiceResult.Accepted), (new[] { 2 }, ChoiceResult.Accepted),
                (new[] { 0 }, ChoiceResult.InvalidTarget), (new[] { 0, 0 }, ChoiceResult.InvalidTarget),
                (new[] { 1, 2 }, ChoiceResult.InvalidTarget), (Array.Empty<int>(), ChoiceResult.InvalidTarget)
            })
            {
                var actual = new TargetChoice.Entities(indexes.Select(index => ids[index].ToString()).ToArray()).Validate(weighted);
                if (actual != expected) failures.Add($"Weighted [{string.Join(',', indexes)}]: expected {expected}, got {actual}");
            }
        }
        catch (Exception error) { failures.Add(error.ToString()); log.LogError($"SELECTION TEST FAILED: {error}"); }
        try
        {
            foreach (var choice in new int?[] { null, 0, 2 })
            {
                var nativeChoice = new dwd.core.match.messages.outgoing.GameCustomChoice(new GameID(Guid.NewGuid().ToString()),
                    choice is int value ? new Il2CppSystem.Nullable<int>(value) : new Il2CppSystem.Nullable<int>(), 42);
                results.Add(new { customChoice = Canis.json.JSON.ToJSON(nativeChoice, false) });
                JsonElement sent = default;
                var client = new PrivateClient("test-token", request =>
                {
                    sent = JsonSerializer.SerializeToElement(request);
                    return Task.FromResult(JsonSerializer.SerializeToElement(new { ok = true }));
                });
                PrivateClient.SendChoice(nativeChoice);
                client.Update(0);
                if (client.Notice is not null || sent.GetProperty("op").GetString() != "custom" ||
                    sent.GetProperty("counter").GetInt32() != 42 || sent.GetProperty("choice").Deserialize<int?>() != choice)
                    failures.Add($"Native custom choice {choice?.ToString() ?? "decline"} was not forwarded correctly");
                client.Stop();
            }
        }
        catch (Exception error) { failures.Add(error.ToString()); log.LogError($"CUSTOM CHOICE TEST FAILED: {error}"); }
        try
        {
            JsonElement sent = default;
            var client = new PrivateClient("test-token", request =>
            {
                sent = JsonSerializer.SerializeToElement(request);
                return Task.FromResult(JsonSerializer.SerializeToElement(new { ok = true }));
            });
            PrivateClient.SendChoice(new zen.src.matchMaking.messages.ResignPBMGame(new GameID(Guid.NewGuid().ToString())));
            client.Update(0);
            if (client.Notice is not null || sent.GetProperty("op").GetString() != "resign")
                failures.Add("The native resignation was not forwarded to the private host");
            client.DiscardQueuedMoves();
            if (client.Transitioning || !client.ConnectionInterrupted)
                failures.Add("A lost resignation acknowledgement left the client waiting for a table update");
            client.Resign();
            if (client.Transitioning || client.Notice is null)
                failures.Add("A disconnected client attempted another resignation");
            client.Stop();
        }
        catch (Exception error) { failures.Add(error.ToString()); log.LogError($"RESIGNATION TEST FAILED: {error}"); }
        File.WriteAllText(Path.Combine(output, "native-results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(output, "test-results.json"), JsonSerializer.Serialize(new { status = failures.Count == 0 ? "passed" : "failed", failures }));
        UnityEngine.Application.Quit(failures.Count == 0 ? 0 : 1);
    }
}
