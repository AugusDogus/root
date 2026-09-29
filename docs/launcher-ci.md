# Launcher builds and releases

`.github/workflows/launcher.yml` uses GitHub-hosted machines. It does not use the
development desktop or a running game. SteamCMD uses a saved login stored in
GitHub Actions secrets to download Root.

Actions use Node.js 24. Standalone C# tests and the binding generator run on
.NET 10 LTS. The mod still targets .NET 6 because the pinned BepInEx loader bundles
that runtime. Updating the game runtime requires separate compatibility testing;
the CI toolchain upgrade does not update the runtime shipped to players.

- Pull requests run Linux and Windows launcher unit tests, release validation,
  packaging-helper tests, and the standalone multiplayer protocol tests.
- Pushes to `main` and manual **Run workflow** runs also compile the mod, build
  both launchers, and upload a `launcher-<commit>` artifact for 14 days.
- Version tags such as `v0.7.0` do the same and automatically publish a release
  after all checks, including Windows installation and update tests, pass. Pushing
  the tag is the release action. It becomes the latest release so the README's
  download link resolves to it. Existing releases are never overwritten.

Changes limited to Markdown files or `docs/` skip push and pull-request runs.
`RELEASE.md` still triggers checks because its version header is validated.
Version tags and manual runs always run, regardless of changed paths.

Tests and packaging run in parallel; publishing waits for both and the Windows
installer checks. New branch runs cancel superseded runs. Tag runs are not cancelled.
CI caches Go modules and compiled packages, plus checksum-verified packaging
downloads. Steam sessions, game files, and generated game bindings are not cached.
Cached downloads are checked against the pinned hashes on every use.

Player assets are `RootSixPlayer-<version>-Setup.exe`,
`RootSixPlayer-<version>-x86_64.AppImage`, and `SHA256SUMS`. The Windows installer
installs per user and creates a Start menu shortcut. Linux users run the AppImage.
Dependencies and their notices are embedded.

## One-time Steam login

Root does not allow anonymous SteamCMD downloads. A Steam Web API key does not
provide download authorization. CI needs a reusable SteamCMD session for an
account that owns Root.

On this Linux development machine, run in your own terminal:

```sh
python3 scripts/setup-steam-ci.py
```

The helper needs Python 3.12 or newer, `bubblewrap`, SteamCMD's 32-bit system
libraries, and an authenticated `gh` CLI. It downloads Valve's SteamCMD bootstrap
into a temporary directory and masks the real home directory while SteamCMD runs.
Do this when the account is not being used to play, since simultaneous Steam
logins can conflict. Your regular Steam files are not read or changed.

Enter your Steam account name, password, and Steam Guard approval directly in
that terminal. The helper verifies login reuse in a second clean directory, then
uploads two repository secrets using stdin:

- `STEAM_USERNAME`: the account login name.
- `STEAM_CONFIG_VDF`: the base64-encoded saved session, not the password or an API key.

The saved session is a credential. Neither it nor SteamCMD authentication output
is printed into CI logs, cached, committed, or included in player packages.
Temporary session files are deleted after setup and each download. No password
or Steam Guard shared secret is stored in GitHub.

Steam can expire or revoke the session. If CI requests reauthentication, rerun
the same setup command, then rerun the failed workflow. This does not guarantee
indefinite unattended downloads. The saved-session flow has been verified on a GitHub-hosted runner.

## Building from Steam

CI downloads the Windows game through SteamCMD and checks that its manifest is
exactly the supported build. A newer Steam build fails with an update-review
message, rather than silently producing a mod for an unsupported version.

The workflow downloads checksum-pinned BepInEx and Unity libraries, then calls
BepInEx's binding generator directly on the downloaded game files. No Root
process, Steam desktop client, Wine, or display is needed. The generator uses two
internal methods from the pinned loader; loader updates must revalidate them.
It requires fresh output and verifies BepInEx's completion marker and game DLLs.
The mod is then compiled against these generated references.

Game files and generated bindings stay on the temporary runner. Only the mod
and normal launcher dependencies enter the player packages. There is no manual
build-reference archive upload.

The [first successful hosted build](https://github.com/AugusDogus/root/actions/runs/36350950836)
verified authenticated downloading, offline binding generation, mod compilation,
both launcher packages, and the native Windows executable smoke test.

## Cutting a player release

Update version declarations in the launcher, packager, mod, and `RELEASE.md`
together. CI rejects a tag that disagrees with these versions. After the reviewed
commit is on `main`, push its matching version tag. Downloadable artifacts are
available from the workflow run; the published release holds the same tested files.
Use a new version if a release already exists.

Local builds still use `scripts/safe-test.py`. The packaging and CI preparation commands have a
`--github-hosted` mode, which checks the GitHub-hosted runner environment. Game
tests retain the desktop resource guards.

## Startup window

Windows and Linux use the same Go-rendered startup window. A short-lived child
of the launcher owns the native event loop and receives status through stdin.
It exits when preparation finishes. Linux uses X11, including XWayland on
Wayland desktops. No Root process is needed to show the window.

The window reads Root's logo and forest illustration from Steam's artwork
cache. The original Baskerville font is read from the supported game's
`resources.assets`, using a pinned byte range and SHA-256 check. Missing artwork
or a changed font uses a plain background and bundled Go font. Updates to the
supported game build should recheck that font range. Extracted game assets are
never committed or bundled.

To test rendering and shutdown without opening Root:

```sh
python3 scripts/safe-test.py python3 scripts/test-launcher-ui.py \
  --game '/path/to/steamapps/common/Root' --steam '/path/to/Steam'
```

The test creates a dedicated Xvfb display and captures normal, scaled, and
missing-artwork states under its test-run directory. Windows CI exercises the
native window lifecycle separately.

## Launcher updates

Normal launches check this repository's latest public GitHub release. Only a
newer three-part stable version is accepted. The updater downloads the platform
asset and its `SHA256SUMS` and verifies the download before installation.

On Windows, the launcher stages the NSIS installer under `launcher-updates` in
its data folder. It closes its window, releases the setup lock, starts Setup,
and exits. Setup waits up to 30 seconds for the old process to exit, replaces
the installed launcher, and restarts it. It never terminates a running process.

On Linux, the launcher stages the new AppImage beside the current one, atomically
replaces it, and restarts with the original arguments. The AppImage must be in a
writable folder. Failed downloads or staging preserve the existing image.

Private-repository responses, rate limits, and network failures keep the
installed version usable. Headless tests, preparation-only runs, and `--no-update`
skip automatic updating. Update downloads use unauthenticated GitHub requests
and require public releases.

CI verifies packaging, native launcher tests on both operating systems, protocol
logic, and notice extraction. Windows checks exercise installation, shortcuts,
registration, updates while the old launcher is running, restart, and uninstall.
These automated checks do not exercise native Windows gameplay, Steam invitations
between accounts, or the complete game UI; those require separate playtests.
