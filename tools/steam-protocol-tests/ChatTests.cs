using System.Text.Json;
using RootEngineProbe;

internal static class ChatTests
{
    public static void Run()
    {
        long now = 0;
        var chat = new MatchChat(() => now);
        var account = Guid.NewGuid().ToString();
        JsonElement Request(string text) => JsonSerializer.SerializeToElement(new { text, seat = 5, name = "spoofed" });
        void Check(bool condition, string detail) { if (!condition) throw new Exception(detail); }
        Check(chat.Add(0, account, "Host", Request("hello")) == ChatResult.Accepted, "First chat rejected");
        Check(chat.Messages[0] is { Seat: 1, Name: "Host", Text: "hello" } && chat.Messages[0].Account == account, "Chat identity must come from authority");
        Check(chat.Add(0, account, "Host", Request("too soon")) == ChatResult.ChatRateLimited, "Flood accepted");
        Check(chat.Add(1, account, "Friend", Request("hi")) == ChatResult.Accepted, "Different seat throttled");
        foreach (var text in new[] { "", "  ", "line\nfeed", new string('a', 501) })
            Check(chat.Add(1, account, "Friend", Request(text)) == ChatResult.InvalidChat, "Invalid text accepted");
        for (var i = 0; i < 140; i++) { now += 1000; Check(chat.Add(0, account, "Host", Request($"Message {i}")) == ChatResult.Accepted, "Valid message rejected"); }
        Check(chat.Messages.Length == MatchChat.Capacity && chat.Messages[0].Id == 15, "Chat history must stay bounded");
        Check(chat.Read(JsonSerializer.SerializeToElement(new { chatVersion = chat.Version })) is null, "Unchanged history resent");
        var update = chat.Read(JsonSerializer.SerializeToElement(new { chatVersion = 0 }));
        Check(update is not null && update.Messages.Length == MatchChat.Capacity, "Reconnect lacks history");
        var restored = new MatchChat();
        restored.Restore(MatchChat.Parse(JsonSerializer.SerializeToElement(update)).Messages);
        Check(restored.Version == chat.Version && restored.Messages.SequenceEqual(chat.Messages), "Chat save round trip failed");
        try { restored.Restore(new[] { chat.Messages[1], chat.Messages[0] }); throw new Exception("Unordered chat accepted"); }
        catch (InvalidDataException) { Check(restored.Version == chat.Version, "Invalid restore discarded history"); }
    }
}
