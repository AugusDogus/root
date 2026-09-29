using Networking.selection.targetinformation;
using Networking.selection.targetresponse;
using dwd.core.match.selection;

namespace RootEngineProbe;

// Exercise numeric responses produced by native controls. Canceling an
// unselected control is a root selection response, not a numeric response.
internal static class NumericChoiceProbe
{
    public static void Run(List<object> results, List<string> failures)
    {
        foreach (var forced in new[] { false, true })
        foreach (var minimum in new[] { 0, 1, 3 })
        foreach (var maximum in new[] { minimum, minimum + 3 })
        for (var selected = minimum; selected <= maximum; selected++)
        {
            var information = new XTargetInformation { Min = minimum, Max = maximum, Forced = forced };
            var following = new Il2CppSystem.Collections.Generic.List<TargetInformation>();
            var node = new XTargetNode(null, information, false, null, null,
                following.Cast<Il2CppSystem.Collections.Generic.IEnumerable<TargetInformation>>(), "numeric-probe");
            node.Select(selected);
            if (node.satisfied)
                Check(information, node.getResponse(), $"range forced={forced} min={minimum} max={maximum} selected={selected}", results, failures);
            foreach (var invalid in new[] { minimum - 1, maximum + 1 })
                if (new TargetChoice.Number(invalid).Validate(information) == ChoiceResult.Accepted)
                    failures.Add($"Numeric range accepted out-of-range value {invalid} for {minimum}..{maximum}");
        }
        foreach (var forced in new[] { false, true })
        foreach (var minimum in new[] { -1, 0, 1 })
        for (var selected = 0; selected <= 1; selected++)
        {
            var information = new CustomChoiceWithAttributesTargetInformation
            {
                Choices = new[] { new Canis.attributes.SerializableAttributes(), new Canis.attributes.SerializableAttributes() },
                NumberToSelect = 1, MinimumToSelect = minimum, Forced = forced
            };
            var following = new Il2CppSystem.Collections.Generic.List<TargetInformation>();
            var node = new CustomChoiceWithAttributesTargetInfoNode(null, information, false, null, null,
                following.Cast<Il2CppSystem.Collections.Generic.IEnumerable<TargetInformation>>(), "numeric-probe");
            node.Select(selected);
            if (node.satisfied)
                Check(information, node.getResponse(), $"attributed forced={forced} min={minimum} selected={selected}", results, failures);
        }
    }

    private static void Check(TargetInformation information, TargetResponse response, string description,
        List<object> results, List<string> failures)
    {
        if (response.TryCast<IntTargetResponse>() is not { } number)
        {
            failures.Add($"Native numeric control emitted {response.GetIl2CppType().FullName}: {description}");
            return;
        }
        var actual = new TargetChoice.Number(number.Amount).Validate(information);
        if (actual != ChoiceResult.Accepted)
            failures.Add($"Native numeric response {number.Amount} rejected as {actual}: {description}");
        results.Add(new { kind = "numeric", description, number = number.Amount, ours = actual.ToString() });
    }
}
