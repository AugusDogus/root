# Root Six Player

A mod and launcher for private six-player games of [Root](https://store.steampowered.com/app/965580/Root/) with Steam friends, on Windows and Linux.

The launcher finds your Steam installation, prepares a separate modded copy, and opens Root. Host, join, and play through the game's native menus and board. Your regular Steam installation stays unchanged.

## Features

- Six seats with human players, ordinary faction AI, and Clockwork bots.
- Steam invitations and peer-to-peer networking.
- Native faction selection, expansion options, maps, decks, and advanced setup.
- In-game chat, turn timers, autosaves, and match recovery.
- Automatic mod setup, with no Python installation or manual BepInEx configuration.

## Get the launcher

For now, download a test build from [GitHub Actions](https://github.com/AugusDogus/root-six-player/actions/workflows/launcher.yml): open the latest successful run and download its **launcher-…** artifact. Extract that download, then use the file for your system:

| System | What to open |
| --- | --- |
| Windows | `RootSixPlayer.exe` |
| Linux | Extract `root-six-player-…-linux.zip`, then open **Root Six Player** |

Everyone in a match should use the same build. Downloads currently require access to this private repository. Tagged builds will also appear on the [Releases page](https://github.com/AugusDogus/root-six-player/releases).

Each player needs Root installed through Steam and Steam running. The supported game version is **Root 2.1.5, Steam build 22238765**. Linux also needs **Proton Experimental** and **Steam Linux Runtime 4**, installed through Steam.

First launch can take a few minutes while the launcher prepares the mod. It closes automatically once Root opens.

## Play with friends

1. **Host:** open the launcher, choose **Host a game**, configure the seats and game options, and create the room.
2. **Invite:** choose **Invite friends**, select an open human seat, and send a Steam invitation.
3. **Join:** friends open their launcher and choose **Join friends** before accepting the invitation. Each friend chooses a faction in the waiting room and clicks **Join Game**.
4. **Start:** the host starts the match once everyone has joined.

Open the mod before accepting an invitation, or Steam will open ordinary Root. Keep the host's computer on while playing.

Matches save automatically. To continue later, the host chooses **Resume a game** and sends new invitations. Active turn timers continue while the host is closed.

All gameplay expansions included in the installed game are available in this private playtest. Root's faction and setup restrictions still apply. Ordinary Steam Root and purchases are unchanged.

## Updates and troubleshooting

Close Root before opening a newer launcher. Updates preserve compatible saved matches. Everyone should update together.

If something goes wrong, use **Copy diagnostics** in the **Match** menu or on the error screen. Include that text and a short description when [reporting an issue](https://github.com/AugusDogus/root-six-player/issues). Diagnostics exclude saves, chat, account names, and invitation secrets.

## Playtest status

This is an experimental build. Automated checks cover game logic, recovery, selected six-player screens, and launcher builds on Windows and Linux. Steam invitations between different accounts, Internet play, and native Windows gameplay still need real-player testing.

## Development

- [Development setup, architecture, and test coverage](docs/development.md)
- [CI builds and releases](docs/launcher-ci.md)
- [Six-player UI audit](docs/native-ui-audit.md)
- [Release notes](RELEASE.md)

The repository contains the mod and launcher source. Root game files are not distributed. BepInEx and other bundled dependencies include their license notices and corresponding sources where required.
