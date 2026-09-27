using System.Text.Json;
using lotus;
using tuber.client.match;
using UnityEngine;
using Object = UnityEngine.Object;

namespace RootEngineProbe;

// Called only by the isolated online UI probe. It inspects public communication
// controls, never game snapshots, seat tokens, or private player state.
internal static class NativeCommunicationProbe
{
    private sealed record Point(float X, float Y);
    private sealed record Bounds(float X, float Y, float Width, float Height);
    private sealed record SendState(bool Active, bool Enabled, Point? Center);
    private sealed record MessageState(string? Name, string? Text, bool? NameRichText, bool? TextRichText,
        bool NameActive, bool SocialButtonActive, bool SocialButtonEnabled,
        bool TextActive, bool? TextCulled, float? TextAlpha, Bounds? TextBounds, Bounds? TextGlyphBounds, int MeshCharacters);
    private sealed record ChatState(string Name, string ViewPointer, bool Active, string? Provider, string? ProviderPointer, bool Initialized,
        int MessageCount, string? Input, bool? InputRichText, bool PanelCollapsed,
        SendState? SendButton, MessageState[] Rendered, Bounds? ViewportBounds, float? ScrollValue, bool ForceScrollToEnd, string? Error);
    private sealed record TimerState(string Account, string? TimerId, double? SecondsRemaining, bool? Ended, string? Error);
    private sealed record TimerManagerState(string Name, bool Active, TimerState[] Timers, string? Error);
    private sealed record TimerWidgetState(string Name, bool Active, string? TimerId, bool Running,
        float ShowTimerDuration, float Buffer, float TotalWaitTime, bool ImageActive, bool ImageEnabled,
        bool? ImageCulled, float? ImageAlpha, float? FillAmount, Bounds? ImageBounds, string? Error);
    private sealed record CommandState(long Sequence, string? Operation, bool Ok, string? Error);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static float nextCapture;
    private static CommandState? lastCommand;
    private static long commandSequence;

    public static void Update(string output)
    {
        if (Time.unscaledTime < nextCapture) return;
        nextCapture = Time.unscaledTime + 0.25f;
        var views = Object.FindObjectsOfType<ChatView>(true).ToArray();
        ProcessCommand(output, views);
        var snapshot = new
        {
            renderedFrame = Time.renderedFrameCount,
            screen = new { width = Screen.width, height = Screen.height },
            chats = views.Select(ReadChat).ToArray(),
            timerManagers = Object.FindObjectsOfType<TuberTimerManager>(true).Select(ReadTimerManager).ToArray(),
            timerWidgets = Object.FindObjectsOfType<zen.src.match.monobehaviours.AnimateTimer>(true).Select(ReadTimerWidget).ToArray(),
            command = lastCommand
        };
        AtomicWrite(Path.Combine(output, "communication-state.json"), JsonSerializer.Serialize(snapshot, JsonOptions));
    }

    private static void ProcessCommand(string output, ChatView[] views)
    {
        var path = Path.Combine(output, "communication-command.json");
        if (!File.Exists(path)) return;
        string? operation = null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            File.Delete(path);
            if (!document.RootElement.TryGetProperty("op", out var property) || property.ValueKind != JsonValueKind.String)
            {
                lastCommand = new(++commandSequence, null, false, "Communication command requires a string op.");
                return;
            }
            operation = property.GetString();
            var view = views.Where(item => item.gameObject.activeInHierarchy && item.chatProvider != null &&
                    (item.chatProvider.TryCast<GameLobbyChatProvider>() is not null || item.chatProvider.TryCast<TuberGameChatProvider>() is not null))
                .OrderByDescending(item => item.chatProvider.TryCast<TuberGameChatProvider>() is not null)
                .FirstOrDefault();
            if (view == null)
            {
                lastCommand = new(++commandSequence, operation, false, "No active native private chat view is available in this scene.");
                return;
            }
            switch (operation)
            {
                case "chat-expand": view.Event_Expand(); break;
                case "chat-collapse": view.Event_Collapse(); break;
                case "chat-input":
                    if (!document.RootElement.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String ||
                        text.GetString() is not { Length: <= 4096 } value)
                    {
                        lastCommand = new(++commandSequence, operation, false, "chat-input requires a text string of at most 4096 characters.");
                        return;
                    }
                    if (view.messageInput == null)
                    {
                        lastCommand = new(++commandSequence, operation, false, "The active native chat view has no message input.");
                        return;
                    }
                    view.messageInput.text = value;
                    break;
                default:
                    lastCommand = new(++commandSequence, operation, false, "Unknown communication command. Use chat-expand, chat-collapse, or chat-input.");
                    return;
            }
            lastCommand = new(++commandSequence, operation, true, null);
        }
        catch (Exception error)
        {
            File.Delete(path);
            lastCommand = new(++commandSequence, operation, false, Describe("Execute native communication command", error));
        }
    }

    private static ChatState ReadChat(ChatView view)
    {
        try
        {
            var provider = view.chatProvider;
            var send = view.sendButton;
            var scroll = view.GetComponentInChildren<UnityEngine.UI.ScrollRect>(true);
            return new(view.name, view.Pointer.ToString("x"), view.gameObject.activeInHierarchy, provider?.GetIl2CppType().FullName, provider?.Pointer.ToString("x"),
                provider != null && provider.Initialized, provider?.Messages?.Count ?? 0,
                view.messageInput?.text, view.messageInput?.richText, view.panelCollapsed,
                send == null ? null : new(send.gameObject.activeInHierarchy, send.interactable, ButtonCenter(send)),
                view.chatMessages == null ? Array.Empty<MessageState>() : view.chatMessages.ToArray()
                    .Where(message => message != null).Select(ReadMessage).ToArray(),
                scroll == null || scroll.viewport == null ? null : ScreenBounds(scroll.viewport),
                view.scrollBar?.value, view.forceScrollToEnd, null);
        }
        catch (Exception error)
        {
            return new(view.name, view.Pointer.ToString("x"), view.gameObject.activeInHierarchy, null, null, false, 0, null, null,
                view.panelCollapsed, null, Array.Empty<MessageState>(), null, null, view.forceScrollToEnd, Describe("Read native chat view", error));
        }
    }

    private static MessageState ReadMessage(ChatMessageView message)
    {
        var named = message.TryCast<tuber.client.chat.TuberChatMessageView>();
        var text = message.messageField;
        var renderer = text == null ? null : text.canvasRenderer;
        return new(named?.nameField?.text, message.messageField?.text, named?.nameField?.richText,
            message.messageField?.richText, named != null && named.nameField != null && named.nameField.gameObject.activeInHierarchy,
            named != null && named.playerChatSocialButton != null && named.playerChatSocialButton.gameObject.activeInHierarchy,
            named != null && named.playerChatSocialButton != null && named.playerChatSocialButton.interactable,
            text != null && text.gameObject.activeInHierarchy, renderer?.cull,
            text == null || renderer == null ? null : text.color.a * renderer.GetAlpha() * renderer.GetInheritedAlpha(),
            text == null ? null : ScreenBounds(text.rectTransform), text == null ? null : GlyphBounds(text), text?.textInfo?.characterCount ?? 0);
    }

    private static Bounds? GlyphBounds(TMPro.TMP_Text text)
    {
        var bounds = text.textBounds;
        return ScreenBounds(text.rectTransform, new Rect(bounds.min.x, bounds.min.y, bounds.size.x, bounds.size.y));
    }

    private static Bounds? ScreenBounds(RectTransform rect, Rect? extent = null)
    {
        if (Screen.width <= 0 || Screen.height <= 0) return null;
        var canvas = rect.GetComponentInParent<Canvas>();
        var camera = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        var local = extent ?? rect.rect;
        var corners = new[] { new Vector3(local.xMin, local.yMin, 0), new Vector3(local.xMin, local.yMax, 0),
            new Vector3(local.xMax, local.yMin, 0), new Vector3(local.xMax, local.yMax, 0) }
            .Select(corner => RectTransformUtility.WorldToScreenPoint(camera, rect.TransformPoint(corner))).ToArray();
        var left = corners.Min(point => point.x);
        var right = corners.Max(point => point.x);
        var bottom = corners.Min(point => point.y);
        var top = corners.Max(point => point.y);
        return new(left * 1280f / Screen.width, (Screen.height - top) * 800f / Screen.height,
            (right - left) * 1280f / Screen.width, (top - bottom) * 800f / Screen.height);
    }

    private static Point? ButtonCenter(UnityEngine.UI.Button button)
    {
        var rect = button.GetComponent<RectTransform>();
        if (rect == null || Screen.width <= 0 || Screen.height <= 0) return null;
        var canvas = button.GetComponentInParent<Canvas>();
        var camera = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        var center = rect.rect.center;
        var point = RectTransformUtility.WorldToScreenPoint(camera, rect.TransformPoint(new Vector3(center.x, center.y, 0)));
        return new(point.x * 1280f / Screen.width, (Screen.height - point.y) * 800f / Screen.height);
    }

    private static TimerManagerState ReadTimerManager(TuberTimerManager manager)
    {
        try
        {
            if (manager.accountTimers is null)
                return new(manager.name, manager.gameObject.activeInHierarchy, Array.Empty<TimerState>(), "Native timer manager accountTimers is not initialized.");
            var timers = new List<TimerState>();
            foreach (var pair in manager.accountTimers)
            {
                var account = pair.Key.ToString();
                try
                {
                    var timer = pair.Value.GetOne<dwd.canis.PlayerTimerData>();
                    if (timer is null)
                    {
                        timers.Add(new(account, null, null, null, "Native timer composition has no PlayerTimerData."));
                        continue;
                    }
                    timers.Add(new(account, timer.timer?.TimerID?.ToString(), timer.TimeRemaining.TotalSeconds, timer.Ended, null));
                }
                catch (Exception error)
                {
                    timers.Add(new(account, null, null, null, Describe($"Read PlayerTimerData for account {account}", error)));
                }
            }
            return new(manager.name, manager.gameObject.activeInHierarchy, timers.ToArray(), null);
        }
        catch (Exception error)
        {
            return new(manager.name, manager.gameObject.activeInHierarchy, Array.Empty<TimerState>(), Describe("Read native timer manager", error));
        }
    }

    private static TimerWidgetState ReadTimerWidget(zen.src.match.monobehaviours.AnimateTimer view)
    {
        try
        {
            var id = view.TimerID?.ToString();
            var image = view.timerImage;
            var renderer = image == null ? null : image.canvasRenderer;
            return new(view.name, view.isActiveAndEnabled, id, id is not null && view.Running(),
                view.showTimerDuration, view.buffer, view.totalWaitTime,
                image != null && image.gameObject.activeInHierarchy, image != null && image.enabled,
                renderer?.cull, image == null || renderer == null ? null : image.color.a * renderer.GetAlpha() * renderer.GetInheritedAlpha(),
                image?.fillAmount, image == null ? null : ScreenBounds(image.rectTransform), null);
        }
        catch (Exception error)
        {
            return new(view.name, view.isActiveAndEnabled, null, false, 0, 0, 0, false, false,
                null, null, null, null, Describe("Read native timer widget", error));
        }
    }

    private static string Describe(string operation, Exception error) => $"{operation} failed: {error.GetType().Name}: {error.Message}";

    private static void AtomicWrite(string path, string content)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, content);
        File.Move(temporary, path, true);
    }
}
