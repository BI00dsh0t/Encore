---
title: 'Library and Cleanup end to end on the builder''s PC'
type: 'chore'
ticket: '11'
created: '2026-11-04'
status: done
baseline_revision: 'f943c9f'
route: 'hitl'
risk: 'low'
context: ['{project-root}/CLAUDE.md']
---

# 7.11 Library and Cleanup end to end on the builder's PC

The epic's closing check. The unattended part drives the real engine and the App's real Library and settings pages against a temp Library of small MP4s on a throwaway test instance, with no capture (the builder has paused GPU tests). What only a person can check, or what needs a real capture, is the ordered list below.

## What was added

- `tests/ReplayBuffer.App.Tests/LibraryEndToEndTests.cs` (Heavy, Explicit, CPU only). Nothing else ran the Library through the real engine and the App's pages together. The test hosts the whole engine (`EngineHost.Start`) in-process on a throwaway test instance (`ReplayBuffer-Test-library-<guid>`, Replay Buffer off, so nothing is captured). It uses the App's own `EngineConnection`, a `LibraryPage` with the real shell thumbnails (`LibraryThumbnails.Shell`) and a recording `ILibraryShell`, and the real `MainViewModel`'s Library and Cleanup settings page. `ffmpeg.exe` from `native/ffmpeg` makes 2 s and 3 s H.264/AAC MP4s on the CPU (libx264, ultrafast). They're copied into a temp Output Folder under two game folders with back-dated creation times (today, 3, 20 and 40 days ago). The folder also gets a junction to an outside folder holding a 100-day-old "victim" MP4. A clock shifted by 61 s lets Cleanup's 60 s start delay pass at once. It checks, in order:
  - **settings page, folder (R8):** `ChooseOutputFolder` asks first ("Existing files aren't moved."), then the engine lists the five files from the new folder, and not the junction's;
  - **corrupt index (R2):** with `library.json` and `.bak` both unparsable, a restart rebuilds the index from the folder, raises `library-corrupt`, and blocks Cleanup (the Library banner and the settings page's note both show);
  - **length and thumbnails (R3):** every row gets a length from the engine (`0:02`, `0:03`) and a shell thumbnail;
  - **grouping and filters (R4):** grouped by game (Alpha, Beta) and by day (4 days, Today first); filtered by game, Today, Last 7 days, Last 30 days, All, and Favourites only;
  - **Favourite (R6):** two rows starred through the row command; the Favourites filter shows only them;
  - **outside changes (R2):** a starred file renamed in Explorer's way (`File.Move`) keeps its star at the new path; a file deleted outside goes; a file copied in appears;
  - **Open, Show in Explorer, Copy (R5):** each passes the absolute path to the shell (recorded, nothing launched); Copy's data is exactly that path as a file drop;
  - **rename and delete (R6):** an inline rename renames the file in place; Delete asks, No keeps the file, Yes moves it to the real Recycle Bin (taken back out when the test ends);
  - **locked while written (R6):** a growing file under `Library.BeginWrite` (a stand-in for a Recording) shows locked with Copy, Rename and Delete disabled, and the engine refuses a rename with `file-being-written`;
  - **Cleanup and its guards (R7):** a size limit needing one file is turned on through the settings page: the preview prompt says "Cleanup would delete 1 file" and the limit saves, but nothing is deleted while `library-corrupt` is unacknowledged. The banner's Acknowledge unblocks Cleanup, which deletes only the oldest non-Favourite (40 days old, not the 20-day-old Favourite). Lowered to one byte (previewed again), it deletes the last non-Favourite and keeps both Favourites, the file being written and the junction's target, and raises `cleanup-limit-unreachable`;
  - **the Cleanup log:** the engine logs the plan before deleting ("Cleanup plan: 1 files, 57681 bytes: Alpha\a-old.mp4 …"), then each delete and the result;
  - **settings page, size:** "… in 3 files" after Cleanup;
  - no `Detected` or `Capturing` line in the engine log.
- No product code changed.

## Unattended results

Run on 2026-11-04 in this worktree, Release, no GPU work (see Findings for the first two runs).

| What | Tests | Result |
|------|-------|--------|
| Index and reconciliation, corrupt index, length and thumbnails, grouping and filters, Favourite, Open/Explorer/Copy, rename and delete, locked file, Cleanup with its guards and log, settings page | `LibraryEndToEndTests` (Heavy, new, CPU only) | pass, 3 runs in a row (3.4–3.6 s each), after one fix to the test (it read the page before the page had rendered the Favourite) |
| App default suite | `ReplayBuffer.App.Tests` | 122 passed, 17 skipped (explicit), 0 failed |
| The Library over a real pipe (a fake writer's Clip within 2 s, Favourite, rename, preview, restart, failed write, file copied in, corrupt index) | `LibraryServeTests` (Engine) | 8 passed |
| Build | `dotnet build ReplayBuffer.slnx -c Release` | 0 warnings, 0 errors |

Totals: 131 passed, 0 failed.

Not run, with the reason:
- **A Clip saved by a Save Hotkey on a real capture, appearing within 2 s with its thumbnail, length and size** (the ticket's first automated check). Not run (GPU tests paused by the builder). `LibraryServeTests` covers the same path with a fake writer's `BeginWrite`/`EndWrite`, and step 2 below covers it on a real game.
- **A live Recording locked in the Library.** Not run (GPU tests paused by the builder). The test uses a growing file under `BeginWrite` instead, and step 8 below covers a real one.
- `LibraryActionsTests.CopyPutsTheFileOnTheRealClipboard` (Heavy, not GPU): it would overwrite the builder's clipboard. 7.6's QA ran it, and step 5 below pastes into Discord.
- Engine, Core and the other projects beyond `LibraryServeTests`: the diff adds one App.Tests file, with no product code, so no NativeAOT publish either. Their epic-7 parts were QA'd in 7.1–7.10.

## Builder's hand checks for epic 7 (in order)

One sitting with the Dev instance, once GPU tests are back on. Start `ReplayBuffer.Engine serve` (no `--debug-capture`) and the Main App. Each step names the Human Check Queue row it closes; the row has the detail.

**Cleanup deletes permanently.** For steps 1 to 7, point the Output Folder at a scratch folder holding copies of a few real Clips (in game folders, as the engine writes them), not your real Clips. Point it back in step 9.

1. **Settings > Library and Cleanup, the folder (7.9).** Click Browse, choose the scratch folder, and confirm. The Library tab lists only its files, and nothing is moved. Check the page and the prompt by eye. Optionally choose a folder inside OneDrive: cloud-only files aren't listed (7.8's QA note). Say whether that's acceptable.
2. **A new Clip appears (7.1, 7.3).** Play a detected game and press the Save Hotkey. The Clip shows within 2 s as "Saving…" with no thumbnail or length, then with its thumbnail, length (m:ss) and size. Its game column shows the profile's name. Check the thumbnails look right: upright, right colours, not stretched. Restart the engine: the Clip is still listed.
3. **Browse by game, date and Favourite (R4, the ticket's Verify).** Group by Game, then by Day. Filter to one game, Today, Last 7 days and Last 30 days. Star a Clip and tick Favourites only.
4. **Changes outside the app (R2).** In Explorer, rename the starred Clip (its star follows it), delete another, and copy one in (it appears).
5. **Open, Show in Explorer and Copy (7.6).** Double-click a Clip to open it in the Media Player. Use Show in Explorer, then Copy and paste into a Discord message: it attaches and plays.
6. **Rename and delete (7.7).** Rename a Clip inline (focus, selection, Enter, Escape, clicking away). Delete one: No keeps it, Yes moves it to the Recycle Bin, then restore it. Press Save and right-click the "Saving…" row at once: Rename and Delete are greyed out.
7. **Cleanup keeps a Favourite (7.9, the ticket's Verify).** Star the oldest Clip. Set a size limit just under the folder's size, read the prompt's count and size, and confirm. The oldest non-Favourite goes, and the starred one stays. Try Cancel, lowering and raising the limit. Then turn the limit off.
8. **A Recording is locked (R6).** With Recordings on for the game's profile, press the Record Hotkey (Alt+F9). The Recording's row is locked, with Rename and Delete greyed out, until you stop it.
9. **Put the Output Folder back** to your real Clips folder (or Use default) on Settings > Library and Cleanup.

The automated run covers each Done-when line against the real engine and pages, without a capture. The list above covers what it can't: a real Save and Recording, the real folder picker and message boxes, Explorer, the Media Player, Discord, and how the pages look and feel.

## Findings

- None of the epic-7 checks failed.
- **Finding (not fixed: it touches GPU tests that can't run now):** `ClipsEndToEndTests` (4.14) and `OverlayEndToEndTests` (5.14) name their instance `Instance.TestPrefix + Path.GetFileName(Directory.CreateTempSubdirectory(...))`. The random suffix has a dot (`rb-e2e-clips-abcd1234.xyz`), and `Instance.IsTestName` allows only letters, digits and dashes, so `IsTest` is false: the engine treats it as a real instance. Both set `ReplayBuffer = true` anyway, so their captures are intended. But on a non-test instance the engine plays real Feedback Sounds (`ClipsEndToEndTests` injects none) and creates a missing Output Folder. The fix is one line each: name the instance from `Guid.NewGuid().ToString("N")`, as `LibraryEndToEndTests` and `SupervisionTests` do. Rerun both under the Heavy category once GPU tests are back on. `new Instance(...)` could also refuse a test-prefixed name that isn't a test name.
- **Incident: the GPU pause was broken twice, briefly.** This story's first two runs used the same pattern (copied from 5.14). Each started a real Replay Buffer for about 5 s on the foreground window (`msrdc.exe`, the Windows 365 client), with NVENC, in memory only. There was no Save press, so no Clip was written and nothing was saved to disk. The second run happened because a scripted edit failed and the build-and-test command still ran on the old file. Fixed: a Guid-named test instance, `ReplayBuffer = false` set explicitly, an `IsTest` assert, and an assert that the engine log has no `Detected` or `Capturing` line. Logged in the automation state's Log with the skipped GPU runs.
- Observation: the test uses the real Recycle Bin for one temp file and restores it at the end, as `RecycleBinTests` does; none was left behind.

## Log

- 2026-11-04: branch `feature/7.11-library-cleanup-end-to-end` cut from `develop` at `f943c9f` (7.1–7.10 merged). Added `LibraryEndToEndTests`. The first two runs captured briefly (see Findings), which is now fixed. The test passed 3 times in a row with no capture, App.Tests' default suite and `LibraryServeTests` passed, and the build had 0 warnings. Pointed the five epic-7 Human Check Queue rows (7.1, 7.3, 7.6, 7.7, 7.9) at the checklist.
