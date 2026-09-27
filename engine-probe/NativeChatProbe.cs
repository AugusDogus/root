using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Canis.utils.ids;
using lotus;
using tuber.client.match;
using UnityEngine;
using Object = UnityEngine.Object;

namespace RootEngineProbe;

// Short native smoke test. Each boundary is persisted before entering native
// code so an unmanaged crash still leaves the exact unfinished stage.
public sealed class NativeChatProbe : MonoBehaviour
{
    private readonly List<string> completed = new();
    private readonly List<string> sent = new();
    private readonly List<object> providers = new();
    private readonly List<object> methods = new();
    private readonly List<object> startSteps = new();
    private string output = "";
    private string stage = "starting";
    private NativeChat? chat;
    public NativeChatProbe(IntPtr pointer) : base(pointer) { }

    public void Start()
    {
        output = Path.GetFullPath(Path.Combine(BepInEx.Paths.GameRootPath, "..", "results", "chat-probe.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(output) ?? throw new InvalidOperationException("Missing native chat probe output directory."));
        AudioListener.volume = 0;
        try
        {
            Step("reflection-initialization", dwd.core.data.ReflectionTypeInitializer.Initialize);
            Step("native-method-addresses", RecordMethods);
            Step("chat-constructor-and-patches", () => chat = new NativeChat(sent.Add));
            Step("private-account-provider", InitializeAccount);
            if (chat is not { } bridge) throw new InvalidOperationException("Native chat constructor did not produce a bridge.");

            GameObject? lobbyRoot = null;
            GameLobbyChatProvider? lobbyProvider = null;
            ChatView? lobbyView = null;
            Step("lobby-components", () =>
            {
                lobbyRoot = new GameObject("Private chat probe lobby");
                lobbyRoot.SetActive(false);
                lobbyProvider = lobbyRoot.AddComponent<GameLobbyChatProvider>();
                var view = lobbyRoot.AddComponent<ChatView>();
                // This tests the provider boundary without needing a rendered
                // prefab's animator, prototype, scrollbar, or input controls.
                view.enabled = false;
                lobbyView = view;
            });
            if (lobbyRoot == null || lobbyProvider == null || lobbyView == null) throw new InvalidOperationException("Native lobby probe components were not created.");
            Step("lobby-attach-before-provider", () => bridge.Attach(lobbyRoot));
            Step("lobby-provider-late-assignment", () => lobbyView.chatProvider = lobbyProvider);
            Step("lobby-discover-and-native-init", bridge.Update);
            Step("lobby-publish", () =>
            {
                bridge.Apply(new[] { new ChatEntry(1, 1, TestAccount, "<b>Probe player</b>", "<b>Literal probe text</b>", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) });
                CheckMessages(lobbyProvider, "lobby", 1);
            });
            Step("lobby-native-provider-reset", () =>
            {
                lobbyProvider._Initialized_k__BackingField = false;
                lobbyProvider.Messages.Clear();
            });
            Step("lobby-reinitialize-after-reset", () =>
            {
                bridge.Attach(lobbyRoot);
                CheckMessages(lobbyProvider, "lobby after reset", 1);
            });
            Step("lobby-history-cleared-while-initialized", () => lobbyProvider.Messages.Clear());
            Step("lobby-restore-cleared-history", () =>
            {
                if (!lobbyProvider.Initialized) throw new InvalidOperationException("Fixture expected the native provider to stay initialized.");
                bridge.Attach(lobbyRoot);
                CheckMessages(lobbyProvider, "lobby after initialized history reset", 1);
            });
            Step("lobby-history-request", lobbyProvider.GetChatMessages);
            Step("lobby-send-message", () =>
            {
                lobbyProvider.AddChatMessage("Private lobby send probe");
                if (sent.Count != 1 || sent[0] != "Private lobby send probe")
                    throw new InvalidOperationException("The native lobby chat send did not reach the private callback exactly once.");
            });

            GameObject? boardRoot = null;
            TuberGameChatProvider? boardProvider = null;
            Step("board-components", () =>
            {
                boardRoot = new GameObject("Private chat probe board");
                boardRoot.SetActive(false);
                boardProvider = boardRoot.AddComponent<TuberGameChatProvider>();
                boardProvider.enabled = false;
                var view = boardRoot.AddComponent<ChatView>();
                view.enabled = false;
                view.chatProvider = boardProvider;
            });
            if (boardRoot == null || boardProvider == null) throw new InvalidOperationException("Native board probe components were not created.");
            Step("board-attach-and-native-init", () => bridge.Attach(boardRoot));
            Step("board-history-request", boardProvider.GetChatMessages);
            Step("board-chat-availability", () =>
            {
                if (!boardProvider.chatAvailable()) throw new InvalidOperationException("Private native board chat was unavailable.");
            });
            Step("board-send-message", () =>
            {
                boardProvider.AddChatMessage("Private board send probe");
                if (sent.Count != 2 || sent[1] != "Private board send probe")
                    throw new InvalidOperationException("The native board chat send did not reach the private callback exactly once.");
                CheckMessages(boardProvider, "board", 1);
            });
            Step("chat-provider-update", bridge.Update);
            Step("board-offline-service-fixture", InitializeBoardServices);
            Il2CppSystem.Collections.IEnumerator? createdStart = null;
            Step("board-native-start-construction", () => createdStart = boardProvider.Start());
            if (createdStart is not { } boardStart) throw new InvalidOperationException("Native board chat Start returned no coroutine.");
            var running = true;
            for (var index = 0; index < 4 && running; index++)
            {
                var iteration = index;
                Step($"board-native-start-step-{iteration}", () =>
                {
                    running = boardStart.MoveNext();
                    startSteps.Add(new { iteration, running, yielded = running ? boardStart.Current?.GetIl2CppType().FullName : null,
                        hasSession = boardProvider.session is not null, hasMatch = boardProvider.match is not null });
                });
            }
            Step("board-history-after-native-start", () =>
            {
                bridge.Attach(boardRoot);
                CheckMessages(boardProvider, "board after native Start", 1);
            });
            Step("board-scroll-after-private-send-echo", () =>
            {
                var view = boardRoot.GetComponent<ChatView>();
                var scrollbarRoot = new GameObject("Private chat probe scrollbar");
                scrollbarRoot.SetActive(false);
                var scroll = scrollbarRoot.AddComponent<UnityEngine.UI.Scrollbar>();
                scroll.size = 0.5f;
                scroll.value = ChatView.SCROLL_END < 0.5f ? 1 : 0;
                view.scrollBar = scroll;
                view.forceScrollToEnd = false;
                bridge.Apply(new[]
                {
                    new ChatEntry(1, 1, TestAccount, "Probe player", "First message", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                    new ChatEntry(2, 1, TestAccount, "Probe player", "Private board send probe", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                });
                if (!view.forceScrollToEnd)
                    throw new InvalidOperationException("The private send acknowledgement did not re-arm native scrolling for the new message.");
            });
            Step("board-incoming-preserves-history-position", () =>
            {
                var view = boardRoot.GetComponent<ChatView>();
                view.forceScrollToEnd = false;
                bridge.Apply(new[]
                {
                    new ChatEntry(1, 1, TestAccount, "Probe player", "First message", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                    new ChatEntry(2, 1, TestAccount, "Probe player", "Private board send probe", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                    new ChatEntry(3, 2, "00000000-0000-0000-0000-000000000002", "Probe friend", "Incoming message", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                });
                if (view.forceScrollToEnd)
                    throw new InvalidOperationException("An incoming message moved a reader away from older chat history.");
            });
            Step("chat-dispose", bridge.Dispose);
            Step("lobby-disable-after-dispose", lobbyProvider.OnDisable);
            Save("completed", null);
            Application.Quit();
        }
        catch (Exception error)
        {
            Save("failed", error.ToString());
            BepInEx.Logging.Logger.CreateLogSource("Native chat probe").LogError($"{stage}: {error}");
            Application.Quit(1);
        }
    }

    private const string TestAccount = "00000000-0000-0000-0000-000000000001";

    private void InitializeAccount()
    {
        GameObject? root = null;
        Step("account-game-object", () => root = new GameObject("Private chat probe account"));
        if (root is not { } accountRoot) throw new InvalidOperationException("Account fixture object is missing.");
        dwd.core.account.AccountProvider? created = null;
        Step("account-add-component", () => created = accountRoot.AddComponent<dwd.core.account.AccountProvider>());
        if (created is not { } provider) throw new InvalidOperationException("Account fixture provider is missing.");
        Step("account-finder-tag", () => accountRoot.tag = dwd.core.Finder.TAG);
        lotus.data.account.LotusAccountProvider? createdFactory = null;
        Step("account-factory-constructor", () => createdFactory = new lotus.data.account.LotusAccountProvider());
        if (createdFactory is not { } factory) throw new InvalidOperationException("Account fixture factory is missing.");
        dwd.core.account.IAccountFactory? typedFactory = null;
        Step("account-factory-cast", () => typedFactory = factory.Cast<dwd.core.account.IAccountFactory>());
        if (typedFactory is not { } accountFactory) throw new InvalidOperationException("Account factory interface is missing.");
        Step("account-set-factory", () => provider.SetFactory(accountFactory));
        dwd.core.account.SerializableAccount? createdIdentity = null;
        Step("account-record-constructor", () => createdIdentity = new dwd.core.account.SerializableAccount());
        if (createdIdentity is not { } identity) throw new InvalidOperationException("Account fixture identity is missing.");
        AccountID? createdId = null;
        Step("account-id-constructor", () => createdId = new AccountID(TestAccount));
        if (createdId is not { } id) throw new InvalidOperationException("Account fixture ID is missing.");
        Step("account-record-id", () => identity.AccountID = id);
        Step("account-record-name", () => identity.Username = "Chat probe player");
        Canis.attributes.SerializableAttributes? createdAttributes = null;
        Step("account-attributes-constructor", () => createdAttributes = new Canis.attributes.SerializableAttributes());
        if (createdAttributes is not { } attributes) throw new InvalidOperationException("Account fixture attributes are missing.");
        Step("account-record-attributes", () => identity.Attributes = attributes);
        Step("account-initialize", () => provider.Initialize(identity));
        Step("account-finder-clear-cache", dwd.core.Finder.ClearCache);
    }

    private void CheckMessages(ChatProvider provider, string context, int expected)
    {
        var count = provider.Messages?.Count ?? -1;
        providers.Add(new { context, initialized = provider.Initialized, count, version = provider.Version });
        if (!provider.Initialized || count != expected)
            throw new InvalidOperationException($"Native {context} chat expected initialized provider with {expected} messages, got initialized={provider.Initialized}, messages={count}.");
    }

    private void InitializeBoardServices()
    {
        GameObject? createdSessionRoot = null;
        Step("board-session-object", () => createdSessionRoot = new GameObject("Private chat probe session"));
        if (createdSessionRoot is not { } sessionRoot) throw new InvalidOperationException("Board session fixture object is missing.");
        Step("board-session-finder-tag", () => sessionRoot.tag = dwd.core.Finder.TAG);
        dwd.core.session.SessionProvider? createdSession = null;
        Step("board-session-component", () => createdSession = sessionRoot.AddComponent<dwd.core.session.SessionProvider>());
        if (createdSession is not { } session) throw new InvalidOperationException("Board session fixture provider is missing.");
        Step("board-session-disable-update", () => session.enabled = false);

        GameObject? createdMatchRoot = null;
        Step("board-match-object", () => createdMatchRoot = new GameObject("Private chat probe offline match"));
        if (createdMatchRoot is not { } matchRoot) throw new InvalidOperationException("Board match fixture object is missing.");
        Step("board-match-finder-tag", () => matchRoot.tag = dwd.core.Finder.TAG);
        tuber.client.match.canis.TuberCanisMatch? createdMatch = null;
        Step("board-match-component", () => createdMatch = matchRoot.AddComponent<tuber.client.match.canis.TuberCanisMatch>());
        if (createdMatch is not { } match) throw new InvalidOperationException("Board match fixture provider is missing.");
        Step("board-match-disable-update", () => match.enabled = false);
        Step("board-match-session", () => match.session = session);
        tuber.canis.TuberMatch? createdAuthority = null;
        Step("board-authority-constructor", () => createdAuthority = new tuber.canis.TuberMatch());
        if (createdAuthority is not { } authority) throw new InvalidOperationException("Board match fixture authority is missing.");
        Step("board-authority-configure-without-start", () => authority.Configure(HostedMatch.CreateInitialization(true)));
        Step("board-match-authority", () => match.match = authority);
        Step("board-match-local-account", () => match.localAccountID = new AccountID(TestAccount));
        Step("board-services-finder-clear-cache", dwd.core.Finder.ClearCache);
    }

    private void RecordMethods()
    {
        foreach (var type in new[] { typeof(ChatProvider), typeof(ChatView), typeof(GameLobbyChatProvider),
                     typeof(GameChatProvider), typeof(TuberGameChatProvider), typeof(ChatMessageView), typeof(tuber.client.chat.TuberChatMessageView),
                     typeof(dwd.core.account.AccountProvider), typeof(lotus.data.account.LotusAccountProvider) })
            foreach (var field in type.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                if (field.Name.StartsWith("NativeMethodInfoPtr_", StringComparison.Ordinal) &&
                    field.GetValue(null) is IntPtr info && info != IntPtr.Zero)
                    methods.Add(new { type = type.FullName, field = field.Name, address = Marshal.ReadIntPtr(info).ToInt64().ToString("x") });
    }

    private void Step(string name, Action action)
    {
        stage = name;
        Save("running", null);
        action();
        completed.Add(name);
        Save("running", null);
    }

    private void Save(string status, string? error)
    {
        var temporary = output + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new { status, stage, completed, sent, providers, methods, startSteps, error }));
        File.Move(temporary, output, true);
    }
}
