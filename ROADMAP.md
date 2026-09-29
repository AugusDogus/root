# Beyond the friends' MVP

Implementation and validation checklist for the next release:

- [x] Bounded per-seat history, paginated updates, and snapshot resynchronization.
- [x] Reconnect to Steam without replaying uncertain moves.
- [x] Grouped selections and actionable in-game errors for unsupported actions.
- [x] Seat presence, readiness, and explicit reassignment of disconnected seats.
- [x] Resignation, match results, and returning to the menu.
- [x] Versioned packages, compatibility checks, safe updates, and release tooling.
- [x] Clockwork traits and native Vagabot characters.
- [x] Focused automated checks and constrained native regression tests.

Friends have tested multiplayer between separate Steam accounts on Windows and
Linux. Further playtests are needed for faction interactions, network conditions,
and display configurations beyond those sessions. Local simulations cannot
replace these checks.
Native development jobs use `scripts/safe-test.py`, one at a time, with the existing
8 GiB memory cap and isolated muted displays.
