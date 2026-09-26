using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace RootEngineProbe;

// Private loopback control plane. Gameplay and invitations stay in Root's Steam process.
internal static class LauncherControl
{
    public static Task StartHost(string lab, string? save, MatchSetup setup) => Run(lab,
        save is null ? "host-native" : "resume-native", new { save = save ?? "", setup = JsonSerializer.Serialize(setup) }, true);
    public static Task ReturnHome(string lab) => Run(lab, "return-menu", new { }, false);

    private static async Task Run(string lab, string action, object values, bool wait)
    {
        using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(lab, "launcher-control.json")));
        var port = config.RootElement.GetProperty("port").GetInt32();
        var token = config.RootElement.GetProperty("token").GetString();
        if (port is < 1 or > 65535 || token?.Length != 43)
            throw new InvalidDataException("Close Root and reopen the six-player launcher to reconnect its controls.");
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(10)
        };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var content = new StringContent(JsonSerializer.Serialize(values), Encoding.UTF8, "application/json");
        // The loopback API intentionally accepts an exact content type.
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await http.PostAsync("/api/" + action, content);
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (!response.IsSuccessStatusCode)
            throw new IOException(result.RootElement.GetProperty("error").GetString());
        if (!wait) return;
        var deadline = DateTime.UtcNow.AddMinutes(11);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(500);
            using var statusResponse = await http.GetAsync("/api/status");
            statusResponse.EnsureSuccessStatusCode();
            using var status = JsonDocument.Parse(await statusResponse.Content.ReadAsStringAsync());
            if (status.RootElement.GetProperty("busy").GetBoolean()) continue;
            var error = status.RootElement.GetProperty("error").GetString();
            if (!string.IsNullOrEmpty(error)) throw new IOException(error);
            return;
        }
        throw new IOException("The host did not finish starting. Close Root and reopen the launcher. Saved matches are preserved.");
    }
}
