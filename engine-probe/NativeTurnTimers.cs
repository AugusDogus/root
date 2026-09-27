using Canis.json;
using Canis.json.events;
using Canis.messages.timer;
using Canis.utils.ids;
using tuber.canis;

namespace RootEngineProbe;

internal sealed record SavedTurnTimer(string Account, string TimerID, string Type, int WaitTime, int TotalTime, long StartedAt, long EndsAt);

// Root owns the turn bank, grace period, cancellation, and timeout-to-AI rule.
// Keep only the latest display messages so reconnecting clients see its clock.
internal sealed class NativeTurnTimers
{
    private sealed record Display(string Timer, string Json, long EndsAt, SavedTurnTimer Saved);
    private readonly Dictionary<string, Dictionary<string, Display>> recipients = new();
    private NativeTimerScheduler? scheduler;
    private readonly Dictionary<(string Timer, string Account), SavedTurnTimer> restored = new();

    public void Attach(TuberMatch match) => scheduler = new NativeTimerScheduler(match);
    public void Tick() => scheduler?.Tick();
    public void Stop() { scheduler?.Dispose(); scheduler = null; }

    public static void Configure(TuberMatch match)
    {
        var value = match.TuberMatchInitData.ValueForOption("Timers", "0");
        if (!int.TryParse(value, out var seconds) || seconds < 0)
            throw new InvalidDataException("The turn timer is invalid. Choose a timer in game setup and create the room again.");
        // Configure defaults these flags to true, even when Timers is zero.
        match.UseTimers = seconds > 0;
        if (seconds == 0) match.UseTurnTimers = false;
    }

    public string Observe(AccountID recipient, DWDEvent message)
    {
        var json = JSON.ToJSON(message, false);
        var account = recipient.ToString();
        if (!recipients.TryGetValue(account, out var timers))
        {
            timers = new();
            recipients.Add(account, timers);
        }
        if (message.TryCast<DisplayTimer>() is { } nativeDisplay)
        {
            // Root's timestamp helper encodes local wall-clock ticks. Convert a
            // copy because the same native message is broadcast to every seat.
            var display = PublicClock(nativeDisplay);
            var timerID = display.TimerID.ToString();
            var timedAccount = display.AccountID.ToString();
            if (restored.TryGetValue((timerID, timedAccount), out var previous))
            {
                display.StartedAt = previous.StartedAt;
                display.EndsAt = previous.EndsAt;
                display.TotalTime = previous.TotalTime;
                display.WaitTime = previous.WaitTime;
            }
            var saved = new SavedTurnTimer(timedAccount, timerID, "", display.WaitTime, display.TotalTime, display.StartedAt, display.EndsAt);
            json = JSON.ToJSON(display, false);
            timers[timedAccount] = new(timerID, json, display.EndsAt, saved);
        }
        else if (message.TryCast<HideTimer>() is { } hide)
        {
            var id = hide.TimerID.ToString();
            var hiddenAccount = hide.AccountID?.ToString();
            foreach (var key in timers.Where(timer => timer.Value.Timer == id && (hiddenAccount is null || timer.Key == hiddenAccount))
                .Select(timer => timer.Key).ToArray()) timers.Remove(key);
        }
        return json;
    }

    public static DisplayTimer PublicClock(DisplayTimer native)
    {
        var display = JSON.Deserialize<DisplayTimer>(JSON.ToJSON(native, false));
        var local = Il2CppSystem.DateTime.Now;
        var correction = (local.ToUniversalTime().Ticks - local.Ticks) / TimeSpan.TicksPerMillisecond;
        display.StartedAt += correction;
        display.EndsAt += correction;
        return display;
    }

    public SavedTurnTimer[] Capture(HostedMatch host)
    {
        if (host.Match.TimerID is not { } active) return Array.Empty<SavedTurnTimer>();
        var id = active.ToString();
        var pending = host.SeatPlayers.Where(player => !player.IsAI && !host.Match.HasResigned(player.AccountID) &&
            host.Thread.HasPendingResponse(host.Thread.GetCounterForPlayerEntity(player))).Select(player => player.AccountID.ToString()).ToHashSet();
        return recipients.Values.SelectMany(timers => timers.Values).Where(timer => timer.Timer == id && pending.Contains(timer.Saved.Account))
            .Select(timer => timer.Saved with { Type = host.Match.TimerType }).DistinctBy(timer => timer.Account).ToArray();
    }

    public void Restore(HostedMatch host, SavedTurnTimer[] saved)
    {
        if (saved.Length == 0) return;
        if (!host.Match.UseTimers)
            throw new InvalidDataException("Saved countdowns conflict with disabled match timers. The checkpoint was not changed.");
        if (scheduler is null) throw new InvalidOperationException("Native timer scheduler is not attached.");
        if (saved.Any(timer => timer is null || string.IsNullOrWhiteSpace(timer.Account) || string.IsNullOrWhiteSpace(timer.TimerID) ||
            timer.StartedAt < 0 || timer.EndsAt > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds() ||
            timer.StartedAt > timer.EndsAt || timer.WaitTime < 0 || timer.TotalTime < 0))
            throw new InvalidDataException("Saved timer configuration is invalid. The checkpoint was not changed.");
        if (saved.Length > host.PlayerCount || saved.Select(timer => timer.Account).Distinct().Count() != saved.Length)
            throw new InvalidDataException("Saved timers do not match the private roster. The checkpoint was not changed.");
        var groups = saved.GroupBy(timer => timer.TimerID).ToArray();
        if (groups.Length != 1) throw new InvalidDataException("Saved timers contain more than one native selection group.");
        var group = groups[0].ToArray();
        var first = group[0];
        if (first.Type is not ("SetTimer" or "SetSimultaneousTimer") || first.Type == "SetTimer" && group.Length != 1 ||
            group.Any(timer => timer.Type != first.Type || timer.EndsAt != first.EndsAt))
            throw new InvalidDataException("Saved timer configuration is invalid. The checkpoint was not changed.");
        var selections = new Il2CppSystem.Collections.Generic.Dictionary<AccountID, Networking.selection.messages.SelectionMessage>();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var grace = Math.Max(0, first.EndsAt - first.StartedAt - first.WaitTime * 1000L);
        var wait = (int)Math.Min(int.MaxValue, Math.Max(0, Math.Ceiling((first.EndsAt - now - grace) / 1000d)));
        foreach (var timer in group)
        {
            var player = host.SeatPlayers.SingleOrDefault(candidate => candidate.AccountID.ToString() == timer.Account);
            if (player is null || player.IsAI || host.Match.HasResigned(player.AccountID))
                throw new InvalidDataException("A saved timer belongs to an unavailable human seat.");
            var counter = host.Thread.GetCounterForPlayerEntity(player);
            if (!host.Thread.HasPendingResponse(counter)) throw new InvalidDataException("A saved timer has no pending native decision.");
            selections.Add(player.AccountID, host.Thread.GetPlayerPendingResponse(counter).Item2.Selection);
        }
        foreach (var timer in group) restored[(timer.TimerID, timer.Account)] = timer;
        var id = new TimerID(first.TimerID);
        scheduler.SetRestoredDeadline(first.TimerID, first.StartedAt, first.EndsAt);
        host.Match.TimerID = id;
        host.Match.TimerType = first.Type;
        if (first.Type == "SetTimer")
        {
            var account = new AccountID(first.Account);
            host.Match.Write(new SetTimer { TimerID = id, AccountID = account, Wait = wait,
                SelectionMessage = selections[account] }, account);
        }
        else
        {
            host.Match.ActiveSimultaneousAccounts = selections;
            host.Match.Write(new SetSimultaneousTimer(host.Match.GameID) { TimerID = id, Wait = wait, SelectionMessageMap = selections }, new AccountID(first.Account));
        }
    }

    public string[] Snapshot(AccountID recipient)
    {
        if (!recipients.TryGetValue(recipient.ToString(), out var timers)) return Array.Empty<string>();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return timers.Values.Where(timer => timer.EndsAt > now).Select(timer => timer.Json).ToArray();
    }
}
