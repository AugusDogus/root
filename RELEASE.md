# Root Six Player 0.7.4

- Use Root's original forest artwork, logo, and menu font in the launcher startup window on Windows and Linux.
- Check for launcher updates when opened, verify the download, and start the newer version automatically. Update downloads become available when this repository is public.
- Keep the installed version usable when update checks or downloads fail.
- Retry interrupted build dependency downloads.

Known issue: Shift+Tab may not open the Steam overlay when launched directly.
The mod's invitation controls still work. This release adds overlay status to
**Copy diagnostics**, but does not fix overlay loading.

On Windows, download and open **RootSixPlayer.exe**. On Linux, extract the ZIP and open
**Root Six Player**. Install Root and keep Steam open. Linux also needs Proton
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
