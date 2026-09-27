using Canis.json;
using tuber.client.data;
using tuber.client.menus.prompts;
using UnityEngine;
using Object = UnityEngine.Object;

namespace RootEngineProbe;

// Runs only in the isolated native setup test, without creating a match.
public sealed class NativeSetupProbe : MonoBehaviour
{
    private NativeSetupFlow? native;
    private bool captured;
    private bool failed;
    private readonly string output = Path.GetFullPath(Path.Combine(BepInEx.Paths.GameRootPath, "..", "results", "native-setup-probe"));
    public NativeSetupProbe(IntPtr pointer) : base(pointer) { }
    public void Start() { Directory.CreateDirectory(output); QualitySettings.SetQualityLevel(0, true); Application.targetFrameRate = 10; }
    public void Update()
    {
        AudioListener.volume = 0;
        if (failed) return;
        try
        {
            if (native is null && Environment.GetEnvironmentVariable("ROOT_LAB_MODE") == "native-setup-probe")
            {
                if (Object.FindObjectOfType<LandingPromptBehaviour>() == null) return;
                native = new NativeSetupFlow(result =>
                {
                    File.WriteAllText(Path.Combine(output, "configured.json"), JSON.ToJSON(result, false));
                    failed = true;
                }, () => { File.WriteAllText(Path.Combine(output, "configured.json"), "null"); failed = true; });
                native.Show();
            }
            if (!captured && Object.FindObjectOfType<ConfigureOfflineGamePromptBehaviour>() is { } view)
            {
                File.WriteAllText(Path.Combine(output, "view.txt"), $"Prefab slots: {view.playerSlots.Length}\n" +
                    string.Join("\n", view.GetComponentsInChildren<Transform>(true).Select(item => item.name)));
                File.WriteAllText(Path.Combine(output, "options.json"), System.Text.Json.JsonSerializer.Serialize(view.gameOptions.Select(option => new { option.name, option.value, choices = option.possibleChoices.Select(choice => choice.choiceName).ToArray() }).ToArray()));
                captured = true;
            }
            if (captured && Object.FindObjectOfType<ConfigureOfflineGamePromptBehaviour>() is { } setup)
            {
                var figures = Object.FindObjectsOfType<ConfigureGamePlayerSlot>();
                var visible = figures.Count(figure => figure.GetComponentsInChildren<Renderer>().Any(renderer => renderer.isVisible));
                File.WriteAllText(Path.Combine(output, "visual.json"), System.Text.Json.JsonSerializer.Serialize(new
                {
                    slots = setup.playerSlots.Length, visibleFigures = visible,
                    title = setup.GetComponentsInChildren<TMPro.TMP_Text>(true).FirstOrDefault(item => item.name == "HeaderText")?.text
                }));
            }
            if (captured && Object.FindObjectOfType<ConfigureOfflineGamePromptBehaviour>() is { } active && File.Exists(Path.Combine(output, "command.txt")))
            {
                var command = File.ReadAllText(Path.Combine(output, "command.txt")).Trim();
                File.Delete(Path.Combine(output, "command.txt"));
                if (command == "create") active.Event_CreateGame();
                if (command == "dump") File.WriteAllText(Path.Combine(output, "slots.txt"), string.Join("\n", Enumerable.Range(0, active.Prompt.PlayerSlots.Count).Select(index => active.Prompt.PlayerSlots[index]).Select(slot => string.Join(";", slot.components.ToArray().Select(component => component.GetIl2CppType().FullName + ": " + component.ToString())))));
                if (command == "layout")
                {
                    var nodes = active.playerSlots.SelectMany(slot => new[] { slot.transform, slot.transform.parent })
                        .Concat(Object.FindObjectsOfType<ConfigureGamePlayerSlot>().SelectMany(slot => new[] { slot.transform, slot.transform.parent, slot.transform.parent.parent }));
                    File.WriteAllText(Path.Combine(output, "layout.txt"), string.Join("\n", nodes.DistinctBy(item => item.Pointer).Select(item =>
                        $"{item.name} pos={item.localPosition} scale={item.localScale} rect={item.TryCast<RectTransform>()?.rect} anchors={item.TryCast<RectTransform>()?.anchorMin}/{item.TryCast<RectTransform>()?.anchorMax} components=" +
                        string.Join(",", item.GetComponents<Component>().Select(component => component.GetIl2CppType().FullName)))));
                }
                if (command.StartsWith("{", StringComparison.Ordinal))
                {
                    using var settings = System.Text.Json.JsonDocument.Parse(command);
                    if (settings.RootElement.TryGetProperty("options", out var options))
                    {
                        foreach (var option in options.EnumerateObject()) active.SelectOptionByValue(option.Name, option.Value.GetInt32());
                        active.Event_ConfirmAllSettingsChanges();
                    }
                    if (settings.RootElement.TryGetProperty("factions", out var factions))
                        for (var index = 0; index < factions.GetArrayLength(); index++)
                        {
                            active.Prompt.PlayerSlots[index].GetOne<tuber.client.match.data.FactionData>().Faction = new Il2CppSystem.Nullable<tuber_canis.data.Factions>((tuber_canis.data.Factions)factions[index].GetInt32());
                            if (MatchSetup.IsClockwork(factions[index].GetInt32()))
                                active.Prompt.PlayerSlots[index].GetOne<lib.src.data.ClockworkConfigData>().Reset(new Il2CppSystem.Nullable<tuber_canis.data.Factions>((tuber_canis.data.Factions)factions[index].GetInt32()));
                        }
                    if (settings.RootElement.TryGetProperty("controllers", out var controllers))
                        for (var index = 0; index < controllers.GetArrayLength(); index++)
                            active.Prompt.PlayerSlots[index].GetOne<PlayerTypeData>().Type = (PlayerTypeData.PlayerType)controllers[index].GetInt32();
                }
            }
            native?.Update();
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(output, "error.txt"), error.ToString()); failed = true; }
    }
}
