# Root private six-player prototype

An experimental private host and client mod for Root 2.1.5 (Steam build 22238765). It runs the game's bundled rules engine in a separate Unity process and connects Root's native board and selection controls to that host. The host chooses six factions, including expansion factions and Clockwork bots in friend seats, plus the map, deck, Vagabond characters, landmarks, and hirelings.

Development uses copied game installations and separate Wine prefixes. Nothing is installed into the normal Steam game directory. Each process gets its own muted Xvfb display, with reduced rendering quality and process priority.

This repository contains source and build scripts only. Game files, generated bindings, downloaded dependencies, packaged launchers, and local test evidence are excluded. References to `results/` describe local development runs; those captures are not included. A fresh checkout requires the development setup below before building packages. Clean-checkout setup has not been separately verified.

The [Windows and Linux friends’ launcher](launcher/README.md) provides separate modded installations, Host/Join/Resume controls, targeted Steam invitations, and experimental SteamNetworkingSockets P2P. The host runs the rules engine; Steam supplies the peer transport. The [Cloudflare Durable Object relay](relay/README.md) remains available in the backend for development. The player interface uses Steam. DLC ownership verification is deferred for this MVP. Native Windows gameplay, real invitation delivery, and cross-account Internet play still need testing.

## Friends' launcher MVP

Build the mod and the two mod-only packages:

```sh
bash scripts/build-probe.sh
python3 scripts/safe-test.py python3 scripts/package-launcher.py
```

Outputs are `dist/root-six-player-0.1.0-windows.zip` and `dist/root-six-player-0.1.0-linux.zip`. Windows ships a standalone `Root Six Player.exe`, also available directly in `dist`. Linux requires Python 3.10+ and uses `Play.sh`. Neither package contains Root files or a Markdown guide. The launcher opens Root directly; Host, Join friends, Resume and invitations use an in-game menu built from Root's own fonts and sprites. First-time preparation is automatic. The browser controls are available only with --browser for development. Preparation generates host and client bindings in separate processes to limit peak memory. Steam mode needs no relay deployment. Friends open **Join friends** before accepting an invite. Steam launches ordinary Root if the mod is closed. The development browser controls also support pasted private seat invitations. Cloudflare mode still requires relay deployment.

Verified: all six relay seat credentials, rejection of token spoofing, a native six-seat board and accepted setup move through the local Durable Object, and Windows embedded-Python imports under isolated Proton. Evidence is in `results/relay-test.json`, `results/native-relay-test.json`, and `results/windows-launcher-import-test.json`. These checks do not establish native Windows gameplay or deployed Internet connectivity. The native menu is captured in `results/native-menu-home.png`. Its loopback control API retains authentication and origin checks.

Steam transport checks: `results/steamworks-probe.json` records five logical guests in one signed-in Steam process, ten nearly 4 MiB responses, simulated invitation callbacks, and rejection of invalid tokens, duplicate seats, and seat switching. The native board regression in `results/native-steam-test.json` also rendered six factions and accepted a Riverfolk placement over Steam P2P. These checks do not establish cross-account routing or real invitation delivery.

Launcher checks:

```sh
python3 scripts/test-friends-launcher.py
.lab/sdk/dotnet run --project tools/steam-protocol-tests
python3 scripts/safe-test.py python3 scripts/test-test-budget.py
python3 scripts/safe-test.py python3 scripts/test-native-menu.py --dlc
python3 scripts/safe-test.py python3 scripts/test-dlc.py
python3 scripts/safe-test.py python3 scripts/test-windows-exe.py
python3 scripts/safe-test.py python3 scripts/test-steamworks.py
```

Run game tests and Windows packaging through `scripts/safe-test.py`. It holds a shared exclusive lock and uses one fixed systemd user service with a 4 GiB total RAM cap, 3 GiB memory throttle, no swap, 150% CPU quota, and low CPU/I/O priority. It requires 12 GiB available RAM before starting and stops the job if available RAM drops below the 8 GiB desktop reserve. Headless launcher and Windows build adapters reject launches outside this service. Child Wine and Xvfb processes are removed when the service exits. The reserve cannot prevent unrelated software from exhausting memory, but the test workload stays bounded.

The native DLC menu test uses its own `.lab/friends-launcher-test` copies and prefixes. Only open visible player mode when requested. The remaining commands below are the original development tools; wrap native runs with the same constrained runner. Six simultaneous graphical clients exceed the current budget and are deferred.

## DLC setup

Choose **Host a game** to configure the match in Root. The host can play any human faction. Friend seats can use human factions or any of the four Clockwork bots, with a shared difficulty setting. Bot seats have no invitations and reject player connections.

The setup menu exposes Riverfolk, Underground Duchy, Corvid Conspiracy, Lord of the Hundreds, Keepers in Iron, a second Vagabond, all nine Vagabond characters, four maps, both decks, six landmarks, and all thirteen hireling families available in this build. Hirelings use their demoted sides at six players. Native faction/hireling exclusions are enforced. Lake includes the Ferry and Mountain includes the Tower; up to two additional landmarks can be selected. Advanced setup uses the chosen factions without a draft.

Public settings travel with the private match so guests load the chosen board. Saved native initialization preserves the settings and bot seats. Private seat identities follow the chosen factions even when the native engine exchanges the two Vagabond assignments. Clockwork bots use the shared native AI evaluators for expansion interactions, including returning destroyed Badger relics. Clockwork currently uses no optional traits and Vagabot uses Tinker. Cosmetic DLC uses the game's existing settings. Ownership verification remains deferred; entitlement checks are unchanged.

## What works

- Six authenticated seats, each with a separate native message stream.
- Native six-player board rendering and legal placements through Root's controls in seats 1 and 6.
- Complete faction setup through six TCP clients.
- One full round through all six factions in the native rules probe, with 50 accepted decisions.
- A complete six-player match through authenticated TCP seats: 632 accepted decisions, ending in a native Woodland Alliance victory at 33 points. All six seats received matching standings, including late joins to the completed match.
- Combat in the full-match run, including battles, dice rolls, ambushes, casualties, and Field Hospitals. A separate graphical client test plays an ambush through Root's native card dialog.
- Entity and integer target responses, optional passes, faction custom choices, and Riverfolk prices. Entity selections preserve repeated targets for recruiting multiple warriors in one clearing. Weighted selections use Root's native selection-control validation.
- Current-state join snapshots with seat-specific visibility. Tests cover hidden shared decks, Eyrie's private deck, and dealt hands, including Riverfolk's public hand.
- Host validation of seat ownership, selection counters, targets, selection counts, prices, and request format.
- Autosave and native checkpoint recovery. A resumed match accepted 436 further decisions and reached an Alliance victory at 31 points. The focused finished-match regression verifies all six snapshots, hands, and standings after restart, including explicit ownership of transferred cards.
- A native client in seat 6 joined and made a setup move through an authenticated SSH tunnel between isolated local processes.

This is a working development prototype, not a complete release. Six simultaneous graphical clients and the final results UI have not been verified. One full match does not establish exhaustive faction or combat coverage. Weighted selections pass native-control checks but were not encountered in the full-match run. Grouping selections and other specialized messages, chat, and resignation are not implemented. Unsupported actions can leave the client waiting; inspect its log. Restarting a client requires its still-valid invitation and a running host. There is no automatic reconnection.

## Prepare and test

Run from this directory:

```sh
python3 scripts/setup-lab.py
bash scripts/build-probe.sh
bash scripts/setup-client-lab.sh
python3 scripts/test-loopback.py
python3 scripts/test-gameplay.py
python3 scripts/test-gameplay.py --restart-at 250
python3 scripts/test-client.py --seat 1
python3 scripts/test-client.py --seat 6
python3 scripts/test-client.py --scenario ambush
python3 scripts/test-client.py --seat 6 --ssh-tunnel-test
python3 scripts/test-launcher.py
python3 scripts/test-runner-isolation.py
bash scripts/run-lab.sh selection-tests
bash scripts/run-lab.sh recovery-tests
ROOT_LAB_TEST_SEED=12345 bash scripts/run-lab.sh setup 6
```

Prerequisites: installed and initialized Root, Python 3.12+, Xvfb, xvfb-run, xauth, flock, Steam's Proton Experimental, and SteamLinuxRuntime_4. Setup copies local game files and the Proton prefix, and downloads checksum-verified BepInEx and .NET dependencies. First binding generation may take several minutes. Do not distribute the copied game files.

Build and setup scripts acquire lab locks. Stop running sessions before updating their plugins. The host lab is `.lab/`; the separate client lab is `.lab/client/`. Tests clean up their own processes and temporary credentials.

Retained evidence is in `results/`. Detailed logs and generated API bindings stay in ignored `.lab/`. Native test results must report `"status": "passed"`; Proton's exit code alone is insufficient.

`test-gameplay.py` drives all six seats to a native victory, checks hand privacy every 100 decisions, and verifies finished-match joins. `selection-tests` covers repeated recruitment targets, 32 comparisons with native weighted controls, and six weighted boundary cases. The gameplay driver uses seeded choices, but native entity ordering can change the path between runs.

`test-gameplay.py --restart-at 250` kills only the lab host's Root process, restores its checkpoint with fresh credentials, compares all six snapshots and pending decisions, and continues to victory. The focused `test-completed-recovery.py CHECKPOINT PRE_CRASH_SNAPSHOTS` reruns finished-match recovery against captured pre-crash snapshots. The final ownership fix was verified with that focused regression; the entire long gameplay test has not been repeated after that fix.

## Start a manual session

Start a host in one terminal:

```sh
python3 scripts/private-match.py host --minutes 180 --save .lab/saves/my-match.json
```

It prints a private invitation directory containing `seat-1.json` through `seat-6.json`. In another terminal, substitute that actual path:

```sh
python3 scripts/private-match.py client --invite /path/to/invitations/seat-1.json --minutes 180
```

The manual client makes no automatic moves. It renders on its own Xvfb display, so no game window appears on the normal desktop. While it runs, `.lab/client/results/client/display.json` records its `DISPLAY` and `XAUTHORITY` values for attaching your own X11 tools. A viewer is not bundled. Additional client labs can be prepared with `bash scripts/setup-client-lab.sh 2` and selected with `python3 scripts/private-match.py client --seat-lab 2 --invite ...`. Seat lab numbers run from 1 to 6; the invitation determines the actual game seat.

The host binds only to `127.0.0.1` on an automatically assigned port. Invitations are bearer credentials for individual seats, stored with mode 0600. Keep them private. The native client has passed a setup move through an authenticated SSH tunnel with a pinned host key, using isolated processes on this machine. Separate-machine LAN and Internet routing remain untested. The protocol has no TLS and must not be exposed directly to the Internet.

Ctrl+C stops the launched session. Invitations expire when the host stops and its invitation directory is removed. With `--save`, every accepted move is checkpointed before acknowledgment. Resume it with:

```sh
python3 scripts/private-match.py host --resume .lab/saves/my-match.json
```

The resumed host issues new invitations. Restart clients using those invitations; old tokens are rejected. Recreate any SSH forward using the resumed host's new port. Checkpoints contain all private hands, so keep their mode-0600 files private. Checkpoints are tied to this game build. A new host refuses to overwrite an existing save; use `--resume` or a new filename. Without `--save`, the match lives only in memory. The default lifetime is three hours; `--minutes` accepts 1 through 720. Logs are in `.lab/results/server/` and `.lab/client/results/client/`.

## Simultaneous graphical-client test

Six simultaneous graphical clients remain unverified. That test exceeds the current 4 GiB development budget and is deferred. Do not increase the cap to run it during normal desktop use.

## SSH forwarding

On a client machine, forward a local port to the host's printed port:

```sh
ssh -N -T -o ExitOnForwardFailure=yes \
  -L 127.0.0.1:29655:127.0.0.1:HOST_PORT user@host
```

Copy that seat's invitation privately to the client, change its `port` to `29655`, and keep its token unchanged. The mod connects to the local tunnel endpoint. The host needs an SSH service and both machines need their own Root installations and prepared labs. The local `--ssh-tunnel-test` uses disposable keys and a temporary loopback-only SSH service; it does not change your SSH configuration or authorized keys.

## Implementation

- `HostedMatch.cs`: native six-player authority, per-seat serialization, snapshots, response validation.
- `LoopbackProbe.cs`: authenticated JSON-lines transport, one request per TCP connection.
- `PrivateClient.cs`: redirects native outgoing selections to the host and feeds its messages into Root's board. It supplies a local identity during snapshot initialization without official account tokens.
- `PrivateClientBehaviour.cs`: loopback client lifecycle.
- `SteamSessionBehaviour.cs`: Steam host, invite waiting, guest lifecycle, and targeted invitation controls.
- `SteamNative.cs` and `SteamSockets.cs`: the bundled Steam C ABI and owned-socket callback routing.
- `SteamHost.cs`, `SteamGuest.cs`, `SteamChannel.cs`, and `SteamFrames.cs`: seat authentication, loopback authority forwarding, and bounded reliable framing.
- `SteamInvitation.cs` and `SteamInvitations.cs`: private invite encoding, sender validation, and Steam callbacks.
- `TargetChoice.cs` and `WeightedTargets.cs`: entity, integer, and weighted selection validation.
- `MatchCheckpoint.cs`: atomic native checkpoints and host recovery, with fresh credentials on restart.
- `ClientProbe.cs`, `SetupProbe.cs`, and `SelectionValidationProbe.cs`: automated native integration checks.

The host uses `TuberMatch` with `matchType=Live`, native JSON analyzers, and `ObfuscatedMessageActionFactory`. The client suppresses its local rules authority. The native six-seat layout works without an additional layout patch.

Tested stack: Unity 2022.3.62f2, Windows x64 IL2CPP, BepInEx 6.0.0-be.788+5b766a3, .NET SDK 6.0.428, Proton Experimental / Wine 11.0. The mod is tied to this game build and may break after an update. History is retained in memory and responses are capped at 4 MiB by the client; long-match history handling still needs work.

The private match transport does not use Dire Wolf's multiplayer service. The unmodified game startup can still request public store/catalog data. These tests do not establish how the official multiplayer service enforces its four-player limit.
