using System.Globalization;
using System.Text.RegularExpressions;

namespace RootEngineProbe;

// Only targeted invitations carry bearer tokens. Never publish these in rich presence.
internal sealed record SteamInvitation(ulong Host, int Port, int Seat, string Token)
{
    public const string Prefix = "root6:6:22238765:";
    public string Encode() => $"{Prefix}{Host}:{Port}:{Seat}:{Token}";

    public static bool TryParse(string? text, out SteamInvitation? invitation)
    {
        invitation = null;
        if (text is null || text.Length >= 256 || !text.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        var fields = text[Prefix.Length..].Split(':');
        if (fields.Length != 4 ||
            !ulong.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var host) ||
            // Public-universe individual desktop Steam identity.
            (host >> 32) != 0x01100001 || (uint)host == 0 ||
            !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 100 or > 999 ||
            !int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var seat) || seat is < 2 or > 6 ||
            !Regex.IsMatch(fields[3], "\\A[0-9a-f]{32}\\z")) return false;
        invitation = new(host, port, seat, fields[3]);
        return true;
    }
}
