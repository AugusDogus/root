using System.Text.Json;
using Canis.json;
using Canis.messages;
using Canis.utils.ids;
using Networking.game;
using Networking.lobby;
using tuber.canis;
using tuber.canis.data.matchinitdata;
using tuber_canis.data;

namespace RootEngineProbe;

// The online Create request is configuration, not a running match. Keep it
// here until the host starts, using Root's own packed AI/Clockwork metadata.
internal sealed class PendingLobby
{
    private readonly CreateLobbyGame request;
    private readonly TuberMatchInitData initialization;
    private readonly TuberPlayerMatchInitData[] players;
    private enum SeatState { Absent, Choosing, Joined }
    private readonly SeatState[] seats = new SeatState[6];
    private readonly MatchLobby presence = new();
    private readonly MatchChat chat = new();
    public HostedMatch? Started { get; private set; }
    public MatchSetup Setup => NativeMatchSetup.Read(initialization, players);

    public PendingLobby(string json)
    {
        dwd.core.data.ReflectionTypeInitializer.Initialize();
        request = JSON.Deserialize<CreateLobbyGame>(json);
        if (request.NumberOfPlayers is < 1 or > 6 || request.NumberOfAIPlayers < 0 ||
            request.NumberOfPlayers + request.NumberOfAIPlayers != 6)
            throw new InvalidDataException("Choose six active seats before creating the lobby.");
        if (!string.IsNullOrEmpty(request.Password))
            throw new InvalidDataException("Private games use Steam seat invitations. Clear the password and create the room again.");
        if (request.MatchType != TuberMatchTypes.LiveType && request.MatchType != TuberMatchTypes.CasualType)
            throw new InvalidDataException("Root returned an unsupported timer mode. Return to setup and choose Fast or Slow.");
        initialization = JSON.Deserialize<TuberMatchInitData>(request.MatchInitData);
        if (initialization.TuberPlayers.Count != 0 || initialization.gameState is not null || initialization.saveData is not null)
            throw new InvalidDataException("The online lobby must describe a new game.");
        initialization.gameID = new GameID(Guid.NewGuid().ToString());
        NativeOnlineConfiguration.Apply(request, initialization);
        var bots = new Il2CppSystem.Collections.Generic.List<TuberPlayerMatchInitData>();
        foreach (var option in request.Options)
            if (option.Key.StartsWith("AIPlayerMetadata.", StringComparison.Ordinal))
                PlayerMatchInitDataPacker.UnpackPlayerMatchInitData(ref bots, option.Key, option.Value);
        if (bots.Count != request.NumberOfAIPlayers)
            throw new InvalidDataException("Root's bot settings did not match the lobby's seats. Return to setup and try again.");
        players = Enumerable.Range(0, request.NumberOfPlayers)
            .Select(seat => new TuberPlayerMatchInitData(seat == 0 ? "Host" : $"Player {seat + 1}", new AccountID(Guid.NewGuid().ToString()))
            { Faction = Factions.Invalid })
            .Concat(bots.ToArray()).ToArray();
        for (var seat = 0; seat < 6; seat++)
        {
            var player = players[seat];
            player.accountID = new AccountID(Guid.NewGuid().ToString());
            player.PlayerStartingOrder = seat;
            player.metadata[NativeHostConfiguration.SeatKey] = seat.ToString(System.Globalization.CultureInfo.InvariantCulture);
            player.metadata["Faction"] = player.Faction.ToString();
            NativeMatchSetup.RecordController(player);
        }
        foreach (var entry in request.PlayerMetadata) players[0].metadata[entry.Key] = entry.Value;
        ApplyFaction(players[0]);
    }

    private static void ApplyFaction(TuberPlayerMatchInitData player)
    {
        if (player.metadata.TryGetValue("Faction", out var faction) && Enum.TryParse<Factions>(faction, out var value)) player.Faction = value;
        if (player.metadata.ContainsKey("AllowedRandomFactions"))
        {
            player.allowedRandomFactions = null;
            player.allowedRandomFactions = player.AllowedRandomFactions(false);
        }
    }

    public object Handle(int seat, string op, JsonElement input)
    {
        if (seat >= request.NumberOfPlayers) return Error("BotSeat");
        if (op == "join")
        {
            if (input.TryGetProperty("name", out var name))
            {
                if (name.ValueKind != JsonValueKind.String || name.GetString() is not { } text || text.Length > 64 || text.Any(char.IsControl))
                    return Error("InvalidRequest");
                if (text.Length != 0) players[seat].name = text;
            }
            if (seats[seat] == SeatState.Absent) seats[seat] = seat == 0 ? SeatState.Joined : SeatState.Choosing;
            if (input.TryGetProperty("metadata", out var local) && local.ValueKind == JsonValueKind.Object)
                foreach (var key in new[] { "OwnedProducts", "clientVersion", "clientPlatform" })
                    if (local.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: <= 128 } text)
                        players[seat].metadata[key] = text;
        }
        if (seats[seat] == SeatState.Absent) return Error("NotInLobby");
        presence.Touch(seat);
        if (op == "chat")
        {
            var result = chat.Add(seat, players[seat].accountID.ToString(), players[seat].name, input);
            return result == ChatResult.Accepted ? new { ok = true, chatAccepted = true } : Error(result.ToString());
        }
        if (op == "lobby-leave")
        {
            seats[seat] = SeatState.Choosing;
            players[seat].Faction = Factions.Invalid;
            players[seat].metadata["Faction"] = Factions.Invalid.ToString();
        }
        else if (op is "lobby-metadata" or "lobby-join")
        {
            if (!input.TryGetProperty("metadata", out var values) || values.ValueKind != JsonValueKind.Object) return Error("InvalidRequest");
            var metadata = new Dictionary<string, string>();
            foreach (var value in values.EnumerateObject())
            {
                if (value.Name is not ("Faction" or "AllowedRandomFactions" or "OwnedProducts" or "clientVersion" or "clientPlatform") ||
                    value.Value.ValueKind != JsonValueKind.String || value.Value.GetString() is not { } text || text.Length > 1024)
                    return Error("InvalidRequest");
                metadata[value.Name] = text;
            }
            if (metadata.TryGetValue("Faction", out var faction) &&
                (!Enum.TryParse<Factions>(faction, out var chosen) || chosen != Factions.Invalid &&
                    (!lib.data.FactionUtils.ReleasedPlayerFactions.Contains(chosen) || players.Where((_, index) => index != seat).Any(player => player.Faction == chosen))))
                return Error("FactionUnavailable");
            var replacement = JSON.Deserialize<TuberPlayerMatchInitData>(JSON.ToJSON(players[seat], false));
            foreach (var value in metadata) replacement.metadata[value.Key] = value.Value;
            try { ApplyFaction(replacement); }
            catch (Il2CppInterop.Runtime.Il2CppException) { return Error("InvalidRequest"); }
            var proposed = players.ToArray();
            proposed[seat] = replacement;
            if (NativeMatchSetup.Validate(NativeMatchSetup.Read(initialization, proposed), pendingLobby: true) is not null)
                return Error("FactionUnavailable");
            players[seat] = replacement;
            if (op == "lobby-join") seats[seat] = SeatState.Joined;
        }
        else if (op == "lobby-start")
        {
            if (seat != 0) return Error("HostOnly");
            if (!CanStart) return Error("PlayersMissing");
            foreach (var player in players) initialization.AddTuberPlayer(player);
            Started = new HostedMatch(initialization);
            Started.Chat.Restore(chat.Messages);
            for (var index = 0; index < request.NumberOfPlayers; index++) Started.Lobby.SetName(index, players[index].name);
            return new { ok = true };
        }
        else if (op is not ("join" or "poll")) return Error("LobbyNotStarted");
        return View(seat, input);
    }

    public object View(int seat, JsonElement input = default)
    {
        var metadata = new DWDPendingGameMetadata
        {
            GameID = initialization.gameID, CreatorID = players[0].accountID,
            Name = "Six Player", MaximumPlayerSessionCount = request.NumberOfPlayers,
            AIPlayerCount = request.NumberOfAIPlayers,
            CurrentPlayerSessionCount = seats.Take(request.NumberOfPlayers).Count(value => value == SeatState.Joined),
            AlreadyInGame = seats[seat] == SeatState.Joined, HasPassword = false, MatchType = request.MatchType,
            Options = request.Options, MatchInitData = request.MatchInitData, PlayersInGame = new()
        };
        for (var index = 0; index < request.NumberOfPlayers; index++)
            if (seats[index] == SeatState.Joined) metadata.PlayersInGame.Add(new AccountIDUsernameMetadata
            { accountID = players[index].accountID, username = players[index].name, metadata = players[index].metadata });
        return new
        {
            ok = true, phase = "lobby", seat, account = players[seat].accountID.ToString(),
            serverTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            setup = Setup, canStart = CanStart,
            chat = input.ValueKind == JsonValueKind.Object ? chat.Read(input) : new ChatUpdate(chat.Version, chat.Messages),
            pendingGame = JsonSerializer.Deserialize<JsonElement>(JSON.ToJSON(metadata, false))
        };
    }

    private bool CanStart => Enumerable.Range(0, request.NumberOfPlayers).All(index => seats[index] == SeatState.Joined && presence.Connected(index));

    private static object Error(string code) => new { ok = false, error = code };
}
