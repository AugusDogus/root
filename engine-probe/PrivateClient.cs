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
    private readonly Func<object, Task<JsonElement>> exchange;
    private readonly Queue<object> outgoing = new();
    private readonly Queue<string> incoming = new();
    private Task<JsonElement>? pending;
    private TuberCanisMatch? relay;
    private int cursor;
    private float nextPoll;
    private bool launched;
    private AccountID? account;
    private dwd.core.account.SerializableAccount? localAccount;
    private static dwd.core.account.SerializableAccount? accountForSnapshot;
    public int ReceivedMessages { get; private set; }
    public int Cursor => cursor;
    public int AcceptedChoices { get; private set; }
    public SelectionOffer? Offer { get; private set; }

    public void Stop()
    {
        if (Active == this) Active = null;
    }

    public PrivateClient(string file) : this(ReadConnection(file)) { }

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
            var response = pending.GetAwaiter().GetResult();
            pending = null;
            if (response.GetProperty("ok").GetBoolean() == false)
                throw new InvalidOperationException($"Private host rejected request: {response.GetProperty("error").GetString()}");
            if (response.TryGetProperty("messages", out var messages))
            {
                if (launched == false)
                    Launch(response);
                foreach (var message in messages.EnumerateArray())
                    incoming.Enqueue(message.GetRawText());
                cursor = response.GetProperty("next").GetInt32();
                Offer = response.GetProperty("offer").Deserialize<SelectionOffer>();
            }
            else
                AcceptedChoices++;
        }
        if (relay is not null && account is not null)
            while (incoming.TryDequeue(out var json))
            {
                var message = JSON.Deserialize<DWDEvent>(json);
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
            var request = outgoing.TryDequeue(out var choice) ? choice : new { op = launched ? "poll" : "join", token, after = cursor };
            pending = exchange(request);
            nextPoll = now + 0.25f;
        }
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
        if (setup is not null) NativeMatchSetup.Apply(init, setup);
        var seat = response.GetProperty("seat").GetInt32();
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
            init.AddTuberPlayer(setup is not null ? NativeMatchSetup.Player(player.GetProperty("name").GetString() ?? "Player", id, index++, setup) : new TuberPlayerMatchInitData(player.GetProperty("name").GetString(), id)
            {
                Faction = (Factions)factionValue,
                StartingCharacter = setup is null ? VagabondCharacters.Unknown : (VagabondCharacters)setup.Characters[index],
                PlayerStartingOrder = index++
            });
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

    private static bool SendChoice(Il2CppSystem.Object __0)
    {
        if (Active is not { } client) return false;
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
                    Log.LogWarning($"Unsupported target response: {target.GetIl2CppType().FullName}");
                    return false;
                }
            }
            client.outgoing.Enqueue(new { op = "targets", client.token, counter = selection.counter,
                source = selectedTarget.entityID.ToString(), targets = choices.Select(choice => choice.ToWire()).ToArray() });
            Log.LogInfo($"Sending native selection {selection.counter} to the private host");
        }
        else
        {
            using var document = JsonDocument.Parse(JSON.ToJSON(__0, false));
            var root = document.RootElement;
            if (root.TryGetProperty("name", out var name) && name.GetString() == "GameCustomChoice" &&
                root.TryGetProperty("value", out var value) && value.TryGetProperty("counter", out var counter) &&
                counter.TryGetInt32(out var counterNumber) && value.TryGetProperty("selection", out var selected) &&
                selected.ValueKind is JsonValueKind.Number or JsonValueKind.Null)
            {
                int? choice = selected.ValueKind == JsonValueKind.Null ? null : selected.GetInt32();
                client.outgoing.Enqueue(new { op = "custom", client.token, counter = counterNumber, choice });
                Log.LogInfo($"Sending native custom choice {counterNumber} to the private host");
            }
            else
                Log.LogWarning($"Private transport does not yet support {__0.GetIl2CppType().FullName}");
        }
        return false;
    }
}
