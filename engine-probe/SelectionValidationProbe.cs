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
            var ids = Enumerable.Range(0, 3).Select(_ => new EntityID(Guid.NewGuid().ToString())).ToArray();
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
            var groups = new[]
            {
                new EntityGroupingTargetInformation.Grouping { Items = new[] { ids[0], ids[1] } },
                new EntityGroupingTargetInformation.Grouping { Items = new[] { ids[2] } }
            };
            foreach (var forced in new[] { false, true })
            for (var mask = 0; mask < 4; mask++)
            {
                var information = new EntityGroupingTargetInformation { ValidTargets = groups, NumberToSelect = 1, MinimumToSelect = 0, Forced = forced };
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
                if ((actual == ChoiceResult.Accepted) != (available && node.satisfied)) failures.Add($"Grouping forced={forced} mask={mask}: native={available && node.satisfied}, host={actual}");
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
        File.WriteAllText(Path.Combine(output, "native-results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(output, "test-results.json"), JsonSerializer.Serialize(new { status = failures.Count == 0 ? "passed" : "failed", failures }));
        UnityEngine.Application.Quit(failures.Count == 0 ? 0 : 1);
    }
}
