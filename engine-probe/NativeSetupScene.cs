using tuber.client.menus.prompts;
using tuber.client.menus.behaviours;
using UnityEngine;
using Object = UnityEngine.Object;

namespace RootEngineProbe;

// The animated faction figures live in a separate native scene from the cards.
internal sealed class NativeSetupScene : IDisposable
{
    private readonly ConfigureGameScenePromptBehaviour view;
    private readonly SubscriptionProvider[] original;
    private readonly Vector3[] positions;
    private readonly Vector3[] scales;
    private readonly GameObject[] added;
    private readonly PlayerSlotSpacingRenderer[] spacing;

    public NativeSetupScene(ConfigureGameScenePromptBehaviour view)
    {
        this.view = view;
        original = view.playerSlots.ToArray();
        positions = original.Select(slot => slot.transform.localPosition).ToArray();
        scales = original.Select(slot => slot.transform.localScale).ToArray();
        spacing = view.GetComponentsInChildren<PlayerSlotSpacingRenderer>(true).Where(item => item.enabled).ToArray();
        foreach (var item in spacing) item.enabled = false;
        var slots = original.ToList();
        var template = original[^1];
        added = Enumerable.Range(0, 6 - original.Length)
            .Select(_ => Object.Instantiate(template.gameObject, template.transform.parent)).ToArray();
        slots.AddRange(added.Select(item => item.GetComponent<SubscriptionProvider>()));
        view.playerSlots = slots.ToArray();
        var margin = (positions[^1] - positions[0]) * 0.05f;
        for (var index = 0; index < 6; index++)
        {
            slots[index].transform.localPosition = Vector3.Lerp(positions[0] - margin, positions[^1] + margin, index / 5f);
            slots[index].transform.localScale = scales[0] * 0.7f;
        }
    }

    public void Dispose()
    {
        if (view == null) return;
        view.playerSlots = original;
        for (var index = 0; index < original.Length; index++)
            if (original[index] != null)
            {
                original[index].transform.localPosition = positions[index];
                original[index].transform.localScale = scales[index];
            }
        foreach (var item in added) if (item != null) Object.Destroy(item);
        foreach (var item in spacing) if (item != null) item.enabled = true;
    }
}
