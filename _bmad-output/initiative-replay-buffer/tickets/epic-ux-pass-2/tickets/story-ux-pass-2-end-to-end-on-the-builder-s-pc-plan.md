---
title: "UX pass 2 end to end on the builder's PC"
ticket: 8
status: done
---

# Plan: 10.8 UX pass 2 end to end on the builder's PC

**Intent:** the epic's closing check of its Done when, integrated, in two parts: an unattended suite against the real engine, and the builder's session on the installed App.

**Against what.** The ticket says "against the installed build". As 9.9 did, and under the coordinator's limit (never use, install over, launch or kill the installed App in `%LOCALAPPDATA%\Programs\ReplayBuffer`, and never touch the real settings or Library), the suite runs against this worktree's built binaries. It hosts the engine in-process on a Guid-named test instance whose folders are all under temp. Installing the new version is step 1 of the builder's session.

## What was added

- **`tests/ReplayBuffer.App.Tests/UxPass2EndToEndTests.cs`** (default suite, about 2.5 s, no capture and no GPU). Following 9.9's `ModernUiEndToEndTests`, it hosts the whole engine (`EngineHost.Start`) on `ReplayBuffer-Test-ux2-<guid>`, with the Replay Buffer off. Endpoints are fake: default speakers, a second output, and a default BEACN-named mic. Its Recycle Bin deletes the temp file. It uses the App's own `EngineConnection`, the real `MainViewModel`, and a `LibraryPage` whose shell records Copy. In order, it checks:
  - **seed (R6):** the empty settings folder is seeded with the default playback and the default microphone, the mic marked as one, on the one Everything Track. Audio Tracks shows it.
  - **no Recordings page (R10):** no nav item names Recordings.
  - **a preset applied and saved (R5):** For editing, confirmed and saved. The engine's layout has Everything, Game and chat, and Microphone, and the page is no longer dirty.
  - **a fabricated Library (R1, R2, R4):**
    - three complete MP4s in a Clips folder chosen through the folder picker;
    - Newest first and Name sorts;
    - a search that narrows the list to one row, then is cleared;
    - a click and a Ctrl+click, then a bulk Favourite, committed by the engine;
    - Select all, then Copy, which carries all three paths;
    - Delete: the engine removes only the one that isn't a Favourite, and both Favourites stay on disk.
  - **a game's page (R9):**
    - a game profile is created through `settings.update`, the game shows in Games, and its page opens;
    - Clip length: Change for this game, then 45 s, gives an override of 45 and the chip "Changed for this game";
    - Reset (confirmed) removes the override, and the chip returns to "Uses the main setting".
- **`EngineHostOptions.Recycle`**: a test seam, `null` for the Windows Recycle Bin, so no test file lands in the builder's Recycle Bin. No behaviour change.
- **Every App test, the glossary included (R11, R7, R8):** the App default suite, which `GlossaryTests` is part of. It covers the XAML and, since 10.7, the strings set in C#.

Nothing here needs capture or the GPU, so **no Heavy command is pending**.

## Builder's session (the Human Check Queue rows)

One sitting on the **installed** App. Do rows 2 to 9 once in **Fluent** and once in **Gaming dark** (Settings > General > Theme), then row 10. These rows are added to the Human Check Queue at the merge.

| # | Done-when line | How | Automated proxy |
|---|---|---|---|
| 1 | Install | Quit ReplayBuffer from the tray, then `pwsh tools/publish.ps1` and `pwsh tools/install.ps1`. The App starts from `%LOCALAPPDATA%\Programs\ReplayBuffer`. | none |
| 2 | Library selection, command bar and context menu by mouse and every key; the selection survives filtering, search and refresh (R1, R3) | Library: Ctrl and Shift clicks, Ctrl+A, arrows with Shift, Space, Esc, Enter, F2, Delete, Ctrl+C and the right-click menu. Select a few, then filter by game, search, and save a Clip so the list refreshes. The selection and its "hidden by filters" count hold. | `LibrarySelectionTests`, `LibrarySortSearchTests`, and the e2e selection |
| 3 | Bulk actions on the shown selection; Favourites and files being written are never deleted (R2) | Clear out and share a batch of real Clips: star a few, Copy and paste them into Discord (several attachments), Copy to folder… onto another drive, Show in Explorer across two game folders, and delete five including a Favourite (it's kept). Delete while a Recording is being written: it's kept. | e2e bulk Favourite, Copy and a delete that keeps the Favourites; the real shell calls (Discord paste, shell copy, Explorer's multi-select) never ran |
| 4 | Sort, Group by None and search, with the sort kept across restarts (R4) | Search and sort the Library, pick Group by None and a sort, Quit from the tray and start again: both are kept. | e2e sort and search; `LibrarySortSearchTests.SortAndGroupByAreRememberedAndAnOlderFileLoadsWithTheDefaults` |
| 5 | Audio Tracks: cards, picker with live levels, presets and the save bar; a fresh instance records the defaults with no setup, and an existing layout is untouched (R5, R6) | Set up Audio Tracks from a preset on your BEACN devices with the meters moving, adjust in the picker, Save changes; save a Clip in a game and check its tracks in DaVinci Resolve. Your existing layout stays as it was after the install. | e2e seed and a preset saved; `TrackLayoutSeedServeTests`; Heavy `MeterProbeCostTests` (passed in 10.4) |
| 6 | Games lists every game and a page per game shows, changes and resets each setting (R9) | Walk Games and each game's page (click or Enter; Back, Esc or Alt+Left). On one game: Clips → Change for this game, a longer length, save a Clip in it, then Reset to the main setting. | e2e Clip length changed and reset; `GameProfilesPageTests` |
| 7 | Settings pages named for what they control, no Recordings page, every folder chosen with the folder picker (R10) | Walk the nav: there's no Recordings page. Move the cache folder with the folder picker on Clips (Disk mode), and the Clips folder on Storage and Cleanup. Find the screen picker on Hotkeys. | e2e nav check; `ReplayBufferSettingsPageTests.TheCacheFolderIsEnabledOnlyUnderDiskAndSendsRingPathAndTimeBetweenSaves`, `FolderPickerTests` |
| 8 | No user-visible text uses a glossary term (R11) | Read every page, the status header, the tray menu, the Overlay's messages and the warnings as you go. Note any word from EXPERIENCE.md's glossary left column. | `GlossaryTests` over the XAML and the strings set in C# |
| 9 | Welcome card, kind badge, tray items and status hint (R7, R8) | On a fresh test instance (the Release build's `src/ReplayBuffer.App/bin/Release/net10.0-windows10.0.26100.0/ReplayBuffer.App.exe --instance ReplayBuffer-Test-welcome`; `--instance` takes test names only), read the welcome card and follow its links, then Quit it from its tray. On the installed App, save a Clip and a Recording in a game: the Recording shows its badge and the Clip none. Use the tray's status item, Open Library and Open the Clips folder. While a game is captured, read the status hint. | `LibraryPageTests` welcome and badge, `TrayTests`, `StatusLineTests`; renders in `ux-pass-2/renders/` |
| 10 | The Library's selected row under Fluent in Windows light mode (10.2's deferred check) | With Theme on Fluent, switch Windows to light mode (Settings > Personalization > Colors), select a few Library rows: the selected fill is clearly visible against the others. Switch back. | none: the fill (AccentColorLight2 at 25 %) was rendered in dark mode only, since tests mustn't change Windows settings |

## Verification

Run on 2026-11-05 in this worktree, Release, with no Heavy, GPU or bench work:

| What | Result |
|---|---|
| Build | 0 warnings, 0 errors |
| Contracts, Core, Media, Platform.Audio, Platform.Capture, Platform.Desktop | 135, 169, 102 (4 skipped), 26 (3 skipped), 8 (1 skipped) and 13 passed, 0 failed |
| Platform.FFmpeg, Platform.Windows, Engine | 40 (2 skipped), 32 and 196 (47 skipped) passed, 0 failed |
| App, the new `UxPass2EndToEndTests` and `GlossaryTests` included | 195 passed (20 skipped), 0 failed |
| NativeAOT publish (the engine gained the `Recycle` seam) | clean, 12.1 MB |

Bench wasn't run: 8.8's soak holds the lock, and its engine holds the Hotkey a Bench test registers. 10.8 changes no Bench code.

## Log

- 2026-11-05: built and verified; READY FOR QA.
- 2026-11-05: QA round 1, one finding fixed: run alone, the test could see the wrong Newest first order. A scanned item is dated by its creation time (`LibraryActor`), and the test set only the write time, so three files made within milliseconds could share a date. It now sets both an hour apart, checks the engine's dates are in that order, and waits for the shown order. The class passed alone 5 times in a row, and the full App suite passed (195 passed, 20 skipped, 0 failed).
- 2026-11-05: QA passed at `73d7c5c`. Synced with develop (11.5): 0-warning build; Contracts 135, Core 169, Media 106, Platform.Audio 29, Platform.Capture 9, Platform.Desktop 13, Platform.FFmpeg 42, Platform.Windows 32, Engine 243 and App 215 total, 0 failed; publish clean (12.1 MB). Bench not rerun: the soak holds the lock; 10.8 changes no Bench code. The builder's 10 rows went into the Human Check Queue at the merge.
