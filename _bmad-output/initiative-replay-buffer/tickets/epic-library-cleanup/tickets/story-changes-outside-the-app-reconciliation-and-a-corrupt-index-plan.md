---
title: 'Changes outside the app, reconciliation and a corrupt index'
type: 'feature'
ticket: '2'
created: '2026-11-03'
status: 'built'
baseline_revision: '3bccd44d8bc1619317acefe54d09da97ed3144dd'
route: 'full'
route_source: 'auto'
risk: 'high'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
followup_review_recommended: true
context: []
warnings: ['oversized']
deferred: []
---

<intent-contract>

## Intent

**Problem:** 7.1's index only knows the start scan and the engine's own writes. Files added, renamed or deleted in the Output Folder outside the app never show; a crash mid-save can leave a partial file listed as ready (QA note on 7.1); a corrupt `library.json` silently loses metadata with no warning; an `outputFolder` change isn't followed (R2, AD-17).

**Approach:** The `LibraryActor` owns a `FileSystemWatcher` over the Output Folder and reconciles every event against disk, publishing only differences. Renames move metadata. An overflow or watcher error triggers a full rescan. Files the index doesn't already know are checked as complete MP4s before they are listed. `library.json` gets a `.bak`; if neither parses, the engine raises `library-corrupt`, rebuilds from the scan and keeps `cleanupBlocked` until the warning is dismissed from a Library page banner. An `outputFolder` change rebuilds the index from the new folder.

## Boundaries & Constraints

**Always:**
- AD-17: watcher callbacks only post to the actor's channel. Every index change, disk check and `library.json` write runs on the actor thread.
- Reconciliation checks disk and publishes only what differs from the index, so a late echo of a change already applied publishes nothing.
- Never delete or move a user's `.mp4`. The engine deletes only `*.rbpart` files, plus an empty `.mp4` that sits beside a `.rbpart` (a reservation an interrupted save left). A corrupt index is rebuilt from the folder.
- A file is listed only once it is a complete MP4 (see Design Notes) or the engine's own writer ended it with `Written`. A file the index already holds at the same size isn't re-checked.
- Events for paths being written, and their `.rbpart`, are ignored (the writer's `EndWrite` decides). Paths the actor registers for its own file operations are ignored until the registration expires (5 s), then reconciled against disk.
- AD-22: Contracts change additively, in `Messages/Library.cs`, the `StateSnapshot` field and `ErrorCodes`.
- Lean tests (CLAUDE.md "Tests"), on temp folders.

**Never:**
- Length, thumbnails, Favourite, rename/delete commands, Cleanup itself (7.3–7.8). The ticket only requires the `cleanupBlocked` flag here.
- Changes outside the Library code beyond the `EngineHost` wiring, the `warning.dismiss` hook and the App's Library page (5.6, 6.1, 4.7 and 5.3 run in parallel).
- Creating a test instance's default Output Folder under the user's Videos folder.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Copied in | another process writes a complete `.mp4` under the folder | `itemAdded` within 2 s; profileId from its profile folder, else null | locked or incomplete while the copy runs: re-checked every 250 ms while it changes |
| Partial | a truncated `.mp4` (crash, or a copy that stopped) | not listed at start or from the watcher | — |
| Renamed | `a.mp4` → `b.mp4` (or a folder renamed) | `itemRemoved(a)` + `itemAdded(b)` keeping profileId and date | renamed to a non-`.mp4` name: removed |
| Deleted | file deleted | `itemRemoved` | — |
| Overflow | watcher `Error` (buffer overflow or folder gone) | watcher restarted, full rescan, diff published | — |
| Engine move | the actor's own `Move(from, to)` | exactly one `itemRemoved` + one `itemAdded`; the echo adds nothing | move fails: `failed`, index unchanged |
| Start leftovers | `x.mp4.rbpart` (+ empty `x.mp4`) | both deleted; neither listed | delete fails: `failed`, still not listed |
| Corrupt | `library.json` and `.bak` both unparseable | `warning.raised { library-corrupt, dismissable }`; index from the scan; `cleanupBlocked` true until that warning is dismissed | bad file moved to `library.json.corrupt` |
| Bak | `library.json` bad, `.bak` good | loads the `.bak`, no warning | — |
| Folder change | `outputFolder` A → B (B missing) | B created; `library.reset` with only B's files; json saved for B | — |

</intent-contract>

## Code Map

- `src/ReplayBuffer.Core/Library/LibraryActor.cs` -- 7.1's actor: `Load`, the `Begin`/`End` messages, `Save` (atomic temp + move), Core-private `LibraryFile`/`LibraryFileEntry` and `LibraryFileJsonContext`. Most of the work goes here. Core references only Contracts plus the BCL: no logging, only delegates.
- `src/ReplayBuffer.Core/Library/LibraryWrites.cs` -- the port. Don't change it.
- `src/ReplayBuffer.Core/Output/ProfileFolders.cs` -- `MonitorRecordings`. `GameProfile.Folder` (`Contracts/Settings/SettingsDocument.cs:46`) is a profile's single top-level folder name under the Output Folder.
- `src/ReplayBuffer.Platform.FFmpeg/ClipWriter.cs:18,27-50` -- `PartExtension = ".rbpart"`. A Clip is written to `<final>.rbpart`, then `File.Move(part, final, overwrite: true)` over the empty reservation. Read only.
- `src/ReplayBuffer.Contracts/Messages/Library.cs` -- add the events here (copy the `ItemAdded` shape). `Messages/Engine.cs:13-122` `StateSnapshot`: add `CleanupBlocked` (bool, default false) beside `Library`.
- `src/ReplayBuffer.Contracts/ErrorCodes.cs:175-230` -- add `LibraryCorrupt = "library-corrupt"` with a doc comment, and add it to `All`.
- `src/ReplayBuffer.Engine/EngineHost.cs:202-232` (Load, hub seed, actor construction), `:241` (`ReplayClips`, untouched), `:304` (the `FeedbackMessages.Dismiss` route), `:348-356` (raising a start warning, the `SettingsUnreadable` pattern), `:378-388` / `:392-422` (dispose). Settings changes: `load.Store.Changed += ...` (`:215`); handlers run on the updating thread, so only post.
- `src/ReplayBuffer.Engine/Ipc/EventHub.cs:27-34` -- the ctor seeds `Library`. Add a `cleanupBlocked` seed the same way.
- `src/ReplayBuffer.Engine/Ipc/Warnings.cs` -- `Raise(...)` returns the `Warning` (use its `Id`), and `Dismiss` throws on an unknown id.
- `src/ReplayBuffer.App/Library/LibraryPage.cs`, `LibraryView.xaml` -- the page. It's created by `new LibraryPage()` in `MainViewModel.cs:35`; pass `_engine` (an `IEngineSession`; send with `SendAsync(FeedbackMessages.Dismiss, new WarningDismiss(id))`, the pattern in `Settings/GameProfilesPage.cs:80-92`). `RelayCommand` is in `Observable.cs`.
- Tests: `tests/ReplayBuffer.Core.Tests/LibraryActorTests.cs` (7.1's two tests; they write zero-byte fake `.mp4`s, which the completeness check now rejects for unknown files, so give them a valid minimal MP4); `tests/ReplayBuffer.Engine.Tests/LibraryServeTests.cs` (`NextAsync` asserts the very next event, so `InitializeAsync` must now also await the `library.reset` that follows its `outputFolder` update); `tests/ReplayBuffer.Contracts.Tests/MessagesTests.cs:18` `Samples` (one per event); `tests/ReplayBuffer.App.Tests/LibraryPageTests.cs`, `FakeEngineSession.Sent`.

## Tasks & Acceptance

**Execution:**
- [x] `src/ReplayBuffer.Contracts/Messages/Library.cs`, `Messages/Engine.cs`, `ErrorCodes.cs` -- `LibraryReset(IReadOnlyList<LibraryItem> Items)` → `library.reset` (field `library`, replaces the list); `LibraryCleanupBlocked(bool Blocked)` → `library.cleanupBlockedChanged` (field `cleanupBlocked`); `StateSnapshot.CleanupBlocked`; `ErrorCodes.LibraryCorrupt` -- R2 contracts
- [x] `src/ReplayBuffer.Core/Library/Mp4File.cs` (new) -- `internal static bool IsComplete(string path)`: walks the top-level boxes (32-bit size, `1` = 64-bit size, `0` = to end of file). It's true when the first box is `ftyp`, a `moov` is present and the boxes end exactly at the file's length. Open with `FileShare.ReadWrite | FileShare.Delete`; an `IOException` propagates (the file is locked: retry) -- partial-file check
- [x] `src/ReplayBuffer.Core/Library/LibraryActor.cs` -- (a) `Load(outputFolder, libraryFile, Func<string, string?> profileForFolder, failed)` returns `LibraryLoad(IReadOnlyList<LibraryItem> Items, bool Corrupt)`. It deletes leftover `.rbpart` files (and an empty `.mp4` beside one), reads `library.json`, else `.bak` (a bad `library.json` is moved to `.corrupt`). It ignores entries saved for a different `outputFolder`. It lists json entries whose size still matches, and other non-empty `.mp4`s only when `Mp4File.IsComplete`, with profileId from the top-level folder via `profileForFolder`. (b) The json gains optional `outputFolder` and per-entry `size`. `Save` keeps the previous file as `library.json.bak` (`File.Replace` when the file exists, else `File.Move`). (c) The ctor adds `profileForFolder`, `bool cleanupBlocked`, `Action<LibraryReset> reset` and `Action<LibraryCleanupBlocked> cleanupBlockedChanged`, and `bool createMissingFolder`. The actor thread keeps the current folder and creates it when missing and allowed. It starts the watcher (`Filter "*"`, subdirectories, `FileName | DirectoryName | Size | LastWrite`, 64 KB buffer) and runs one diff rescan to cover the gap after `Load`. (d) Watcher events go to a pending set with a 250 ms debounce on `TimeProvider` timers; settling reconciles each path against disk, with the renames and directory events of the Matrix. (e) `public void OutputFolderChanged()` re-reads `outputFolder()`: when the folder differs, it rebuilds (scan only, no json metadata), publishes reset, clears `_beingWritten`, saves, and re-watches. (f) `public void AcknowledgeCorrupt()` clears `cleanupBlocked` and publishes it. (g) `internal void Move(string from, string to)` is the actor's own rename: it registers both paths, does `File.Move`, moves the item and publishes removed + added. 7.6 reuses it. (h) Test seams: `internal bool Watching { set; }` toggles `EnableRaisingEvents`, and `internal void OnWatcherError(Exception)` is the `Error` handler -- R2, AD-17
- [x] `src/ReplayBuffer.Engine/Ipc/EventHub.cs`, `EngineHost.cs` -- seed `CleanupBlocked` from `Load`. If the load is corrupt, raise `library-corrupt` (severity Warning, source `library`, no overlay, dismissable; message: the index was rebuilt from the Output Folder, Favourites may be lost, Cleanup stays off until acknowledged) and keep its id. After a successful `warnings.Dismiss` of that id, call `AcknowledgeCorrupt()`. Subscribe `load.Store.Changed` to `OutputFolderChanged`. `profileForFolder` = the profile whose `Folder` matches (ordinal ignore-case) in `load.Store.Current`. `createMissingFolder: !instance.IsTest`. Wire the new publishers to the hub -- wiring
- [x] `src/ReplayBuffer.App/Library/LibraryPage.cs`, `LibraryView.xaml`, `MainViewModel.cs` -- `LibraryPage(IEngineSession engine)`. When `state.CleanupBlocked` is set, a banner at the top says the index was rebuilt and Cleanup is off until acknowledged, with an Acknowledge button. The button sends `warning.dismiss` for the active `library-corrupt` warning's id -- R2 banner
- [x] `tests/ReplayBuffer.Contracts.Tests/MessagesTests.cs` -- two `Samples` -- events map to their fields
- [x] `tests/ReplayBuffer.Core.Tests/LibraryActorTests.cs` -- fix 7.1's two tests for the new `Load` and give them valid minimal MP4 bytes (a shared helper: `ftyp` + `moov` + `mdat`). Add: (1) start leftovers and partial: a `.rbpart` with its empty reservation is deleted; a truncated `.mp4` isn't listed; a file under a profile folder takes its profileId. (2) External changes with a live watcher: copy in, rename (metadata kept), delete, each reflected within 2 s. (3) Overflow: `Watching = false`, change files, `OnWatcherError(new InternalBufferOverflowException())`, and the index is correct. (4) `Move`: exactly removed + added, nothing more within 500 ms. (5) Corrupt: both bad means `Corrupt` and the scan is used; a bad `library.json` with a good `.bak` loads the `.bak`. (6) Folder change to a missing folder: it's created, and `reset` holds only the new folder's files
- [x] `tests/ReplayBuffer.Engine.Tests/LibraryServeTests.cs` -- await `library.reset` in `InitializeAsync`. Add: (1) a complete `.mp4` copied into the Output Folder arrives as `library.itemAdded` within 2 s. (2) Corrupt `library.json` and `.bak`, then restart: the first snapshot holds the `library-corrupt` warning and `CleanupBlocked`; `warning.dismiss` then gives `library.cleanupBlockedChanged { blocked: false }`
- [x] `tests/ReplayBuffer.App.Tests/LibraryPageTests.cs` -- a blocked snapshot with the warning shows the banner, and Acknowledge sends `WarningDismiss(id)`. `SetLibrary` sets the flag, so the binding test renders the banner

**Acceptance Criteria:**
- Given a running engine on a temp Output Folder, when another process copies a complete `.mp4` in, renames one or deletes one, then the App client's snapshot reflects it within 2 s, and a renamed item keeps its profileId.
- Given a crash left a truncated `.mp4` and a `.rbpart`, when the engine starts, then the `.rbpart` is gone and neither file is listed.
- Given `library.json` and its `.bak` are both corrupt, when the engine starts, then the first snapshot holds a dismissable `library-corrupt` warning, the items scanned from the folder and `cleanupBlocked: true`, and dismissing the warning clears `cleanupBlocked`.
- Given the Library page with `cleanupBlocked`, when the builder presses Acknowledge, then the App sends `warning.dismiss` for that warning.
- Given `outputFolder` changes, when the actor applies it, then the Library lists only the new folder's files.

## Implementation Notes

- The actor's start work (folder creation, watcher, the gap rescan) is its first channel message, so it runs on the actor thread under the same failure handling as every other message. Changes are saved once per drained batch of messages.
- The settle timer isn't re-armed by later events, so a file that keeps changing is still re-checked every 250 ms rather than starved by its own events.
- A `library.json` entry without a size (a 7.1 file) is trusted as before. An entry whose size no longer matches is listed only if the file is complete, and it keeps its saved profile and date.
- A corrupt load is saved straight away. `cleanupBlocked` is saved in `library.json` (main session, after the subagent: otherwise a restart before acknowledging would silently lift the block R7 relies on), so `Load` reports `Corrupt` and the engine raises `library-corrupt` again until it is dismissed; acknowledging saves the cleared flag.
- The matrix's folder rename is covered in `ChangesOutsideTheAppAreReconciled` (added in the main session).
- The `failed` log line in `EngineHost` now reads "A Library disk operation failed", since the callback covers more than `library.json` writes.

## Plan Change Log

- 2026-11-03 (coordinator): `Mp4File` is `public static class` with `public static bool IsComplete(string path)`, not internal, so that 6.2's add of the same file merges as a trivial add/add. Its semantics are unchanged.

## Review Triage Log

### 2026-11-03 — Review pass
- verdicts: 5 findings — high 0, medium 2, low 3, false 0, maybe-false 0
- findings:
  - `medium` `patch` `OnFolderChanged` started the watcher after the scan and the reset, so a file created in between was never listed, and `LibraryServeTests.AFileCopiedInIsAdded` raced it — `StartWatcher()` now runs before the scan, as in `OnStart`/`OnWatchError`.
  - `medium` `patch` `Reconcile` removed a known item whenever `Check` returned null, even when `Check` had re-pended it as locked or recently written, losing its profile and date — the item is now removed only when the path wasn't re-pended.
  - `low` `reject` A `library.json` that parses but is locked at start reads as bad: it is moved to `.corrupt` and, with no good `.bak`, `library-corrupt` is raised — the file is kept (as `.corrupt`), the warning only blocks Cleanup until dismissed, and a lock on the roaming json at engine start is unlikely. The fix needs a new read outcome and branch.
  - `low` `patch` The `Watching` doc comment said a watcher restart turns events back on, but `StartWatcher` keeps the setting — comment corrected.
  - `low` `patch` `Check`'s recent-write window compared the injected `TimeProvider` with the file system's clock — it now compares `file.LastWriteTimeUtc` with `DateTime.UtcNow`.

## Design Notes

Reconcile one path (`abs`, key `k`): if it's being written or registered, skip it. If it's missing on disk, remove `k` if indexed. If it isn't a `.mp4`, or it's empty: remove. If indexed with the same size: nothing. Otherwise, if `IsComplete`, add it (or change its size), else remove it. On an `IOException`, or an incomplete file written within the last 2 s, re-pend it in 250 ms. A rescan runs the same check over the union of the indexed keys and the files on disk. A Renamed event: if old is indexed and new is a `.mp4` in the folder, move the item (keeping profileId and date), then pend new. A directory rename moves every key under the old prefix. A directory create, or a delete/rename of a non-`.mp4` name with keys under it, triggers a full rescan. Each published change (except a write in progress) saves `library.json`.

The completeness check is the safety net for the QA note. The engine's writer never leaves a partial `.mp4` (it writes `.rbpart` and renames). A crash leaves an empty reservation and a `.rbpart` (cleaned up at start). Anything else truncated, such as a copy that stopped or a future Recording cut off mid-fragment, fails the box walk.

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings, 0 errors
- `dotnet test --project tests/ReplayBuffer.Contracts.Tests -c Release --no-build`, then `ReplayBuffer.Core.Tests`, `ReplayBuffer.Engine.Tests`, `ReplayBuffer.App.Tests` (with a timeout) -- expected: all pass
- `pwsh tools/publish-engine.ps1` -- expected: 0 IL/AOT warnings

## Auto Run Result

**Summary:** The `LibraryActor` now keeps the index right whatever happens to the Output Folder (R2, AD-17). A `FileSystemWatcher` (all names, subdirectories) posts to the actor, which settles events for 250 ms and reconciles each path against disk, publishing only differences. Renames move an item's metadata, and a folder rename moves every item under it. A watcher error or overflow restarts the watcher and rescans. Files the index doesn't know at their size are listed only when `Mp4File.IsComplete` passes, so a partial file left by a crash or an unfinished copy isn't shown as ready (the QA note on 7.1). Locked or recently written files are re-checked. At start, leftover `.rbpart` files and their empty reservations are deleted. `library.json` keeps a `.bak`, records its Output Folder, per-item sizes and `cleanupBlocked`. When neither copy parses, the engine raises a dismissable `library-corrupt`, rebuilds from the scan and keeps `state.snapshot.cleanupBlocked` set until the warning is dismissed, also across restarts; the Library page shows a banner with Acknowledge. An `outputFolder` change creates the folder if needed and rebuilds from it as `library.reset`. The actor's own `Move` registers its paths so the echo adds nothing (for 7.6).

**Files changed:**
- `src/ReplayBuffer.Contracts/Messages/Library.cs`, `Messages/Engine.cs`, `ErrorCodes.cs` -- `library.reset`, `library.cleanupBlockedChanged`, `StateSnapshot.CleanupBlocked`, `library-corrupt`
- `src/ReplayBuffer.Core/Library/Mp4File.cs` -- new public `IsComplete` box walk (same path and signature as 6.2's, for a trivial merge)
- `src/ReplayBuffer.Core/Library/LibraryActor.cs` -- watcher, reconciliation, rescans, leftovers, `.bak`/corrupt handling, folder change, acknowledge, `Move`
- `src/ReplayBuffer.Engine/EngineHost.cs`, `Ipc/EventHub.cs` -- seed `cleanupBlocked`, raise `library-corrupt`, acknowledge on dismiss, follow `outputFolder`, profile folders
- `src/ReplayBuffer.App/Library/LibraryPage.cs`, `LibraryView.xaml`, `MainViewModel.cs` -- the banner and Acknowledge
- Tests: `Core.Tests/LibraryActorTests.cs` (8), `Engine.Tests/LibraryServeTests.cs` (+2), `App.Tests/LibraryPageTests.cs` (+1), two samples in `Contracts.Tests/MessagesTests.cs`

**Review:** Quick lens: 4 patches applied (medium 2, low 2), 0 deferred, 1 rejected (low: a transiently locked `library.json` at start reads as corrupt; the file is kept and the warning only blocks Cleanup). Follow-up review: recommended, because two medium patches (the watcher order in `OnFolderChanged` and the re-pend guard in `Reconcile`) landed after the review pass. QA should check that a known file that is briefly locked keeps its profile and date.

**Verification:** `dotnet build ReplayBuffer.slnx -c Release`: 0 warnings. Default runs: Contracts 109, Core 86, Engine 158 (18 explicit skipped) and App 70 (7 explicit skipped) passed, after the patches. Before the patches, `LibraryActorTests` ran 5 times and `LibraryServeTests` 3 times without a failure, and Core 3 more times after the folder-rename step. `pwsh tools/publish-engine.ps1`: no IL/AOT warnings. The heavy suite wasn't run: the capture, encode, mux and audio paths are unchanged.

**Residual risks:** A copy that sets the file's full size up front and doesn't lock the destination could pass the box walk before its data is in (the usual copy tools lock it). A Recording cut off mid-fragment fails the walk and isn't listed until 6.2 trims it at start. `ReplayClips` still writes to `MyVideos\<instance>` rather than `outputFolder` (pre-existing), so with a custom Output Folder the engine's own Clips land outside the Library. A test instance's default folder isn't created (`createMissingFolder: !instance.IsTest`).
