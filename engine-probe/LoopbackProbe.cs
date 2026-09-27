using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using BepInEx.Logging;

namespace RootEngineProbe;

// Development transport only. One request per connection, bound to loopback.
// Native snapshots and validated selection responses are scoped to one seat.
internal static class LoopbackProbe
{
    public static IEnumerator<object?> Run(ManualLogSource log, Action<Il2CppSystem.Collections.IEnumerator> startAi)
    {
        using var owner = new AuthorityOwner();
        if (!owner.Alive) { UnityEngine.Application.Quit(); yield break; }
        var output = Path.GetFullPath(Path.Combine(BepInEx.Paths.GameRootPath, "..", "results", "server"));
        Directory.CreateDirectory(output);
        var endpointFile = Path.Combine(output, "endpoint.json");
        var listener = new TcpListener(IPAddress.Loopback, 0);
        HostedMatch? activeHost = null;
        try
        {
            var checkpoint = Environment.GetEnvironmentVariable("ROOT_LAB_SAVE_FILE");
            var resume = Environment.GetEnvironmentVariable("ROOT_LAB_RESUME") == "1";
            if (resume && string.IsNullOrEmpty(checkpoint))
                throw new InvalidDataException("Resuming requires a checkpoint path.");
            if (resume == false && checkpoint is not null && File.Exists(checkpoint))
                throw new IOException("Checkpoint already exists. Resume it or choose a new save path; the existing file was not changed.");
            var settingsFile = Path.GetFullPath(Path.Combine(BepInEx.Paths.GameRootPath, "..", "match-setup.json"));
            var nativeFile = Path.GetFullPath(Path.Combine(BepInEx.Paths.GameRootPath, "..", "native-setup.json"));
            var nativeJson = File.Exists(nativeFile) ? File.ReadAllText(nativeFile) : null;
            using var nativeDocument = nativeJson is null ? null : JsonDocument.Parse(nativeJson);
            var lobby = !resume && nativeDocument?.RootElement.TryGetProperty("name", out var kind) == true && kind.GetString() == "CreateLobbyGame"
                ? new PendingLobby(nativeJson ?? throw new InvalidDataException("Missing online configuration.")) : null;
            var host = lobby is not null ? null : resume && checkpoint is not null ? MatchCheckpoint.Load(checkpoint)
                : nativeJson is not null ? new HostedMatch(NativeHostConfiguration.Parse(nativeJson))
                : File.Exists(settingsFile) ? new HostedMatch(MatchSetup.Parse(File.ReadAllText(settingsFile))) : new HostedMatch(sixPlayers: true);
            activeHost = host;
            if (checkpoint is not null && resume == false && host is not null) MatchCheckpoint.Save(host, checkpoint);
            var tokens = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid().ToString("N")).ToArray();
            var controlToken = Guid.NewGuid().ToString("N");
            listener.Start();
            if (listener.LocalEndpoint is not IPEndPoint endpoint)
                throw new InvalidOperationException("Loopback listener did not expose an IP endpoint.");
            File.WriteAllText(endpointFile, JsonSerializer.Serialize(new { port = endpoint.Port, tokens, controlToken, setup = host?.PublicSetup ?? lobby?.Setup, pending = lobby is not null }));
            log.LogInfo($"SERVER: six-player engine listening on 127.0.0.1:{endpoint.Port}");
            var elapsed = Stopwatch.StartNew();
            var lifetime = int.TryParse(Environment.GetEnvironmentVariable("ROOT_LAB_LIFETIME_SECONDS"), out var seconds)
                && seconds >= 30 && seconds <= 43200 ? seconds : 300;
            var running = true;
            var savedCursor = host?.Messages.Sum(history => history.Count) ?? 0;
            var playerSession = Environment.GetEnvironmentVariable("ROOT_FRIENDS_LAUNCHER") == "1";
            while (running && owner.Alive && (playerSession || elapsed.Elapsed < TimeSpan.FromSeconds(lifetime)))
            {
                yield return null;
                host?.Timers.Tick();
                var resigned = host?.CompleteResignation() ?? false;
                host?.AdvanceAIPlayers(startAi);
                var currentCursor = host?.Messages.Sum(history => history.Count) ?? 0;
                if (host is not null && checkpoint is not null && !host.Resigning && (resigned || savedCursor != currentCursor))
                {
                    MatchCheckpoint.Save(host, checkpoint);
                    savedCursor = currentCursor;
                }
                if (listener.Pending() == false)
                {
                    continue;
                }
                using var client = listener.AcceptTcpClient();
                client.ReceiveTimeout = 3000;
                client.SendTimeout = 5000;
                using var stream = client.GetStream();
                try
                {
                    using var request = JsonDocument.Parse(ReadRequest(stream));
                    var root = request.RootElement;
                    object reply;
                    if (root.ValueKind != JsonValueKind.Object ||
                        TryString(root, "op", out var op) == false ||
                        TryString(root, "token", out var token) == false)
                        reply = Error("InvalidRequest");
                    else if (op == "shutdown" && token == controlToken)
                    {
                        running = false;
                        reply = new { ok = true };
                    }
                    else
                    {
                        var seat = Array.IndexOf(tokens, token);
                        reply = seat < 0 ? Error("Unauthorized") : host is not null ? Handle(host, seat, op, root)
                            : lobby?.Handle(seat, op, root) ?? Error("HostUnavailable");
                        host ??= lobby?.Started;
                        activeHost = host;
                        if (host is not null && checkpoint is not null && seat >= 0 && !host.Resigning && op is not ("poll" or "join" or "ready") &&
                            JsonSerializer.SerializeToElement(reply).GetProperty("ok").GetBoolean())
                        {
                            try { MatchCheckpoint.Save(host, checkpoint); savedCursor = host.Messages.Sum(history => history.Count); }
                            catch (Exception error) when (error is IOException or InvalidDataException)
                            {
                                throw new InvalidOperationException("Checkpoint write failed. Host stopped before acknowledging the move; the previous checkpoint is preserved.", error);
                            }
                        }
                    }
                    WriteReply(stream, reply);
                }
                catch (Exception error) when (error is JsonException or DecoderFallbackException or InvalidDataException)
                {
                    WriteReply(stream, Error("InvalidRequest"));
                }
                catch (IOException error)
                {
                    log.LogWarning($"SERVER: client connection ended: {error.GetType().Name}");
                }
            }
            log.LogInfo("SERVER: stopped");
        }
        finally
        {
            activeHost?.Timers.Stop();
            listener.Stop();
            File.Delete(endpointFile);
        }
        UnityEngine.Application.Quit();
    }

    private static object Handle(HostedMatch host, int seat, string op, JsonElement request)
    {
        if (!host.IsHumanSeat(seat)) return Error("BotSeat");
        if (op is "lobby-start" or "lobby-metadata" or "lobby-join" or "lobby-leave") return Error("LobbyAlreadyStarted");
        host.Lobby.Touch(seat);
        if (op == "chat")
        {
            var player = host.SeatInitialization[seat];
            var result = host.Chat.Add(seat, player.accountID.ToString(), player.name, request);
            return result == ChatResult.Accepted ? new { ok = true, chatAccepted = true } : Error(result.ToString());
        }
        if (op == "join" && request.TryGetProperty("name", out var name))
        {
            if (name.ValueKind != JsonValueKind.String || name.GetString() is not { } text || text.Length > 64 || text.Any(char.IsControl))
                return Error("InvalidRequest");
            if (text.Length != 0)
            {
                host.Lobby.SetName(seat, text);
                host.SeatInitialization[seat].name = text;
            }
        }
        if (op is "poll" or "join")
        {
            var after = 0;
            if (op == "poll" && (TryInt(request, "after", out after) == false || after < 0 || after > host.Messages[seat].Count))
                return Error("InvalidCursor");
            var history = host.Messages[seat];
            var reset = op == "join" || after < history.First;
            (string[] Messages, int Next) batch;
            try { batch = reset ? (host.Snapshot(seat), history.Count) : history.Read(after); }
            catch (InvalidDataException) when (!reset)
            {
                // A single update cannot be split inside native JSON. Replace it
                // with the current private state instead of retrying it forever.
                reset = true;
                batch = (host.Snapshot(seat), history.Count);
            }
            var setup = host.PublicSetup;
            return new
            {
                ok = true,
                seat,
                players = host.PlayerCount,
                serverTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                // The rules set this inherited flag at victory. The similarly
                // named TuberMatch.GameOver remains false in completed matches.
                gameOver = host.Match.GameOverD,
                transitioning = host.Resigning,
                winner = host.Match.WinnerExists ? host.Match.Winner.ToString() : null,
                gameId = host.Match.TuberMatchInitData.gameID.ToString(),
                setup,
                chat = host.Chat.Read(request),
                initialization = op == "join" || after == 0 ? (JsonElement?)NativeClientConfiguration.Create(host) : null,
                roster = Enumerable.Range(0, host.PlayerCount).Select(i => new
                {
                    account = host.SeatInitialization[i].accountID.ToString(),
                    faction = (int)host.SeatPlayers[i].Faction,
                    name = MatchLobby.CleanName(host.SeatInitialization[i].name)
                }).ToArray(),
                next = batch.Next,
                reset,
                lobby = host.Lobby.Status(setup,
                    index => host.Match.HasResigned(host.SeatInitialization[index].accountID)),
                offer = host.GetOffer(seat),
                messages = batch.Messages.Select(json => JsonSerializer.Deserialize<JsonElement>(json)).ToArray()
            };
        }
        if (op == "ready")
        {
            if (!request.TryGetProperty("ready", out var ready) || ready.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return Error("InvalidRequest");
            host.Lobby.SetReady(seat, ready.GetBoolean());
            return new { ok = true };
        }
        if (op == "resign")
        {
            if (host.Resigning) return Error("MatchBusy");
            host.Resign(seat);
            return new { ok = true, pending = host.Resigning };
        }
        if (host.Resigning || host.Timers.Capture(host).Any(timer => timer.EndsAt <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
            return Error("MatchBusy");
        if (host.Match.GameOverD) return Error("MatchFinished");
        if (host.Match.HasResigned(host.SeatInitialization[seat].accountID)) return Error("SeatResigned");
        if (op == "prices")
        {
            if (TryInt(request, "counter", out var counter) == false || counter < 0 ||
                TryInt(request, "handCard", out var handCard) == false ||
                TryInt(request, "riverboats", out var riverboats) == false ||
                TryInt(request, "mercenaries", out var mercenaries) == false)
                return Error("InvalidSelection");
            var result = host.SetPrices(seat, counter, handCard, riverboats, mercenaries);
            return result == ChoiceResult.Accepted ? new { ok = true } : Error(result.ToString());
        }
        if (op == "targets")
        {
            if (TryInt(request, "counter", out var counter) == false || counter < 0 ||
                TryString(request, "source", out var source) == false || Guid.TryParse(source, out _) == false ||
                request.TryGetProperty("targets", out var targets) == false || TargetChoice.TryParse(targets, out var choices) == false)
                return Error("InvalidSelection");
            var result = host.ChooseTargets(seat, counter, source, choices);
            return result == ChoiceResult.Accepted ? new { ok = true } : Error(result.ToString());
        }
        if (op == "custom")
        {
            if (TryInt(request, "counter", out var counter) == false || counter < 0 ||
                request.TryGetProperty("choice", out var selected) == false)
                return Error("InvalidSelection");
            int? choice = null;
            if (selected.ValueKind != JsonValueKind.Null)
            {
                if (selected.ValueKind != JsonValueKind.Number || selected.TryGetInt32(out var number) == false)
                    return Error("InvalidSelection");
                choice = number;
            }
            var result = host.ChooseCustom(seat, counter, choice);
            return result == ChoiceResult.Accepted ? new { ok = true } : Error(result.ToString());
        }
        if (op == "pass")
        {
            if (TryInt(request, "counter", out var counter) == false || counter < 0)
                return Error("InvalidSelection");
            var result = host.Pass(seat, counter);
            return result == ChoiceResult.Accepted ? new { ok = true } : Error(result.ToString());
        }
        if (op == "choose")
        {
            if (TryInt(request, "counter", out var counter) == false || counter < 0 ||
                TryString(request, "source", out var source) == false || Guid.TryParse(source, out _) == false ||
                TryString(request, "target", out var target) == false || Guid.TryParse(target, out _) == false)
                return Error("InvalidSelection");
            var result = host.Choose(seat, counter, source, target);
            return result == ChoiceResult.Accepted ? new { ok = true } : Error(result.ToString());
        }
        return Error("UnknownOperation");
    }

    private static object Error(string code) => new { ok = false, error = code };

    private static bool TryString(JsonElement root, string key, out string value)
    {
        value = "";
        if (root.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.String && property.GetString() is string text)
        {
            value = text;
            return true;
        }
        return false;
    }

    private static bool TryInt(JsonElement root, string key, out int value)
    {
        value = 0;
        return root.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out value);
    }

    private static string ReadRequest(NetworkStream stream)
    {
        using var buffer = new MemoryStream();
        while (buffer.Length < 65536)
        {
            var next = stream.ReadByte();
            if (next == '\n')
                return new UTF8Encoding(false, true).GetString(buffer.ToArray());
            if (next < 0)
                throw new InvalidDataException("Request ended before its newline.");
            buffer.WriteByte((byte)next);
        }
        throw new InvalidDataException("Request exceeds 64 KiB.");
    }

    private static void WriteReply(NetworkStream stream, object reply)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(reply) + "\n");
        if (bytes.Length > SteamFrames.MaxResponse)
            bytes = Encoding.UTF8.GetBytes("{\"ok\":false,\"error\":\"SnapshotTooLarge\"}\n");
        stream.Write(bytes);
    }
}
