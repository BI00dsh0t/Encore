---
title: 'Cleanup'
type: 'feature'
ticket: '8'
created: '2026-11-04'
status: 'built'
baseline_revision: 'f45ff2fa7e308cc9b0cf0beaebf3e1a06918d486'
route: 'full'
route_source: 'auto'
risk: 'high'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
followup_review_recommended: false
context: []
warnings: ['oversized']
deferred: []
---

<intent-contract>

## Intent

**Problem:** The Output Folder grows without bound (R7, FR-32). Cleanup must permanently delete the oldest non-Favourite `*.mp4` items once `cleanup.maxBytes` and/or `maxAgeSeconds` is passed, and it deletes the builder's files on its own, so every guard must hold.

**Approach:** Cleanup lives in the LibraryActor (AD-17). A pure `CleanupPlanner.Plan` picks what to delete from the index; a pass logs that plan, then deletes one item per actor message, re-planning and re-checking each item immediately before its delete, so commands queued meanwhile (a Favourite) are honoured. An additive `cleanup.preview` command returns the same plan without deleting.

## Boundaries & Constraints

**Always:**
- Plan (pure, `now`, limits, items, skip set): both limits null → nothing. Total = sum of every indexed item's `Size` (Favourites and being-written included). Candidates = items not `BeingWritten`, not `Favourite`, not in skip, whose key is under the Output Folder (`TryKey` on `GetFullPath(Combine(folder, key))`) and ends `.mp4`; ordered by `Date` then `Path` (ordinal ignore-case). Walk them oldest first: delete while `total > maxBytes` or `now - Date > maxAgeSeconds`; stop at the first that is neither. `Unreachable` = `maxBytes` set, `total` still `> maxBytes` after all deletions, and no skipped candidate remains.
- Gate (a pass starts and each step continues only when): limits not both null, the start `Rescan` has run, no rescan is pending (`_rescanPending`), `_cleanupBlocked` is false, and `now - start >= 60 s` (`start` = `_time.GetUtcNow()` in `OnStart`).
- Triggers: a successful `EndWrite`; `CleanupChanged()` (a settings change whose resolved `cleanup` differs from the last seen); the end of a rescan (`OnSettle`'s and `OnWatchError`'s); `AcknowledgeCorrupt`; a one-shot timer at start + 60 s; a 10 min periodic timer. All timers from the injected `TimeProvider`.
- Pass: on start, clear the skip set and log the plan (count, bytes, each path and size) through the info log port. Each step re-plans; when the plan is empty the pass ends; else the first item is re-checked and deleted, then the next step is posted to the channel.
- Re-check right before `File.Delete` (any failure → add to skip, log, next step): still indexed, not `BeingWritten`, not `Favourite`, full path not in `_beingWritten`, full path under the Output Folder and `.mp4`, the file exists with the indexed `Size` and `Modified` (a missing or changed file is `Pend`ed for reconcile). Then `Register(full)`, `File.Delete`, `Remove(key)` publishing `library.itemRemoved { path, reason: "cleanup" }`, and log path and size.
- `cleanup-limit-unreachable`: when a pass ends with `Unreachable`, raise it once (dismissable warning, source `library`) via a port; re-armed when a pass ends reachable or with both limits null.
- `cleanup.preview { maxBytes, maxAgeSeconds }` → `{ count, bytes, paths }` from `Plan` at `now` with an empty skip set, ignoring the gate; deletes nothing.
- Tests only create and delete files in their own temp folder.

**Never:**
- Never delete a Favourite, a file being written, a path outside the Output Folder, or anything while blocked, reconciling or in the first 60 s; never a non-`.mp4`; no Recycle Bin (Cleanup is permanent per the ticket; 7.7's delete is unchanged); no retry of a failed delete within a pass; no App or settings-page UI (7.9); no breaking Contracts change.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Over size | 4 equal items, oldest a Favourite, maxBytes = 2 items | 2nd and 3rd oldest deleted, `reason: cleanup`; preview before named exactly those | — |
| Over age | items 2 h, 2 h, 10 min old, maxAge 1 h | only the 2 h items deleted | — |
| Favourites only | all Favourites, over maxBytes | nothing deleted; one `cleanup-limit-unreachable` over two passes | — |
| Being written | old items + one between BeginWrite/EndWrite | the written file stays | — |
| Favourited after plan | plan names A, B; B made Favourite before its step | B stays | — |
| Gates | < 60 s, `cleanupBlocked`, rescan pending | nothing deleted until the gate opens, then it runs | — |
| Outside | an indexed `..\outside.mp4` | never deleted | — |
| Locked | oldest held open without delete sharing, age limit | skipped; deleted on the 10 min pass after release | logged |
| Off | both limits null | nothing ever deleted | — |

</intent-contract>

## Code Map

- `src/ReplayBuffer.Contracts/Messages/Library.cs` -- `LibraryItemRemoved(string Path, string? Reason = null)` (additive) + `public static class LibraryRemovalReasons { Cleanup = "cleanup" }`; `LibraryCleanupPreview(long? MaxBytes, long? MaxAgeSeconds)`, `LibraryCleanupPlan(int Count, long Bytes, IReadOnlyList<string> Paths)`, `[JsonSerializable]` both; `CommandType<LibraryCleanupPreview, LibraryCleanupPlan> CleanupPreview` "cleanup.preview" appended to `Commands`. Pattern: `Delete`.
- `src/ReplayBuffer.Contracts/ErrorCodes.cs` -- `CleanupLimitUnreachable = "cleanup-limit-unreachable"` with doc, in `All`.
- `src/ReplayBuffer.Core/Library/CleanupPlanner.cs` (new) -- `internal static` pure `Plan(...)` returning a record (items to delete, bytes, unreachable).
- `src/ReplayBuffer.Core/Library/LibraryActor.cs` -- ctor params appended: `Func<CleanupSettings>? cleanup = null` (null → off), `Action<string>? limitUnreachable = null`, `Action<string>? log = null`. Public `CleanupChanged()`, `Task<LibraryCleanupPlan> PreviewCleanup(LibraryCleanupPreview)` (a `TaskCompletionSource<T>` request like `Post`). New messages `CleanupTick`, `CleanupStep`, `PreviewRequest`; `Remove(key, reason)`; hook triggers into `OnEnd`, `OnSettle`/`OnWatchError` (after `Rescan`), `OnAcknowledge`, `OnStart` (timers, start time). Dispose the cleanup timers in `Run`'s exit. Reuse `TryKey`, `IsMp4`, `Register`, `Pend`, `Utc`. Update the class doc.
- `src/ReplayBuffer.Engine/EngineHost.cs` (~276, ~295, ~489) -- pass `cleanup: () => SettingsResolver.Resolve(load.Store.Current, null).Cleanup`, `limitUnreachable` → `warnings.Raise(ErrorCodes.CleanupLimitUnreachable, WarningSeverity.Warning, "library", message, overlay: false)` (warnings is created after the actor: use a captured nullable like `publishTo`), `log` → `libraryLog.LogInformation`; `load.Store.Changed += _ => libraryActor.CleanupChanged()`; register `LibraryMessages.CleanupPreview`.
- Tests: `tests/ReplayBuffer.Core.Tests/LibraryCleanupTests.cs` (new; own temp folder, `LibraryActorTests.Mp4`), `tests/ReplayBuffer.Core.Tests/ManualTime.cs` (new `TimeProvider` with `Advance` firing due timers, thread-safe, and a way to wait until a timer is armed), `tests/ReplayBuffer.Engine.Tests/LibraryServeTests.cs` (+1 preview test), `tests/ReplayBuffer.Contracts.Tests/MessagesTests.cs` only if a list test needs the new command.

## Tasks & Acceptance

**Execution:**
- [x] `src/ReplayBuffer.Contracts/ErrorCodes.cs`, `Messages/Library.cs` -- code, reason, preview command -- AD-22 additive
- [x] `src/ReplayBuffer.Core/Library/CleanupPlanner.cs` -- pure plan -- R7
- [x] `src/ReplayBuffer.Core/Library/LibraryActor.cs` -- gate, triggers, stepwise pass, re-check, preview, unreachable latch -- R7, AD-17
- [x] `src/ReplayBuffer.Engine/EngineHost.cs` -- wiring, warning, log, router -- R7
- [x] Tests -- Core (manual clock, fabricated items with `Date`/`Size`/`Modified` passed to the ctor): one per matrix row; the "favourited after plan" test calls `SetFavourite` from inside the log port when the plan line is logged (it lands before the first step). The rescan-pending test creates a subfolder (watcher → `PendRescan`), waits until the settle timer is armed on the manual clock, triggers, sees nothing deleted, then advances 250 ms and sees the delete. Engine: `cleanup.preview` over IPC returns the saved Clip's path and size.

**Acceptance Criteria:**
- Given `cleanup` limits are set and the gate is open, when a pass runs, then exactly the oldest non-Favourite, not-being-written items needed to get within both limits are deleted from disk, each published as `library.itemRemoved` with `reason: "cleanup"` and logged with its path and size.
- Given a Favourite, a file being written, or a path outside the Output Folder, when any pass runs, then that file is still on disk.
- Given `cleanup.preview` is sent with limits, when the next pass runs with those limits, then it deletes exactly the paths the preview returned.
- Given only Favourites keep the folder over `maxBytes`, when passes run, then one `cleanup-limit-unreachable` warning is raised.

## Implementation Notes

- A pass whose first plan is empty logs one "Cleanup finished: ..." line and ends; a pass with work logs "Cleanup plan: ..." (count, bytes, each path and size), each delete or skip, then "Cleanup finished: ...".
- A trigger while a pass runs is ignored (the pass re-plans each step, and its skip set survives); an unexpected I/O or argument failure in a step ends the pass. An Output Folder change clears the skip set.
- The re-check refreshes an item whose file was touched at the same size (reconcile compares sizes only, so it would otherwise be skipped forever), like `TryMatch`; the next pass may delete it. A size change or a missing file is `Pend`ed.
- `TryKey` and `IsMp4` are internal so `CleanupPlanner` shares them. `EngineHost` raises the warning through a captured `warnTo` set once `Warnings` exists.
- Tests: `LibraryCleanupTests` (9 tests, the gate as a 3-case theory) on `ManualTime`; `LibraryServeTests.CleanupPreviewOverIpcNamesTheSavedClip`.

- QA round 1 (high): the scan follows junctions, so a junction or symbolic link inside the Output Folder put files from elsewhere in the index, and the lexical guards let Cleanup delete them. `LibraryActor.ThroughReparsePoint` now refuses a file when it, or any folder between the Output Folder and it, is a reparse point (or can't be read). The planner checks this for each item it reaches, so preview and pass agree, and `CleanupDelete` checks it again before the delete. The Output Folder itself may be a junction. Test: `AFileReachedThroughAJunctionIsNeverDeleted` (`mklink /J` in the temp folder).

- Coordinator follow-up to QA round 1: the guard is now layered. (1) The scan's `Recursive` options skip `FileAttributes.ReparsePoint`, so a junction, symbolic link or mount point is neither followed nor indexed, and `Reconcile` drops an item reached through one. (2) Right before a Cleanup delete, and before 7.7's rename and recycle (same hole), `Inside` checks for reparse points below the Output Folder, then compares the file's final path with the Output Folder's own, both resolved by the injected `finalPath` port (`GamePaths.Canonicalize`: `GetFinalPathNameByHandle` on an attributes-only handle), ignoring case, with a trailing separator. A failure refuses: Cleanup skips the file, and rename and delete answer `command-failed`. (3) The planner takes the same check as a predicate, cached per pass, so the preview matches. Test: `AScannedLibraryWithAJunctionAndALinkNeverLosesTheVictims` (a real `Load` scan; the symbolic-link part runs only where links can be created without admin rights).

## Plan Change Log

## Review Triage Log

### 2026-11-04 — Review pass
- verdicts: 4 findings — high 0, medium 1, low 3, false 0, maybe-false 0
- findings:
  - `[low]` `[patch]` `EndCleanup` re-armed `cleanup-limit-unreachable` on a pass that skipped a locked file while still over `maxBytes`, so the next pass raised it again with no crossing in between. — Re-armed only when `maxBytes` is off or the remaining size is within it.
  - `[low]` `[patch]` A re-armed crossing raised a second warning beside an undismissed first (`Warnings.Raise` makes a new id each time). — `EngineHost` keeps the last warning's id and withdraws it before raising the next.
  - `[medium]` `[patch]` `AFileBeingWrittenIsKept` passed with the being-written guard removed: the size walk stopped before reaching the newest, 0-byte written item. — The test now uses a 30 s age limit (below the 60 s start delay), so the walk reaches the written item.
  - `[low]` `[patch]` `Start` returned once the 60 s timer was armed, before the 10 min timer existed, so an early `Advance` could arm it 11 min out and the periodic tests could flake. — `Start` also waits for the 10 min timer.

## Design Notes

- Stepwise passes keep the actor responsive and make "re-checked immediately before each delete" real: a command posted after the plan is handled before the next delete.
- Unreachable is about `maxBytes` only: an old Favourite under an age limit is simply kept (R7 "never deletes a Favourite"), so it doesn't raise a warning forever. Unattended default, to record in Decisions Awaiting Review.
- A being-written item counts at its index size (0 until `EndWrite`).

## Verification

**Before any build or test:** `%USERPROFILE%\Documents\repos\ReplayBuffer\.bench-running` must not exist (another session's GPU benchmark is measuring). While it exists, don't build or test: keep writing code, then wait for it to go (check once a minute, up to 30 min); if it is still there, report the work as unverified.

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings, 0 errors
- `dotnet test --project tests/ReplayBuffer.<X>.Tests -c Release --no-build` for Contracts, Core, Engine -- expected: all pass
- `pwsh tools/publish-engine.ps1` -- expected: 0 IL/AOT warnings

## Auto Run Result

**Summary:** Cleanup runs in the LibraryActor. A pure `CleanupPlanner.Plan` picks the oldest non-Favourite, not-being-written `.mp4` items under the Output Folder, until the folder is within `maxBytes` and nothing is older than `maxAgeSeconds`. A pass logs its plan, then deletes one item per actor message. Each step plans again and re-checks the item right before `File.Delete`: still indexed, not being written, not a Favourite, under the folder, and on disk at its indexed size and time. So a Favourite set after the plan is honoured. Each delete is registered, published as `library.itemRemoved { reason: "cleanup" }` and logged with its path and size. A file that can't be deleted is skipped until the next pass.

Cleanup is gated:
- Never in the first 60 s after start, while a rescan is pending, or while `cleanupBlocked` is set.
- It runs after an `EndWrite`, a `cleanup` change, a rescan or an acknowledged rebuild, at 60 s, and every 10 min.

When only Favourites and files being written keep the folder over `maxBytes`, it raises `cleanup-limit-unreachable` once per crossing, and a new crossing replaces the old warning. `cleanup.preview` returns the same plan without deleting.

**Files:**
- Contracts (additive, AD-22):
  - `ErrorCodes.CleanupLimitUnreachable`.
  - `Library.cs`: `LibraryItemRemoved.Reason`, `LibraryRemovalReasons`, `LibraryCleanupPreview`/`LibraryCleanupPlan` and the `cleanup.preview` command.
- Core: `CleanupPlanner.cs` (new); `LibraryActor.cs` (gate, triggers, stepwise pass, re-check, preview, warning latch).
- Engine: `EngineHost.cs` (settings, warning, log, router).
- Tests:
  - Core: `LibraryCleanupTests.cs` and `ManualTime.cs` (new).
  - Engine: `LibraryServeTests.cs` (+1).

**Review:** quick lens, 4 findings, all patched (1 medium, 3 low):
- re-arming on a skipped pass
- stacked duplicate warnings
- a being-written test that didn't reach the guard
- a timer race in the test setup

Nothing deferred or rejected. A follow-up review isn't recommended: no high was patched, and only one medium.

**Verification:**
- `dotnet build ReplayBuffer.slnx -c Release`: 0 warnings, 0 errors.
- Contracts: 118/118.
- Core: 131/131.
- Engine: 170 passed, 28 explicit skipped.
- `pwsh tools/publish-engine.ps1`: clean, no IL or AOT warnings.
- Heavy tests not run (no capture, encode, mux or audio change).
- Platform.Windows and App untouched, so not run.

**Residual risks:**
- Cleanup is permanent by design (no Recycle Bin), and it is untried on the builder's real Output Folder (7.11).
- The unreachable warning covers the size limit only: an old Favourite under an age limit is kept silently. This is an unattended default, for Decisions Awaiting Review.
- A pass saves `library.json` once per delete.

