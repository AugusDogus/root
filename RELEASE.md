# Root Six Player 0.7.5

- Ship the Linux launcher as an AppImage.
- Verify downloaded updates, replace the AppImage in place, and restart it automatically.
- Preserve the installed AppImage if downloading or staging an update fails.

Linux users upgrading from 0.7.4 or earlier: download the AppImage once. The Linux
ZIP and standalone launcher update path have been removed. Keep the AppImage in
a writable folder. Automatic downloads become available when this repository is
public; until then, download updates from the release page.

Known issue: Shift+Tab may not open the Steam overlay when launched directly.
The mod's invitation controls still work. This release adds overlay status to
**Copy diagnostics**, but does not fix overlay loading.

On Windows, download and open **RootSixPlayer.exe**. On Linux, download
**RootSixPlayer-0.7.5-x86_64.AppImage**, allow it to run as a program in file
properties, and open it. If FUSE is unavailable, run it with
`--appimage-extract-and-run`. Install Root and keep Steam open. Linux also needs Proton
Experimental and Steam Linux Runtime 4 installed through Steam. First-time setup
can take several minutes while bindings are generated locally. The launcher closes
after opening Root. Hosting, saves, and returning to the menu are handled by the mod.

To update, close Root and open the new launcher. It preserves compatible saves
and the normal Steam installation. All players should use the same release.
Open the mod before accepting a Steam invitation. Active timers continue while
the host is closed.

Supported game: Root 2.1.5, Steam build 22238765. Private playtest expansion choices
do not require account unlocks. This does not change purchases or ordinary Steam Root.
This remains a playtest build.
