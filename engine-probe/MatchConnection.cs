using System.Text.Json;

namespace RootEngineProbe;

internal interface IMatchConnection : IDisposable
{
    Task<JsonElement> Exchange(object request);
    void Tick();
}

// A lost acknowledgement is resolved by joining again, never replaying a move.
internal sealed class RecoveringConnection : IMatchConnection
{
    private readonly Func<IMatchConnection> connect;
    private readonly Func<double> now;
    private readonly object join;
    private IMatchConnection? connection;
    private Task<JsonElement>? response;
    private TaskCompletionSource<JsonElement>? pending;
    private int attempts;
    private double retryAt;
    private bool stopped;
    public bool Reconnecting => attempts > 0;

    public RecoveringConnection(Func<IMatchConnection> connect, Func<double> now, string token)
    {
        this.connect = connect; this.now = now;
        join = new { op = "join", token };
        connection = connect();
    }
    public Task<JsonElement> Exchange(object request)
    {
        if (stopped) return Task.FromException<JsonElement>(new IOException("Connection stopped. Rejoin your saved seat from the match menu."));
        if (pending is not null) throw new InvalidOperationException("A match request is already pending.");
        pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (connection is { } active)
        {
            try { response = active.Exchange(request); }
            catch (IOException) { Retry(); }
        }
        return pending.Task;
    }
    public void Tick()
    {
        if (stopped) return;
        try
        {
            if (connection is null && now() >= retryAt)
            {
                connection = connect();
                response = connection.Exchange(join);
            }
            connection?.Tick();
            if (response is not { IsCompleted: true } reply) return;
            var result = reply.GetAwaiter().GetResult();
            response = null;
            pending?.TrySetResult(result);
            pending = null;
            attempts = 0;
        }
        catch (IOException) { Retry(); }
    }
    private void Retry()
    {
        connection?.Dispose(); connection = null;
        // Observe the abandoned request's fault without allowing its result to be replayed.
        if (response is { } abandoned) _ = abandoned.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        response = null;
        attempts++;
        if (attempts > 6)
        {
            var error = new IOException("The host is still unreachable. Check Steam and ask the host to keep the game open, then choose Reconnect. An unconfirmed move was not repeated.");
            pending?.TrySetException(error); pending = null;
            stopped = true;
            throw error;
        }
        retryAt = now() + Math.Min(15, attempts * 2);
    }
    public void Dispose()
    {
        stopped = true;
        connection?.Dispose(); connection = null;
        pending?.TrySetCanceled(); pending = null;
        if (response is { } abandoned) _ = abandoned.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        response = null;
    }
}
