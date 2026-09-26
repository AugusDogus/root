using System.Text.Json;
using Canis.utils.ids;
using Networking.selection.targetinformation;
using Networking.selection.targetresponse;

namespace RootEngineProbe;

internal abstract record TargetChoice
{
    public abstract ChoiceResult Validate(TargetInformation information);
    public abstract TargetResponse ToNative();
    public abstract object ToWire();

    public sealed record Entities(string[] Values) : TargetChoice
    {
        public override ChoiceResult Validate(TargetInformation information)
        {
            if (information.TryCast<KnapsackEntityListTargetInformation>() is { } weighted)
                return WeightedTargets.Validate(weighted, Values);
            var target = information.TryCast<EntityListTargetInformation>();
            if (target is null || target.NumberToSelect < 0) return ChoiceResult.UnsupportedSelection;
            // Repeated IDs are offered intentionally, for example recruiting
            // multiple warriors in a clearing containing multiple recruiters.
            var valid = target.ValidTargets.Select(id => id.ToString()).GroupBy(id => id)
                .ToDictionary(group => group.Key, group => group.Count());
            var maximum = Math.Min(target.NumberToSelect, target.ValidTargets.Length);
            var minimum = target.Forced ? maximum : target.MinimumToSelect;
            return Values.Length >= minimum && Values.Length <= maximum &&
                Values.GroupBy(id => id).All(group => valid.TryGetValue(group.Key, out var count) && group.Count() <= count)
                ? ChoiceResult.Accepted : ChoiceResult.InvalidTarget;
        }
        public override TargetResponse ToNative() => new EntityListTargetResponse(Values.Select(value => new EntityID(value)).ToArray());
        public override object ToWire() => new { kind = "entities", values = Values };
    }

    public sealed record Number(int Value) : TargetChoice
    {
        public override ChoiceResult Validate(TargetInformation information)
        {
            if (information.TryCast<XTargetInformation>() is { } range)
                return Value >= range.Min && Value <= range.Max ? ChoiceResult.Accepted : ChoiceResult.InvalidTarget;
            if (information.TryCast<CustomChoiceTargetInformation>() is { } choice)
                return Value >= 0 && Value < choice.Choices.Length ? ChoiceResult.Accepted : ChoiceResult.InvalidTarget;
            return ChoiceResult.UnsupportedSelection;
        }
        public override TargetResponse ToNative() => new IntTargetResponse(Value);
        public override object ToWire() => new { kind = "number", value = Value };
    }

    public static bool TryParse(JsonElement element, out TargetChoice[] choices)
    {
        choices = Array.Empty<TargetChoice>();
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > 16) return false;
        var parsed = new List<TargetChoice>();
        foreach (var target in element.EnumerateArray())
        {
            if (target.ValueKind != JsonValueKind.Object || target.TryGetProperty("kind", out var kind) == false ||
                kind.ValueKind != JsonValueKind.String) return false;
            if (kind.GetString() == "entities")
            {
                if (target.TryGetProperty("values", out var values) == false || values.ValueKind != JsonValueKind.Array ||
                    values.GetArrayLength() > 256) return false;
                var ids = new List<string>();
                foreach (var value in values.EnumerateArray())
                {
                    if (value.ValueKind != JsonValueKind.String || Guid.TryParse(value.GetString(), out var id) == false) return false;
                    ids.Add(id.ToString());
                }
                parsed.Add(new Entities(ids.ToArray()));
            }
            else if (kind.GetString() == "number" && target.TryGetProperty("value", out var value) &&
                value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
                parsed.Add(new Number(number));
            else return false;
        }
        choices = parsed.ToArray();
        return true;
    }
}
