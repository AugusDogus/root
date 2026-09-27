# Six-player native UI audit

Scope: Root 2.1.5, Steam build 22238765. This is a targeted capacity audit, not a claim that every faction interaction has been played through.

## Evidence and method

The static scan examined 165 addressable bundles plus `resources.assets` and `globalgamemanagers.assets`. It read 125,843 MonoBehaviour components and recorded 4,407 instances of 177 UI-related component types. All addressable bundles were readable. Twenty-five components in the two standalone asset files lack a usable full type tree; the report lists these explicitly.

`NativeApi --addresses` resolves 110,704 entries from the installed Il2CppInterop method map without executing the game. The disassembler uses those RVAs to inspect actual native instructions, rather than mistaking generated C# binding wrappers for game implementations. Shared native addresses can have multiple method names, so call annotations are evidence of candidates, not unique call targets.

Local evidence lives in `results/native-ui-assets.json`, `.lab/native-ui-methods.tsv`, and `.lab/native-ui-*.asm`. No game binaries, prefab exports, private match state, or disassembly are included in this repository.

## Findings

| Area | What the shipped game contains | Implication |
| --- | --- | --- |
| Setup and waiting room | Four `playerSlots` in online/offline prompts and setup scene | Existing `NativeSixSeatLayout` and `NativeSetupScene` adaptations are required. |
| Player-target dialogs | All three prefabs have 15 faction entries, including the second Vagabond and Clockwork | These are faction choices, not four seat positions. `SelectAPlayerPromptBehaviour.initialize` iterates the configured array and offered choices. |
| Player information | A dynamically built list; `Event_Increment` wraps at its current count | No four-page bound in the inspected navigation code. Check six-page navigation and button reachability at runtime. |
| Player panels | `PlayerUILoader.InitFromContext` uses collection counts and prefab loading | No four-slot prefab array. Remaining concern is crowding and clipping. |
| Combat | Four `leftPlayers` and four `rightPlayers` views | `InitializeCasualties` binds every left view to `GetAttacker()` and every right view to `GetDefender()`. These arrays do not represent four participants. |
| Board unit placement | Seven unit items and seven positions in clearing pools across all four maps | `PlayerCountPositionPoolItem.UsePool` compares players plus hirelings against a minimum. It is not an exact-four check. Six players plus several piece-bearing hirelings remains a separate crowding case. |
| Board token placement | Six candidate token items and five positions per pool | Native `PositionPool.Event_Layout` logs and truncates when eligible items exceed available positions. The candidates are wood, sympathy, trade posts, plots, mobs, and Sunward Expedition footholds. Determine whether all six are eligible together under native hireling rules before treating this as a reachable bug. |
| Riverfolk prices | Four entries for each service's price toggles | These represent service-price choices, not player seats. |
| Vagabond relationship markers | Fifteen faction-specific provider instances | No serialized four-opponent list found in these providers. Paired-Vagabond gameplay still needs focused visual coverage. |
| Results | Three winner slots and three slots in each loser layout | The stock scene is capacity-limited. The mod uses its six-row results screen. |
| Challenge selection | Four faction icons | This is the stock challenge menu, outside the private multiplayer path. |

## Runtime observations and follow-ups

- Verified at 1280×800 and 1280×720: six player panels are active, mouse navigation reaches all six player-information indices including wraparound, and all six synthetic target choices are inside the window and reachable. Clicking a target resolves the native prompt without submitting a game move. Both relocated toolbar buttons open their menus and return to the board. Evidence: run `20260927T183122Z-60a3e63d`.
- The rendering follow-up (`20260927T184259Z-dc7b6882`) shows all six faction buttons after the opening animation at both resolutions, and the last button is clickable. Captures park the pointer over the background to clear native card tooltips. Earlier captures taken during animations or covered by hover cards are not used as proof of dialog appearance.
- **Resolution test fixed:** native `TuberWatchForScreenSettingsChanged.Update` detects the probe's resize and starts `confirmOrRevert`. Its confirmation timer restores the last accepted size when the test does not respond. This coincided with opening dialogs, but was not caused by them. The development probe now calls Root's `AutoConfirmNextScreenChange` before resizing. The driver asserts the actual Unity resolution at every capture, and all six information pages and the target dialog pass at 720p. Normal player resolution confirmation is unchanged.
- The Vagabond information page visibly renders five relationship markers in the six-faction fixture.
- Native information-arrow hitboxes extend roughly three pixels beyond the screen edge at rest. Their visible area and central hit targets remain usable. A requirement that the entire hitbox be onscreen would incorrectly fail this layout.
- **Toolbar fixed:** Invite friends / Match now stack beneath Root's top-right controls, anchored to the right edge instead of covering the instruction banner. The toolbar hides while native selection, combat, information, settings, or higher prompt scopes are active, then returns when they close. The regression verifies hidden controls during information and target dialogs, restored click targets afterward, and usable placement at both tested resolutions.
- The first synthetic player-selection attempt passed null prefab tags and failed inside Root's prefab loader. This was a diagnostic fixture error, not evidence of a six-player defect. The fixture now supplies an empty native collection.
- **Private setup cancellation fixed:** native `ConfigureOnlineGamePromptBehaviour.Event_Back` starts `RunOnlineMatchesFlow` before dismissing setup. Entering private setup, going Back, and resuming a save therefore produced an official-login error over the board. The private adapter now dismisses only its own prompt; its existing completion handler returns to the private menu. The regression blocks and records any attempted official online flow. Run `20260927T193617Z-c2fb3b4d` passed this navigation and all six information pages and target choices at 1920×1080 Small, then failed on the new trading fixture described below.
- The synthetic Riverfolk pricing prompt requires the `RiverfolkSetPrices` lookup flavor. An empty tag list produces a prefab-lookup failure. The tag was recovered from `SetRiverfolkPricesCommand.idle` and its IL2CPP string literal; this failure did not establish a defect in normal trading.
- Run `20260927T200432Z-c1be6cb0` completed the 1920×1080 desktop checks: six active panels, six information pages including separate Harrier and Ronin pages, six reachable targets, three Riverfolk service-price rows, prompt resolution, both toolbar return paths, and verified clipboard diagnostics. Screenshots show distinct inventories and four relationship markers for each Vagabond, corresponding to the four other factions. The source checkpoint passed 120 decisions and save/reload in `20260927T200207Z-27a0bb7f`.
- That run then failed an invalid attempt to select mobile Large UI scale. Native `ZenPlatformUtil.CurrentPlatform` always returns Desktop (3), or Steam Deck (5), in this Windows build. `PlatformToUIScale` maps those to Small, regardless of the setter. The driver no longer tries to force mobile layouts or labels them as tested. Earlier screenshot filenames ending in `Small` show the desktop layout. OS-level DPI scaling on native Windows remains unverified.
- The pricing fixture now constructs its three prices through a native dictionary. The earlier list of interop-created key/value structs failed the native initialization predicate and left an incomplete dialog. The driver detects both that initialization exception and prefab-lookup failures immediately. Only the successfully initialized and resolved fixture is used as pricing UI evidence; it submits no game move or purchase.
- The nine installed gameplay products are available through the normal private setup path without the old ownership fixture. Winter Map is a free catalog entry, so it does not require a store ownership result. The diagnostics report is an allowlist of public build, display, and session fields; no logs, account names, invitations, chat, paths, or saves enter it.
- The corrected focused 1080p run passed end to end: `20260927T201828Z-0f2c6f88`. It reports Desktop, six active paired-Vagabond panels, six reachable targets, all three price rows changed and resolved, verified clipboard contents, toolbar restoration, and all nine gameplay products. It intentionally does not repeat the information-page navigation already captured above.
- A second 120-decision paired-Vagabond run passed recovery and explicit character/controller preservation checks (`20260927T202459Z-684cc20d`). The assertion permits only the native first/second Vagabond ID swap, while preserving the six-faction roster and each seat's selected character and controller.
- The real ambush regression passed with the current plugin (`20260927T202700Z-df682c8e`). It submitted and confirmed an ambush through native card-dialog handlers, verified that the card left the defender's hand, and checked all private snapshots. Its 1280×800 captures show the Lizard Cult versus Alliance battle in a six-player game. This verifies native handlers and battle rendering, not mouse reachability of every combat dialog.

The investigation does not establish exhaustive UI parity. Full battle animations, every faction's special dialogs, complete Riverfolk purchases and paired-Vagabond interactions, native Windows DPI scaling, gamepad focus, translations, ultrawide windows, and reachable token-pool saturation remain separate checks. The existing game-engine regressions do not substitute for those visual checks.

## Reproducing the investigation

Keep every build, scanner, and game test inside `scripts/safe-test.py`. The current shared budget is 8 GiB maximum, a 6 GiB throttle, no swap, and an 8 GiB desktop reserve.

```sh
python3 scripts/safe-test.py python3 -m venv .lab/ui-audit-venv
python3 scripts/safe-test.py .lab/ui-audit-venv/bin/pip install -r scripts/ui-audit-requirements.txt
python3 scripts/safe-test.py .lab/ui-audit-venv/bin/python scripts/audit-native-ui.py
python3 scripts/safe-test.py env DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 .lab/sdk/dotnet build tools/native-api -p:UseSharedCompilation=false
python3 scripts/safe-test.py bash -c 'DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 .lab/sdk/dotnet tools/native-api/bin/Debug/net6.0/NativeApi.dll .lab/game/BepInEx/interop --addresses > .lab/native-ui-methods.tsv'
python3 scripts/safe-test.py .lab/ui-audit-venv/bin/python scripts/disassemble-native.py PlayerCountPositionPoolItem::UsePool SelectAPlayerPromptBehaviour::initialize
```

The graphical check is `python3 scripts/safe-test.py python3 scripts/test-native-ui.py`. It requires a prepared `.lab/native-session-probe` fixture with a saved six-player game, the current mod build, and the current packaged launcher. It opens native player information and a synthetic six-choice player-selection prompt. Use `--targets-only` to repeat the target-dialog and toolbar checks, `--full-hd` for 1920×1080, and `--trading` for the synthetic pricing dialog. `--fixture PATH` resumes a temporary copy of a private checkpoint and removes only the copy afterward. `test-native-configuration.py --export-checkpoint PATH` can retain a tested checkpoint under `.lab` for this purpose. Captures move the pointer away from native hover cards, wait for three additional rendered frames, and target the game window, avoiding stale pixels outside a resized window. Synthetic prompts test presentation and resolution without sending gameplay moves. Screenshots and structured observations are written to the individual test run directory.
