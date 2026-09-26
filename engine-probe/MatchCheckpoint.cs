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
    private sealed record Document(int Version, string Build, JsonElement Initialization, string?[] CompletionMessages);

    public static HostedMatch Load(string path)
    {
        if (new FileInfo(path).Length > MaximumBytes)
            throw new InvalidDataException("Checkpoint exceeds 64 MiB. The file was not changed.");
        var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(path));
        if (document is null || document.Version is not (1 or 2) || document.Build != GameBuild ||
            document.Initialization.ValueKind != JsonValueKind.Object || document.CompletionMessages?.Length != 6)
            throw new InvalidDataException("Checkpoint format or game build is incompatible. The file was not changed.");
        dwd.core.data.ReflectionTypeInitializer.Initialize();
        var init = JSON.Deserialize<TuberMatchInitData>(document.Initialization.GetRawText());
        if (init.TuberPlayers?.Count != 6 || init.gameState is null || init.saveData is null ||
            Enumerable.Range(0, 6).Select(seat => init.TuberPlayers[seat].accountID.ToString()).Distinct().Count() != 6)
            throw new InvalidDataException("Checkpoint lacks a complete six-seat native state. The file was not changed.");
        var host = new HostedMatch(init);
        RestoreOwners(host, init.gameState.Entities);
        host.RestoreCompletionMessages(document.CompletionMessages);
        for (var seat = 0; seat < host.PlayerCount; seat++)
        {
            host.Messages[seat].Clear();
            host.Messages[seat].AddRange(host.Snapshot(seat));
        }
        return host;
    }

    private static void RestoreOwners(HostedMatch host, Canis.messages.SerializedEntity saved)
    {
        // The native loader assigns container ownership to transferred cards,
        // even when the save has a different explicit owner. Preserve the
        // recorded entity state; physical membership stays in the native tree.
        if (saved.OwningPlayerID is { } owner)
            host.Match.GetEntity(saved.EntityID).SetPlayer(host.Match.GetPlayerEntity(owner));
        foreach (var child in saved.Children) RestoreOwners(host, child);
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
        // Older mods cannot distinguish configured AI seats from resigned humans.
        var checkpoint = new Document(2, GameBuild, native.RootElement, host.CompletionMessages);
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
