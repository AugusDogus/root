using System.Text.Json;

namespace RootEngineProbe;

// An allowlisted report, not redacted logs. Never accepts errors, paths, account
// names, invitations, chat, or match snapshots.
internal static class SupportDiagnostics
{
    public static string Create(string platform, string gameVersion, string unityVersion,
        int width, int height, bool playing, bool hosting, string screen, bool? steamOverlayEnabled = null) => JsonSerializer.Serialize(new
        {
            product = "Root Six Player", version = "0.7.4", supportedBuild = "22238765",
            platform, gameVersion, unityVersion, width, height, playing, hosting, screen,
            privatePlaytestContent = true, steamOverlayEnabled
        }, new JsonSerializerOptions { WriteIndented = true });
}
