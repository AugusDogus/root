using Canis.utils.ids;
using dwd.core.match.selection;
using Networking.selection.messages;
using Networking.selection.messages.outgoing;
using Networking.selection.targetinformation;
using Networking.selection.targetresponse;

namespace RootEngineProbe;

// Exercise the complete response built by Root, including targets it omits.
internal static class NativeActionResponseProbe
{
    public static void Run(List<object> results, List<string> failures)
    {
        var source = new EntityID(Guid.NewGuid().ToString());
        var target = new EntityID(Guid.NewGuid().ToString());
        var writerObject = new UnityEngine.GameObject("Native action response test");
        try
        {
            var writer = writerObject.AddComponent<tuber.client.match.canis.TuberCanisMatch>();
            var factory = new SelectionNodeFactory();
            factory.OverrideSessionWriter(writer.Cast<dwd.core.session.ISessionWriter>());
            for (var mask = 0; mask < 8; mask++)
            {
                var information = Enumerable.Range(0, 3).Select(index => (TargetInformation)new EntityListTargetInformation
                {
                    Selected = (mask & (1 << index)) != 0, ValidTargets = new[] { target },
                    NumberToSelect = 1, MinimumToSelect = 1, Forced = true
                }).ToArray();
                var offer = new SelectionWithTargetsRequired { SourceID = source };
                offer.TargetMap.Add(source, information);
                var node = new SelectionWithTargetsNode(offer, factory.Cast<ISelectionNodeFactory>());
                node.Select(source);
                node.Current = node.Cast<ISelectionNode>();
                var next = node.NodeToAdvanceTo();
                for (var index = 0; next is not null && index < information.Length; index++)
                {
                    var child = next.Cast<EntityListTargetNode>();
                    child.Select(target);
                    node.Current = next;
                    next = child.NodeToAdvanceTo();
                }
                if (next is not null) throw new InvalidOperationException("Native target chain did not terminate.");
                var response = node.getResponseMessage(false).Cast<SelectionWithTargets>();
                var choices = response.selection.targetResponses.Select(item =>
                    (TargetChoice)new TargetChoice.Entities(item.Cast<EntityListTargetResponse>().EntityList
                        .Select(id => id.ToString()).ToArray())).ToArray();
                var actual = TargetChoice.ValidateResponse(information, choices);
                if (actual != ChoiceResult.Accepted)
                    failures.Add($"Native action response mask={mask}, targets={choices.Length}: {actual}");
                if (TargetChoice.ValidateResponse(information, choices.Append(new TargetChoice.Entities(Array.Empty<string>())).ToArray()) == ChoiceResult.Accepted)
                    failures.Add($"Action accepted an extra target response: mask={mask}");
                if (choices.Length > 0 && TargetChoice.ValidateResponse(information, choices.Skip(1).ToArray()) == ChoiceResult.Accepted)
                    failures.Add($"Action accepted a missing player-selected target: mask={mask}");
                results.Add(new { kind = "native-action-response", mask, targets = choices.Length, ours = actual.ToString() });
            }
        }
        finally { UnityEngine.Object.Destroy(writerObject); }
    }
}
