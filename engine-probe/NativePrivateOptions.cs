using TMPro;
using tuber.client.menus.prompts;
using UnityEngine;

namespace RootEngineProbe;

// Seat invitations replace passwords. Root owns the remaining game settings.
internal static class NativePrivateOptions
{
    public static void Configure(ConfigureGameDetailsPromptBehaviour view)
    {
        foreach (var input in view.GetComponentsInChildren<TMP_InputField>(true))
        {
            input.SetTextWithoutNotify("");
            input.interactable = false;
            if (input.placeholder.TryCast<TMP_Text>() is { } label) label.text = "Steam invitations only";
        }
    }
}
