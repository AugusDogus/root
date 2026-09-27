using System.Text.Json;
using dwd.core.match;
using lib.src.match.prompt.behaviours;
using tuber.client.match.behaviours;
using tuber.client.match.prompt.prompts;
using tuber.client.prompt;
using tuber.client.prompt.commands;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace RootEngineProbe;

// Development-only presentation fixtures. Never submits moves or records hands.
internal static class NativeUiAuditProbe
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static SelectAPlayerPrompt? selection;
    private static Lib.src.match.prompt.prompts.RiverfolkSetPricesPrompt? priceSelection;
    private static bool enabled;
    private static float next;
    private static int sequence;
    private static string? error;

    public static void Update(string output)
    {
        var command = Path.Combine(output, "ui-audit-command.json");
        if (!enabled && !File.Exists(command)) return;
        enabled = true;
        if (Time.unscaledTime < next) return;
        next = Time.unscaledTime + .25f;
        if (File.Exists(command))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(command));
                File.Delete(command);
                Execute(document.RootElement);
                error = null;
            }
            catch (Exception exception) { error = exception.ToString(); }
            sequence++;
        }
        var info = Object.FindObjectOfType<PlayerInformationPromptBehaviour>();
        var selector = Object.FindObjectOfType<SelectAPlayerPromptBehaviour>();
        var prices = Object.FindObjectOfType<Lib.src.match.prompt.behaviours.RiverfolkSetPricesPromptBehaviour>();
        var snapshot = new
        {
            sequence, error, frame = Time.renderedFrameCount, width = Screen.width, height = Screen.height,
            platform = dwd.core.platformdependent.PlatformUtil.CurrentPlatformAsString,
            toolbar = Toolbar(),
            prices = prices == null ? null : new
            {
                rows = new[] { prices.handCardToggles, prices.riverboatsToggles, prices.mercenariesToggles }
                    .Select(row => row.Select(toggle => new { toggle.isOn, control = Button(toggle) }).ToArray()).ToArray()
            },
            pricesResolved = priceSelection?.Resolved ?? false,
            diagnosticsButton = Button(GameObject.Find("Root Six Player Menu")?.GetComponentsInChildren<Button>()
                .FirstOrDefault(button => button.name == "Copy diagnostics")),
            playtestProducts = PrivatePlaytestContent.Products.Select(product => new
            {
                product = product.ToString(),
                requiresPurchase = lib.data.TuberProductUtils.ProductRequiresPurchase(product),
                gameplay = tuber_canis.data.TuberIAPUtilities.UserOwnsProduct(product),
                menu = Object.FindObjectOfType<dwd.iap.store.IAPStoreBehaviour>()?.UserOwnsProduct(lib.data.TuberProductUtils.ArchIDForProduct(product)) ?? false
            }).ToArray(),
            panels = Object.FindObjectsOfType<PlayerUILoader>().Select(loader => new
            {
                count = loader.currentUI.Count,
                views = Enumerable.Range(0, loader.currentUI.Count).Select(index => loader.currentUI[index]).Select(view => new { view.name, active = view.gameObject.activeInHierarchy }).ToArray()
            }).ToArray(),
            info = info == null ? null : new { count = info.currentPlayerInfoPrefabs.Count, index = info.CurrentIndex, view = info.currentShownPlayerInfo?.name,
                initialized = info.Initialized, active = info.currentShownPlayerInfo?.activeInHierarchy,
                next = Button(info.rightButton), previous = Button(info.leftButton) },
            selector = selector == null ? null : new
            {
                choices = selector.Prompt.Choices.Count,
                slots = selector.playerSlots.Where(slot => slot.gameObject.activeInHierarchy).Select(slot => new
                {
                    faction = (int)slot.faction, button = Button(slot.gameObject.GetComponentInChildren<Button>())
                }).ToArray()
            },
            selectionResolved = selection?.Resolved ?? false,
            selectionMatchesLast = selection is { Resolved: true } selected && selected.Result?.ToString() == selected.Choices[selected.Choices.Count - 1].EntityID.ToString()
        };
        var path = Path.Combine(output, "ui-audit-state.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(snapshot, JsonOptions));
        File.Move(path + ".tmp", path, true);
    }

    private static void Execute(JsonElement command)
    {
        switch (command.GetProperty("op").GetString())
        {
            case "capture": break;
            case "back-to-board":
                var menu = GameObject.Find("Root Six Player Menu");
                if (menu == null) throw new InvalidOperationException("The private match menu is missing.");
                menu.GetComponentsInChildren<Button>().Single(button => button.name == "Back to game").onClick.Invoke();
                break;
            case "resize":
                // Use Root's confirmation hook so its normal timeout does not
                // revert the test resolution while another dialog is open.
                var watcher = Object.FindObjectOfType<tuber.client.menus.TuberWatchForScreenSettingsChanged>();
                if (watcher == null) throw new InvalidOperationException("Root's screen settings watcher is missing.");
                watcher.AutoConfirmNextScreenChange();
                Screen.SetResolution(command.GetProperty("width").GetInt32(), command.GetProperty("height").GetInt32(), false);
                break;
            case "info-open":
                Object.FindObjectsOfType<tuber.client.match.ui.PlayerInformationButton>()
                    .First(button => button.gameObject.activeInHierarchy).Event_ShowInformation();
                break;
            case "info-next": Object.FindObjectOfType<PlayerInformationPromptBehaviour>().Event_Increment(); break;
            case "info-close": Object.FindObjectOfType<PlayerInformationPromptBehaviour>().Prompt.Dismiss(); break;
            case "prices-open":
                var current = new Il2CppSystem.Collections.Generic.Dictionary<tuber_canis.data.RiverfolkService, int>();
                foreach (var service in new[] { tuber_canis.data.RiverfolkService.HandCard, tuber_canis.data.RiverfolkService.Riverboats, tuber_canis.data.RiverfolkService.Mercenaries })
                    current.Add(service, 2);
                var required = new Il2CppSystem.Collections.Generic.List<Il2CppSystem.Collections.Generic.KeyValuePair<tuber_canis.data.RiverfolkService, int>>();
                // Native SetRiverfolkPricesCommand supplies this lookup flavor.
                var priceTags = new Il2CppSystem.Collections.Generic.List<string>();
                priceTags.Add("RiverfolkSetPrices");
                priceSelection = new Lib.src.match.prompt.prompts.RiverfolkSetPricesPrompt(new dwd.core.data.composition.DataComposition(
                    new dwd.core.data.composition.DataComponent[] { new dwd.core.data.composition.BasicName("Set service prices") }),
                    current.Cast<Il2CppSystem.Collections.Generic.IReadOnlyCollection<Il2CppSystem.Collections.Generic.KeyValuePair<tuber_canis.data.RiverfolkService, int>>>(),
                    required.Cast<Il2CppSystem.Collections.Generic.IReadOnlyCollection<Il2CppSystem.Collections.Generic.KeyValuePair<tuber_canis.data.RiverfolkService, int>>>(),
                    priceTags.Cast<Il2CppSystem.Collections.Generic.IEnumerable<string>>());
                dwd.core.commands.CommandExecutor.Get().Execute(new DisplayTuberPrompt(TuberModalScope.PlayerInfo,
                    priceSelection.Cast<dwd.core.ui.prompt.prompts.IPrompt>(), new TuberPromptDisplayData(true, true)));
                break;
            case "prices-close": Object.FindObjectOfType<Lib.src.match.prompt.behaviours.RiverfolkSetPricesPromptBehaviour>().Event_Resolve(); break;
            case "select-open":
                var entities = Object.FindObjectOfType<TuberEntitiesProvider>().TuberEntities;
                var choices = new Il2CppSystem.Collections.Generic.List<EntityComponent>();
                for (var index = 0; index < entities.AllPlayers.Count; index++) choices.Add(entities.AllPlayers[index]);
                selection = new SelectAPlayerPrompt(new dwd.core.data.composition.DataComposition(), choices, true,
                    new Il2CppSystem.Collections.Generic.List<string>().Cast<Il2CppSystem.Collections.Generic.IEnumerable<string>>());
                var display = new DisplayTuberPrompt(TuberModalScope.PlayerInfo,
                    selection.Cast<dwd.core.ui.prompt.prompts.IPrompt>(), new TuberPromptDisplayData(true, true));
                dwd.core.commands.CommandExecutor.Get().Execute(display);
                break;
            default: throw new InvalidDataException("Unknown UI audit command.");
        }
    }

    private static object[] Toolbar()
    {
        var root = GameObject.Find("Root Six Player Menu");
        return root == null ? Array.Empty<object>() : root.GetComponentsInChildren<Button>(true)
            .Where(button => button.name is "Invite friends" or "Match")
            .Select(button => (object)new { button.name, active = button.gameObject.activeInHierarchy, bounds = Button(button) }).ToArray();
    }

    private static object? Button(Selectable? button)
    {
        if (button == null) return null;
        var rectangle = button.GetComponent<RectTransform>();
        var canvas = button.GetComponentInParent<Canvas>();
        var camera = canvas?.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas?.worldCamera;
        var center = RectTransformUtility.WorldToScreenPoint(camera, rectangle.TransformPoint(rectangle.rect.center));
        var corners = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>(4);
        rectangle.GetWorldCorners(corners);
        var points = corners.Select(point => RectTransformUtility.WorldToScreenPoint(camera, point)).ToArray();
        var left = points.Min(p => p.x);
        var right = points.Max(p => p.x);
        var bottom = points.Min(p => p.y);
        var top = points.Max(p => p.y);
        var hits = new Il2CppSystem.Collections.Generic.List<RaycastResult>();
        EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = center }, hits);
        return new
        {
            x = center.x, y = Screen.height - center.y, width = points.Max(p => p.x) - points.Min(p => p.x),
            height = points.Max(p => p.y) - points.Min(p => p.y), enabled = button.interactable,
            visibleWidth = Math.Max(0, Math.Min(Screen.width, right) - Math.Max(0, left)),
            visibleHeight = Math.Max(0, Math.Min(Screen.height, top) - Math.Max(0, bottom)),
            inside = points.All(p => p.x >= 0 && p.x <= Screen.width && p.y >= 0 && p.y <= Screen.height),
            reachable = hits.Count > 0 && hits[0].gameObject.transform.IsChildOf(button.transform)
        };
    }
}
