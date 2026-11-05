---
title: "Games list and a page per game"
ticket: 10
status: done
---

# Plan: 10.10 Games list and a page per game

**Intent:** the Game Profiles page rebuilt as Games, to EXPERIENCE.md's Drill-in pages and Games sections and the DESIGN.md addendum's breadcrumb header, game row, pills and inheritance section. Settings keys, messages and Contracts are unchanged. R23 to R26 behave as before. Only App and Platform.Desktop files change.

## Done

- [x] **The drill-in pattern** (`Settings/DrillIn.cs`, `DrillInHeader.xaml`, shell wiring), reusable by any list page through `SettingsPage.DrillIn`:
  - While an item page is open, the shell's page header becomes the breadcrumb: Back (U+E72B, "Back to Games", tooltip "Back (Esc)"), the parent's title as a link, ›, and the item's name. The nav selection stays on the parent.
  - It closes on Back, the link, Esc (on the bubbling KeyDown, after a control's own Esc; the rename box handles its own), Alt+Left, the mouse's Back button, or a click on the parent's nav item. Leaving the page shows its list on return.
  - Opening focuses the item page's first control. Going back focuses the row it came from, with the list's scroll offset restored. The list is hidden, not removed, so it keeps its selection.
  - The parent keeps each item page's view model for the run, keyed by id, so a draft survives going back. An item that leaves the document returns to the list with "{name} is no longer in the list."
- [x] **The Games list** (`GameProfilesPage`, `GameProfilesView`):
  - One keyboard list by name with letter jump (`TextSearch`), Ignored games grouped last under "Ignored games". It opens a page on click or Enter.
  - Each row has the program's icon, read through the shell (`ShellThumbnail.TryGetIcon`, `SIIGBF_ICONONLY`, off the UI thread), else a letter tile. Then the name; "Detected automatically" or "Added by hand" (from `manualGames`); "· {n} settings changed"; the Capturing now pill (the snapshot's game being captured); Not captured; the unsaved dot; and the › chevron. No profile id or path is shown.
  - The empty state, and Add a game…, which is 10.9's picker in its file variant (programs only) and sends `game.add` with "Added. {name} appears here the next time it runs."
- [x] **A game's page** (`GamePage`, `GamePageView`):
  - The header renames inline: F2 focuses it, Enter or leaving commits, Esc cancels, and a blank name shows the old one again. It shows "Detected automatically" or "Added by hand", the program's location and Show in Explorer.
  - Capture is `ignored`, inverted. Recordings is `recordingsEnabled`, with its sentence and the Record key.
  - Clips, Video quality and Audio tracks are `InheritedSetting` sections:
    - The chip reads "Uses the main setting" or "Changed for this game".
    - The main value shows as readable text, with Change for this game and Edit the main setting (a link that opens Clips or Audio Tracks).
    - Changed, the section shows the editor and the applies hint. Reset to the main setting asks "Reset {game}'s {section} to the main setting?" first.
    - The ops are today's override `set` (copying the main value, Track ids kept) and `remove`, through the game-scoped Clips view model and 10.5's Audio Tracks editor.
  - Files is read-only: the game's folder (AD-21) with Open in Explorer, an R26 file-name example from the current name, and its note.
  - For a hand-added game, Stop detecting it by hand removes the program from `manualGames` with one Global update.
- [x] **The Use Global check boxes are gone** from the Clips view and the Audio Tracks view. The inheritance sections drive the same properties (`BufferWindowUsesGlobal`, `VideoUsesGlobal`, `UsesGlobal`).
- [x] **Styles** go in their own `Theme/GamesTheme.xaml`, placed by `ThemeService`: the game row, icon tile, pills, chip, link, chevron and unsaved dot. Every colour is a token. The Gaming dark TextBox template isn't touched (story-2-7's bugfix).
- [x] **Unsaved dots:** a game whose audio tracks hold a draft shows the dot on its row and on the Games nav item.

## Decisions

- **The 10.9 follow-up (per-game folders against the cache folder) needs no engine change here.** 10.10 exposes no per-game folder setting. A game's folder is assigned once by the engine under the Clips folder and shown read-only (AD-21), and `outputFolder` isn't an overridable key (AD-4). A game's folder always sits inside the Clips folder, so the existing rule that the cache folder may not be inside the Clips folder, or contain it, already keeps them apart.
- The game's video and Clip length editors are small views of their own (`GameClipsEditor`, `GameVideoEditor`) bound to a game-scoped `ReplayBufferSettingsPage`, rather than the Clips page's whole view, so its cards don't nest inside the section's card.

## Tests (lean)

- `GameProfilesPageTests`:
  - the list groups Ignored last, shows Added by hand, Capturing now and the changed count, and holds no profile id;
  - opening a game and going back, with the last-opened row recorded;
  - Change for this game copies the main value with the Track ids kept, and Reset asks before removing;
  - a main change shows at once;
  - Capture off sets `ignored`, and Stop detecting removes the program from `manualGames`;
  - a draft survives going back and shows on the row and the nav;
  - a removed game returns its page to the list;
  - Add a game… sends `game.add` and shows a refusal;
  - the list and a game's page render with no binding errors, and Back focuses the row.
- `SettingsLayoutTests`: Games and a game's page load in both themes. The breadcrumb replaces the title, the sections are cards, and every theme key resolves. They're saved as `settings-games-{fluent,gaming-dark}.png` and `settings-game-page-{fluent,gaming-dark}.png` beside the App test binary.
- Existing tests were updated for the renamed page and the removed check boxes. `RecordingsEndToEndTests` switches Recordings on through a game's page.

## HITL (builder)

- Walk your real games, give one game longer Clips and reset it, Ignore one, and add one by hand.

## Log

- 2026-11-05: built and verified; READY FOR QA.
- 2026-11-05: QA passed at e348991 with 3 minor findings, fixed before the merge: the game page shows the program's icon, Enter or Space on the selected Games nav item goes back like a click (`MainViewModel.ReselectNavItem`), and the source caption lost its double spaces.
