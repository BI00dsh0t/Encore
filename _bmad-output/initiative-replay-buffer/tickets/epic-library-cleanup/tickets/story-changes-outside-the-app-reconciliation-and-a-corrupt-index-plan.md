---
title: 'Changes outside the app, reconciliation and a corrupt index'
type: 'feature'
ticket: '2'
created: '2026-11-03'
status: 'ready-for-dev'
baseline_revision: ''
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
- [ ] `src/ReplayBuffer.Contracts/Messages/Library.cs`, `Messages/Engine.cs`, `ErrorCodes.cs` -- `LibraryReset(IReadOnlyList<LibraryItem> Items)` → `library.reset` (field `library`, replaces the list); `LibraryCleanupBlocked(bool Blocked)` → `library.cleanupBlockedChanged` (field `cleanupBlocked`); `StateSnapshot.CleanupBlocked`; `ErrorCodes.LibraryCorrupt` -- R2 contracts
- [ ] `src/ReplayBuffer.Core/Library/Mp4File.cs` (new) -- `internal static bool IsComplete(string path)`: walks the top-level boxes (32-bit size, `1` = 64-bit size, `0` = to end of file). It's true when the first box is `ftyp`, a `moov` is present and the boxes end exactly at the file's length. Open with `FileShare.ReadWrite | FileShare.Delete`; an `IOException` propagates (the file is locked: retry) -- partial-file check
- [ ] `src/ReplayBuffer.Core/Library/LibraryActor.cs` -- (a) `Load(outputFolder, libraryFile, Func<string, string?> profileForFolder, failed)` returns `LibraryLoad(IReadOnlyList<LibraryItem> Items, bool Corrupt)`. It deletes leftover `.rbpart` files (and an empty `.mp4` beside one), reads `library.json`, else `.bak` (a bad `library.json` is moved to `.corrupt`). It ignores entries saved for a different `outputFolder`. It lists json entries whose size still matches, and other non-empty `.mp4`s only when `Mp4File.IsComplete`, with profileId from the top-level folder via `profileForFolder`. (b) The json gains optional `outputFolder` and per-entry `size`. `Save` keeps the previous file as `library.json.bak` (`File.Replace` when the file exists, else `File.Move`). (c) The ctor adds `profileForFolder`, `bool cleanupBlocked`, `Action<LibraryReset> reset` and `Action<LibraryCleanupBlocked> cleanupBlockedChanged`, and `bool createMissingFolder`. The actor thread keeps the current folder and creates it when missing and allowed. It starts the watcher (`Filter "*"`, subdirectories, `FileName | DirectoryName | Size | LastWrite`, 64 KB buffer) and runs one diff rescan to cover the gap after `Load`. (d) Watcher events go to a pending set with a 250 ms debounce on `TimeProvider` timers; settling reconciles each path against disk, with the renames and directory events of the Matrix. (e) `public void OutputFolderChanged()` re-reads `outputFolder()`: when the folder differs, it rebuilds (scan only, no json metadata), publishes reset, clears `_beingWritten`, saves, and re-watches. (f) `public void AcknowledgeCorrupt()` clears `cleanupBlocked` and publishes it. (g) `internal void Move(string from, string to)` is the actor's own rename: it registers both paths, does `File.Move`, moves the item and publishes removed + added. 7.6 reuses it. (h) Test seams: `internal bool Watching { set; }` toggles `EnableRaisingEvents`, and `internal void OnWatcherError(Exception)` is the `Error` handler -- R2, AD-17
- [ ] `src/ReplayBuffer.Engine/Ipc/EventHub.cs`, `EngineHost.cs` -- seed `CleanupBlocked` from `Load`. If the load is corrupt, raise `library-corrupt` (severity Warning, source `library`, no overlay, dismissable; message: the index was rebuilt from the Output Folder, Favourites may be lost, Cleanup stays off until acknowledged) and keep its id. After a successful `warnings.Dismiss` of that id, call `AcknowledgeCorrupt()`. Subscribe `load.Store.Changed` to `OutputFolderChanged`. `profileForFolder` = the profile whose `Folder` matches (ordinal ignore-case) in `load.Store.Current`. `createMissingFolder: !instance.IsTest`. Wire the new publishers to the hub -- wiring
- [ ] `src/ReplayBuffer.App/Library/LibraryPage.cs`, `LibraryView.xaml`, `MainViewModel.cs` -- `LibraryPage(IEngineSession engine)`. When `state.CleanupBlocked` is set, a banner at the top says the index was rebuilt and Cleanup is off until acknowledged, with an Acknowledge button. The button sends `warning.dismiss` for the active `library-corrupt` warning's id -- R2 banner
- [ ] `tests/ReplayBuffer.Contracts.Tests/MessagesTests.cs` -- two `Samples` -- events map to their fields
- [ ] `tests/ReplayBuffer.Core.Tests/LibraryActorTests.cs` -- fix 7.1's two tests for the new `Load` and give them valid minimal MP4 bytes (a shared helper: `ftyp` + `moov` + `mdat`). Add: (1) start leftovers and partial: a `.rbpart` with its empty reservation is deleted; a truncated `.mp4` isn't listed; a file under a profile folder takes its profileId. (2) External changes with a live watcher: copy in, rename (metadata kept), delete, each reflected within 2 s. (3) Overflow: `Watching = false`, change files, `OnWatcherError(new InternalBufferOverflowException())`, and the index is correct. (4) `Move`: exactly removed + added, nothing more within 500 ms. (5) Corrupt: both bad means `Corrupt` and the scan is used; a bad `library.json` with a good `.bak` loads the `.bak`. (6) Folder change to a missing folder: it's created, and `reset` holds only the new folder's files
- [ ] `tests/ReplayBuffer.Engine.Tests/LibraryServeTests.cs` -- await `library.reset` in `InitializeAsync`. Add: (1) a complete `.mp4` copied into the Output Folder arrives as `library.itemAdded` within 2 s. (2) Corrupt `library.json` and `.bak`, then restart: the first snapshot holds the `library-corrupt` warning and `CleanupBlocked`; `warning.dismiss` then gives `library.cleanupBlockedChanged { blocked: false }`
- [ ] `tests/ReplayBuffer.App.Tests/LibraryPageTests.cs` -- a blocked snapshot with the warning shows the banner, and Acknowledge sends `WarningDismiss(id)`. `SetLibrary` sets the flag, so the binding test renders the banner

**Acceptance Criteria:**
- Given a running engine on a temp Output Folder, when another process copies a complete `.mp4` in, renames one or deletes one, then the App client's snapshot reflects it within 2 s, and a renamed item keeps its profileId.
- Given a crash left a truncated `.mp4` and a `.rbpart`, when the engine starts, then the `.rbpart` is gone and neither file is listed.
- Given `library.json` and its `.bak` are both corrupt, when the engine starts, then the first snapshot holds a dismissable `library-corrupt` warning, the items scanned from the folder and `cleanupBlocked: true`, and dismissing the warning clears `cleanupBlocked`.
- Given the Library page with `cleanupBlocked`, when the builder presses Acknowledge, then the App sends `warning.dismiss` for that warning.
- Given `outputFolder` changes, when the actor applies it, then the Library lists only the new folder's files.

## Implementation Notes

## Plan Change Log

## Review Triage Log

## Design Notes

Reconcile one path (`abs`, key `k`): if it's being written or registered, skip it. If it's missing on disk, remove `k` if indexed. If it isn't a `.mp4`, or it's empty: remove. If indexed with the same size: nothing. Otherwise, if `IsComplete`, add it (or change its size), else remove it. On an `IOException`, or an incomplete file written within the last 2 s, re-pend it in 250 ms. A rescan runs the same check over the union of the indexed keys and the files on disk. A Renamed event: if old is indexed and new is a `.mp4` in the folder, move the item (keeping profileId and date), then pend new. A directory rename moves every key under the old prefix. A directory create, or a delete/rename of a non-`.mp4` name with keys under it, triggers a full rescan. Each published change (except a write in progress) saves `library.json`.

The completeness check is the safety net for the QA note. The engine's writer never leaves a partial `.mp4` (it writes `.rbpart` and renames). A crash leaves an empty reservation and a `.rbpart` (cleaned up at start). Anything else truncated, such as a copy that stopped or a future Recording cut off mid-fragment, fails the box walk.

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings, 0 errors
- `dotnet test --project tests/ReplayBuffer.Contracts.Tests -c Release --no-build`, then `ReplayBuffer.Core.Tests`, `ReplayBuffer.Engine.Tests`, `ReplayBuffer.App.Tests` (with a timeout) -- expected: all pass
- `pwsh tools/publish-engine.ps1` -- expected: 0 IL/AOT warnings
