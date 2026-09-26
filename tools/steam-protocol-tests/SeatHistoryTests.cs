using RootEngineProbe;

internal static class SeatHistoryTests
{
    public static void Run()
    {
        var history = new SeatHistory(12);
        history.AddRange(new[] { "\"aaa\"", "\"bbb\"", "\"ccc\"" });
        if (history.Count != 3 || history.First != 1 || history.RetainedBytes != 12)
            throw new Exception("History eviction changed absolute cursors or exceeded its budget");
        var page = history.Read(1, 6);
        if (page.Next != 2 || page.Messages.Single() != "\"bbb\"") throw new Exception("Page skipped a message");
        if (history.Read(page.Next).Messages.Single() != "\"ccc\"") throw new Exception("Page continuation failed");
        if (history.Read(3).Messages.Length != 0) throw new Exception("Idle poll returned history");
        try { history.Read(0); throw new Exception("Evicted cursor was accepted"); }
        catch (ArgumentOutOfRangeException) { }
        history.Add("\"a message larger than the entire history\"");
        if (history.First != history.Count || history.RetainedBytes != 0) throw new Exception("Oversized entry retained");
    }
}
