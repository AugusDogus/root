# Make private six-player Root easy to play

Historical delivery plan, retained for context. Some milestones below are superseded by the native menu, Steam networking, and DLC setup. The Cloudflare prototype has been removed; Steam is now the only supported multiplayer transport. The current private demo exposes installed gameplay expansions locally, superseding the earlier entitlement scope below. See [README.md](README.md) for current behavior and validation limits.

## Target experience

Ship a friends' launcher for **Windows and Linux**. Each player installs Root through Steam, opens the launcher, and chooses **Host**, **Join**, or **Resume**. The launcher prepares a separate modded installation from that player's local files. Playing ordinary Root through Steam continues to work normally.

The first release uses the tested base-game plus Riverfolk roster: Marquise, Eyrie, Alliance, Vagabond, Lizards, and Riverfolk. It does not promise every expansion or a faction picker. DLC ownership verification is deferred at the user's request for the friends' MVP. This does not establish that base-game ownership alone is sufficient or authorize changing entitlement checks.

## MVP implementation

The browser launcher, Windows/Linux ZIP packager, Steam discovery, separate game copies, sequential binding preparation, and Host/Join/Resume controls are implemented. The initial Cloudflare relay and its Alchemy deployment configuration were replaced by Steam networking and invitations.

Current local validation is listed in README.md. Native Windows and real multi-machine play remain separate validation steps. The sections below describe the broader delivery goals, including work beyond this MVP.

## Existing foundation

- `scripts/private-match.py` already starts hosts and clients, creates six private seat invitations, and resumes checkpoints with fresh credentials.
- The native rules host has completed six-player matches. Native graphical clients have submitted setup moves and an ambush response.
- An authenticated SSH tunnel has carried a native client move locally. Separate-machine Internet play remains untested.
- Current setup and launch scripts are Linux development tools with hard-coded Steam paths. They are not a Windows launcher or a player-facing installer.

## Delivery sequence

### 1. Detect and validate the installed game

- Discover Steam and its library folders on Windows and Linux, including games installed outside the default library. Offer a folder picker when discovery fails.
- Read Root's app manifest (app ID `965580`), check required files, and compare the build against the supported mod version. Initial tested build: `22238765`, Root 2.1.5.
- Distinguish installation detection from Steam ownership and DLC verification. A manifest alone does not establish entitlement. Verify normal Steam integration and expansion requirements without changing entitlement checks.
- Report unsupported builds and missing prerequisites before copying files or launching the game.

Acceptance: fixture tests cover both platforms, multiple libraries, missing files, and unsupported builds. A read-only check locates this machine's installation without starting Steam or Root.

### 2. Prepare and launch one playable client

- Package the launcher and our compiled mod so friends need no Python, SDK, terminal, or manual plugin installation. Check redistribution terms for bundled third-party dependencies; generate game-specific bindings locally.
- Copy game files into launcher-owned storage, with free-space checks, progress, and cancellation. Never package proprietary game files or patch the Steam installation.
- Keep modded saves and settings separate. Use a dedicated Proton prefix on Linux. Verify equivalent save/settings isolation on Windows before declaring support.
- Implement separate platform launch adapters: Windows runs the Windows game; Linux uses the supported Proton runtime. The existing Linux lab runner remains a separate test path.
- Show a visible, interactive game for players. Development runs here remain muted on individually allocated Xvfb displays and isolated prefixes.
- Lock active installations against updates. Prepare updates separately, verify their integrity, and retain the working version if preparation fails. Keep compatible versions available for existing saves.

Acceptance: a fresh installation reaches the private native board on each platform, and ordinary Steam Root still starts with its original saves and settings. Windows runtime validation requires a Windows environment; Linux checks cannot establish it.

### 3. Host, join, and resume without terminal commands

- Host starts the private authority and the host player's client, then displays six seat assignments and private invitations. Account for both game processes in host resource checks.
- Join accepts an invitation, checks protocol/game/mod compatibility, and opens the assigned seat. The launcher handles connection metadata internally.
- Resume lists compatible local saves, restores the host, and issues replacement invitations. Explain that older invitations expire.
- Preserve automatic saves and show actionable startup, connection, and unsupported-action errors. Keep seat credentials and private checkpoint contents out of shared logs.
- Remove the development runner's fixed session timeout from normal player sessions. Closing a host must explain the effect on the match and preserve its latest acknowledged save.

Acceptance: host plus client completes a native move using only launcher controls; restarting the host resumes the same match, rejects old invitations, and allows rejoining with new ones.

### 4. Make connecting friends practical

- Keep the current game protocol bound to loopback. It has no TLS and must not be exposed directly to the Internet.
- Use SteamNetworkingSockets in the game mod. The host computer runs Root's native rules authority. Validate cross-account routing before promising Internet play.
- Players should not edit invitation JSON, type SSH commands, configure router port forwarding, or share account passwords. Protect invitation secrets and verify the remote host.
- Handle disconnects and rejoining with current private state; never automatically replay a move whose acknowledgment was lost without checking whether it was accepted.
- Retain local multi-process tunnel tests for development. Real separate-machine testing is a release requirement for Internet-play claims. Any hosted relay, deployment, or ongoing cost requires a separate decision.

Acceptance: Windows and Linux players join the same host across separate networks, reconnect after a connection loss, and continue without duplicated moves or leaked hands.

### 5. Verify a friends-ready release

- Resolve or explicitly restrict unsupported selection/message paths for the fixed roster. An unexplained waiting client is not release-ready.
- Verify six simultaneous graphical players, full-match completion, final results UI, and recovery. Distribute clients across machines rather than exhausting this desktop's RAM.
- After the initial friends' MVP, verify host and guest DLC requirements and document exactly what each friend needs to own.
- Test interrupted setup, incompatible updates, installation paths with spaces, expired invitations, and uninstalling launcher-owned files while preserving saves unless explicitly removed.
- Run a fresh-user walkthrough on both platforms with no development tools installed.

## Next implementation increment

Have a Windows friend test installation, real Steam invitation delivery, and a two-machine game. Prioritize blockers found during that walkthrough over additional launcher features.

The launcher is now the main player-facing deliverable. It wraps the existing private host and native client; the remaining gameplay and network verification above still determines when it is ready for friends.
