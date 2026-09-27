using System.Diagnostics;
using System.Globalization;

namespace RootEngineProbe;

// An orphaned rules process must not keep advancing AI or timers after Root exits.
internal sealed class AuthorityOwner : IDisposable
{
    private readonly Process? parent;
    private readonly long started;
    private readonly bool required;
    public AuthorityOwner()
    {
        var id = Environment.GetEnvironmentVariable("ROOT_LAB_PARENT_PID");
        required = id is not null;
        if (!required) return;
        if (!int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 0 ||
            !long.TryParse(Environment.GetEnvironmentVariable("ROOT_LAB_PARENT_START"), NumberStyles.None, CultureInfo.InvariantCulture, out started))
            throw new InvalidDataException("The private host owner is invalid; no match was started.");
        try { parent = Process.GetProcessById(pid); }
        catch (ArgumentException) { }
    }
    public bool Alive => !required || parent is { HasExited: false } && parent.StartTime.ToUniversalTime().Ticks == started;
    public void Dispose() => parent?.Dispose();
}
