using System.Text.Json;

namespace RootEngineProbe;

internal sealed record SeatStatus(int Seat, string Name, string State, bool Ready);

internal sealed class MatchLobby
{
    private readonly DateTime[] seen = new DateTime[6];
    private readonly bool[] ready = new bool[6];
    private readonly string[] names = Enumerable.Repeat("", 6).ToArray();
    public static SeatStatus[] Parse(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 6)
            throw new InvalidDataException("The host returned an invalid table roster. Install the same mod release as the host.");
        var seats = value.Deserialize<SeatStatus[]>() ?? throw new InvalidDataException("The host returned an empty table roster.");
        for (var index = 0; index < seats.Length; index++)
            if (seats[index] is not { } seat || seat.Seat != index + 1 || seat.Name is null || seat.Name.Length > 160 || seat.Name.Any(char.IsControl) ||
                seat.State is not ("Waiting" or "Connected" or "Disconnected" or "Clockwork bot" or "AI · Easy" or "AI · Medium" or "AI · Hard" or "Resigned"))
                throw new InvalidDataException("The host returned invalid seat details. Rejoin using the same mod release as the host.");
        return seats;
    }
    public static string CleanName(string value) => new(value.Where(character => !char.IsControl(character)).Take(64).ToArray());
    public void SetName(int seat, string name)
    {
        if (names[seat] != name) ready[seat] = false;
        names[seat] = name;
    }
    public void Touch(int seat) => seen[seat] = DateTime.UtcNow;
    public bool Connected(int seat) => seen[seat] != default && DateTime.UtcNow - seen[seat] <= TimeSpan.FromSeconds(15);
    public void SetReady(int seat, bool value) { ready[seat] = value; Touch(seat); }
    public SeatStatus[] Status(MatchSetup setup, Func<int, bool> resigned) => Enumerable.Range(0, 6).Select(seat =>
        new SeatStatus(seat + 1, MatchSetup.FactionName(setup.Factions[seat]) + (string.IsNullOrEmpty(names[seat]) ? "" : $": {names[seat]}"),
            MatchSetup.IsClockwork(setup.Factions[seat]) ? "Clockwork bot"
            : setup.AI[seat] is { } difficulty ? $"AI · {difficulty}" : resigned(seat) ? "Resigned"
            : seen[seat] == default ? "Waiting" : DateTime.UtcNow - seen[seat] > TimeSpan.FromSeconds(15) ? "Disconnected" : "Connected",
            ready[seat])).ToArray();
}
