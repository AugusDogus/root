using System.Text.Json;
using System.Diagnostics;
using UnityEngine;

namespace RootEngineProbe;

// Does not create a public lobby, publish presence, or invite/message friends.
public sealed class SteamworksProbe : MonoBehaviour
{
    private readonly Stopwatch clock = new();
    private bool finished;
    private SteamNative? api;
    private SteamP2pProbe? p2p;
    private bool p2pPassed;
    private SteamTransportProbe? transport;
    private bool transportPassed;
    private bool invitationPassed;
    private uint first;
    private uint second;
    private int verified;
    private byte[]? expected;
    private readonly string output = Path.GetFullPath(Path.Combine(BepInEx.Paths.GameRootPath, "..", "results", "steam-probe"));

    public SteamworksProbe(IntPtr pointer) : base(pointer) { }
    public void Start() { clock.Start(); QualitySettings.SetQualityLevel(0, true); Application.targetFrameRate = 10; }

    public void Update()
    {
        AudioListener.volume = 0;
        if (finished) return;
        var elapsed = clock.Elapsed.TotalSeconds;
        try
        {
            if (elapsed > 180) { Finish("blocked", "Steam did not complete the local probe within 180 seconds."); return; }
            if (elapsed < 25) return;
            if (!Steamworks.SteamClient.IsValid || !Steamworks.SteamClient.IsLoggedOn)
            {
                if (elapsed > 90) Finish("blocked", "Root's Steam client is not initialized and signed in in this isolated launch.");
                return;
            }
            if (api is null)
            {
                api = new SteamNative();
                (first, second) = api.Pair();
                var count = 0;
                using var invites = new SteamInvitations(api, _ => count++);
                const ulong identity = 0x0110000100000001;
                var invitation = new SteamInvitation(identity, 555, 2, new string('a', 32));
                invites.SimulateAcceptance(identity, "ordinary-root-invitation");
                invites.SimulateAcceptance(identity + 1, invitation.Encode());
                if (count != 0) throw new InvalidDataException("Steam invitation accepted an invalid sender or format.");
                invites.SimulateAcceptance(identity, invitation.Encode());
                invitationPassed = count == 1;
                if (!invitationPassed) throw new InvalidOperationException("Steam invitation callback did not dispatch.");
            }
            // Move a 4 MiB-equivalent stream in bounded reliable chunks, both ways.
            for (var step = 0; step < 8 && verified < 64; step++)
            {
                var sender = verified % 2 == 0 ? first : second;
                var receiver = verified % 2 == 0 ? second : first;
                if (expected is null)
                {
                    expected = new byte[65536];
                    for (var index = 0; index < expected.Length; index++) expected[index] = (byte)((index + verified) % 251);
                    var result = api.SendReliable(sender, expected);
                    if (result != 1) throw new IOException($"Steam reliable send failed with result {result}.");
                }
                var received = api.ReceiveOne(receiver);
                if (received is null) break;
                if (!received.SequenceEqual(expected)) throw new InvalidDataException("Steam socket-pair payload did not match.");
                expected = null;
                verified++;
            }
            if (verified == 64)
            {
                p2p ??= new SteamP2pProbe(api);
                if (!p2pPassed) p2pPassed = p2p.Tick();
                if (!p2pPassed) return;
                transport ??= new SteamTransportProbe(api, Path.Combine(BepInEx.Paths.GameRootPath, "..", "steam-config.json"));
                transportPassed = transport.Tick();
                if (transportPassed) Finish("passed", "Local Steam P2P, five-seat transport and simulated invitation callback verified. Cross-account routing and real invitations are not tested.");
            }
        }
        catch (Exception error) { Finish("failed", error.Message); }
    }

    private void Finish(string status, string detail)
    {
        finished = true;
        Directory.CreateDirectory(output);
        var report = new
        {
            status, detail, chunks = verified, bytes = verified * 65536,
            steamInitialized = Steamworks.SteamClient.IsValid,
            loggedOn = Steamworks.SteamClient.IsValid && Steamworks.SteamClient.IsLoggedOn,
            overlayEnabled = api?.OverlayEnabled,
            inviteExport = api?.HasExport("SteamAPI_ISteamFriends_InviteUserToGame"),
            listenP2PExport = api?.HasExport("SteamAPI_ISteamNetworkingSockets_CreateListenSocketP2P"),
            connectP2PExport = api?.HasExport("SteamAPI_ISteamNetworkingSockets_ConnectP2P"),
            p2pSelfConnection = p2pPassed, p2pCallbacks = p2p?.CallbackCount,
            invitationCallback = invitationPassed, fiveSeatTransport = transportPassed,
            responses = transport?.CompletedResponses, rejectedPeers = transport?.Rejections,
            transportCallbacks = transport?.Callbacks, acceptedConnections = transport?.AcceptedConnections,
            guestIncomingCallbacks = transport?.GuestIncomingCallbacks,
            remotePeerTested = false, invitationsSent = 0
        };
        File.WriteAllText(Path.Combine(output, "test-results.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Cleanup();
        Application.Quit(status == "passed" ? 0 : 1);
    }

    private void Cleanup()
    {
        if (api is null) return;
        transport?.Dispose(); transport = null;
        p2p?.Dispose();
        p2p = null;
        if (first != 0) api.CloseConnection(first);
        if (second != 0) api.CloseConnection(second);
        api.Dispose();
        api = null;
    }
    public void OnDestroy() => Cleanup();
}
