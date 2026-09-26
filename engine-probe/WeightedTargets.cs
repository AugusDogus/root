using Canis.utils.ids;
using dwd.core.match.selection;
using Networking.selection.targetinformation;

namespace RootEngineProbe;

internal static class WeightedTargets
{
    public static ChoiceResult Validate(KnapsackEntityListTargetInformation information, string[] values)
    {
        if (values.Length > information.ValidTargets.Count || values.Distinct().Count() != values.Length)
            return ChoiceResult.InvalidTarget;
        // Weighted choices have mode-specific completion and availability rules.
        // Replay them in an unattached native control to use the same rules as
        // Root's UI, without a session writer or any access to match authority.
        var following = new Il2CppSystem.Collections.Generic.List<TargetInformation>();
        var node = new KnapsackEntityListTargetNode(null, information, false, null, null,
            following.Cast<Il2CppSystem.Collections.Generic.IEnumerable<TargetInformation>>(), "private-validation");
        foreach (var value in values)
        {
            var id = new EntityID(value);
            if (node.AvailableSelections.IndexOf(id) < 0) return ChoiceResult.InvalidTarget;
            node.Select(id);
        }
        return node.satisfied ? ChoiceResult.Accepted : ChoiceResult.InvalidTarget;
    }
}
