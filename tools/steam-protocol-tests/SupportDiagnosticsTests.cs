using System.Text.Json;
using RootEngineProbe;

internal static class SupportDiagnosticsTests
{
    public static void Run()
    {
        using var report = JsonDocument.Parse(SupportDiagnostics.Create("WindowsPlayer", "2.1.5", "2022.3.62f1", 1920, 1080, true, false, "error"));
        var expected = new[] { "product", "version", "supportedBuild", "platform", "gameVersion", "unityVersion", "width", "height", "playing", "hosting", "screen", "privatePlaytestContent" };
        if (!report.RootElement.EnumerateObject().Select(property => property.Name).OrderBy(name => name).SequenceEqual(expected.OrderBy(name => name)))
            throw new Exception("Support report must contain only the approved public fields.");
        if (report.RootElement.GetProperty("hosting").GetBoolean() || report.RootElement.GetProperty("height").GetInt32() != 1080)
            throw new Exception("Support report does not describe the current client.");
    }
}
