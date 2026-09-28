using Canis.actions;
using Canis.json;
using Canis.utils.ids;
using System.Text.Json;
using tuber.canis;

namespace RootEngineProbe;

// A checkpoint contains every player's private state. The lab runner's umask
// keeps it private. Seat credentials are deliberately regenerated on restart.
internal static class MatchCheckpoint
{
    private const string GameBuild = "22238765";
    private const long MaximumBytes = 64 * 1024 * 1024;
    private sealed record Document(int Version, string Build, JsonElement Initialization, string?[] CompletionMessages, ChatEntry[]? Chat = null, SavedTurnTimer[]? Timers = null);

    public static HostedMatch Load(string path)
    {
        if (new FileInfo(path).Length > MaximumBytes)
            throw new InvalidDataException("Checkpoint exceeds 64 MiB. The file was not changed.");
        var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(path));
        if (document is null || document.Version is not (1 or 2 or 3 or 4) || document.Build != GameBuild ||
            document.Initialization.ValueKind != JsonValueKind.Object || document.CompletionMessages?.Length != 6)
            throw new InvalidDataException("Checkpoint format or game build is incompatible. The file was not changed.");
        dwd.core.data.ReflectionTypeInitializer.Initialize();
        var init = JSON.Deserialize<TuberMatchInitData>(document.Initialization.GetRawText());
        if (init.TuberPlayers?.Count != 6 || init.gameState is null || init.saveData is null ||
            Enumerable.Range(0, 6).Select(seat => init.TuberPlayers[seat].accountID.ToString()).Distinct().Count() != 6)
            throw new InvalidDataException("Checkpoint lacks a complete six-seat native state. The file was not changed.");
        if (document.Version >= 4 && document.Chat is null) throw new InvalidDataException("Checkpoint chat history is missing. The file was not changed.");
        if (document.Version >= 4 && document.Timers is null) throw new InvalidDataException("Checkpoint timer state is missing. The file was not changed.");
        var host = new HostedMatch(init);
        try
        {
            host.Chat.Restore(document.Chat ?? Array.Empty<ChatEntry>());
            RestoreOwners(host, init.gameState.Entities);
            NativePrivateVisibility.RestoreExploredRuins(host.Match);
            host.Timers.Restore(host, document.Timers ?? Array.Empty<SavedTurnTimer>());
            host.RestoreCompletionMessages(document.CompletionMessages);
            for (var seat = 0; seat < host.PlayerCount; seat++)
            {
                host.Messages[seat].Clear();
                host.Messages[seat].AddRange(host.Snapshot(seat));
            }
            return host;
        }
        catch
        {
            host.Timers.Stop();
            throw;
        }
    }

    private static void RestoreOwners(HostedMatch host, Canis.messages.SerializedEntity saved, AccountID? inheritedOwner = null)
    {
        // Restore both inherited and explicit ownership. During faction drafts
        // the native loader can assign neutral ruins to an unassigned player.
        // Transferred cards also need their saved owner, not their container's.
        var owner = saved.OwningPlayerID ?? inheritedOwner;
        host.Match.GetEntity(saved.EntityID).SetPlayer(owner is null ? null : host.Match.GetPlayerEntity(owner));
        foreach (var child in saved.Children) RestoreOwners(host, child, owner);
    }

    public static void Save(HostedMatch host, string path)
    {
        host.Match.SaveOnPause();
        if (host.Match.SaveData is null)
            throw new InvalidOperationException("The native engine did not produce save data. The previous checkpoint was not changed.");
        var accounts = new Il2CppSystem.Collections.Generic.List<AccountID>();
        foreach (var player in host.Match.TuberMatchInitData.TuberPlayers) accounts.Add(player.accountID);
        var state = new SerializeGameState(host.Match.TuberPlaymat,
            accounts.Cast<Il2CppSystem.Collections.Generic.IEnumerable<AccountID>>(), host.Match).GetState();
        // The native clone assumes optional lists are initialized. JSON preserves
        // the actual initialization, including absent optional expansion lists.
        var init = JSON.Deserialize<TuberMatchInitData>(host.CheckpointInitialization);
        // Resignation changes native control to AI. The cached starting roster
        // must not turn that faction back into a human seat when loading a save.
        foreach (var savedPlayer in host.Match.SaveData.orderedPlayers)
        {
            var player = init.TuberPlayers.ToArray().Single(item => item.accountID.ToString() == savedPlayer.AccountID.ToString());
            player.isHuman = savedPlayer.IsHuman;
            player.aiLevel = savedPlayer.AILevel;
        }
        init.gameState = state;
        init.saveData = host.Match.SaveData;
        init.currentTurnPlayer = host.Match.ActiveAccountID;
        using var native = JsonDocument.Parse(JSON.ToJSON(init, false));
        // Older mods cannot restore stable seats while factions are still being drafted.
        var checkpoint = new Document(4, GameBuild, native.RootElement, host.CompletionMessages, host.Chat.Messages, host.Timers.Capture(host));
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is null) throw new InvalidDataException("Checkpoint path has no directory.");
        Directory.CreateDirectory(directory);
        var temporary = path + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, checkpoint);
                if (file.Length > MaximumBytes)
                    throw new InvalidDataException("Checkpoint exceeds 64 MiB. The previous checkpoint was not changed.");
                file.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { File.Delete(temporary); }
    }
}
