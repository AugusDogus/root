using System.Text.Json;
using RootEngineProbe;

internal static class RecoveryTests
{
    private sealed class Connection : IMatchConnection
    {
        public bool Fail;
        public readonly List<string> Operations = new();
        public Task<JsonElement> Exchange(object request)
        {
            Operations.Add(JsonSerializer.SerializeToElement(request).GetProperty("op").GetString() ?? "");
            return Fail ? Task.FromException<JsonElement>(new IOException("Lost acknowledgement"))
                : Task.FromResult(JsonSerializer.SerializeToElement(new { ok = true, reset = true }));
        }
        public void Tick() { }
        public void Dispose() { }
    }
    public static void Run()
    {
        var broken = new Connection { Fail = true };
        var recovered = new Connection();
        var created = 0;
        double now = 0;
        using var connection = new RecoveringConnection(() => ++created == 1 ? broken : recovered, () => now, "test-seat");
        var move = connection.Exchange(new { op = "targets" });
        connection.Tick();
        if (move.IsCompleted || !connection.Reconnecting) throw new Exception("A lost move was reported complete");
        now = 10;
        connection.Tick();
        if (!move.GetAwaiter().GetResult().GetProperty("reset").GetBoolean()) throw new Exception("Recovery missed the current snapshot");
        if (!broken.Operations.SequenceEqual(new[] { "targets" }) || !recovered.Operations.SequenceEqual(new[] { "join" }))
            throw new Exception("Recovery replayed an uncertain move");
        if (connection.Reconnecting) throw new Exception("Recovery status never cleared");

        var retries = 0;
        using var unreachable = new RecoveringConnection(() => { retries++; return new Connection { Fail = true }; }, () => now, "test-seat");
        var uncertain = unreachable.Exchange(new { op = "targets" });
        var exhausted = false;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            now += 20;
            try { unreachable.Tick(); }
            catch (IOException) { exhausted = true; break; }
        }
        if (!exhausted || retries != 7 || !uncertain.IsFaulted)
            throw new Exception("An unreachable host did not exhaust its bounded recovery attempts");
        try { uncertain.GetAwaiter().GetResult(); }
        catch (IOException error) when (error.Message.Contains("Reconnect")) { return; }
        throw new Exception("Exhausted recovery did not explain how to reconnect");
    }
}
