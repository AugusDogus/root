# Development and validation

Developer setup, architecture, and recorded test coverage. For installation and play instructions, see the [project README](../README.md). Commands below run from the repository root. The TCP and SSH sessions are diagnostic tools; the player launcher uses Steam.

An experimental private host and client mod for Root 2.1.5 (Steam build 22238765). It runs the game's bundled rules engine in a separate Unity process and connects Root's native board and selection controls to that host. The host configures six seats, including ordinary AI or Clockwork, plus the map, deck, landmarks, and hirelings. Friends choose their factions in Root's native waiting room.

Development uses copied game installations and separate Wine prefixes. Nothing is installed into the normal Steam game directory. Each process gets its own muted Xvfb display, with reduced rendering quality and process priority.

This repository contains source and build scripts only. Game files, generated bindings, downloaded dependencies, packaged launchers, and local test evidence are excluded. References to `results/` describe local development runs; those captures are not included. A fresh checkout requires the development setup below before building packages. GitHub Actions has verified a clean checkout through SteamCMD download, binding generation, mod compilation, and launcher packaging. The local development setup below still requires an installed game.

The [Windows and Linux friends’ launcher](../launcher/README.md) provides separate modded installations, Host/Join/Resume controls, targeted Steam invitations, and experimental SteamNetworkingSockets P2P. The host runs the rules engine; Steam supplies the peer transport. Steam is the only supported multiplayer transport. The private playtest exposes installed gameplay expansions without account unlocks. Native Windows gameplay, real invitation delivery, and cross-account Internet play still need testing.

## Building the launcher

Build the mod and the two mod-only packages:

```sh
python3 scripts/safe-test.py bash scripts/build-probe.sh
python3 scripts/safe-test.py python3 scripts/package-launcher.py
```

Building the current source produces `dist/root-six-player-0.7.2-windows.zip` and `dist/root-six-player-0.7.2-linux.zip`. Each ZIP contains one native Go executable, with BepInEx, its .NET runtime, matching Unity base libraries, the mod, and third-party notices embedded. Players need neither Python nor Go. Windows opens **Root Six Player.exe**; Linux opens **Root Six Player**. The launcher discovers Root in Steam, prepares two isolated copies automatically, generates bindings sequentially, opens Root's native menu, and exits. The mod manages the rules host, saved matches, and returning to the menu within the running game. There is no game selector, browser interface, or dependency download during player setup. Steam and Root must already be installed; Linux also needs Steam's Proton Experimental and Steam Linux Runtime 4.

The launcher reuses the existing data folder and compatible saved matches when upgrading from the Python launcher. It refuses to overwrite an unknown modified mod file. Friends open **Join friends** before accepting an invite; Steam opens ordinary Root if the mod is closed. Python launcher modules remain solely as development adapters for older engine probes.

Build requirements are Go 1.23+, .NET 10 for standalone tools, Python for development scripts, the local game/mod build prerequisites below, and optionally MinGW windres for the Windows icon. Dependency archives and their notices are pinned and verified during packaging. Run `Root Six Player --licenses DIRECTORY` to extract embedded third-party notices and LGPL source archives. No Root game files or generated bindings are bundled.

The native menu is captured in `results/native-menu-home.png`. The mod launches its separate rules host directly and stops it when leaving the table or closing Root. The host also watches its owning game process, so a crashed client cannot leave an orphaned match advancing. There is no launcher HTTP API or persistent launcher process.

First-time setup uses embedded archives with a filename-only Unity library source, so BepInEx cannot fall back to downloading those libraries. Binding generation still runs locally from the player's installed game. The Windows and Linux launchers are standalone binaries; the C# mod uses the bundled BepInEx runtime.

Steam transport checks: `results/steamworks-probe.json` records five logical guests in one signed-in Steam process, ten nearly 4 MiB responses, simulated invitation callbacks, and rejection of invalid tokens, duplicate seats, and seat switching. It also verifies reassignment, rejection of the previous invitation, and refusal to release a connected seat. The native board regression in `results/native-steam-test.json` rendered six factions, recovered after forcibly closing the Steam connection, and accepted a Riverfolk placement over Steam P2P. These checks do not establish cross-account routing or real invitation delivery.

Launcher checks:

```sh
python3 scripts/safe-test.py python3 scripts/package-launcher.py --stage
python3 scripts/safe-test.py env GOMAXPROCS=2 go -C launcher-native test -p 1 ./...
python3 scripts/safe-test.py python3 scripts/test-friends-launcher.py
python3 scripts/safe-test.py .lab/sdk/dotnet run --project tools/steam-protocol-tests
python3 scripts/safe-test.py python3 scripts/test-test-budget.py
python3 scripts/safe-test.py python3 scripts/test-test-diagnostics.py
python3 scripts/safe-test.py python3 scripts/test-runner-isolation.py
python3 scripts/safe-test.py python3 scripts/test-native-communications.py --chat
python3 scripts/safe-test.py python3 scripts/test-native-communications.py
python3 scripts/safe-test.py python3 scripts/test-online-authority.py --model
python3 scripts/safe-test.py python3 scripts/test-online-authority.py
python3 scripts/safe-test.py python3 scripts/test-online-lobby.py
python3 scripts/safe-test.py python3 scripts/test-online-lobby.py --dlc
python3 scripts/safe-test.py python3 scripts/test-mod-session.py
python3 scripts/safe-test.py python3 scripts/test-native-menu.py --dlc
python3 scripts/safe-test.py python3 scripts/test-native-menu.py --ai
python3 scripts/safe-test.py python3 scripts/test-dlc.py
python3 scripts/safe-test.py python3 scripts/test-windows-exe.py
python3 scripts/safe-test.py python3 scripts/test-steamworks.py
python3 scripts/safe-test.py python3 scripts/test-session-features.py
```

Run game tests and Windows packaging through `scripts/safe-test.py`. It holds a shared exclusive lock and uses one fixed systemd user service with an 8 GiB total RAM cap, 6 GiB memory throttle, no swap, 150% CPU quota, and low CPU/I/O priority. It requires 16 GiB available RAM before starting and stops the job if available RAM drops below the 8 GiB desktop reserve. Headless launcher and Windows build adapters reject launches outside this service. Child Wine and Xvfb processes are removed when the service exits. The reserve cannot prevent unrelated software from exhausting memory, but the test workload stays bounded. The legacy shell runner also rejects launches outside this service.

Each constrained job writes a private `.lab/test-runs/<run-id>/` directory with `output.log` and an atomic `report.json`. `.lab/test-runs/latest.json` points to the latest run. Instrumented tests add named stage durations and screenshots. Native communication probes isolate provider initialization and timer behavior before the slower graphical regression. A closed output reader does not terminate the service.

The native DLC menu test uses its own `.lab/friends-launcher-test` copies and prefixes. Only open visible player mode when requested. The remaining commands below are the original development tools; wrap native runs with the same constrained runner. Six simultaneous graphical clients remain unverified within this budget.

## Match controls and updates

The **Match** button shows seat presence and readiness. Ready indicators are informational; they do not pause turns. Hosts can explicitly reassign disconnected seats, which invalidates the old invitation. A pending move must finish before reassignment. Steam guests recover from temporary connection loss without repeating moves.

**Resign** hands the faction to Root's native AI and lets the player watch. Resignation survives checkpoint recovery. **Return to menu** closes the current game session and opens the launcher menu again. For hosts, this disconnects guests and keeps the saved match. The completion screen identifies the winner and lets players inspect the final board or return to the menu.

Close the game before updating, then open the new launcher. It replaces only recognized mod files, rolls back failed updates, and preserves saves and the Steam installation. Everyone must use the same release. Source version 0.7.2 uses invitation protocol 6 and writes version 4 saves, including chat history and timer deadlines. It reads version 1, 2, and 3 saves; earlier releases cannot load newly written saves. **Get updates** opens the repository's releases page; private downloads require repository access.

GitHub Actions builds Windows and Linux player packages on pushes to `main` and manual runs. Matching version tags automatically publish the latest release after all checks, including the Windows executable smoke test, pass. See [launcher CI setup](launcher-ci.md) for the one-time Steam login setup. CI downloads Root and generates its build references automatically. The local `scripts/release-launcher.py` remains available as a draft-release fallback; both paths validate and upload only the Windows EXE, two player ZIPs, and checksums.

## DLC setup

Choose **Host a game** to open Root's native online setup screen, extended to six player slots. Creating the room opens the native waiting room on the private authority. Invite friends into human seats; each friend chooses a faction and clicks Join Game. The host can start after every human seat has joined and is connected. No match starts or save is created before that point. Root supplies the faction picker, ordinary AI difficulty, Clockwork configuration, maps, decks, advanced setup and faction drafting, clearing suits, landmarks, hirelings, co-op, and Bluff controls. Native content availability and faction exclusions still apply. The host remains human; other seats can use the controllers permitted by the game. Ordinary AI uses Root's shared difficulty setting. AI and Clockwork seats have no invitations and reject player connections.

The complete native configuration goes to the local authority, preserving Root's choices and shuffled turn order. Guests receive a separately constructed public initialization and their own obfuscated board state. Saves preserve the native configuration. Private seats retain their identities during faction drafting, which replaces native player entities. Older saves retain their original faction-based seat mapping. Clockwork uses native rules and traits; cosmetic DLC uses the game's existing settings. All nine installed gameplay expansion products are available locally in this private playtest. Purchases and the ordinary Steam installation are unchanged. Automated DLC engine tests supply configurations directly. The online UI regression declares fixture ownership for Clockwork, Riverfolk, Underworld, and Exiles & Partisans in its isolated test mode. Those earlier fixtures do not verify purchasing or real-account unlocks. The current private playtest has its own local content availability override, installed only when entering private setup or a match.

The original DLC regression matrix covered 14 scenarios with 1,679 accepted decisions, including recovery checks for Clockwork settings and paired Vagabond characters. Seven ordinary-AI cases passed with 718 accepted human decisions plus native AI turns. They cover all ten faction evaluators, individual difficulties, mixed Clockwork games, paired human/AI Vagabonds, and a human with five AI opponents. Every AI case passed recovery; three games reached native victories, including hosting as the second Vagabond while AI controlled the first. These runs cover setup, turns, and recovery, not every possible expansion interaction.

## Setup, chat, and timer coverage

The [native UI audit](native-ui-audit.md) records code and prefab evidence for six-player capacity, targeted graphical checks, and remaining coverage gaps. Its scanners inspect the installed game without launching it.

Before the online waiting-room integration, the native offline configuration regression covered standard setup, faction drafting with randomized clearing suits, random factions, paired human/AI Vagabonds, and Clockwork co-op each passed 120 decisions and save/recovery checks through the private authority. An additional draft test restarted after the first choice with five unassigned factions, then completed 120 decisions. Recovery preserves neutral ruin ownership during the draft. An older completed version 1 save also passed all six snapshot, private-hand, and standings comparisons. The native launcher flow passed hosting with four ordinary AI opponents, invitation filtering, readiness, and return to menu with the save preserved. These are local tests, not a cross-account Internet playtest.

The online setup now preserves Bluff and uses private lobby membership, faction updates, and host-controlled start. Native model tests cover six humans, one human with five AI, faction selection following the local player between seats five and six, and leaving/rejoining without losing the local choice. Private-authority tests verify host-only start, required human confirmations, faction rejection, stable identities, save creation at start, and preservation of non-default maps and decks. The online adapter projects settings from the native request options into match initialization and rejects malformed values. The isolated graphical regression clicks the native Create, Join, and Start buttons, leaves and rejoins the waiting room, and opens the Lake board with Exiles & Partisans, two human seats, one Clockwork, three ordinary AI, and Bluff enabled. It detects no official lobby writes or lobby socket connections. The friend is simulated locally; content ownership is a test fixture. Private sessions use per-seat Steam invitations instead of passwords, and the waiting room disables the stock lobby socket. Chat uses native lobby and board controls with an authenticated private history. Fast and Slow setup retain native turn timer settings. The original offline setup remains a diagnostic helper. Full parity with official online multiplayer is not claimed.

Chat retains the most recent 128 messages, limits messages to 500 characters and one per second per seat, and restores history after reconnect or host restart. Sender identity comes from the authenticated seat. Native chat renders player text literally and disables official-account social actions.

The private host uses Root's native timer actions, grace period, and timeout-to-AI behavior. Native continuations run on the authority thread. Display clocks use the host's UTC time, independent of the client timezone. Saves retain active deadlines and original start times so restarts do not reset the clock or charge elapsed time twice. Deadlines continue while the host is closed; an expired deadline triggers timeout on resume. Native probes verify Fast/Slow expiry, countdown conversion, two restarts followed by a legal choice, preserved time bank/padding, expired-save recovery, and malformed checkpoint rejection. Standard and Advanced Setup openings produce one active decision in the probe; simultaneous-decision restore and expiry remain unverified. The graphical regression verifies incoming chat and mouse-driven sending in both lobby and board, visible message text inside the chat viewport, and a visible animated clock that advances across rendered frames. In the tested five-minute Fast game, Root shows that clock during the final minute.

## Gameplay validation

- Six authenticated seats, each with a separate native message stream.
- Native six-player board rendering and legal placements through Root's controls in seats 1 and 6.
- Complete faction setup through six TCP clients.
- One full round through all six factions in the native rules probe, with 50 accepted decisions.
- A complete six-player match through authenticated TCP seats: 1,020 accepted decisions, ending in a native Woodland Alliance victory at 31 points. All six seats received matching standings, including late joins to the completed match. Eleven periodic checks verified private hand visibility.
- Combat in the full-match run, including battles, dice rolls, ambushes, casualties, and Field Hospitals. A separate graphical client test plays an ambush through Root's native card dialog.
- Entity and integer target responses, optional passes, grouped targets, attributed choices, faction custom choices, and Riverfolk prices. Entity selections preserve repeated targets for recruiting multiple warriors in one clearing. Weighted and grouped selections use Root's native selection-control validation.
- Current-state join snapshots with seat-specific visibility. Tests cover hidden shared decks, Eyrie's private deck, and dealt hands, including Riverfolk's public hand.
- Host validation of seat ownership, selection counters, targets, selection counts, prices, and request format.
- Autosave and native checkpoint recovery. The host was killed at decision 250, resumed with all six snapshots and pending decisions intact, and accepted 770 further decisions to victory. A second restart preserved the completed match, hands, and standings, including explicit ownership of transferred cards.
- A native client in seat 6 joined and made a setup move through an authenticated SSH tunnel between isolated local processes.

This is a working playtest build. Six simultaneous graphical clients have not been verified. The native UI test covers invitations, readiness, and returning to the menu with the save preserved. A completed save displays all six standings and can return to the final board. Root's built-in four-player victory scene is replaced by the mod's results screen. One full match does not establish exhaustive faction or combat coverage. Weighted selections pass native-control checks but were not encountered in the full-match run. Unknown actions show an in-game explanation and refresh the board. Steam guests reconnect automatically to a running host and fetch a current private snapshot without replaying an unconfirmed move. If retries fail, the game offers Reconnect. A host restart still requires fresh invitations.

## Prepare and test

Run from the repository root:

```sh
python3 scripts/safe-test.py python3 scripts/setup-lab.py
python3 scripts/safe-test.py bash scripts/build-probe.sh
python3 scripts/safe-test.py bash scripts/setup-client-lab.sh
python3 scripts/safe-test.py python3 scripts/test-loopback.py
python3 scripts/safe-test.py python3 scripts/test-gameplay.py
python3 scripts/safe-test.py python3 scripts/test-gameplay.py --restart-at 250
python3 scripts/safe-test.py python3 scripts/test-client.py --seat 1
python3 scripts/safe-test.py python3 scripts/test-client.py --seat 6
python3 scripts/safe-test.py python3 scripts/test-client.py --scenario ambush
python3 scripts/safe-test.py python3 scripts/test-client.py --seat 6 --ssh-tunnel-test
python3 scripts/safe-test.py python3 scripts/test-launcher.py
python3 scripts/safe-test.py python3 scripts/test-runner-isolation.py
python3 scripts/safe-test.py bash scripts/run-lab.sh selection-tests
python3 scripts/safe-test.py bash scripts/run-lab.sh recovery-tests
python3 scripts/safe-test.py env ROOT_LAB_TEST_SEED=12345 bash scripts/run-lab.sh setup 6
```

Rerun `scripts/setup-lab.py` after updating to install the pinned .NET 10 SDK alongside any existing SDK. The mod itself remains a .NET 6 library for compatibility with the bundled BepInEx runtime.

Prerequisites: installed and initialized Root, Python 3.12+, Xvfb, xvfb-run, xauth, flock, Steam's Proton Experimental, and SteamLinuxRuntime_4. Setup copies local game files and the Proton prefix, and downloads checksum-verified BepInEx and .NET dependencies. First binding generation may take several minutes. Do not distribute the copied game files.

Run these commands sequentially. The wrapper allows one job at a time, including all of that job's host and client processes. It refuses to start while Root is running outside the test service and stops its own job if another Root game opens. `run-lab.sh` rejects launches outside the constrained service. Build and setup scripts also acquire lab locks. Stop running sessions before updating their plugins. The host lab is `.lab/`; the separate client lab is `.lab/client/`. Tests clean up their own processes and temporary credentials.

Local generated evidence is in ignored `results/`. Detailed logs and generated API bindings stay in ignored `.lab/`. Native test results must report `"status": "passed"`; Proton's exit code alone is insufficient.

`test-gameplay.py` drives all six seats to a native victory, checks hand privacy every 100 decisions, and verifies finished-match joins. Fresh games must demonstrate combat and completed Riverfolk purchases in the native action log. The driver exercises both purchases and declines, honoring disabled service choices. `selection-tests` compares optional and forced choices, repeated recruitment targets, overlapping groups, numeric ranges, and weighted choices with native controls. The gameplay driver uses seeded choices, but native entity ordering can change the path between runs.

Add `--exercise-undo` to the full-match test to submit each offered Undo prompt once per seat and continue to victory. The result lists accepted Undo prompts; the run fails if it reaches victory without exercising one. Ordinary random-play runs still avoid Undo loops.

Run `python3 scripts/safe-test.py python3 scripts/test-gameplay.py --alliance-actions --seed 12345` for Alliance Recruit, Organize, and Undo after Organize. This policy trains officers and moves warriors so those actions become available. It fails unless the authority accepts all three actions. The regression completed after 250 decisions.

The native response probe checks complete selection responses, including mixed automatic and player-selected targets. Root omits targets marked `Selected=false`; the authority must validate only player-selected targets. Earlier test players supplied automatic targets themselves, hiding failures in native Recruit and Undo buttons. Two full matches passed after correcting that mismatch (402 and 610 decisions). These checks cover response generation and rule execution, not every button or animation.

`test-online-lobby.py --navigation-only` checks six individual character models, Reset, lobby Back, and creating a replacement lobby with AI seats. For a checkpoint stopped at a two-item Explore decision, `test-native-ui.py --fixture CHECKPOINT --explore-item` verifies both rendered items and submits one through the native prompt. It disables timers in a disposable copy. The original checkpoint is preserved. `ROOT_LAB_RECOVERY_FIXTURE=CHECKPOINT bash scripts/run-lab.sh recovery-tests 6` checks that only the explorer can see the items and that the host accepts the selection. Run each through `safe-test.py`.

For a checkpoint with a Vagabond, `test-native-ui.py --fixture CHECKPOINT --reported-ui` checks the expanded backpack at 720p, 800p, and 1080p, native resignation confirmation, and returning to an interactive menu after a failed connection. It uses an untimed disposable copy of the save. `ROOT_LAB_RECOVERY_FIXTURE=CHECKPOINT ROOT_LAB_RECOVERY_ACTION=discard-continue bash scripts/run-lab.sh recovery-tests` replays an empty Continue choice from a pending discard checkpoint.

`test-dlc.py` accepts case names to restrict the expansion sweep. `--resume-checkpoint CHECKPOINT` replays one selected case from a disposable copy; replay failures use separate diagnostic filenames. Set `ROOT_DLC_TEST_SEED` inside the constrained job to vary both engine setup and test-player choices, for example `python3 scripts/safe-test.py env ROOT_DLC_TEST_SEED=24680 python3 scripts/test-dlc.py marauder vagabonds`. These engine probes exercise submitted actions and save recovery. They do not establish that every corresponding screen is reachable or correctly laid out.

`test-gameplay.py --restart-at 250` kills only the lab host's Root process, restores its checkpoint with fresh credentials, compares all six snapshots and pending decisions, and continues to victory. This full regression passes, including a second restart after the match finishes. The focused `test-completed-recovery.py CHECKPOINT PRE_CRASH_SNAPSHOTS` can rerun finished-match recovery against captured pre-crash snapshots.

## Manual diagnostic sessions

For a local host-and-client smoke test, use the existing orchestrator. It starts both processes within one budget, checks the connection, and stops them:

```sh
python3 scripts/safe-test.py python3 scripts/test-launcher.py
```

For a host-only session:

```sh
python3 scripts/safe-test.py python3 scripts/private-match.py host --minutes 180 --save .lab/saves/my-match.json
```

The wrapper prints its durable log location. That log contains the private invitation directory with `seat-1.json` through `seat-6.json`. A second wrapper on this machine will be rejected while the host runs.

For a longer local manual session, put both helpers in a shell script: start `scripts/private-match.py host` in the background, wait for its invitations, then start `scripts/private-match.py client --invite /actual/invitation/seat-1.json`. Keep the script running until both helpers stop and clean up both on exit. Run the complete script once through the wrapper:

```sh
python3 scripts/safe-test.py bash /path/to/session.sh
```

Do not wrap the helpers again inside that script. The wrapper does not provide an interactive shell; read progress from its printed log path.

The manual client makes no automatic moves. It renders on its own Xvfb display, so no game window appears on the normal desktop. While it runs, `.lab/client/results/client/display.json` records its `DISPLAY` and `XAUTHORITY` values for attaching your own X11 tools. A viewer is not bundled. Additional client labs can be prepared before the session with `python3 scripts/safe-test.py bash scripts/setup-client-lab.sh 2`. Select one with the client helper's `--seat-lab 2` option inside the shared session script. Seat lab numbers run from 1 to 6; the invitation determines the actual game seat. All local processes share the same 8 GiB budget.

The host binds only to `127.0.0.1` on an automatically assigned port. Invitations are bearer credentials for individual seats, stored with mode 0600. Keep them private. The native client has passed a setup move through an authenticated SSH tunnel with a pinned host key, using isolated processes on this machine. Separate-machine LAN and Internet routing remain untested. The protocol has no TLS and must not be exposed directly to the Internet.

Ctrl+C stops the launched session. Invitations expire when the host stops and its invitation directory is removed. With `--save`, every accepted move is checkpointed before acknowledgment. Resume it with:

```sh
python3 scripts/safe-test.py python3 scripts/private-match.py host --resume .lab/saves/my-match.json
```

The resumed host issues new invitations. Restart clients using those invitations; old tokens are rejected. Recreate any SSH forward using the resumed host's new port. Checkpoints contain all private hands, so keep their mode-0600 files private. Checkpoints are tied to this game build. A new host refuses to overwrite an existing save; use `--resume` or a new filename. Without `--save`, the match lives only in memory. The default lifetime is three hours; `--minutes` accepts 1 through 720. Logs are in `.lab/results/server/` and `.lab/client/results/client/`.

## Simultaneous graphical-client test

Six simultaneous graphical clients remain unverified. The increased 8 GiB development budget has not been validated for that workload. Keep any future test within the shared cap and desktop reserve.

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

Tested stack: Unity 2022.3.62f2, Windows x64 IL2CPP, BepInEx 6.0.0-be.788+5b766a3, .NET SDK 10.0.401, Proton Experimental / Wine 11.0. The mod is tied to this game build and may break after an update. Each seat retains at most 8 MiB of serialized history, with absolute cursors and updates paginated below the 4 MiB transport limit. Lagging clients receive a private snapshot. A snapshot that exceeds the transport limit produces an explicit error. Normal player sessions have no fixed lifetime; development runs retain their time limits.

The private match transport does not use Dire Wolf's multiplayer service. The unmodified game startup can still request public store/catalog data. These tests do not establish how the official multiplayer service enforces its four-player limit.
