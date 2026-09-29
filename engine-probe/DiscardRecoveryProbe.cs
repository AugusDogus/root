using System.Text.Json;
using Networking.selection.messages;

namespace RootEngineProbe;

internal static class DiscardRecoveryProbe
{
    public static void Run(string fixture, string output)
    {
        var host = MatchCheckpoint.Load(fixture);
        try
        {
            var seat = Enumerable.Range(0, host.PlayerCount).Single(index =>
            {
                var counter = host.Thread.GetCounterForPlayerEntity(host.SeatPlayers[index]);
                return host.Thread.HasPendingResponse(counter) &&
                    host.Thread.GetPlayerPendingResponse(counter).Item2.Selection
                        .TryCast<SelectionWithTargetsRequired>()?.Prompt.ID == "tuber.canis.actions.DrawThenDiscard.NoActions";
            });
            var counter = host.Thread.GetCounterForPlayerEntity(host.SeatPlayers[seat]);
            var selection = host.Thread.GetPlayerPendingResponse(counter).Item2.Selection.Cast<SelectionWithTargetsRequired>();
            var result = host.ChooseTargets(seat, counter, selection.SourceID.ToString(),
                new TargetChoice[] { new TargetChoice.Entities(Array.Empty<string>()) });
            if (result != ChoiceResult.Accepted)
                throw new InvalidOperationException($"Native discard Continue was rejected: {result}");
            if (host.Thread.HasPendingResponse(counter))
                throw new InvalidOperationException("Discard Continue did not advance the saved match.");
            File.WriteAllText(Path.Combine(output, "test-results.json"),
                JsonSerializer.Serialize(new { status = "passed", discardContinue = true }));
        }
        finally { host.Timers.Stop(); }
    }
}
