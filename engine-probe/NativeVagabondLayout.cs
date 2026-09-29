using HarmonyLib;
using tuber.client.match.voodoo;
using UnityEngine;

namespace RootEngineProbe;

internal static class NativeVagabondLayout
{
    public static void Install(Harmony harmony)
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.RegisterTypeInIl2Cpp<VagabondBottomRow>();
        harmony.Patch(AccessTools.Method(typeof(VagabondSatchelLayout), "Start"),
            postfix: new HarmonyMethod(typeof(NativeVagabondLayout), nameof(PositionSatchel)));
    }

    private static void PositionSatchel(VagabondSatchelLayout __instance)
    {
        if (PrivateClient.Active is null || __instance.transform.TryCast<RectTransform>() is not { } satchel ||
            satchel.parent?.TryCast<RectTransform>() is not { } drawer || drawer.name != "DrawerContainer") return;
        // Keep the inventory on the same bottom row as its drawer, clear of
        // the sixth opponent. Give the hand room beside the wider inventory.
        satchel.anchoredPosition = new Vector2(drawer.rect.width + 12, 0);
        if (drawer.parent?.parent?.TryCast<RectTransform>() is { } canvas &&
            canvas.Find("Hand")?.TryCast<RectTransform>() is { } hand &&
            canvas.Find("ActionContainer")?.TryCast<RectTransform>() is { } actions)
            __instance.gameObject.AddComponent<VagabondBottomRow>().Initialize(canvas, satchel, hand, actions);
    }
}

public sealed class VagabondBottomRow : MonoBehaviour
{
    private RectTransform? canvas;
    private RectTransform? satchel;
    private RectTransform? hand;
    private RectTransform? actions;
    private lotus.FanView? fan;
    private float normalFanWidth;
    private readonly Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3> corners = new(4);

    public VagabondBottomRow(IntPtr pointer) : base(pointer) { }

    public void Initialize(RectTransform canvas, RectTransform satchel, RectTransform hand, RectTransform actions)
    {
        this.canvas = canvas;
        this.satchel = satchel;
        this.hand = hand;
        this.actions = actions;
        fan = hand.GetComponent<lotus.FanView>();
        if (fan != null) normalFanWidth = fan.maxArea;
    }

    public void LateUpdate()
    {
        if (canvas == null || satchel == null || hand == null || actions == null || fan == null) return;
        satchel.GetWorldCorners(corners);
        var left = canvas.InverseTransformPoint(corners[2]).x + 12;
        actions.GetWorldCorners(corners);
        var right = canvas.InverseTransformPoint(corners[0]).x - 12;
        // Native animations own anchoredPosition. Move the anchor instead so
        // hiding, showing, and hovering the hand retain their stock animations.
        var anchor = ((left + right) / 2 - hand.anchoredPosition.x - canvas.rect.xMin) / canvas.rect.width;
        hand.anchorMin = new Vector2(anchor, hand.anchorMin.y);
        hand.anchorMax = new Vector2(anchor, hand.anchorMax.y);
        // Reserve the width of an angled card at the ends of the fan.
        var width = Mathf.Clamp(right - left - 240, 0, normalFanWidth);
        if (Mathf.Abs(fan.maxArea - width) < .1f) return;
        fan.maxArea = width;
        fan.LayoutViews();
    }
}
