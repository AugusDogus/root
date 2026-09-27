using Canis.actions.timers;
using HarmonyLib;
using NativeContext = Il2CppSystem.Threading.SynchronizationContext;

namespace RootEngineProbe;

// Native Task continuations otherwise resume on IL2CPP pool threads. Capture
// a native Unity context only for this authority's timers and pump it on Update.
internal sealed class NativeTimerScheduler : IDisposable
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, WeakReference<NativeTimerScheduler>> owners = new();
    private static bool installed;
    private readonly tuber.canis.TuberMatch match;
    private readonly UnityEngine.UnitySynchronizationContext context;
    private readonly int mainThread = Environment.CurrentManagedThreadId;
    private sealed record RestoredClock(long StartedAt, long EndsAt);
    private readonly Dictionary<string, RestoredClock> restoredDeadlines = new();

    public NativeTimerScheduler(tuber.canis.TuberMatch match)
    {
        this.match = match;
        context = new UnityEngine.UnitySynchronizationContext(Il2CppSystem.Threading.Thread.CurrentThread.ManagedThreadId);
        owners[match.Pointer] = new(this);
        if (installed) return;
        installed = true;
        var harmony = new Harmony("local.root.private-timer-scheduler");
        foreach (var type in new[] { typeof(SetTimerAction), typeof(SetSimultaneousTimerAction) })
        {
            harmony.Patch(AccessTools.Method(type, "waitForTime"),
                prefix: new HarmonyMethod(typeof(NativeTimerScheduler), nameof(Capture)),
                finalizer: new HarmonyMethod(typeof(NativeTimerScheduler), nameof(Restore)));
            harmony.Patch(AccessTools.Method(type, "execute"),
                prefix: new HarmonyMethod(typeof(NativeTimerScheduler), nameof(RestoreDeadline)));
        }
    }

    private sealed class ContextScope : IDisposable
    {
        private readonly NativeContext? previous = NativeContext.Current;
        public ContextScope(NativeContext current) => NativeContext.SetSynchronizationContext(current);
        public void Dispose() => NativeContext.SetSynchronizationContext(previous);
    }

    private static void Capture(Canis.actions.Action __instance, out ContextScope? __state)
    {
        __state = null;
        if (owners.TryGetValue(__instance.Match.Pointer, out var owner) && owner.TryGetTarget(out var scheduler))
            __state = new ContextScope(scheduler.context);
    }

    private static void Restore(Canis.actions.Action __instance, ContextScope? __state)
    {
        try
        {
            if (!owners.TryGetValue(__instance.Match.Pointer, out var owner) || !owner.TryGetTarget(out var scheduler)) return;
            var single = __instance.TryCast<SetTimerAction>();
            var simultaneous = __instance.TryCast<SetSimultaneousTimerAction>();
            var id = single?.TimerID.ToString() ?? simultaneous?.TimerID.ToString();
            // waitForTime writes startTime before its first await. Restore after
            // that initial call, so native ClearTimerAction debits all elapsed
            // time once when the restored decision is answered.
            if (id is not null && scheduler.restoredDeadlines.Remove(id, out var clock) && single is not null)
                single.startTime = new Il2CppSystem.DateTime(DateTime.UnixEpoch.Ticks + clock.StartedAt * TimeSpan.TicksPerMillisecond,
                    Il2CppSystem.DateTimeKind.Utc).ToLocalTime();
        }
        finally { __state?.Dispose(); }
    }

    public void SetRestoredDeadline(string timer, long startedAt, long endsAt) => restoredDeadlines[timer] = new(startedAt, endsAt);

    private static void RestoreDeadline(Canis.actions.Action __instance)
    {
        if (!owners.TryGetValue(__instance.Match.Pointer, out var owner) || !owner.TryGetTarget(out var scheduler)) return;
        var single = __instance.TryCast<SetTimerAction>();
        var simultaneous = __instance.TryCast<SetSimultaneousTimerAction>();
        var id = single?.TimerID.ToString() ?? simultaneous?.TimerID.ToString();
        if (id is null || !scheduler.restoredDeadlines.TryGetValue(id, out var clock)) return;
        var seconds = Math.Max(0, (clock.EndsAt - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) / 1000d);
        if (single is not null)
        {
            single.Wait = Math.Min(single.Wait, (int)Math.Min(int.MaxValue, Math.Floor(seconds)));
            single.buffer = (float)Math.Max(0, seconds - single.Wait);
        }
        else if (simultaneous is not null)
        {
            simultaneous.timerMessage.Wait = Math.Min(simultaneous.timerMessage.Wait, (int)Math.Min(int.MaxValue, Math.Floor(seconds)));
            simultaneous.buffer = (float)Math.Max(0, seconds - simultaneous.timerMessage.Wait);
        }
    }

    public void Tick()
    {
        if (Environment.CurrentManagedThreadId != mainThread)
            throw new InvalidOperationException("Native turn timers must advance on the game's main thread.");
        using var scope = new ContextScope(context);
        context.Exec();
    }

    public void Dispose()
    {
        foreach (var player in match.Players.ToArray()) match.MessageRouter.ClearTimers(player.AccountID);
        match.MatchDisposed = true;
        Tick();
        owners.TryRemove(match.Pointer, out _);
    }
}
