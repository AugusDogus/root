namespace RootEngineProbe;

// Match controls stay separate from setup and the game's own board controls.
internal sealed class NativeMatchMenu
{
    private readonly NativeMenu view;
    private readonly PrivateClient client;
    private readonly Func<SeatStatus[]> roster;
    private readonly Func<int, bool>? release;
    private readonly Action returnHome;
    private float refreshAt;
    private bool resultsShown;
    private bool transitioning;
    private SeatStatus[] displayedSeats = Array.Empty<SeatStatus>();

    public NativeMatchMenu(NativeMenu view, PrivateClient client, Func<SeatStatus[]> roster, Func<int, bool>? release, Action returnHome)
    { this.view = view; this.client = client; this.roster = roster; this.release = release; this.returnHome = returnHome; }

    public void Update(float now)
    {
        if (view.Screen == "error") return;
        if (client.Transitioning)
        {
            if (!transitioning) view.Connecting("Updating the table…", "Waiting for Root to finish the change and save the match.");
            transitioning = true;
            return;
        }
        if (transitioning) { transitioning = false; view.ReturnToBoard(); }
        if (client.Notice is { } notice)
        {
            client.Notice = null;
            view.Notice("Action needs attention", notice);
        }
        if (client.GameOver && client.Standings.Length == 6 && view.IsPlaying && !resultsShown) { resultsShown = true; Results(); }
        // Keep the pressed button alive through release and Unity's click event.
        // Readiness can change while the player is already pressing another control.
        if (view.Screen == "match" && UnityEngine.Input.anyKey)
            refreshAt = Math.Max(refreshAt, now + 0.3f);
        if (view.Screen == "match" && now >= refreshAt)
        {
            refreshAt = now + 2;
            if (!roster().SequenceEqual(displayedSeats)) Show();
        }
    }

    public void Show()
    {
        view.Begin(true, "match");
        view.Text("Your table", 250, 40, 780, 65, 38);
        view.Text("Mark ready when you are ready to play. Readiness does not pause the match.", 100, 110, 1080, 45, 20);
        displayedSeats = roster();
        foreach (var seat in displayedSeats)
        {
            var y = 175 + (seat.Seat - 1) * 65;
            view.Text($"{seat.Seat}. {seat.Name}", 80, y, 550, 55, 23);
            view.Text(seat.State + (seat.Ready && seat.State == "Connected" ? " · Ready" : ""), 640, y, 290, 55, 21);
            if (release is not null && seat.Seat != 1 && seat.State is "Disconnected" or "Waiting")
                view.Button("Reassign", 955, y, 245, 50, () => ConfirmRelease(seat.Seat));
        }
        var self = client.Lobby.FirstOrDefault(seat => seat.Seat == client.Seat);
        if (!client.GameOver && self?.State != "Resigned")
        {
            view.Button(self?.Ready == true ? "Not ready" : "I'm ready", 90, 600, 320, 55, () => client.Ready(self?.Ready != true));
            view.Button("Resign", 460, 600, 320, 55, () => Confirm("Resign from this match?",
                "Root's AI will take over your faction. This cannot be undone. You can stay to watch the match.", client.Resign));
        }
        if (client.GameOver) view.Button("Results", 460, 600, 320, 55, Results);
        view.Button("Return to menu", 840, 600, 350, 55, () => Confirm("Return to the menu?",
            release is null ? "You will leave this table. The host can invite you again."
                : "This stops the match for everyone. Your latest saved moves are kept. Resume later and send new invitations.", returnHome));
        view.Button("Back to game", 450, 700, 380, 55, view.ReturnToBoard);
    }

    private void ConfirmRelease(int seat) => Confirm("Reassign this seat?",
        "The previous invitation will stop working. Choose the friend who will take over this faction.", () =>
        {
            if (release?.Invoke(seat) == true) view.InviteSeat(seat);
            else view.Notice("Seat is still connected", "Wait for the player to disconnect and any pending move to finish, then try again.");
        });

    private void Confirm(string title, string detail, Action action)
    {
        view.Begin(true, "confirm");
        view.Text(title, 230, 150, 820, 80, 34);
        view.Text(detail, 240, 260, 800, 150, 24);
        view.Button("Cancel", 250, 525, 340, 65, Show);
        view.Button("Confirm", 690, 525, 340, 65, () => { view.ReturnToBoard(); action(); });
    }

    private void Results()
    {
        view.Begin(true, "results");
        view.Text("Match complete", 250, 60, 780, 70, 40);
        view.Text(client.Winner is { } winner ? $"Winner: {winner}" : "The match has ended.", 230, 140, 820, 60, 30);
        for (var index = 0; index < client.Standings.Length; index++)
        {
            var standing = client.Standings[index];
            view.Text(standing.Faction, 150, 230 + index * 50, 600, 45, 24);
            view.Text(standing.Dominance ? "Dominance victory" : $"{standing.Score} points" + (standing.Won ? " · Winner" : ""), 760, 230 + index * 50, 380, 45, 23);
        }
        view.Button("View final board", 150, 650, 450, 65, view.ReturnToBoard);
        view.Button("Return to menu", 680, 650, 450, 65, returnHome);
    }
}
