---
title: 'Refactor sweep'
type: 'refactor'
ticket: '10'
created: '2026-11-04'
status: 'built'
baseline_revision: '8c0aa70f53583ef521bd72e8ef81e6ab3b6076eb'
route: 'full'
route_source: 'auto'
risk: 'low'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
followup_review_recommended: false
context: ['{project-root}/CLAUDE.md']
warnings: ['oversized']
deferred: []
---

<intent-contract>

## Intent

**Problem:** Epic 7 (7.1–7.9) deferred one review finding (7.3: a length read may block a rename or delete) and left QA notes, and two older items in `_bmad-output/deferred-work.md` name epic 7 as owner. The code also has small leftovers:
- duplicated helpers in `LibraryActor`, `LibraryPage` and `LibrarySettingsPage`;
- members with no production caller;
- over-public members;
- stale doc comments;
- duplicated test helpers and near-duplicate tests;
- no test of rename, delete and Cleanup's final-path guard (7.8 QA note 1).

**Approach:** Fix those in place with no behaviour change. Add the one missing guard test. Record every open item in `deferred-work.md` with an owner.

## Boundaries & Constraints

**Always:**
- Behaviour-preserving: same IPC, events, `library.json`, log text, UI text, timing and refusal codes.
- Keep every safety guard and its test. The guards are:
  - reparse-point skipping (`Recursive` options, `ThroughReparsePoint`);
  - the final-path check (`Inside`, `RefuseOutside`, the `finalPath` port);
  - Favourite and being-written exclusion, in `CleanupPlanner` and in `CleanupStep`/`CleanupDelete`;
  - `RefuseBeingWritten`;
  - the gate: `CleanupOpen` (`_started`, the 60 s delay, `_rescanPending`, `_cleanupBlocked`, and its persistence);
  - the on-disk re-check before each delete;
  - the recycle-only delete (`RecycleBin`'s drive check and `PreDeleteItem` refusal; a missing port refuses);
  - the preview (dry run) using the same planner.
- Before removing or narrowing any member, grep `_bmad-output/initiative-replay-buffer/tickets` for its name. Keep it if an unbuilt ticket or the epic Notes name it. Keep `Mp4File.PlayableLength`: the epic 6 crash-safe Recordings plan names it.
- Keep each file's line endings, doc-comment style and file-scoped namespaces.

**Never:**
- Touch the files that 8.2 (in QA) or 6.10 change:
  - `EngineHost.cs`, `EventHub.cs`, `ErrorCodes.cs`, `EngineConnection.cs`, `ShellApp.cs`, `TrayCapture.cs`, `StatusLine.cs`, `Program.cs`;
  - anything under `Supervision/`, `bench/` or the capture/monitor-recording code.
- Change `Contracts` beyond doc comments.
- Change the behaviour of any open `deferred-work.md` item (shutdown mid-write, the planner's not-inside count, the junction folder picker, the thumbnail cache).
- Edit the bodies of the Explicit/Heavy tests.

</intent-contract>

## Code Map

LA = `src/ReplayBuffer.Core/Library/LibraryActor.cs` (2105 lines; read each part before editing).

- LA:412-418 `PreviewCleanup` and LA:677-686 `Post` -- the same TCS + `TryWrite` + `EngineDisconnected` "The Library has shut down." pattern. Make one generic `Post<T>(Func<TaskCompletionSource<T>, Message>)`. `Post` for `Task` stays as it is, or forwards to it.
- LA:717-720 (end of `Run`) and LA:1089-1094 (`OnFolderChanged`) -- both dispose every `_registered` timer. Extract a private `DisposeRegistrations()`. `OnFolderChanged` still calls `_registered.Clear()` afterwards.
- LA:1037-1040 `OnSettle` and LA:1057-1060 `OnWatchError` -- both run `_rescanPending = false; _pending.Clear(); Rescan(); StartCleanup();`. Extract a private helper, keeping the order.
- LA:911-912 -- two identical timer callbacks. Use one private method or a cached `TimerCallback`.
- LA:1645 and LA:1662 -- the same "Cleanup skipped {key}: the file changed on disk." log line. Use one helper or a local function, with the text unchanged.
- LA:~1597-1605 `EndCleanup` -- calls `CleanupLimits()` a second time while `maxBytes` is already in scope. Read the limits once. Check first that nothing can change between the two reads on the actor thread; settings are read through the delegate.
- LA:243 `ThreadId` -- no caller. Remove it (only 7.1's plan names it, as a shape to copy).
- LA:421-432 `Move`, LA:796-798 the `MoveFile` case, LA:1128-1154 `OnMove`, LA:~2062 the `MoveFile` record -- used only by `TheActorsOwnMoveIsPublishedOnce` (LibraryActorTests:161). Production rename goes through `OnRename`, and `ARenameMovesTheFileKeepsItsFavouriteAndIsPublishedOnce` (:337) covers the same echo. Remove all of them and that test. `MoveItem` stays: `OnRename` and the watcher use it.
- LA:465 `ValidName` -- internal; it is used only at LA:1346. Make it private (no test uses it).
- LA:16-40 class doc -- add `library.setFavourite` (7.4) and `cleanup.preview` (7.8). The `Reconcile` doc (LA:1172-1177) should add that a path reached through a reparse point is dropped.
- `src/ReplayBuffer.Core/Library/LibraryWrites.cs:16-17,36` -- the docs still say the actor "replaces" `NoLibraryWrites` and call it "the stand-in until the LibraryActor exists". Reword: it is the no-op port for hosts and tests with no Library (`SessionOrchestrator`'s default).
- `src/ReplayBuffer.Contracts/Messages/Library.cs:8` -- "a Clip (later a Recording)": Recordings are indexed now. This is a doc-only change.
- `src/ReplayBuffer.App/Library/LibraryPage.cs`:
  - `RenameAsync` (:539) and `DeleteAsync` (:558) share the send → `ClearActionError` → catch `Fail(RefusalText(...))` shape. Extract one private helper that takes the send and the fallback text.
  - `"yyyy-MM-dd"` at :357 and :370: use one private const.
  - Make these private: `MonitorRecordings` (:30), `GameKeyOf` (:233), `Sync` (:401) and `BaseName` (:532). Grep the tests first; none use them today.
- `src/ReplayBuffer.App/Library/LibraryPage.cs:586` `ConfirmDelete` and `src/ReplayBuffer.App/Settings/LibrarySettingsPage.cs:356` `ConfirmWithMessageBox` -- the same Yes/No `MessageBox` with an optional owner window. Read both. If they are equal apart from the text, add one `internal static` helper in a new file, `src/ReplayBuffer.App/Prompts.cs`, and call it from both.
- `LibrarySettingsPage.cs:187,343` -- the repeated "The Library will list only the new folder. Existing files aren't moved." becomes one private const. Leave `ChangeLimitsAsync`'s catch/finally restores alone: they are 7.9 QA fixes, and removing them changes the notification count.
- `src/ReplayBuffer.App/Library/LibraryThumbnails.cs:16` `Size` and `LibraryViewState.cs:34` `Default` -- used only inside their class. Make them private (grep the tests and XAML first).
- Tests, `tests/ReplayBuffer.Core.Tests/`:
  - `LibraryActorTests.cs`: remove `TheActorsOwnMoveIsPublishedOnce`. In `ABadNameATakenNameAndAStaleIdentityAreRefused` (:446), drop the stale-identity part, which repeats `AStaleIdentityIsRefusedAndAFileChangedOnDiskIsRefreshed` (:306), and rename the test to match.
  - Have `LibraryActorTests` reuse `LibraryCleanupTests.Modified(path)` in place of the inline copy at :59, or the reverse: one `internal static` helper.
  - `Fragment` (:419-425) should reuse `Box` where it writes the same header.
- New guard test: `LibraryCleanupTests` (or `LibraryActorTests` via `Harness`) gets one test. A `finalPath` port maps one item's file outside the Output Folder; the test asserts that the preview leaves it out, a Cleanup pass keeps it, and `library.delete` is refused with `command-failed` and keeps the file. Read how `Harness`/`Start` pass `finalPath` (13-argument ctor) first. This closes 7.8 QA note 1.
- `_bmad-output/deferred-work.md` -- add an epic 7 group in the existing `source_plan` / `summary` / `evidence` / `outcome` format.

## Tasks & Acceptance

**Execution:**
- [x] LA -- the generic `Post<T>`, `DisposeRegistrations`, the rescan helper, one timer callback, one log helper, the single limits read; remove `ThreadId` and the `Move` path; make `ValidName` private; doc updates -- duplicates, dead code, stale docs
- [x] `LibraryWrites.cs`, `Contracts/Messages/Library.cs` -- doc rewording -- stale docs
- [x] `App/Library/LibraryPage.cs`, `LibraryThumbnails.cs`, `LibraryViewState.cs`, `Settings/LibrarySettingsPage.cs`, new `App/Prompts.cs` -- the shared send helper, the date const, the shared Yes/No prompt, the prompt-text const, private members -- duplicates, over-public members
- [x] `tests/ReplayBuffer.Core.Tests/LibraryActorTests.cs`, `LibraryCleanupTests.cs` -- remove the Move test (the stale-identity check is kept, see Implementation Notes), share `Modified`/`Box`, add the final-path guard test -- lean tests, guard coverage
- [x] `_bmad-output/deferred-work.md` -- the epic 7 group, one entry each:
  - (a) A write in flight at shutdown never gets `EndWrite` (4.13, owned by epic 7). Still open: it changes dispose behaviour. Owner: epic 8's hardening (crash recovery).
  - (b) Serve's `OutputNaming` (3.7 named epics 4 and 7). Epic 7 writes no files; Recordings are epic 6's. Owner changed to epic 6's sweep.
  - (c) 7.3's deferred finding, that a length read may block a rename or delete. Mitigated: 7.7's sharing-violation retry (`Retried`, about 500 ms) covers a short hold. Open (unverified); owner epic 8's hardening.
  - (d) 7.3 QA: the App's thumbnail cache is never pruned or retried. Open, low; owner epic 8's sweep.
  - (e) 7.8 QA 2: OneDrive placeholders aren't listed. Open; checked in 7.11.
  - (f) 7.8 QA 3: the planner doesn't count a not-inside item as skipped. Open, as it changes the warning; owner epic 8's hardening.
  - (g) 7.9 QA 1: the folder picker doesn't refuse a junction. Open; the engine guards cover it; owner epic 8's hardening.
  - (h) The `Mp4`/`Box` helpers are copied across the Core and Engine test projects, and the binding-error trace block across App test classes. Open, low; owner epic 8's sweep.
  - (i) What 7.10 closed, including 7.8 QA note 1.

**Acceptance Criteria:**
- Given the sweep, when `dotnet build ReplayBuffer.slnx -c Release` runs, then it reports 0 warnings and 0 errors.
- Given the sweep, when the default runs of `ReplayBuffer.Core.Tests`, `ReplayBuffer.App.Tests`, `ReplayBuffer.Engine.Tests` and `ReplayBuffer.Contracts.Tests` run, then all pass. That includes these tests:
  - a saved Clip in the Library (`LibraryServeTests`);
  - Favourite, rename, delete, open, Explorer and copy;
  - every Cleanup guard test;
  - the new final-path test.
- Given `grep -rn "MoveFile\|ThreadId" src/ReplayBuffer.Core/Library --include=*.cs`, then nothing matches.
- Given `deferred-work.md`, then items (a) to (i) are recorded, each with an owner.

## Implementation Notes

- `Post<T>` sits beside the non-generic `Post`; both share `ShutDown()` (the same `engine-disconnected` text). `EndCleanup` reads the limits once: the delegate is read on the actor thread, and the message now names the `maxBytes` that the check just used.
- `ConfirmDelete` and `ConfirmWithMessageBox` differ in caption and icon (Question vs Warning) as well as text, so `Prompts.YesNo` takes both as arguments.
- The new guard test is `LibraryCleanupTests.AFileWhoseFinalPathIsOutsideIsNeitherPreviewedCleanedRenamedNorDeleted`; `Start` gained optional `finalPath` and `recycle` ports. It also checks rename (`command-failed`).
- `ABadNameATakenNameAndAStaleIdentityAreRefused` keeps its stale-identity Rename check and its name: it is rename's only stale-identity (`library-item-changed`) test, as `AStaleIdentityIsRefusedAndAFileChangedOnDiskIsRefreshed` covers only SetFavourite.
- AC 3's grep: plain `grep "ThreadId"` still matches `Environment.CurrentManagedThreadId`/`Thread.ManagedThreadId` (BCL names, three lines). `grep -w "MoveFile\|ThreadId"` matches nothing; the `ThreadId` member and the `MoveFile` path are gone.
- Verified: build 0 warnings; Core 145/145, App 121 passed (12 explicit skipped), Engine 172 passed (32 explicit skipped), Contracts 119/119; `tools/publish-engine.ps1` clean.

## Plan Change Log

## Review Triage Log

### 2026-11-04 — Review pass
- verdicts: 2 findings — high 0, medium 1, low 1, false 0, maybe-false 0
- findings:
  - `[medium]` `[patch]` Dropping the stale-identity part of `ABadNameATakenNameAndAStaleIdentityAreRefused` left rename's `library-item-changed` refusal (7.7's verify) untested: `AStaleIdentityIsRefusedAndAFileChangedOnDiskIsRefreshed` calls only SetFavourite. — The plan's duplicate claim was wrong. Restored the assertion and the test's name; the rest of the diff is kept as is, so it is patched in place rather than re-derived. Core 145/145.
  - `[low]` `[reject]` AC 3's plain grep still matches the BCL's `ManagedThreadId`/`CurrentManagedThreadId`. — The intent (the `ThreadId` member and the `MoveFile` path gone) is met, as `grep -w` shows; the fix would be an edit to this build's plan.

## Design Notes

Ticket verify: "a saved Clip still appears in the Library within 2 s, every action still works, and Cleanup still spares Favourites". `LibraryServeTests` covers the saved Clip over IPC, `LibraryActionsTests` and `LibraryPageTests` cover the actions, and `LibraryCleanupTests` covers Favourites and the other guards. Engine code isn't changed (`EngineHost` is off limits). Core changes, but the NativeAOT publish runs the engine, which links Core, so `tools/publish-engine.ps1` runs too.

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings
- `dotnet test --project tests/ReplayBuffer.Core.Tests -c Release --no-build` -- expected: pass. Likewise `App.Tests`, `Engine.Tests` and `Contracts.Tests`.
- `pwsh tools/publish-engine.ps1` -- expected: 0 IL warnings

Before any build or test, check that `%USERPROFILE%\Documents\repos\ReplayBuffer\.bench-running` doesn't exist (another session's GPU run); if it does, wait until it's gone. Rerun a failing test once before treating it as failed. Run test projects one at a time.

## Auto Run Result

Status: built

**Summary:** A behaviour-preserving sweep of epic 7's code.
- `LibraryActor` gained shared helpers: `Post<T>`/`ShutDown`, `DisposeRegistrations`, `RescanNow`, one Cleanup timer callback, `LogChangedOnDisk`, and a single limits read in `EndCleanup`.
- Removed the unused `ThreadId` and the test-only `Move` path; `ValidName` is now private.
- Docs are current: the actor's class doc, `Reconcile`, `ILibraryWrites`/`NoLibraryWrites` and `LibraryItem`.
- In the App, rename and delete share `SendActionAsync`. A new `Prompts.YesNo` serves the delete and settings prompts. The day format and the folder-change note are consts. The members used only inside their own classes are now private.
- Tests:
  - removed the duplicate Move test;
  - `Box` and `Modified` are shared;
  - one new guard test, `AFileWhoseFinalPathIsOutsideIsNeitherPreviewedCleanedRenamedNorDeleted`, closes 7.8 QA note 1.
- `deferred-work.md` has the epic 7 group (a)–(i), each with an owner.

**Files:**
- `src/ReplayBuffer.Core/Library/LibraryActor.cs`: the helpers, the removals and the docs.
- `LibraryWrites.cs`, `Contracts/Messages/Library.cs`: doc comments only.
- `App/Library/LibraryPage.cs`, `LibraryThumbnails.cs`, `LibraryViewState.cs`, `Settings/LibrarySettingsPage.cs`, new `App/Prompts.cs`: the shared send and prompt, the consts, and the private members.
- `tests/ReplayBuffer.Core.Tests/LibraryActorTests.cs`, `LibraryCleanupTests.cs`: the test changes.
- `_bmad-output/deferred-work.md`: the epic 7 group.

**Review:** quick lens, 2 findings.
- 1 medium, patched: the stale-identity rename check is restored.
- 1 low, rejected: the AC grep wording.
- Nothing deferred.

**Follow-up review recommended:** false (one medium and no high patched).

**Verification:**
- `dotnet build ReplayBuffer.slnx -c Release`: 0 warnings, 0 errors (run again after the patch).
- `ReplayBuffer.Core.Tests`: 145 passed (run again after the patch).
- `ReplayBuffer.App.Tests`: 121 passed, 12 skipped (Explicit).
- `ReplayBuffer.Engine.Tests`: 172 passed, 32 skipped (Explicit); this includes `LibraryServeTests`.
- `ReplayBuffer.Contracts.Tests`: 119 passed.
- `tools/publish-engine.ps1`: no warnings.
- The patch touched only a Core test file, so App, Engine and Contracts weren't run again.
- No Heavy or GPU tests: none cover this code, and the builder has paused GPU tests.

**Residual risks:** None known. The open items wait in `deferred-work.md`: shutdown mid-write, the planner's skip count, the junction picker, OneDrive placeholders (7.11), and the thumbnail cache.
