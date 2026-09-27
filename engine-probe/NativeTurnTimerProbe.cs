using System.Text.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using Canis.actions;
using Canis.json;
using Canis.json.events;
using Canis.messages.timer;
using Canis.utils.ids;
using tuber.canis;
using UnityEngine;

namespace RootEngineProbe;

// Diagnostic-only mode. No choices, hands, or board snapshots are written.
public sealed class NativeTurnTimerProbe : MonoBehaviour
{
    private readonly List<object> events = new();
    private readonly Dictionary<string, int> messages = new();
    private readonly List<object> configurations = new();
    private TuberMatch? match;
    private float started;
    private string output = "";
    private int removed;
    private object? clientClock;
    private enum Phase { Expiry, Recovery, Restored, Rerestored, Expired, SlowNative, SlowEnabled, Complete }
    private Phase phase;
    private HostedMatch? recoveryMatch;
    private object? recovery;
    private string checkpoint = "";
    private sealed record ClockView(int WaitTime, int TotalTime, long StartedAt, long EndsAt);
    private ClockView[] recoveryBefore = Array.Empty<ClockView>();
    private float savedElapsed;
    private object? recoveryBeforeNative;
    private readonly HashSet<int> dispatcherThreads = new();
    private int mainThread;
    private NativeTimerScheduler? scheduler;
    private TuberMatch? slowMatch;
    private readonly List<object> slowEvents = new();
    private object? slowResults;
    private bool clockPassed;
    private string expiredAccount = "";
    private object? expiredRecovery;
    private SavedTurnTimer[] expiredSource = Array.Empty<SavedTurnTimer>();
    private object? recoveryChoice;
    private readonly List<string> rejectedTimers = new();
    private readonly List<object> simultaneousCoverage = new();
    public NativeTurnTimerProbe(IntPtr pointer) : base(pointer) { }

    public void Start()
    {
        try
        {
            UnityEngine.Object.DontDestroyOnLoad(gameObject);
            mainThread = Environment.CurrentManagedThreadId;
            Application.targetFrameRate = 60;
            output = Path.GetFullPath(Path.Combine(BepInEx.Paths.GameRootPath, "..", "results", "timer-probe.json"));
            Directory.CreateDirectory(Path.GetDirectoryName(output) ?? throw new InvalidOperationException("Missing probe output directory."));
            dwd.core.data.ReflectionTypeInitializer.Initialize();
            WriteTimerMethods();
            foreach (var mode in new[] { "Live", "Async", tuber_canis.data.TuberMatchTypes.CasualType }.Distinct())
            foreach (var value in new[] { "0", "1", "300" })
            {
                var init = HostedMatch.CreateInitialization(true);
                init.AddOption("matchType", mode);
                init.AddOption("Timers", value);
                var configured = new TuberMatch();
                configured.Configure(init);
                configurations.Add(new { mode, option = value, configured.UseTimers, configured.UseTurnTimers, configured.TimerType, configured.IsLive, configured.IsPBM });
                if (mode == "Live" && value == "1") match = configured;
            }
            if (match is not { } current) throw new InvalidOperationException("Missing timer test match.");
            current.SetMessageDispatcher(new Action<AccountID, DWDEvent>((_, message) =>
            {
                dispatcherThreads.Add(Environment.CurrentManagedThreadId);
                var name = message.GetIl2CppType().FullName;
                messages[name] = messages.GetValueOrDefault(name) + 1;
                if (message.TryCast<DisplayTimer>() is { } display)
                {
                    events.Add(new { type = "display", display.WaitTime, display.TotalTime, display.StartedAt, display.EndsAt });
                    if (clientClock is null)
                    {
                        try
                        {
                            var data = new dwd.canis.PlayerTimerData(display);
                            var nativeRemaining = data.TimeRemaining.TotalSeconds;
                            var publicClock = NativeTurnTimers.PublicClock(display);
                            var normalized = new dwd.canis.PlayerTimerData(publicClock);
                            var normalizedRemaining = normalized.TimeRemaining.TotalSeconds;
                            const long skew = 3_600_000;
                            publicClock.StartedAt += skew;
                            publicClock.EndsAt += skew;
                            var original = JSON.ToJSON(publicClock, false);
                            var adapted = new dwd.canis.PlayerTimerData(NativeTimerUI.ForClient(publicClock,
                                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + skew));
                            var adaptedRemaining = adapted.TimeRemaining.TotalSeconds;
                            adapted.timer = NativeTimerUI.ForClient(publicClock, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + skew);
                            var updatedRemaining = adapted.TimeRemaining.TotalSeconds;
                            var originalUnchanged = JSON.ToJSON(publicClock, false) == original;
                            adapted.Hide();
                            var nativeHidden = adapted.Ended;
                            clockPassed = adaptedRemaining is > 4 and <= 6.1 && updatedRemaining is > 4 and <= 6.1 && nativeHidden && originalUnchanged;
                            clientClock = new { status = "created", nativeRemaining, normalizedRemaining, adaptedRemaining, updatedRemaining, nativeHidden, originalUnchanged,
                                simulatedClockSkewMilliseconds = skew, hasOnlineMatch = data.onlineMatch is not null };
                        }
                        catch (Exception error) { clientClock = new { status = "failed", error = error.ToString() }; }
                    }
                }
                if (message.TryCast<SetTimer>() is { } set)
                    events.Add(new { type = "set", set.Wait });
                if (message.TryCast<SetSimultaneousTimer>() is { } simultaneous)
                    events.Add(new { type = "simultaneous", simultaneous.Wait });
                if (message.TryCast<HideTimer>() is not null) events.Add(new { type = "hide" });
            }));
            current.SetRemoveIdlePlayer(new Func<AccountID, Il2CppSystem.Threading.Tasks.Task>(_ =>
            {
                removed++;
                return Il2CppSystem.Threading.Tasks.Task.CompletedTask;
            }));
            current.messageActionFactory = new ObfuscatedMessageActionFactory().Cast<IMessageActionFactory>();
            scheduler = new NativeTimerScheduler(current);
            current.Start();
            started = Time.realtimeSinceStartup;
            Save("running");
        }
        catch (Exception error) { Fail(error); }
    }

    public void Update()
    {
        if (match is null || phase == Phase.Complete) return;
        try
        {
            scheduler?.Tick();
            recoveryMatch?.Timers.Tick();
            var elapsed = Time.realtimeSinceStartup - started;
            if (phase == Phase.Expiry && elapsed >= 12)
            {
                scheduler?.Dispose();
                scheduler = null;
                Stop(match);
                var init = HostedMatch.CreateInitialization(true);
                init.AddOption("Timers", "30");
                recoveryMatch = new HostedMatch(init);
                started = Time.realtimeSinceStartup;
                phase = Phase.Recovery;
                Save("recovery");
            }
            else if (phase == Phase.Recovery && elapsed >= 2 && recoveryMatch is { } host)
            {
                checkpoint = output + ".checkpoint";
                recoveryBefore = Timers(host);
                recoveryBeforeNative = NativeState(host);
                savedElapsed = elapsed;
                MatchCheckpoint.Save(host, checkpoint);
                CheckMalformedTimers(host);
                CheckFutureVersion();
                host.Timers.Stop();
                Stop(host.Match);
                recoveryMatch = MatchCheckpoint.Load(checkpoint);
                started = Time.realtimeSinceStartup;
                phase = Phase.Restored;
            }
            else if (phase == Phase.Restored && elapsed >= 1 && recoveryMatch is { } restored)
            {
                var after = Timers(restored);
                Check(recoveryBefore.Length > 0 && after.Length > 0, "Restored decision has no native countdown.");
                Check(after[0].EndsAt == recoveryBefore[0].EndsAt, "Restoring a timer changed its absolute deadline.");
                var remaining = after[0].EndsAt - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                Check(remaining is > 25000 and < 33000, "Restored countdown did not preserve elapsed time.");
                var pending = restored.SeatPlayers.Select(player => restored.Thread.GetCounterForPlayerEntity(player))
                    .Count(counter => restored.Thread.HasPendingResponse(counter));
                recovery = new { secondsBeforeSave = savedElapsed, before = recoveryBefore, after, pending,
                    beforeNative = recoveryBeforeNative, afterNative = NativeState(restored) };
                MatchCheckpoint.Save(restored, checkpoint);
                expiredSource = restored.Timers.Capture(restored);
                restored.Timers.Stop();
                recoveryMatch = MatchCheckpoint.Load(checkpoint);
                started = Time.realtimeSinceStartup;
                phase = Phase.Rerestored;
            }
            else if (phase == Phase.Rerestored && elapsed >= 1 && recoveryMatch is { } rerestored)
            {
                var offer = rerestored.GetOffer(0) ?? throw new InvalidOperationException("Restored Cats setup has no legal test decision.");
                var selection = rerestored.Thread.GetPlayerPendingResponse(offer.Counter).Item2.Selection;
                var player = rerestored.SeatPlayers[0];
                var bankBefore = rerestored.Match.GetTimerForSelection(player, selection);
                var paddingBefore = JSON.ToJSON(player.SerializeAttributes().GetAttribute(Canis.attributes.CoreAttributes.PlayerTurnTimerPadding), false);
                Check(rerestored.Choose(0, offer.Counter, offer.Source, offer.Targets[0]) == ChoiceResult.Accepted,
                    "Native engine rejected the restored legal decision.");
                var bankAfter = rerestored.Match.GetTimerForSelection(player, selection);
                var expected = bankBefore - Convert.ToInt32((DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - recoveryBefore[0].StartedAt) / 1000d);
                var paddingAfter = JSON.ToJSON(player.SerializeAttributes().GetAttribute(Canis.attributes.CoreAttributes.PlayerTurnTimerPadding), false);
                recoveryChoice = new { bankBefore, bankAfter, expected, paddingBefore, paddingAfter };
                Check(bankBefore == 30, "Restart changed the saved native bank before the decision.");
                Check(Math.Abs(bankAfter - expected) <= 1 && bankAfter > 20 && bankAfter < 30,
                    "Native cancellation did not debit elapsed time exactly once across two restarts.");
                Check(paddingBefore == paddingAfter, "Restart changed native turn padding.");
                var expiredNow = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var expired = expiredSource.Select(timer => timer with
                {
                    WaitTime = 1, TotalTime = 1, StartedAt = expiredNow - 6001, EndsAt = expiredNow - 1
                }).ToArray();
                Check(expired.Length > 0, "No timer checkpoint was available for expired recovery.");
                expiredAccount = expired[0].Account;
                rerestored.Timers.Stop();
                var document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(checkpoint))?.AsObject()
                    ?? throw new InvalidDataException("Probe checkpoint is invalid.");
                document["Timers"] = JsonSerializer.SerializeToNode(expired);
                File.WriteAllText(checkpoint, document.ToJsonString());
                recoveryMatch = MatchCheckpoint.Load(checkpoint);
                started = Time.realtimeSinceStartup;
                phase = Phase.Expired;
            }
            else if (phase == Phase.Expired && elapsed >= 2 && recoveryMatch is { } expiredHost)
            {
                var resigned = expiredHost.Match.HasResigned(new AccountID(expiredAccount));
                Check(resigned, "An expired saved deadline did not invoke Root's timeout-to-AI rule.");
                expiredRecovery = new { resigned };
                expiredHost.Timers.Stop();
                File.Delete(checkpoint);
                checkpoint = "";
                StartSlow(false);
                phase = Phase.SlowNative;
                started = Time.realtimeSinceStartup;
                Save("slow-native");
            }
            else if (phase == Phase.SlowNative && elapsed >= 2)
            {
                scheduler?.Dispose();
                StartSlow(true);
                phase = Phase.SlowEnabled;
                started = Time.realtimeSinceStartup;
            }
            else if (phase == Phase.SlowEnabled && elapsed >= 12 && slowMatch is { } slow)
            {
                slowResults = new { slow.IsLive, slow.IsPBM, slow.UseTimers, slow.UseTurnTimers,
                    resigned = slow.Players.ToArray().Count(player => slow.HasResigned(player.AccountID)) };
                Check(slow.Players.ToArray().Any(player => slow.HasResigned(player.AccountID)), "Slow mode did not invoke native expiry.");
                Check(clockPassed, "Native client countdown was not synchronized with the host clock.");
                Check(dispatcherThreads.SetEquals(new[] { mainThread }), "Native timer continuation escaped the main thread.");
                scheduler?.Dispose();
                scheduler = null;
                CheckAdvancedOpening();
                phase = Phase.Complete;
                Save("completed");
                Application.Quit();
                match = null;
            }
        }
        catch (Exception error) { Fail(error); }
    }

    private void StartSlow(bool enable)
    {
        var init = HostedMatch.CreateInitialization(true);
        init.AddOption("matchType", tuber_canis.data.TuberMatchTypes.CasualType);
        init.AddOption("Timers", "1");
        var current = new TuberMatch();
        current.Configure(init);
        if (enable) current.UseTimers = true;
        current.SetRemoveIdlePlayer(new Func<AccountID, Il2CppSystem.Threading.Tasks.Task>(_ => Il2CppSystem.Threading.Tasks.Task.CompletedTask));
        current.SetMessageDispatcher(new Action<AccountID, DWDEvent>((_, message) =>
        {
            dispatcherThreads.Add(Environment.CurrentManagedThreadId);
            if (message.TryCast<DisplayTimer>() is { } display)
                slowEvents.Add(new { enabled = enable, display.WaitTime, display.TotalTime, display.StartedAt, display.EndsAt });
        }));
        current.messageActionFactory = new ObfuscatedMessageActionFactory().Cast<IMessageActionFactory>();
        scheduler = new NativeTimerScheduler(current);
        current.Start();
        slowMatch = current;
    }

    private static ClockView[] Timers(HostedMatch host) => host.Messages[0]
        .Select(json => JSON.Deserialize<DWDEvent>(json).TryCast<DisplayTimer>())
        .Where(timer => timer is not null)
        .Select(timer => timer is { } display
            ? new ClockView(display.WaitTime, display.TotalTime, display.StartedAt, display.EndsAt)
            : throw new InvalidOperationException("Filtered timer was null."))
        .ToArray();

    private static object NativeState(HostedMatch host) => new
    {
        host.Match.UseTimers, host.Match.UseTurnTimers, host.Match.TimerType,
        hasTimerID = host.Match.TimerID is not null,
        simultaneous = host.Match.ActiveSimultaneousAccounts.Count,
        budgets = host.SeatPlayers.Select(player => new
        {
            bank = JSON.ToJSON(player.SerializeAttributes().GetAttribute(Canis.attributes.CoreAttributes.PlayerTurnTimer), false),
            padding = JSON.ToJSON(player.SerializeAttributes().GetAttribute(Canis.attributes.CoreAttributes.PlayerTurnTimerPadding), false)
        }).ToArray(),
        selections = host.SeatPlayers.Select(player =>
        {
            var counter = host.Thread.GetCounterForPlayerEntity(player);
            return host.Thread.HasPendingResponse(counter)
                ? host.Match.GetTimerForSelection(player, host.Thread.GetPlayerPendingResponse(counter).Item2.Selection) : -1;
        }).ToArray()
    };

    private void CheckMalformedTimers(HostedMatch host)
    {
        var saved = host.Timers.Capture(host);
        Check(saved.Length == 1, "Corruption probe needs one native pending timer.");
        var timer = saved[0];
        var invalid = new (string Name, SavedTurnTimer[] Timers)[]
        {
            ("duplicate-account", new[] { timer, timer }),
            ("unknown-account", new[] { timer with { Account = Guid.NewGuid().ToString() } }),
            ("seat-without-pending-decision", new[] { timer with { Account = host.SeatPlayers[1].AccountID.ToString() } }),
            ("unknown-type", new[] { timer with { Type = "UnknownTimer" } }),
            ("empty-id", new[] { timer with { TimerID = "" } }),
            ("negative-wait", new[] { timer with { WaitTime = -1 } }),
            ("negative-total", new[] { timer with { TotalTime = -1 } }),
            ("inverted-time", new[] { timer with { StartedAt = timer.EndsAt + 1 } }),
            ("out-of-range-time", new[] { timer with { EndsAt = long.MaxValue } })
        };
        foreach (var test in invalid)
        {
            try { host.Timers.Restore(host, test.Timers); }
            catch (InvalidDataException) { rejectedTimers.Add(test.Name); continue; }
            throw new InvalidOperationException($"Malformed timer was accepted: {test.Name}.");
        }
        host.Match.UseTimers = false;
        try
        {
            try { host.Timers.Restore(host, saved); }
            catch (InvalidDataException) { rejectedTimers.Add("disabled-with-saved-clock"); return; }
            throw new InvalidOperationException("Disabled match accepted saved countdowns.");
        }
        finally { host.Match.UseTimers = true; }
    }

    private void CheckFutureVersion()
    {
        var original = File.ReadAllText(checkpoint);
        var document = System.Text.Json.Nodes.JsonNode.Parse(original)?.AsObject()
            ?? throw new InvalidDataException("Probe checkpoint is invalid.");
        document["Version"] = 999;
        try
        {
            File.WriteAllText(checkpoint, document.ToJsonString());
            try { MatchCheckpoint.Load(checkpoint).Timers.Stop(); }
            catch (InvalidDataException) { rejectedTimers.Add("future-checkpoint-version"); return; }
            throw new InvalidOperationException("Future checkpoint version was accepted.");
        }
        finally { File.WriteAllText(checkpoint, original); }
    }

    private void CheckAdvancedOpening()
    {
        foreach (var draft in new[] { false, true })
        {
            var init = HostedMatch.CreateInitialization(true, new MatchSetup { AdvancedSetup = true, FactionDraft = draft });
            init.AddOption("Timers", "30");
            for (var seat = 0; seat < init.TuberPlayers.Count; seat++)
                init.TuberPlayers[seat].metadata[NativeHostConfiguration.SeatKey] = seat.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var host = new HostedMatch(init);
            try
            {
                var saved = host.Timers.Capture(host);
                var pending = host.SeatPlayers.Count(player => host.Thread.HasPendingResponse(host.Thread.GetCounterForPlayerEntity(player)));
                if (saved.Length < 2 || saved[0].Type != "SetSimultaneousTimer")
                {
                    simultaneousCoverage.Add(new { draft, pending, timers = saved.Length, status = "no-simultaneous-opening" });
                    continue;
                }
                checkpoint = output + ".checkpoint";
                MatchCheckpoint.Save(host, checkpoint);
                host.Timers.Stop();
                var restored = MatchCheckpoint.Load(checkpoint);
                try
                {
                    var after = restored.Timers.Capture(restored);
                    Check(saved.OrderBy(timer => timer.Account).SequenceEqual(after.OrderBy(timer => timer.Account)),
                        "Restoring a simultaneous native decision changed its accounts or deadlines.");
                    simultaneousCoverage.Add(new { draft, pending, timers = saved.Length, status = "native-group-restored" });
                }
                finally { restored.Timers.Stop(); }
            }
            finally
            {
                host.Timers.Stop();
                if (checkpoint.Length > 0) File.Delete(checkpoint);
                checkpoint = "";
            }
        }
    }

    private void WriteTimerMethods()
    {
        var module = System.Diagnostics.Process.GetCurrentProcess().Modules.Cast<System.Diagnostics.ProcessModule>()
            .Single(item => item.ModuleName == "GameAssembly.dll");
        var types = new[] { typeof(Canis.actions.timers.ClearTimerAction), typeof(Canis.actions.timers.SetTimerAction),
            typeof(Canis.actions.timers.SetSimultaneousTimerAction), typeof(TuberMatch), typeof(tuber.canis.entities.TuberPlayerEntity), typeof(dwd.canis.PlayerTimerData) };
        var lines = new List<string>();
        foreach (var type in types.Concat(types.SelectMany(type => type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)))
            .Where(type => !type.ContainsGenericParameters))
            foreach (var field in type.GetFields(BindingFlags.Static | BindingFlags.NonPublic))
                if (field.Name.StartsWith("NativeMethodInfoPtr_") && field.GetValue(null) is IntPtr info && info != IntPtr.Zero)
                    lines.Add($"{Marshal.ReadIntPtr(info).ToInt64() - module.BaseAddress.ToInt64():x}\t{type.FullName}\t{field.Name}");
        File.WriteAllLines(output + ".methods.tsv", lines);
    }

    private static void Stop(TuberMatch current)
    {
        foreach (var player in current.Players) current.MessageRouter.ClearTimers(player.AccountID);
        current.MatchDisposed = true;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private void Save(string status)
    {
        var report = new { status, configurations, events, messages, removed, clientClock, recovery, recoveryChoice, expiredRecovery, rejectedTimers, simultaneousCoverage, mainThread, dispatcherThreads, slowEvents, slowResults,
            resigned = match is { } current ? current.Players.ToArray().Count(player => current.HasResigned(player.AccountID)) : 0,
            gameOver = match?.GameOverD };
        File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void Fail(Exception error)
    {
        if (checkpoint.Length > 0) File.Delete(checkpoint);
        if (output.Length > 0) File.WriteAllText(output, JsonSerializer.Serialize(new { status = "failed", error = error.ToString(), configurations, events, messages,
            clientClock, recovery, recoveryChoice, expiredRecovery, rejectedTimers, simultaneousCoverage, mainThread, dispatcherThreads }));
        BepInEx.Logging.Logger.CreateLogSource("Timer probe").LogError(error);
        match = null;
        Application.Quit(1);
    }
}
