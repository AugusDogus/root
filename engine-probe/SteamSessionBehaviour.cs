using System.Diagnostics;
using System.Text.Json;
using UnityEngine;

namespace RootEngineProbe;

// Both platforms run this in Root's own Steam context, not in the Python launcher.
public sealed class SteamSessionBehaviour : MonoBehaviour
{
    private readonly Stopwatch clock = new();
    private readonly string lab = Path.GetFullPath(Path.Combine(BepInEx.Paths.GameRootPath, ".."));
    private SteamNative? api;
    private SteamHost? host;
    private RecoveringConnection? guest;
    private SteamInvitations? invitations;
    private PrivateClient? client;
    private SteamInvitation? accepted;
    private string message = "Waiting for Steam to sign in…";
    private string? error;
    private bool showFriends;
    private int inviteSeat = 2;
    private MatchSetup hostedSetup = new();
    private (ulong Id, string Name)[] friends = Array.Empty<(ulong, string)>();
    private Vector2 scroll;
    private NativeMenu? menu;
    private Task? startingHost;
    private bool reconnecting;
    private Task? returningHome;
    public SteamSessionBehaviour(IntPtr pointer) : base(pointer) { }
    public void Start()
    {
        clock.Start();
        if (Environment.GetEnvironmentVariable("ROOT_LAB_MODE") == "steam-menu")
            menu = new NativeMenu(lab, (save, setup) =>
            {
                if (host is not null || guest is not null || accepted is not null || startingHost is not null)
                    throw new InvalidOperationException("A match is already starting. Close Root before starting another.");
                startingHost = LauncherControl.StartHost(lab, save, setup);
            }, () => invitations?.Friends().OrderBy(friend => friend.Name).ToArray() ?? Array.Empty<(ulong, string)>(), SendInvitation);
        Application.targetFrameRate = Environment.GetEnvironmentVariable("ROOT_FRIENDS_LAUNCHER") == "1" ? 60 : 20;
        if (Environment.GetEnvironmentVariable("ROOT_FRIENDS_LAUNCHER") != "1") QualitySettings.SetQualityLevel(0, true);
    }
    public void Update()
    {
        if (Environment.GetEnvironmentVariable("ROOT_FRIENDS_LAUNCHER") != "1") AudioListener.volume = 0;
        try
        {
            menu?.Update((float)clock.Elapsed.TotalSeconds);
            if (returningHome is { IsCompleted: true }) { returningHome.GetAwaiter().GetResult(); returningHome = null; }
            if (error is not null) return;
            if (api is null)
            {
                if (clock.Elapsed.TotalSeconds < 25) return;
                if (!Steamworks.SteamClient.IsValid || !Steamworks.SteamClient.IsLoggedOn ||
                    tuber.client.utils.TuberPrefs.SeenFirstTimeStrategicViewTutorialPrompt is null)
                {
                    if (clock.Elapsed.TotalSeconds > 120) throw new IOException("Steam or Root did not finish initializing. Check Steam, then stop and restart this session.");
                    return;
                }
                Initialize();
            }
            if (startingHost is { IsCompleted: true } && api is { } hostApi)
            {
                startingHost.GetAwaiter().GetResult();
                InitializeHost(hostApi);
                startingHost = null;
            }
            if (accepted is { } invitation && guest is null && host is null && api is { } ready)
            {
                guest = new(() => new SteamGuest(ready, invitation), () => clock.Elapsed.TotalSeconds, invitation.Token);
                client = new(invitation.Token, guest.Exchange);
                AttachControls();
                message = $"Connecting to Steam host as seat {invitation.Seat}…";
                menu?.Connecting("Joining your friends…", "Opening your seat at the table.");
            }
            host?.Tick();
            guest?.Tick();
            if (guest?.Reconnecting == true)
            {
                client?.DiscardQueuedMoves();
                if (!reconnecting) menu?.Connecting("Reconnecting…", "Checking the current board. Your last move will not be repeated.");
                reconnecting = true;
            }
            else if (reconnecting) { reconnecting = false; menu?.ReturnToBoard(); }
            client?.Update((float)clock.Elapsed.TotalSeconds);
            if (client?.ReceivedMessages > 0 && UnityEngine.Object.FindObjectOfType<tuber.client.match.behaviours.TuberEntitiesProvider>()?.TuberEntities?.allPlayers.Count == 6)
                menu?.Playing(host is not null);
        }
        catch (Exception failure)
        {
            error = failure.Message;
            menu?.Error(error, accepted is not null ? Reconnect : null, () => returningHome = LauncherControl.ReturnHome(lab));
            File.WriteAllText(Path.Combine(lab, "steam-status.json"), JsonSerializer.Serialize(new { error }));
            // Keep the host and its saved match alive while a player reads the error.
            guest?.Dispose();
        }
    }
    private void Initialize()
    {
        api = new();
        invitations = new(api, invitation =>
        {
            // Acceptance arrives from Steam's explicit Join action. Never replace a running match.
            if (guest is null && host is null && accepted is null && startingHost is null) accepted = invitation;
        }, detail => menu?.Notice("Invitation needs an update", detail));
        var mode = Environment.GetEnvironmentVariable("ROOT_LAB_MODE");
        if (mode == "steam-host")
        {
            InitializeHost(api);
        }
        else if (mode == "steam-client")
        {
            using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(lab, "steam-config.json")));
            var text = config.RootElement.GetProperty("invite").GetString() ?? "";
            if (!SteamInvitation.TryParse(text, out accepted)) throw new InvalidDataException("Invalid Steam invitation. Ask the host for a new seat invitation.");
        }
        else message = "Ready for a Steam invitation. Ask your host to invite you, then click Join in Steam.";
    }
    private void InitializeHost(SteamNative ready)
    {
        using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(lab, "steam-config.json")));
        var port = config.RootElement.GetProperty("port").GetInt32();
        var tokens = config.RootElement.GetProperty("tokens").Deserialize<string[]>() ?? throw new InvalidDataException("Missing host seats.");
        hostedSetup = config.RootElement.TryGetProperty("setup", out var settings) ? MatchSetup.Parse(settings.GetRawText()) : new();
        menu?.SetHostedSetup(hostedSetup);
        host = new(ready, port, tokens, Enumerable.Range(2, 5).Where(seat => hostedSetup.IsHumanSeat(seat - 1)).ToArray(),
            identity => invitations?.NameFor(identity) ?? "Steam friend");
        client = new(Path.Combine(lab, "connection.json")) { DisplayName = MatchLobby.CleanName(invitations?.NameFor(host.Identity) ?? "Host") };
        AttachControls();
        var texts = new[] { "" }.Concat(Enumerable.Range(2, 5).Select(seat => hostedSetup.IsHumanSeat(seat - 1) ? host.Invitation(seat).Encode() : "")).ToArray();
        File.WriteAllText(Path.Combine(lab, "steam-status.json"), JsonSerializer.Serialize(new { invitations = texts }));
        message = "Steam host ready. Invite friends using the button below.";
    }
    private void Reconnect()
    {
        if (api is not { } ready || accepted is not { } invitation) return;
        guest?.Dispose();
        guest = new(() => new SteamGuest(ready, invitation), () => clock.Elapsed.TotalSeconds, invitation.Token);
        if (client is null) { client = new(invitation.Token, guest.Exchange); AttachControls(); }
        else client.ReplaceTransport(guest.Exchange);
        error = null;
        File.Delete(Path.Combine(lab, "steam-status.json"));
        menu?.Connecting("Reconnecting…", "Checking your seat and the current board.");
        reconnecting = true;
    }
    private void AttachControls()
    {
        if (client is not { } active) return;
        menu?.AttachMatch(active, () => active.Lobby.Select(seat =>
        {
            if (host is null || seat.Seat == 1 || !active.Setup.IsHumanSeat(seat.Seat - 1)) return seat;
            var owner = host.Owner(seat.Seat);
            return seat with {
                State = seat.State == "Resigned" ? seat.State : host.Connected(seat.Seat) ? "Connected" : owner is null ? "Waiting" : "Disconnected" };
        }).ToArray(), host is { } hosting ? hosting.ReleaseSeat : null, () =>
        {
            returningHome = LauncherControl.ReturnHome(lab);
            menu?.Connecting("Returning to the menu…", "Your hosted match remains saved.");
        });
    }
    private string SendInvitation(int seat, ulong friend)
    {
        if (host is null || invitations is null) return "The host is not ready. Close Root and reopen the launcher.";
        if (!host.AssignInvitation(seat, friend)) return "This faction is already assigned to another friend. Choose another faction.";
        return invitations.Send(friend, host.Invitation(seat)) ? "Invitation sent." : "Steam could not send the invitation. Check Steam and try again.";
    }
    public void OnGUI()
    {
        if (Environment.GetEnvironmentVariable("ROOT_LAB_MODE") == "steam-menu") return;
        if (error is not null) { GUI.Box(new Rect(20, 20, 760, 100), error); return; }
        if (host is null)
        {
            if (client?.ReceivedMessages is not > 0) GUI.Box(new Rect(20, 20, 760, 65), message);
            return;
        }
        if (GUI.Button(new Rect(20, 90, 170, 32), "Invite Steam friends"))
        {
            showFriends = !showFriends;
            if (showFriends && invitations is { } invites) friends = invites.Friends().OrderBy(friend => friend.Name).ToArray();
        }
        if (!showFriends) return;
        GUI.Box(new Rect(20, 125, 640, 460), "Steam invitations (friends must open the mod launcher first)");
        for (var seat = 2; seat <= 6; seat++)
            if (GUI.Button(new Rect(35 + (seat - 2) * 120, 155, 112, 30), $"{seat}. {MatchSetup.FactionName(hostedSetup.Factions[seat - 1])}")) inviteSeat = seat;
        GUI.Label(new Rect(35, 190, 600, 45), $"Selected seat: {inviteSeat}. {message}");
        scroll = GUI.BeginScrollView(new Rect(35, 240, 605, 330), scroll, new Rect(0, 0, 580, friends.Length * 36));
        for (var index = 0; index < friends.Length; index++)
        {
            var friend = friends[index];
            if (GUI.Button(new Rect(0, index * 36, 560, 32), $"Invite {friend.Name} to seat {inviteSeat}"))
            {
                if (!host.AssignInvitation(inviteSeat, friend.Id)) message = "Seat belongs to another friend. Resume the host to reset assignments.";
                else message = invitations?.Send(friend.Id, host.Invitation(inviteSeat)) == true
                    ? "Invitation sent." : "Steam did not send the invitation. Check Steam and retry.";
            }
        }
        GUI.EndScrollView();
    }
    private void Cleanup()
    {
        client?.Stop(); client = null;
        guest?.Dispose(); guest = null;
        host?.Dispose(); host = null;
        invitations?.Dispose(); invitations = null;
        api?.Dispose(); api = null;
    }
    public void OnDestroy() { Cleanup(); menu?.Destroy(); }
}
