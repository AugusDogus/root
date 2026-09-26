using Networking.selection.targetinformation;

namespace RootEngineProbe;

internal static class GroupedTargets
{
    public static ChoiceResult Validate(EntityGroupingTargetInformation information, string[] values)
    {
        var remaining = values.GroupBy(id => id).ToDictionary(group => group.Key, group => group.Count());
        var following = new Il2CppSystem.Collections.Generic.List<TargetInformation>();
        var node = new tc.selection.EntityGroupingTargetNode(null, information, false, null, null,
            following.Cast<Il2CppSystem.Collections.Generic.IEnumerable<TargetInformation>>(), "private-validation");
        var budget = 4096;
        bool Search(int left)
        {
            if (--budget < 0) return false;
            if (left == 0) return node.satisfied;
            foreach (var group in Enumerable.Range(0, node.Available.Count).Select(index => node.Available[index]).ToArray())
            {
                var counts = group.Items.Select(id => id.ToString()).GroupBy(id => id)
                    .ToDictionary(items => items.Key, items => items.Count());
                if (counts.Count == 0 || counts.Any(item => !remaining.TryGetValue(item.Key, out var count) || count < item.Value)) continue;
                foreach (var item in counts) remaining[item.Key] -= item.Value;
                node.Select(group);
                if (Search(left - group.Items.Length)) return true;
                node.Unselect(group);
                foreach (var item in counts) remaining[item.Key] += item.Value;
                if (budget < 0) return false;
            }
            return false;
        }
        return Search(values.Length) ? ChoiceResult.Accepted
            : budget < 0 ? ChoiceResult.UnsupportedSelection : ChoiceResult.InvalidTarget;
    }
}
