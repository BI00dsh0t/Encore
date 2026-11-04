---
title: 'Cleanup'
type: 'feature'
ticket: '8'
created: '2026-11-04'
status: 'in-progress'
baseline_revision: 'f45ff2fa7e308cc9b0cf0beaebf3e1a06918d486'
route: 'full'
route_source: 'auto'
risk: 'high'
review: ''
review_source: ''
lenses_ran: []
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
- [ ] `src/ReplayBuffer.Contracts/ErrorCodes.cs`, `Messages/Library.cs` -- code, reason, preview command -- AD-22 additive
- [ ] `src/ReplayBuffer.Core/Library/CleanupPlanner.cs` -- pure plan -- R7
- [ ] `src/ReplayBuffer.Core/Library/LibraryActor.cs` -- gate, triggers, stepwise pass, re-check, preview, unreachable latch -- R7, AD-17
- [ ] `src/ReplayBuffer.Engine/EngineHost.cs` -- wiring, warning, log, router -- R7
- [ ] Tests -- Core (manual clock, fabricated items with `Date`/`Size`/`Modified` passed to the ctor): one per matrix row; the "favourited after plan" test calls `SetFavourite` from inside the log port when the plan line is logged (it lands before the first step). The rescan-pending test creates a subfolder (watcher → `PendRescan`), waits until the settle timer is armed on the manual clock, triggers, sees nothing deleted, then advances 250 ms and sees the delete. Engine: `cleanup.preview` over IPC returns the saved Clip's path and size.

**Acceptance Criteria:**
- Given `cleanup` limits are set and the gate is open, when a pass runs, then exactly the oldest non-Favourite, not-being-written items needed to get within both limits are deleted from disk, each published as `library.itemRemoved` with `reason: "cleanup"` and logged with its path and size.
- Given a Favourite, a file being written, or a path outside the Output Folder, when any pass runs, then that file is still on disk.
- Given `cleanup.preview` is sent with limits, when the next pass runs with those limits, then it deletes exactly the paths the preview returned.
- Given only Favourites keep the folder over `maxBytes`, when passes run, then one `cleanup-limit-unreachable` warning is raised.

## Implementation Notes

## Plan Change Log

## Review Triage Log

## Design Notes

- Stepwise passes keep the actor responsive and make "re-checked immediately before each delete" real: a command posted after the plan is handled before the next delete.
- Unreachable is about `maxBytes` only: an old Favourite under an age limit is simply kept (R7 "never deletes a Favourite"), so it doesn't raise a warning forever. Unattended default, to record in Decisions Awaiting Review.
- A being-written item counts at its index size (0 until `EndWrite`).

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings, 0 errors
- `dotnet test --project tests/ReplayBuffer.<X>.Tests -c Release --no-build` for Contracts, Core, Engine -- expected: all pass
- `pwsh tools/publish-engine.ps1` -- expected: 0 IL/AOT warnings
