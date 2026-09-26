using System.Text.Json;

namespace RootEngineProbe;

// Main-thread transport. PrivateClient never replays requests after an uncertain failure.
internal sealed class SteamGuest : IDisposable
{
    private readonly SteamSockets sockets;
    private readonly SteamInvitation invitation;
    private readonly uint connection;
    private readonly SteamChannel channel;
    private TaskCompletionSource<JsonElement>? pending;
    private uint id;
    private bool stopped;
    public int IncomingCallbacks { get; private set; }
    public SteamGuest(SteamNative api, SteamInvitation invitation)
    {
        this.invitation = invitation;
        sockets = new(api);
        try { connection = sockets.ConnectTo(invitation.Host, invitation.Port); }
        catch { sockets.Dispose(); throw; }
        channel = new(api, connection, SteamFrames.MaxResponse);
    }
    public Task<JsonElement> Exchange(object request)
    {
        if (stopped) return Task.FromException<JsonElement>(new IOException("Steam connection stopped. Rejoin before retrying a move."));
        if (pending is not null) throw new InvalidOperationException("A Steam request is already pending.");
        var body = JsonSerializer.SerializeToUtf8Bytes(request);
        if (body.Length > SteamFrames.MaxRequest) throw new InvalidDataException("Private request exceeds 64 KiB.");
        if (id == uint.MaxValue) throw new IOException("Steam session request limit reached. Rejoin the match.");
        pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.Send(++id, body);
        channel.ResetDeadline();
        return pending.Task;
    }
    public void Tick()
    {
        if (stopped) return;
        try
        {
            foreach (var change in sockets.Changes())
                if (change.Info.ListenSocket != 0) IncomingCallbacks++;
            var state = sockets.State(connection);
            if (state.State is 4 or 5)
                throw new IOException($"Steam connection ended (reason {state.EndReason}). Check Steam and the host, then rejoin. Do not repeat an unacknowledged move before checking the board.");
            if (channel.TimedOut && (pending is not null || state.State != 3))
                throw new IOException("Steam request timed out. The move may have reached the host. Rejoin to check its result.");
            if (state.State != 3) return;
            if (state.Remote.Type != 16 || state.Remote.SteamId != invitation.Host)
                throw new InvalidDataException("Steam connected to an unexpected host identity.");
            channel.Flush();
            if (channel.Receive() is { } message)
            {
                if (pending is null || message.Id != id) throw new InvalidDataException("Unexpected Steam response ID.");
                using var document = JsonDocument.Parse(message.Body);
                var reply = document.RootElement;
                if (reply.TryGetProperty("seat", out var seat) && seat.GetInt32() != invitation.Seat - 1)
                    throw new InvalidDataException("Host returned a different seat from the invitation.");
                pending.SetResult(reply.Clone());
                pending = null;
            }
        }
        catch (Exception error)
        {
            pending?.TrySetException(error);
            pending = null;
            Dispose();
            throw;
        }
    }
    public void Dispose()
    {
        if (stopped) return;
        stopped = true;
        pending?.TrySetException(new IOException("Steam session stopped. Rejoin to check the match state."));
        pending = null;
        sockets.Dispose();
    }
}
