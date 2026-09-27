using System.Text.Json;

namespace RootEngineProbe;

internal sealed record ChatEntry(long Id, int Seat, string Account, string Name, string Text, long Timestamp);
internal sealed record ChatUpdate(long Version, ChatEntry[] Messages);
internal enum ChatResult { Accepted, InvalidChat, ChatRateLimited }

// Seat identity is supplied by the authority, never by the sending client.
internal sealed class MatchChat
{
    public const int Capacity = 128;
    public const int MaximumLength = 500;
    private readonly List<ChatEntry> messages = new();
    private readonly Dictionary<int, long> lastSent = new();
    private readonly Func<long> clock;
    public long Version { get; private set; }
    public ChatEntry[] Messages => messages.ToArray();

    public MatchChat(Func<long>? clock = null) { this.clock = clock ?? (() => Environment.TickCount64); }

    public ChatResult Add(int seat, string account, string name, JsonElement request)
    {
        if (!request.TryGetProperty("text", out var value) || value.ValueKind != JsonValueKind.String ||
            value.GetString() is not { } text || !ValidText(text)) return ChatResult.InvalidChat;
        var now = clock();
        if (lastSent.TryGetValue(seat, out var previous) && now - previous < 1000) return ChatResult.ChatRateLimited;
        lastSent[seat] = now;
        messages.Add(new(++Version, seat + 1, account, MatchLobby.CleanName(name), text.Trim(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        if (messages.Count > Capacity) messages.RemoveAt(0);
        return ChatResult.Accepted;
    }

    public ChatUpdate? Read(JsonElement request) =>
        request.TryGetProperty("chatVersion", out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var known) && known == Version ? null : new(Version, Messages);

    public void Restore(ChatEntry[] entries)
    {
        Validate(entries);
        messages.Clear();
        messages.AddRange(entries);
        Version = entries.LastOrDefault()?.Id ?? 0;
        lastSent.Clear();
    }

    public static ChatUpdate Parse(JsonElement json)
    {
        var update = json.Deserialize<ChatUpdate>() ?? throw new InvalidDataException("The host returned empty chat history. Reconnect to refresh the room.");
        Validate(update.Messages);
        if (update.Version != (update.Messages.LastOrDefault()?.Id ?? 0))
            throw new InvalidDataException("The host returned inconsistent chat history. Reconnect to refresh the room.");
        return update;
    }

    private static bool ValidText(string text) => text.Length is > 0 and <= MaximumLength &&
        !string.IsNullOrWhiteSpace(text) && !text.Any(char.IsControl);

    private static void Validate(ChatEntry[] entries)
    {
        if (entries is null || entries.Length > Capacity)
            throw new InvalidDataException("Chat history exceeds the supported limit. The previous history is preserved.");
        long previous = 0;
        foreach (var entry in entries)
        {
            if (entry is null || entry.Id <= previous || entry.Seat is < 1 or > 6 || !Guid.TryParse(entry.Account, out _) ||
                entry.Name is null || entry.Name.Length > 64 || entry.Name.Any(char.IsControl) ||
                entry.Text is null || !ValidText(entry.Text) || entry.Timestamp is < 0 or > 253402300799999)
                throw new InvalidDataException("Chat history contains an invalid message. The previous history is preserved.");
            previous = entry.Id;
        }
    }
}
