using System.Collections;
using System.Text;
using System.Text.Json;

namespace RootEngineProbe;

// Absolute cursors survive eviction. A lagging client receives a private snapshot.
internal sealed class SeatHistory : IEnumerable<string>
{
    private readonly Queue<(string Json, int Bytes)> entries = new();
    private readonly int capacity;
    private int bytes;
    public int Count { get; private set; }
    public int First => Count - entries.Count;
    public int RetainedBytes => bytes;
    public SeatHistory(int capacity = 8 * 1024 * 1024) { this.capacity = capacity; }

    public void Add(string json)
    {
        // Match the transport serializer, including its escaping of Unicode.
        using var parsed = JsonDocument.Parse(json);
        var size = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(parsed.RootElement)) + 1;
        entries.Enqueue((json, size));
        Count = checked(Count + 1);
        bytes += size;
        while (bytes > capacity && entries.TryDequeue(out var removed)) bytes -= removed.Bytes;
    }
    public void AddRange(IEnumerable<string> messages) { foreach (var message in messages) Add(message); }
    public void Clear() { entries.Clear(); bytes = 0; Count = 0; }

    public (string[] Messages, int Next) Read(int after, int budget = 3 * 1024 * 1024)
    {
        if (after < First || after > Count) throw new ArgumentOutOfRangeException(nameof(after));
        var result = new List<string>();
        var used = 0;
        foreach (var entry in entries.Skip(after - First))
        {
            if (used + entry.Bytes > budget)
            {
                if (result.Count == 0) throw new InvalidDataException("One game update exceeds the transport limit. Rejoin for a current board snapshot.");
                break;
            }
            result.Add(entry.Json);
            used += entry.Bytes;
        }
        return (result.ToArray(), after + result.Count);
    }
    public IEnumerator<string> GetEnumerator() => entries.Select(entry => entry.Json).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
