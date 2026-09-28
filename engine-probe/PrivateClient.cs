using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using BepInEx.Logging;
using Canis.json;
using Canis.json.events;
using Canis.utils.ids;
using HarmonyLib;
using Networking.selection.messages.outgoing;
using Networking.selection.targetresponse;
using tuber.canis;
using tuber.canis.data.matchinitdata;
using tuber.client.match.canis;
using tuber_canis.data;

namespace RootEngineProbe;

// All Unity and IL2CPP calls stay on the main thread. Only TCP/JSON runs in tasks.
internal sealed class PrivateClient
{
    private static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("Private Client");
    public static PrivateClient? Active { get; private set; }
    private readonly int port;
    private readonly string token;
    private Func<object, Task<JsonElement>> exchange;
    private readonly Queue<object> outgoing = new();
    private readonly Queue<string> outgoingChat = new();
    private readonly Queue<string> incoming = new();
    private sealed record ReceivedResponse(JsonElement Body, long SentAt, long ReceivedAt);
    private Task<ReceivedResponse>? pending;
    private TuberCanisMatch? relay;
    private int cursor;
    private float nextPoll;
    private bool launched;
    private AccountID? account;
    private dwd.core.account.SerializableAccount? localAccount;
    private static dwd.core.account.SerializableAccount? accountForSnapshot;
    private static bool patched;
    public int ReceivedMessages { get; private set; }
    public int Cursor => cursor;
    public int AcceptedChoices { get; private set; }
    public SelectionOffer? Offer { get; private set; }
    public string? Notice { get; set; }
    public bool GameOver { get; private set; }
    public bool Transitioning { get; private set; }
    public string? Winner { get; private set; }
    public sealed record Standing(string Faction, int Score, bool Won, bool Dominance);
    public Standing[] Standings { get; private set; } = Array.Empty<Standing>();
    public MatchSetup Setup { get; private set; } = new();
    public SeatStatus[] Lobby { get; private set; } = Array.Empty<SeatStatus>();
    public int Seat { get; private set; }
    public string DisplayName { get; set; } = "";
    public Dictionary<string, string>? JoinMetadata { get; set; }
    public JsonElement? PendingLobby { get; private set; }
    public Action<JsonElement>? LobbyChanged { get; set; }
    public Action? LobbyStarted { get; set; }
    public Action? LobbyRejected { get; set; }
    public Action<ChatEntry[]>? ChatChanged { get; set; }
    private long chatVersion = -1;
    private readonly PrivateHostClock hostClock = new();
    public long ServerTimeMilliseconds => hostClock.Estimate(Environment.TickCount64);
    public void SendChat(string text) => outgoingChat.Enqueue(text);
    public void LobbyCommand(string op, Dictionary<string, string>? metadata) => outgoing.Enqueue(new { op, token, metadata });
    public void Ready(bool ready) => outgoing.Enqueue(new { op = "ready", token, ready });
    public void Resign()
    {
        Transitioning = true;
        outgoing.Enqueue(new { op = "resign", token });
        outgoing.Enqueue(new { op = "join", token });
    }
    public void DiscardQueuedMoves() => outgoing.Clear();
    public void ReplaceTransport(Func<object, Task<JsonElement>> transport)
    {
        if (pending is { } abandoned) _ = abandoned.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        pending = null;
        outgoing.Clear(); incoming.Clear();
        chatVersion = -1;
        exchange = transport;
        outgoing.Enqueue(new { op = "join", token });
    }

    public void Stop()
    {
        if (Active == this) Active = null;
    }

    public PrivateClient(string file) : this(ReadConnection(file)) { }
    public PrivateClient(int port, string token) : this((port, token)) { }

    private static (int Port, string Token) ReadConnection(string file)
    {
        using var config = JsonDocument.Parse(File.ReadAllText(file));
        var port = config.RootElement.GetProperty("port").GetInt32();
        var token = config.RootElement.GetProperty("token").GetString() ?? throw new InvalidDataException("Missing private seat token.");
        if (port < 1 || port > 65535 || token.Length != 32)
            throw new InvalidDataException("Invalid private host connection file.");
        return (port, token);
    }

    private PrivateClient((int Port, string Token) connection) : this(connection.Token, null)
    { port = connection.Port; }

    public PrivateClient(string token, Func<object, Task<JsonElement>>? transport)
    {
        this.token = token;
        exchange = transport ?? (request => Task.Run(() => Exchange(request)));
        Active = this;
        if (patched) return;
        patched = true;
        var harmony = new Harmony("local.root.privateclient");
        harmony.Patch(AccessTools.Method(typeof(boardgames.account.AccountExtensions), "ToSerializableAccount"),
            prefix: new HarmonyMethod(typeof(PrivateClient), nameof(ProvidePrivateAccount)));
        harmony.Patch(AccessTools.Method(typeof(TuberMatch), nameof(TuberMatch.Start), Type.EmptyTypes),
            prefix: new HarmonyMethod(typeof(PrivateClient), nameof(SkipLocalAuthority)));
        harmony.Patch(AccessTools.Method(typeof(TuberCanisMatch), nameof(TuberCanisMatch.Configure), new[] { typeof(TuberMatchInitData) }),
            postfix: new HarmonyMethod(typeof(PrivateClient), nameof(BindRelay)));
        foreach (var arguments in new[] { new[] { typeof(Il2CppSystem.Object) }, new[] { typeof(Il2CppSystem.Object), typeof(AccountID) } })
            harmony.Patch(AccessTools.Method(typeof(TuberCanisMatch), nameof(TuberCanisMatch.Write), arguments),
                prefix: new HarmonyMethod(typeof(PrivateClient), nameof(SendChoice)));
    }

    public void Update(float now)
    {
        if (pending is { IsCompleted: true })
        {
            var received = pending.GetAwaiter().GetResult();
            var response = received.Body;
            pending = null;
            if (response.TryGetProperty("serverTime", out var clock) && clock.TryGetInt64(out var milliseconds))
                hostClock.Observe(milliseconds, received.SentAt, received.ReceivedAt);
            if (response.GetProperty("ok").GetBoolean() == false)
            {
                var code = response.GetProperty("error").GetString();
                Log.LogWarning($"Private host rejected a request: {code}");
                if (code is "InvalidChat" or "ChatRateLimited")
                {
                    Notice = code == "ChatRateLimited" ? "Wait a second before sending another message. Your last message was not sent."
                        : "Your message was not sent. Use 1 to 500 characters on one line.";
                    return;
                }
                if (code == "LobbyAlreadyStarted")
                {
                    LobbyRejected?.Invoke();
                    outgoing.Clear();
                    outgoing.Enqueue(new { op = "join", token });
                    return;
                }
                if (PendingLobby is not null && code is "FactionUnavailable" or "PlayersMissing" or "HostOnly" or "LobbyNotStarted")
                {
                    LobbyRejected?.Invoke();
                    Notice = code switch
                    {
                        "FactionUnavailable" => "That faction is already taken or conflicts with the table. Choose another faction.",
                        "PlayersMissing" => "A human seat is still waiting or disconnected. Invite your friends and wait for them to join before starting.",
                        "HostOnly" => "Only the host can start this match.",
                        _ => "The game is still in the lobby. Wait for the host to start."
                    };
                    outgoing.Clear();
                    outgoing.Enqueue(new { op = "join", token });
                    return;
                }
                if (code is "Unauthorized" or "BotSeat") throw new InvalidDataException("This seat invitation is no longer valid. Ask the host for a new invitation.");
                if (code == "SnapshotTooLarge") throw new InvalidDataException("This board is too large to synchronize with this mod version. The host's save is preserved. Keep it for a newer release.");
                if (code is not ("NoSuchSelection" or "WrongPlayer" or "UnsupportedSelection" or "InvalidSource" or "InvalidTarget" or "EngineDidNotAdvance" or "InvalidSelection" or "InvalidCursor" or "MatchFinished" or "SeatResigned" or "ResignationFailed" or "MatchBusy"))
                    throw new IOException("The host could not complete the request. Check Steam and ask the host to check its game. Saved moves remain on the host.");
                outgoing.Clear();
                Transitioning = false;
                var detail = code switch
                {
                    "UnsupportedSelection" => "This kind of action is not supported by this release yet.",
                    "MatchBusy" => "Root is still finishing a change to the table.",
                    "SeatResigned" => "Root's AI now controls this resigned faction.",
                    "MatchFinished" => "This match has finished.",
                    "WrongPlayer" => "This decision belongs to another player.",
                    _ => "The host could not accept that choice. The turn or available choices may have changed."
                };
                Notice = detail + " The board will refresh so you can check the current turn. If this blocks play, keep the save and report the action.";
                outgoing.Enqueue(new { op = "join", token });
                return;
            }
            if (response.TryGetProperty("chat", out var chat) && chat.ValueKind != JsonValueKind.Null)
            {
                var update = MatchChat.Parse(chat);
                chatVersion = update.Version;
                ChatChanged?.Invoke(update.Messages);
            }
            if (response.TryGetProperty("phase", out var phase) && phase.GetString() == "lobby")
            {
                PendingLobby = response;
                Seat = response.GetProperty("seat").GetInt32() + 1;
                Setup = MatchSetup.Parse(response.GetProperty("setup").GetRawText(), pendingLobby: true);
                LobbyChanged?.Invoke(response);
            }
            else if (response.TryGetProperty("messages", out var messages))
            {
                if (PendingLobby is not null) { PendingLobby = null; LobbyStarted?.Invoke(); }
                if (response.TryGetProperty("reset", out var reset) && reset.GetBoolean())
                { outgoing.Clear(); incoming.Clear(); }
                GameOver = response.GetProperty("gameOver").GetBoolean();
                Transitioning = response.TryGetProperty("transitioning", out var transition) && transition.GetBoolean();
                Winner = response.GetProperty("winner").GetString();
                Lobby = response.TryGetProperty("lobby", out var lobby) ? MatchLobby.Parse(lobby) : Array.Empty<SeatStatus>();
                if (response.TryGetProperty("setup", out var settings)) Setup = MatchSetup.Parse(settings.GetRawText());
                if (Winner is { } winner)
                    foreach (var player in response.GetProperty("roster").EnumerateArray())
                        if (player.GetProperty("account").GetString() == winner) Winner = MatchSetup.FactionName(player.GetProperty("faction").GetInt32());
                if (launched == false)
                    Launch(response);
                foreach (var message in messages.EnumerateArray())
                    incoming.Enqueue(message.GetRawText());
                cursor = response.GetProperty("next").GetInt32();
                Offer = response.GetProperty("offer").Deserialize<SelectionOffer>();
            }
            else if (!response.TryGetProperty("chatAccepted", out _))
                AcceptedChoices++;
        }
        if (relay is not null && account is not null)
            while (incoming.TryDequeue(out var json))
            {
                var message = JSON.Deserialize<DWDEvent>(json);
                if (message.TryCast<Canis.messages.timer.DisplayTimer>() is { } timer)
                    message = NativeTimerUI.ForClient(timer, ServerTimeMilliseconds);
                if (message.TryCast<Canis.messages.sequence.SequenceMessage>()?.Msg.TryCast<tuber.canis.messages.GameResults>() is { } results)
                {
                    // Root's victory scene has only four player slots and leaves
                    // the board. Keep the board and show our six-player standings.
                    Standings = results.results.Select(entry => new Standing(MatchSetup.FactionName((int)entry.faction), entry.score, entry.didWin, entry.wasDominanceWin)).ToArray();
                    ReceivedMessages++;
                    continue;
                }
                var state = message.TryCast<Canis.messages.sequence.SequenceMessage>()?.Msg.TryCast<Canis.messages.SerializedGameState>();
                if (state is not null)
                {
                    // This field identifies locally controlled players, not the roster.
                    // The native offline bootstrap otherwise treats all six as local.
                    state.PlayerAccounts = new[] { account };
                }
                // Single-seat initialization normally consults the official login.
                // Supply our local identity only within this snapshot dispatch.
                accountForSnapshot = state is null ? null : localAccount;
                try { relay.MessageDispatcher(account, message); }
                finally { accountForSnapshot = null; }
                ReceivedMessages++;
            }
        if (pending is null && now >= nextPoll)
        {
            var request = outgoing.TryDequeue(out var choice) ? choice
                : outgoingChat.TryDequeue(out var text) ? new { op = "chat", token, text }
                : (object)new { op = launched || PendingLobby is not null ? "poll" : "join", token, after = cursor, chatVersion, name = DisplayName, metadata = JoinMetadata };
            pending = ExchangeWithReceipt(request);
            nextPoll = now + 0.25f;
        }
    }

    private async Task<ReceivedResponse> ExchangeWithReceipt(object request)
    {
        var sentAt = Environment.TickCount64;
        var response = await exchange(request).ConfigureAwait(false);
        return new(response, sentAt, Environment.TickCount64);
    }

    private JsonElement Exchange(object request)
    {
        using var client = new TcpClient();
        client.ConnectAsync("127.0.0.1", port).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        client.ReceiveTimeout = 5000;
        client.SendTimeout = 5000;
        using var stream = client.GetStream();
        stream.Write(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request) + "\n"));
        using var buffer = new MemoryStream();
        while (buffer.Length < 4 * 1024 * 1024)
        {
            var value = stream.ReadByte();
            if (value == '\n')
            {
                using var response = JsonDocument.Parse(buffer.ToArray());
                return response.RootElement.Clone();
            }
            if (value < 0) throw new IOException("Private host closed its response before the newline.");
            buffer.WriteByte((byte)value);
        }
        throw new InvalidDataException("Private host response exceeded 4 MiB.");
    }

    private void Launch(JsonElement response)
    {
        var init = new TuberMatchInitData(new GameID(response.GetProperty("gameId").GetString())) { DoNotShufflePlayers = true };
        init.AddOption("matchType", "Live");
        // The board scene and expansion controls are chosen before snapshots arrive.
        // Forward only public settings, never the host's initialization/checkpoint.
        var setup = response.TryGetProperty("setup", out var settings) ? MatchSetup.Parse(settings.GetRawText()) : null;
        Setup = setup ?? new();
        var nativeConfiguration = response.TryGetProperty("initialization", out var native) && native.ValueKind == JsonValueKind.Object;
        if (nativeConfiguration) init = NativeClientConfiguration.Parse(native, response.GetProperty("roster"));
        else if (setup is not null) NativeMatchSetup.Apply(init, setup);
        var seat = response.GetProperty("seat").GetInt32();
        Seat = seat + 1;
        var index = 0;
        foreach (var player in response.GetProperty("roster").EnumerateArray())
        {
            var id = new AccountID(player.GetProperty("account").GetString());
            if (index == seat)
            {
                account = id;
                localAccount = new dwd.core.account.SerializableAccount
                {
                    AccountID = id, Username = player.GetProperty("name").GetString(),
                    Attributes = new Canis.attributes.SerializableAttributes()
                };
            }
            var factionValue = player.GetProperty("faction").GetInt32();
            if (Enum.IsDefined(typeof(Factions), factionValue) == false)
                throw new InvalidDataException("Host roster contains an unknown faction.");
            if (!nativeConfiguration)
                init.AddTuberPlayer(setup is not null ? NativeMatchSetup.Player(player.GetProperty("name").GetString() ?? "Player", id, index, setup) : new TuberPlayerMatchInitData(player.GetProperty("name").GetString(), id)
                {
                    Faction = (Factions)factionValue,
                    StartingCharacter = VagabondCharacters.Unknown,
                    PlayerStartingOrder = index
                });
            index++;
        }
        if (index != 6 || account is null) throw new InvalidDataException("Host did not return a six-seat roster with our seat.");
        dwd.core.account.AccountProvider.Find().InitializeWithOfflineID(account);
        dwd.core.commands.CommandExecutor.Get().Execute(new tuber.client.match.commands.PlayOfflineMatch(init, false));
        launched = true;
        Log.LogInfo($"Loading private match as seat {seat + 1}");
    }

    private static bool SkipLocalAuthority()
    {
        Log.LogInfo("Local authority disabled; the private host owns the match");
        return false;
    }

    private static bool ProvidePrivateAccount(ref dwd.core.account.SerializableAccount __result)
    {
        if (accountForSnapshot is not { } identity) return true;
        __result = identity;
        return false;
    }

    private static void BindRelay(TuberCanisMatch __instance)
    {
        if (Active is not { account: { } id } client) return;
        __instance.localAccountID = id;
        client.relay = __instance;
        Log.LogInfo("Native match relay connected to the private host");
    }

    internal static bool SendChoice(Il2CppSystem.Object __0)
    {
        if (Active is not { } client) return false;
        // Root requests chat history automatically when opening the board.
        // Chat is outside this protocol; this read is not a failed player move.
        if (__0.TryCast<Canis.game.messages.chat.GetGameChat>() is not null) return false;
        if (__0.TryCast<tuber.canis.messages.ChosenRiverfolkPrices>() is { } prices)
        {
            client.outgoing.Enqueue(new { op = "prices", client.token, counter = prices.counter,
                handCard = prices.Selection[RiverfolkService.HandCard], riverboats = prices.Selection[RiverfolkService.Riverboats],
                mercenaries = prices.Selection[RiverfolkService.Mercenaries] });
            return false;
        }
        var selection = __0.TryCast<SelectionWithTargets>();
        if (selection is not null && selection.selection is null)
        {
            client.outgoing.Enqueue(new { op = "pass", client.token, counter = selection.counter });
            Log.LogInfo($"Sending native pass {selection.counter} to the private host");
            return false;
        }
        if (selection?.selection is { } selectedTarget)
        {
            var choices = new List<TargetChoice>();
            foreach (var target in selectedTarget.targetResponses)
            {
                if (target.TryCast<EntityListTargetResponse>() is { } entities)
                    choices.Add(new TargetChoice.Entities(entities.EntityList.Select(id => id.ToString()).ToArray()));
                else if (target.TryCast<IntTargetResponse>() is { } number)
                    choices.Add(new TargetChoice.Number(number.Amount));
                else
                {
                    client.Unsupported(target.GetIl2CppType().FullName);
                    return false;
                }
            }
            client.outgoing.Enqueue(new { op = "targets", client.token, counter = selection.counter,
                source = selectedTarget.entityID.ToString(), targets = choices.Select(choice => choice.ToWire()).ToArray() });
            Log.LogInfo($"Sending native selection {selection.counter} to the private host");
        }
        else if (__0.TryCast<dwd.core.match.messages.outgoing.GameCustomChoice>() is { } custom)
        {
            // Root omits Selection when declining. Its generated nullable
            // getter also cannot wrap that null IL2CPP value, so use the native
            // serializer for this field while matching the actual message type.
            using var document = JsonDocument.Parse(JSON.ToJSON(custom, false));
            int? choice = document.RootElement.GetProperty("value").TryGetProperty("selection", out var selected)
                && selected.ValueKind != JsonValueKind.Null ? selected.GetInt32() : null;
            client.outgoing.Enqueue(new { op = "custom", client.token, counter = custom.Counter, choice });
            Log.LogInfo($"Sending native custom choice {custom.Counter} to the private host");
        }
        else if (__0.TryCast<zen.src.matchMaking.messages.ResignPBMGame>() is not null)
            client.Resign();
        else client.Unsupported(__0.GetIl2CppType().FullName);
        return false;
    }

    private void Unsupported(string kind)
    {
        Log.LogWarning($"Unsupported private action: {kind}");
        Notice = "This action is not supported by this release yet. No move was sent. The board will refresh. If this blocks your turn, keep the save and report which action you selected.";
        outgoing.Clear();
        outgoing.Enqueue(new { op = "join", token });
    }
}
