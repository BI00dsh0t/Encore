---
title: 'Tracer: a saved Clip appears in the Library within 2 s'
type: 'feature'
ticket: '1'
created: '2026-11-03'
status: 'built'
baseline_revision: 'd6b57b6ef463e170bf92f9b0f96ea25e148f419d'
route: 'full'
route_source: 'auto'
risk: 'medium'
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

**Problem:** A saved Clip is invisible to the Main App: 4.5's `ILibraryWrites` port is a no-op, nothing indexes the Output Folder, and the App has no Library page (R1, R3 part).

**Approach:** A single-threaded `LibraryActor` in Core implements `ILibraryWrites`, indexes the Output Folder's `*.mp4` files at start (merged with `library.json` in the roaming instance folder, written atomically), and publishes `library.itemAdded`/`itemChanged`/`itemRemoved`, which keep a new `library` field of `state.snapshot` current. A Library tab in the Main App lists the items newest first with name, game, date and size, a being-written one shown locked.

## Boundaries & Constraints

**Always:**
- AD-17: every index change and every `library.json` write runs on the actor's own thread; `BeginWrite`/`EndWrite` only post and never block or throw.
- An item's key is its path relative to the Output Folder (compared `OrdinalIgnoreCase`); the being-written set holds absolute paths (AD-9). A `BeginWrite` path outside the current Output Folder is ignored.
- Lifecycle: `BeginWrite` → `itemAdded { beingWritten: true, size 0, date = now, profileId }`; `EndWrite(Written)` → `itemChanged { beingWritten: false, size from disk }` and a save of `library.json`; `EndWrite(Failed)` → `itemRemoved`. Being-written items are never persisted.
- AD-22: Contracts change additively, all in `Messages/Library.cs` plus the snapshot field; the field defaults to empty for a snapshot without it.
- AD-1: the App only reads the snapshot; it never touches the files.
- Lean tests (CLAUDE.md "Tests").

**Never:**
- The folder watcher, reconciliation beyond the start scan, `.bak`, `library-corrupt`, `outputFolder` change rebuilds (7.2); length or thumbnails (7.3); Favourite, grouping, filters, actions, Cleanup (7.4+).
- Changes to the orchestrator, capture, encode or mux code beyond swapping `NoLibraryWrites.Instance` for the actor in `EngineHost`.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Saved | Begin(p, "game") then End(p, Written) | one `itemAdded` (locked), then `itemChanged` with size; snapshot holds the item with its profileId; `library.json` holds it | — |
| Failed | Begin(p) then End(p, Failed) | `itemAdded` then `itemRemoved`; no item; nothing persisted | — |
| Restart | `library.json` has p with profileId; file still on disk | first snapshot holds p with its profileId and json date, size from disk | — |
| Scan | an `.mp4` on disk not in `library.json` | item with null profileId, date = file creation time | zero-length `.mp4` (a leftover reservation) and non-`.mp4` files are skipped |
| Gone | `library.json` lists a file that no longer exists | not an item | — |
| Bad json | `library.json` doesn't parse | start from the scan alone (7.2 adds `library-corrupt`) | logged via the `failed` callback |
| Save fails | `library.json` write throws IO/access error | index and events unaffected | temp deleted, `failed` callback |

</intent-contract>

## Code Map

- `src/ReplayBuffer.Contracts/Messages/Library.cs` -- placeholder `LibraryMessages` (Commands/Events empty, already spread into `Ipc/MessageTypes.cs` registry). Fill here: `LibraryItem`, `LibraryItemRemoved(Path)`, the three `EventType<T>`s (all with snapshotField `"library"`), and `LibraryJsonContext` with the same attributes as `GameJsonContext` (`Messages/Game.cs:98-108`). Copy `GameMessages.Detected` (`Game.cs:70-74`) for the Apply style: replace-by-key builds a new list.
- `src/ReplayBuffer.Contracts/Messages/Engine.cs:13-122` -- `StateSnapshot`. Add `Library` with a backing field and `?? []` init like `Devices` (L15-18, L43-49), and set it in `Initial` (~L107). `EngineJsonContext` (L186-196) reaches nested types.
- `src/ReplayBuffer.Core/Library/LibraryWrites.cs` -- the port; keep `NoLibraryWrites` (tests use it).
- `src/ReplayBuffer.Core/Sessions/SessionOrchestrator.cs:62-63,141-227,186-208` -- the actor shape to copy: unbounded single-reader `Channel`, dedicated background thread, `Post` = `TryWrite`, `Dispose` completes and joins, `internal int ThreadId`. Core references only Contracts: no logging; take delegates.
- `src/ReplayBuffer.Engine/Settings/SettingsStore.cs:288-311` -- atomic write (temp `.tmp`, `Flush(true)`, `File.Move(overwrite)`); copy the sequence into the actor, plain `FileStream`.
- `src/ReplayBuffer.Contracts/Instance.cs` -- `RoamingDataFolder` holds `library.json` (beside `settings.json`).
- `src/ReplayBuffer.Engine/EngineHost.cs` -- `Start` L173-390: output folder is `SettingsResolver.Resolve(document, null).OutputFolder ?? Path.Combine(MyVideos, instance.Name)` (L251; same expression as the orchestrator ~L710). `ReplayClips(... Library: NoLibraryWrites.Instance)` at L271-272. `DisposeAsync` L392-422 and the catch cleanup L378-388. `hostOptions.Time` is the clock.
- `src/ReplayBuffer.Engine/Ipc/EventHub.cs:26-32,48-64` -- initial snapshot built here (`with { Devices = … }`); `Publish<T>(EventType<T>, payload)`.
- `src/ReplayBuffer.App/MainWindow.xaml:12-56` -- left `TabControl`; add a "Library" `TabItem` between Status and Settings.
- `src/ReplayBuffer.App/MainViewModel.cs:26-36,124-165` -- constructor and `Refresh()` (re-reads `_engine.State` on `Changed`); add `Library` and render it from `Refresh`.
- `src/ReplayBuffer.App/Settings/GameProfilesPage.cs:47-74` and `GameProfilesView.xaml` -- the in-place diff of an `ObservableCollection` and the `ItemsControl` template to copy; `Observable.cs` has `Set`/`RelayCommand`. Profile names: `state.Settings.Profiles.TryGetValue(id, out var p) ? p.Name : …`.
- Tests to copy: `tests/ReplayBuffer.Engine.Tests/GameDetectionServeTests.cs:16-60,262-280` (temp `Instance`, `EngineHost.Start`, `EngineClient.ConnectAsync` with an events `Channel`, `NextAsync`, settings update via `SettingsMessages.Update`); `tests/ReplayBuffer.Contracts.Tests/MessagesTests.cs:18-37` (`Samples`: one per event, the theories then check the field mapping); `tests/ReplayBuffer.App.Tests/GameProfilesPageTests.cs` (state setup, `TheViewRendersWithoutBindingErrors` offscreen under `Sta.Run`, not Heavy); `FakeEngineSession.cs`.

## Tasks & Acceptance

**Execution:**
- [x] `src/ReplayBuffer.Contracts/Messages/Library.cs` -- `LibraryItem(string Path, string? ProfileId, long Size, DateTimeOffset Date, bool BeingWritten)`, `LibraryItemRemoved(string Path)`, `ItemAdded`/`ItemChanged` (replace or append by Path) and `ItemRemoved`, `LibraryJsonContext` -- R1 events
- [x] `src/ReplayBuffer.Contracts/Messages/Engine.cs` -- `IReadOnlyList<LibraryItem> Library` defaulting to empty; in `Initial` -- R1 snapshot field
- [x] `src/ReplayBuffer.Core/Library/LibraryActor.cs` (new) -- `sealed class LibraryActor : ILibraryWrites, IDisposable`. `static IReadOnlyList<LibraryItem> Load(string outputFolder, string libraryFile, TimeProvider, Action<Exception> failed)` does the start scan + json merge (Matrix rows Restart/Scan/Gone/Bad json) and the constructor takes those items, `Func<string> outputFolder`, the file path, publish delegates for the three events, the clock and `failed`. A Core-private JSON context persists `{ "version": 1, "items": [...] }` -- R1 actor, AD-17
- [x] `src/ReplayBuffer.Engine/EngineHost.cs` -- load the items before the `EventHub` and seed its first snapshot with them (`EventHub` takes them, or a `with` after construction—whichever is smaller); construct the actor publishing through `hub`; pass it as `ReplayClips.Library`; expose `internal ILibraryWrites Library`; dispose after `Sessions`, also in the catch cleanup -- wiring
- [x] `src/ReplayBuffer.App/Library/LibraryPage.cs`, `LibraryView.xaml(.cs)` (new), `MainViewModel.cs`, `MainWindow.xaml` -- `LibraryPage : Observable` with `View`, `Render(StateSnapshot)` diffing `ObservableCollection<LibraryRow>` by Path, newest first by Date; a row shows name (file name), game (profile name, else the relative path's top-level folder, else "Other"), local date `yyyy-MM-dd HH:mm`, size (B/KB/MB/GB, one decimal) and a lock marker ("Saving…") when `BeingWritten` -- R1, R3 part
- [x] `tests/ReplayBuffer.Contracts.Tests/MessagesTests.cs` -- three `Samples` entries -- every library event maps to the snapshot field
- [x] `tests/ReplayBuffer.Engine.Tests/LibraryServeTests.cs` (new) -- (1) temp instance, set `outputFolder` to a temp dir, `host.Library.BeginWrite(abs, "c:\\games\\a.exe")`, write bytes, `EndWrite(Written)`: one `library.itemAdded` within 2 s, then `itemChanged`; the client's `State.Library` holds the item with its profileId and size. (2) the same then dispose and restart the host on the same instance: the new client's first snapshot holds the item with its profileId. (3) Begin then End(Failed): `itemRemoved` and no item
- [x] `tests/ReplayBuffer.Core.Tests/LibraryActorTests.cs` (new) -- two quick tests for the Matrix rows the engine test doesn't reach: (1) `Load` over a temp folder with a json-listed file, an unlisted file, a zero-length `.mp4`, a `.rbpart` and a json entry whose file is gone, and (2) a bad `library.json` loads from the scan alone and a save whose target is a directory calls `failed` while the events still arrive
- [x] `tests/ReplayBuffer.App.Tests/LibraryPageTests.cs` (new) -- items list newest first with game name, size text and a locked being-written row; the view renders without binding errors

**Acceptance Criteria:**
- Given a running engine with a temp Output Folder, when a writer's `BeginWrite` then `EndWrite(Written)` run for a file in it, then the App client receives `library.itemAdded` within 2 s and its next `state.snapshot` holds the item with its profileId.
- Given a saved item, when the engine restarts on the same instance, then the first `state.snapshot` holds the item loaded from `library.json`.
- Given a snapshot with items of different dates and one being written, when the Library page renders, then rows are newest first and the being-written row shows locked.
- Given the builder saves a Clip in a game, when they open the Library tab, then the Clip appears within 2 s (Human Check Queue).

## Implementation Notes

- Implemented by a fresh subagent from this plan; three review patches applied in the main session.
- `LibraryActor.Load` takes no `TimeProvider`: nothing in the load reads a clock (a scanned file's date is its creation time). The constructor takes it for a write's date.
- Dates are stored in UTC; the App shows them in local time. An `EndWrite(Written)` for a file that's gone gives size 0.
- `IpcTests.TheSnapshotComesFirstAndEventsFollowInSeqOrder` now copies `Library` from the hub's state, as it already did `DetectedGames` (equal lists, different objects).
- The fourth AC (the builder saves a Clip in a game and sees it in the Library tab within 2 s) is in the Human Check Queue, unverified here.

## Plan Change Log

## Review Triage Log

### 2026-11-03 — Review pass
- verdicts: 5 findings — high 0, medium 0, low 3, false 2, maybe-false 0
- findings:
  - `low` `patch` A `null` element in `library.json`'s items throws `NullReferenceException` out of `Load` and stops engine start — `ReadFile` now skips null entries.
  - `low` `patch` The `_library.Dispose()` comment in `EngineHost.DisposeAsync` claims every begun write was saved; a write in flight at shutdown gets no `EndWrite` (4.5 deferred) — comment reworded to say so.
  - `low` `patch` `LibraryPageTests` binding-error filter `"LibraryRow"` misses errors on `LibraryPage` — filter is now `"Library"`.
  - `false` `reject` `Load` drops the plan's `TimeProvider` — nothing in the load reads a clock, so no behaviour differs; recorded in Implementation Notes.
  - `false` `reject` Plan checkboxes and notes not updated — the fix edits this build's plan, which triage rejects; finalization updates them anyway.

## Design Notes

`BeginWrite` publishes the item at once (locked) so the Library shows a save in progress; `EndWrite(Written)` changes it. That is why the AC counts one `itemAdded` per save. A zero-length `.mp4` at start is a reservation left by a write that never ended (4.5 deferred note: shutdown mid-write), so the start scan skips it; 7.2's reconciliation owns tidier handling.

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings, 0 errors
- `dotnet test --project tests/ReplayBuffer.Contracts.Tests -c Release --no-build`, then `.../ReplayBuffer.Core.Tests`, `.../ReplayBuffer.Engine.Tests`, `.../ReplayBuffer.App.Tests` -- expected: all pass
- `pwsh tools/publish-engine.ps1` -- expected: 0 IL/AOT warnings

## Auto Run Result

**Summary:** A single-threaded `LibraryActor` in Core replaces 4.5's no-op `ILibraryWrites`. It indexes the Output Folder's non-empty `*.mp4` files at start, merged with `library.json` (roaming instance folder, written atomically), and publishes `library.itemAdded` (locked, at `BeginWrite`), `itemChanged` (size, at `EndWrite(Written)`) and `itemRemoved` (at `EndWrite(Failed)`). These keep the new `state.snapshot.library` field current. A Library tab in the Main App lists the items newest first with name, game, date and size; an item being written shows "Saving…".

**Files changed:**
- `src/ReplayBuffer.Contracts/Messages/Library.cs` -- `LibraryItem`, `LibraryItemRemoved`, the three events, `LibraryJsonContext`
- `src/ReplayBuffer.Contracts/Messages/Engine.cs` -- `StateSnapshot.Library`, empty by default
- `src/ReplayBuffer.Core/Library/LibraryActor.cs` -- the actor, start scan and `library.json`
- `src/ReplayBuffer.Engine/EngineHost.cs`, `Ipc/EventHub.cs` -- load, seed the first snapshot, wire and dispose the actor
- `src/ReplayBuffer.App/Library/*`, `MainViewModel.cs`, `MainWindow.xaml` -- the Library tab
- Tests: `Core.Tests/LibraryActorTests.cs` (2), `Engine.Tests/LibraryServeTests.cs` (3), `App.Tests/LibraryPageTests.cs` (2), three samples in `Contracts.Tests/MessagesTests.cs`, one line in `Engine.Tests/IpcTests.cs`

**Review:** 3 low patches applied, 0 deferred, 2 rejected as false (see the Review Triage Log). Follow-up review: not recommended (patched: high 0, medium 0, low 3).

**Verification:** `dotnet build ReplayBuffer.slnx -c Release`: 0 warnings. Default runs: Contracts 105, Core 74, App 69 (7 explicit skipped) and Engine 154 (18 explicit skipped) passed. After the patches, one Engine run had flaky failures in GPU capture tests this diff doesn't touch (`CaptureEndTests.ClosingTheWindowEndsCaptureAndFinalizesTheFile`, `TrackLayoutCaptureTests.EachTrackIsOneTitledAacStreamInLayoutOrder`) while parallel stories loaded the GPU; reruns passed. `pwsh tools/publish-engine.ps1`: no IL/AOT warnings.

**Residual risks:** A Clip still writing at engine shutdown gets no `EndWrite`, so the next start scan finds it without its profile and dated by creation time (4.5 deferred; 7.2's reconciliation). `outputFolder` changes aren't followed until 7.2/7.9. The builder's in-game check (AC 4) is pending.

