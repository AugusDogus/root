using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RootEngineProbe;

internal sealed record AuthorityEndpoint(
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("tokens")] string[] Tokens,
    [property: JsonPropertyName("controlToken")] string? ControlToken,
    [property: JsonPropertyName("setup")] JsonElement Setup,
    [property: JsonPropertyName("pending")] bool Pending)
{
    public static AuthorityEndpoint Parse(string text)
    {
        var endpoint = JsonSerializer.Deserialize<AuthorityEndpoint>(text);
        if (endpoint is null || endpoint.Port is < 1 or > 65535 || endpoint.Tokens?.Length != 6 ||
            endpoint.ControlToken is null || endpoint.Setup.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The private host returned invalid connection details. Return to the menu and try again.");
        var credentials = endpoint.Tokens.Append(endpoint.ControlToken).ToArray();
        if (credentials.Any(token => token is null || token.Length != 32 || !token.All(Uri.IsHexDigit)) ||
            credentials.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 7)
            throw new InvalidDataException("The private host returned invalid seat credentials. Return to the menu and try again.");
        return endpoint;
    }
}

// Root owns the rules process. This class has no Unity calls and runs I/O off-thread.
internal sealed class PrivateSession
{
    private readonly string data;
    private readonly SemaphoreSlim gate = new(1, 1);
    private CancellationTokenSource? starting;
    private Process? process;
    private AuthorityEndpoint? endpoint;
    public string? Save { get; private set; }
    public bool HostExited => process is { HasExited: true };

    public PrivateSession(string clientDirectory) => data = Path.GetFullPath(Path.Combine(clientDirectory, ".."));

    internal static void ClearInheritedRuntime(IDictionary<string, string?> environment)
    {
        // Doorstop marks the current process initialized and disables injection in
        // children. A separate Root host needs its own loader and assembly paths.
        foreach (var key in environment.Keys.Where(key => key.StartsWith("ROOT_LAB_", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("DOORSTOP_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("BEPINEX_", StringComparison.OrdinalIgnoreCase) ||
            key == "IL2CPP_INTEROP_DATABASES_LOCATION").ToArray()) environment.Remove(key);
    }

    public Task<AuthorityEndpoint> StartHost(string? save, MatchSetup setup, string? initialization)
    {
        starting?.Dispose();
        starting = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var cancellation = starting.Token;
        return Task.Run(() => StartHostAsync(save, setup, initialization, cancellation));
    }

    internal static string SavePath(string data, string? save)
    {
        var directory = Path.Combine(data, "saves");
        if (save is null) return Path.Combine(directory, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + "_" + Guid.NewGuid().ToString("N")[..8] + ".json");
        if (save.Length == 0 || save.Contains('/') || save.Contains('\\') || Path.GetFileName(save) != save || !save.EndsWith(".json", StringComparison.Ordinal))
            throw new InvalidDataException("Select a saved match from the menu.");
        var path = Path.Combine(directory, save);
        if (!File.Exists(path) || (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new IOException("The selected save is unavailable. Choose another saved match.");
        return path;
    }

    private async Task<AuthorityEndpoint> StartHostAsync(string? save, MatchSetup setup, string? initialization, CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        if (process is not null)
        {
            gate.Release();
            throw new InvalidOperationException("A private host is already running.");
        }
        try
        {
            var saveFile = SavePath(data, save);
            var host = Path.Combine(data, "host");
            var game = Path.Combine(host, "game");
            var executable = Path.Combine(game, "Root.exe");
            if (!File.Exists(executable)) throw new IOException("The private host files are missing. Close Root and open the launcher to repair setup.");
            Directory.CreateDirectory(Path.Combine(data, "saves"));
            foreach (var item in new[] { (Name: "native-setup.json", Text: initialization), (Name: "match-setup.json", Text: JsonSerializer.Serialize(setup)) })
            {
                var file = Path.Combine(host, item.Name);
                if (save is not null || string.IsNullOrEmpty(item.Text)) File.Delete(file);
                else
                {
                    using var parsed = JsonDocument.Parse(item.Text);
                    if (parsed.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("The match settings are invalid. Return to setup and try again.");
                    File.WriteAllText(file, item.Text);
                }
            }
            var output = Path.Combine(host, "results", "server");
            Directory.CreateDirectory(output);
            var endpointFile = Path.Combine(output, "endpoint.json");
            File.Delete(endpointFile);
            var start = new ProcessStartInfo(executable) { WorkingDirectory = game, UseShellExecute = false, CreateNoWindow = true };
            ClearInheritedRuntime(start.Environment);
            start.Environment["ROOT_LAB_MODE"] = "server";
            start.Environment["ROOT_LAB_SAVE_FILE"] = saveFile;
            start.Environment["ROOT_LAB_RESUME"] = save is null ? "0" : "1";
            start.Environment["ROOT_LAB_LIFETIME_SECONDS"] = "43200";
            using var owner = Process.GetCurrentProcess();
            start.Environment["ROOT_LAB_PARENT_PID"] = owner.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            start.Environment["ROOT_LAB_PARENT_START"] = owner.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
            foreach (var argument in new[] { "--enable-console", "false", "-batchmode", "-nographics", "-noaudio", "-job-worker-count", "2", "-logFile", Path.Combine(output, "player.log") }) start.ArgumentList.Add(argument);
            cancellation.ThrowIfCancellationRequested();
            process = Process.Start(start) ?? throw new IOException("The private host could not start. Close Root and reopen the launcher.");
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                if (process.HasExited) throw new IOException("The private host exited during startup. Check the host log, then reopen Root. Saved matches are preserved.");
                if (File.Exists(endpointFile))
                {
                    if (new FileInfo(endpointFile).Length > 1024 * 1024) throw new InvalidDataException("The host connection file is too large.");
                    try { endpoint = AuthorityEndpoint.Parse(File.ReadAllText(endpointFile)); }
                    catch (JsonException) { /* The host may still be finishing its write. */ }
                    if (endpoint is not null) { Save = Path.GetFileName(saveFile); return endpoint; }
                }
                await Task.Delay(250, cancellation).ConfigureAwait(false);
            }
        }
        catch
        {
            await StopProcessAsync().ConfigureAwait(false);
            throw;
        }
        finally { gate.Release(); }
    }

    public async Task Stop()
    {
        starting?.Cancel();
        await gate.WaitAsync().ConfigureAwait(false);
        try { await StopProcessAsync().ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private async Task StopProcessAsync()
    {
        if (process is not { } child) return;
        try
        {
            if (!child.HasExited && endpoint is { } ready)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try
                {
                    using var tcp = new TcpClient();
                    await tcp.ConnectAsync("127.0.0.1", ready.Port, timeout.Token).ConfigureAwait(false);
                    using var writer = new StreamWriter(tcp.GetStream()) { AutoFlush = true };
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new { op = "shutdown", token = ready.ControlToken })).WaitAsync(timeout.Token).ConfigureAwait(false);
                    await child.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or SocketException or OperationCanceledException) { /* Terminate only our owned child below. */ }
            }
            if (!child.HasExited) child.Kill();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        finally
        {
            if (child.HasExited)
            {
                child.Dispose(); process = null; endpoint = null;
                File.Delete(Path.Combine(data, "host", "results", "server", "endpoint.json"));
                File.Delete(Path.Combine(data, "host", "native-setup.json"));
                File.Delete(Path.Combine(data, "host", "match-setup.json"));
            }
        }
    }
}
