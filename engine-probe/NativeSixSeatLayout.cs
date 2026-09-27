using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace RootEngineProbe;

internal static class NativeSixSeatLayout
{
    public static SubscriptionProvider[] Extend(SubscriptionProvider[] original)
    {
        if (original.Length == 6) return original;
        var slots = original.ToList();
        var template = slots[^1];
        var row = template.transform.parent;
        if (row.GetComponent<HorizontalLayoutGroup>() is { } layout) layout.enabled = false;
        while (slots.Count < 6)
            slots.Add(Object.Instantiate(template.gameObject, row).GetComponent<SubscriptionProvider>());
        for (var index = 0; index < 6; index++)
        {
            var holder = new GameObject($"Six-player slot {index + 1}").AddComponent<RectTransform>();
            holder.SetParent(row, false);
            holder.anchorMin = holder.anchorMax = new Vector2((index + 0.5f) / 6f, 0.5f);
            holder.sizeDelta = new Vector2(290, 80);
            holder.anchoredPosition = Vector2.zero;
            holder.localScale = new Vector3(0.7f, 0.7f, 1);
            var slot = slots[index].GetComponent<RectTransform>();
            slot.SetParent(holder, false);
            slot.anchorMin = slot.anchorMax = new Vector2(0.5f, 0.5f);
            slot.anchoredPosition = Vector2.zero;
        }
        return slots.ToArray();
    }
}
