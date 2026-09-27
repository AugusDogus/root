using Canis.utils.ids;
using HarmonyLib;
using lotus;
using tuber.client.match;
using UnityEngine;
using Object = UnityEngine.Object;

namespace RootEngineProbe;

// Keep Root's chat views and replace their provider's service boundary only.
// This object belongs to one private connection, including its lobby-to-board transition.
internal sealed class NativeChat : IDisposable
{
    private sealed class Binding
    {
        public ChatProvider Provider { get; }
        public long Revision = -1;
        public long LastMessage;
        public bool Initializing;
        public bool ScrollAfterSend;
        public IntPtr PublishedList;
        public List<IntPtr> MessagePointers { get; } = new();
        public Binding(ChatProvider provider) { Provider = provider; }
    }

    private static NativeChat? active;
    private static bool patched;
    private readonly Action<string> send;
    private readonly Dictionary<IntPtr, Binding> bindings = new();
    private readonly Dictionary<IntPtr, GameObject> attachedRoots = new();
    private readonly HashSet<IntPtr> privateMessages = new();
    private readonly HashSet<IntPtr> attachedViews = new();
    private ChatEntry[] history = Array.Empty<ChatEntry>();
    private long revision;
    private float nextDiscovery;
    public int BoundViews { get; private set; }
    public int SentMessages { get; private set; }
    public int MessageCount => history.Length;

    public NativeChat(Action<string> send)
    {
        this.send = send;
        active = this;
        Install();
    }

    public void Apply(ChatEntry[] messages)
    {
        if (history.SequenceEqual(messages)) return;
        history = messages.ToArray();
        revision++;
        foreach (var binding in bindings.Values) Publish(binding);
    }

    public void Attach(GameObject root)
    {
        attachedRoots[root.Pointer] = root;
        AttachRoot(root);
    }

    private void AttachRoot(GameObject root)
    {
        foreach (var view in root.GetComponentsInChildren<ChatView>(true)) AttachView(view);
    }

    public void Update()
    {
        if (Time.unscaledTime < nextDiscovery) return;
        nextDiscovery = Time.unscaledTime + 0.5f;
        foreach (var key in attachedRoots.Where(pair => pair.Value == null).Select(pair => pair.Key).ToArray())
            attachedRoots.Remove(key);
        // Root can assign or replace the view's provider after the waiting-room
        // initialize callback. Revisit only explicitly attached lobby roots.
        foreach (var root in attachedRoots.Values) AttachRoot(root);
        foreach (var key in bindings.Where(pair => pair.Value.Provider == null).Select(pair => pair.Key).ToArray())
        {
            foreach (var pointer in bindings[key].MessagePointers) privateMessages.Remove(pointer);
            bindings.Remove(key);
        }
        BoundViews = 0;
        foreach (var view in Object.FindObjectsOfType<ChatView>(true))
            if (view.chatProvider != null && Owns(view.chatProvider) && AttachView(view)) BoundViews++;
    }

    public void Dispose()
    {
        if (active == this) active = null;
        bindings.Clear();
        attachedRoots.Clear();
        privateMessages.Clear();
        attachedViews.Clear();
        history = Array.Empty<ChatEntry>();
    }

    private static bool IsPrivateKind(ChatProvider provider) =>
        provider.TryCast<GameLobbyChatProvider>() is not null || provider.TryCast<TuberGameChatProvider>() is not null;

    // Lobby attachment is explicit because other menus can contain a lobby chat
    // provider too. The match provider only exists in the private board scene.
    private bool Owns(ChatProvider provider) => bindings.ContainsKey(provider.Pointer) ||
        provider.TryCast<TuberGameChatProvider>() is not null;

    private bool AttachView(ChatView view)
    {
        if (view.chatProvider == null || !IsPrivateKind(view.chatProvider)) return false;
        Bind(view.chatProvider);
        if (attachedViews.Add(view.Pointer))
        {
            if (view.messageInput != null) view.messageInput.richText = false;
            view.gameObject.SetActive(true);
        }
        return true;
    }

    private Binding Bind(ChatProvider provider)
    {
        if (!bindings.TryGetValue(provider.Pointer, out var binding))
        {
            binding = new Binding(provider);
            // Register before native Init: its history callback can reenter the bridge.
            bindings.Add(provider.Pointer, binding);
        }
        if (binding.Initializing) return binding;
        if (!provider.Initialized)
        {
            binding.Initializing = true;
            try { provider.Init(); }
            finally { binding.Initializing = false; }
            binding.Revision = -1;
        }
        if (provider.Messages is null)
            throw new InvalidOperationException("Root's private chat provider did not initialize its message list. Rejoin the room to retry; the host still has the chat history.");
        Publish(binding);
        return binding;
    }

    private void Publish(Binding binding)
    {
        var provider = binding.Provider;
        if (provider == null || provider.Messages is not { } messages) return;
        // Native Start can recreate or clear Messages while Initialized remains
        // true. Restore our acknowledged history even without a new host update.
        if (binding.Revision == revision && binding.PublishedList == messages.Pointer && messages.Count == history.Length) return;
        foreach (var pointer in binding.MessagePointers) privateMessages.Remove(pointer);
        binding.MessagePointers.Clear();
        messages.Clear();
        ClientChatMessage? newest = null;
        foreach (var entry in history)
        {
            var message = new ClientChatMessage
            {
                AccountID = new AccountID(entry.Account), ScreenName = entry.Name,
                Message = entry.Text, Timestamp = entry.Timestamp,
                Metadata = new Il2CppSystem.Collections.Generic.Dictionary<string, string>()
            };
            privateMessages.Add(message.Pointer);
            binding.MessagePointers.Add(message.Pointer);
            messages.Add(message);
            if (entry.Id > binding.LastMessage) newest = message;
        }
        provider.IncrementVersion();
        if (newest is not null) ScrollForNewMessages(binding);
        // Let Root raise its normal notification for newly received messages.
        if (newest is not null && binding.Revision >= 0 && provider.OnChatMessageAdded is { } added)
            added.Invoke(newest);
        binding.LastMessage = history.Length == 0 ? 0 : history[^1].Id;
        binding.Revision = revision;
        binding.PublishedList = messages.Pointer;
    }

    private static void ScrollForNewMessages(Binding binding)
    {
        foreach (var view in Object.FindObjectsOfType<ChatView>(true))
        {
            if (view.chatProvider == null || view.chatProvider.Pointer != binding.Provider.Pointer) continue;
            var scroll = view.scrollBar;
            // Native Send consumes this flag before the private host's echo
            // arrives. Re-arm it on delivery, after the message list changes.
            // Incoming messages preserve a reader's position in older history.
            if (binding.ScrollAfterSend || scroll == null || scroll.size >= 0.999f ||
                Math.Abs(scroll.value - ChatView.SCROLL_END) < 0.01f)
                view.forceScrollToEnd = true;
        }
        binding.ScrollAfterSend = false;
    }

    private static void Install()
    {
        if (patched) return;
        var harmony = new Harmony("local.root.private-chat");
        harmony.Patch(AccessTools.Method(typeof(ChatProvider), nameof(ChatProvider.AddChatMessage)),
            prefix: new HarmonyMethod(typeof(NativeChat), nameof(Send)));
        harmony.Patch(AccessTools.Method(typeof(ChatProvider), nameof(ChatProvider.GetChatMessages)),
            prefix: new HarmonyMethod(typeof(NativeChat), nameof(History)));
        harmony.Patch(AccessTools.Method(typeof(GameLobbyChatProvider), "Init", new[]
            { typeof(platform.websocket.DWDWebSocket<Canis.json.events.NetworkMessageEvent>) }),
            prefix: new HarmonyMethod(typeof(NativeChat), nameof(InitializeLobby)));
        // Lobby getInitialChatMessages is an empty native method. IL2CPP shares
        // its code address with unrelated empty methods, so detouring it also
        // intercepts objects that are not chat providers. The base history
        // entry point above and socket initialization boundary cover this path.
        harmony.Patch(AccessTools.Method(typeof(GameChatProvider), "getInitialChatMessages"),
            prefix: new HarmonyMethod(typeof(NativeChat), nameof(History)));
        harmony.Patch(AccessTools.Method(typeof(TuberGameChatProvider), "chatAvailable"),
            prefix: new HarmonyMethod(typeof(NativeChat), nameof(Available)));
        harmony.Patch(AccessTools.Method(typeof(ChatView), "canSendMessage"),
            prefix: new HarmonyMethod(typeof(NativeChat), nameof(CanSend)));
        foreach (var type in new[] { typeof(ChatMessageView), typeof(tuber.client.chat.TuberChatMessageView) })
            harmony.Patch(AccessTools.Method(type, "ConstructMessage"),
                postfix: new HarmonyMethod(typeof(NativeChat), nameof(PlainText)));
        patched = true;
    }

    private static bool Send(ChatProvider __instance, string __0)
    {
        if (active is not { } bridge || !bridge.Owns(__instance)) return true;
        var binding = bridge.Bind(__instance);
        if (!string.IsNullOrWhiteSpace(__0))
        {
            binding.ScrollAfterSend = true;
            bridge.send(__0);
            bridge.SentMessages++;
        }
        return false;
    }

    private static bool History(ChatProvider __instance)
    {
        if (active is not { } bridge || !bridge.Owns(__instance)) return true;
        bridge.Bind(__instance);
        return false;
    }

    private static bool InitializeLobby(GameLobbyChatProvider __instance) => History(__instance);

    private static bool Available(TuberGameChatProvider __instance, ref bool __result)
    {
        if (active is null) return true;
        __result = true;
        return false;
    }

    private static bool CanSend(ChatView __instance, ref bool __result)
    {
        if (active is not { } bridge || __instance.chatProvider == null ||
            !bridge.bindings.ContainsKey(__instance.chatProvider.Pointer)) return true;
        __result = __instance.messageInput != null && !string.IsNullOrWhiteSpace(__instance.messageInput.text);
        return false;
    }

    private static void PlainText(ChatMessageView __instance, ClientChatMessage __0)
    {
        if (active is not { } bridge) return;
        var view = __instance.GetComponentInParent<ChatView>();
        if (!bridge.privateMessages.Contains(__0.Pointer) &&
            (view == null || view.chatProvider == null || !bridge.bindings.ContainsKey(view.chatProvider.Pointer))) return;
        var named = __instance.TryCast<tuber.client.chat.TuberChatMessageView>();
        if (__instance.messageField != null)
        {
            __instance.messageField.richText = false;
            __instance.messageField.text = named is null ? $"{__0.ScreenName}: {__0.Message}" : __0.Message;
        }
        if (named is null) return;
        if (named.nameField != null)
        {
            named.nameField.richText = false;
            named.nameField.text = __0.ScreenName;
        }
        named.disableChatSocialButton = true;
        // The native social button also contains the sender's name. Keep the
        // label visible while preventing actions for private account IDs.
        if (named.playerChatSocialButton != null) named.playerChatSocialButton.interactable = false;
    }
}
