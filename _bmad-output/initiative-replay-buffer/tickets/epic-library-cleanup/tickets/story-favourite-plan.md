---
title: 'Favourite'
type: 'feature'
ticket: '4'
created: '2026-11-03'
status: 'in-review'
baseline_revision: '1a88a421fae56ada800d72e6f68c7d7e8709268b'
route: 'full'
route_source: 'auto'
risk: 'low'
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

**Problem:** The Library can't mark a Clip as a Favourite (R6 part), which 7.5's filter and 7.7's Cleanup depend on.

**Approach:** A `library.setFavourite` command carries the item's path, size and mtime (AD-3) and the wanted flag. The LibraryActor checks the identity, sets `Favourite` on the item, publishes `library.itemChanged` and saves it in `library.json`; a stale identity is refused with `library-item-changed`. Each Library row has a star toggle that sends the command and follows the snapshot.

## Boundaries & Constraints

**Always:**
- AD-17: the actor thread alone reads and changes `_items`; the command posts a message carrying a `TaskCompletionSource` and the router awaits it.
- AD-3: identity = path + size + mtime. `LibraryItem` gains the mtime so the App can send it. The command is never auto-retried; the App doesn't change the row until the event.
- AD-22: Contracts change additively: trailing optional record parameters, a new command in `LibraryMessages.Commands`, a new code in `ErrorCodes`.
- The Favourite is kept on the item through every path that keeps metadata: a move (outside rename and the actor's own `Move`), `OnEnd`, `Reconcile`'s size change, `Load` from `library.json` or its `.bak`, and an entry whose size changed while the engine was off.
- Being written doesn't block a Favourite: the flag can be set on a `BeingWritten` item and survives its `EndWrite`.
- Lean tests (CLAUDE.md "Tests"): one or two happy-path tests per AC.

**Never:**
- No grouping/filter (7.5), rename/delete (7.6), Cleanup (7.7).
- Don't change the reconciliation rules, the length worker or `ILibraryWrites`.
- No optimistic row update in the App.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Set | identity matches the item and the file | `itemChanged` with `favourite: true`; saved | — |
| Stale mtime | args' `modified` differs | refused `library-item-changed` | item unchanged |
| Changed on disk | item matches args, disk size/mtime don't | refused `library-item-changed`; the item is refreshed to the disk's size and mtime and published | — |
| Being written | item `BeingWritten` (size 0, modified null) and args match it | set; kept after `EndWrite` | disk not checked |
| Outside rename | favourite item renamed in Explorer | moved item keeps `favourite: true` | — |

</intent-contract>

## Code Map

- `src/ReplayBuffer.Contracts/Messages/Library.cs` -- `LibraryItem(Path, ProfileId, Size, Date, BeingWritten, LengthSeconds = null)`: add trailing `bool Favourite = false, DateTimeOffset? Modified = null` (the file's `LastWriteTimeUtc` as UTC; null while being written). New `LibrarySetFavourite(string Path, long Size, DateTimeOffset? Modified, bool Favourite)`. `LibraryMessages.Commands` is `[]`; add `SetFavourite` as `CommandType<LibrarySetFavourite, Empty>` named `library.setFavourite` (see `FeedbackMessages.Dismiss` in `Messages/Feedback.cs:62` for the shape; `LibraryJsonContext` needs `[JsonSerializable(typeof(LibrarySetFavourite))]` and `Empty`, as `FeedbackJsonContext` has it).
- `src/ReplayBuffer.Contracts/ErrorCodes.cs` -- add `LibraryItemChanged = "library-item-changed"` beside `LibraryCorrupt` (:181), with a doc comment. `ErrorCodeException(code, message)` is in `Contracts/ErrorCodeException.cs`.
- `src/ReplayBuffer.Core/Library/LibraryActor.cs` (1314 lines; read the parts you touch) -- message records at :1265-1291, `Handle` switch :563, `Watch(bool, TaskCompletionSource)` is the reply pattern to copy. Modified-time sites: `Load` (:204, both item branches — the entry branch also carries `entry.Favourite`), `Scanned` (:345), `OnEnd` (:668), `Reconcile`'s add and size change (:900), `MoveItem` (:1069; `item with` already keeps fields). `OnBegin` (:648) creates a fresh item (an overwrite drops the old Favourite — intended). `Entry` (:1251) and `LibraryFileEntry` (:1306): add trailing `bool Favourite = false`. `Dispose` (:310): a pending command's TCS must not hang (complete it with `ErrorCodes.EngineDisconnected` or cancel when the channel is closed / `TryWrite` fails).
- `src/ReplayBuffer.Engine/EngineHost.cs:446-463` -- register `LibraryMessages.SetFavourite` beside `FeedbackMessages.Dismiss`, awaiting the actor's task; `libraryActor` is in scope there.
- `src/ReplayBuffer.App/Library/LibraryPage.cs`, `LibraryView.xaml` -- `Render` makes rows with `new LibraryRow(item.Path)` and calls `row.Show(item, game)`. Add a star column (a flat `Button`, content ★ when Favourite else ☆, tooltip "Favourite") bound to a row command. The row keeps the last shown `LibraryItem` so the page can send its identity. `AcknowledgeAsync` shows the `SendAsync` + `catch (ErrorCodeException)` pattern.
- Tests: `tests/ReplayBuffer.Core.Tests/LibraryActorTests.cs` (7.2/7.3 helpers: minimal MP4 bytes, temp folders, actor factory, `Watching` sync), `tests/ReplayBuffer.Engine.Tests/LibraryServeTests.cs` (IPC end-to-end, restart pattern in `ASavedClipIsInTheFirstSnapshotAfterARestart`), `tests/ReplayBuffer.App.Tests/LibraryPageTests.cs` (`FakeEngineSession`, `Sta.Run`), `tests/ReplayBuffer.Contracts.Tests/MessagesTests.cs` (`Samples` needs a `library.setFavourite` payload; the registry tests enumerate every command).

## Tasks & Acceptance

**Execution:**
- [x] `src/ReplayBuffer.Contracts/Messages/Library.cs`, `ErrorCodes.cs` -- the fields, command and code above, with doc comments -- contract
- [x] `src/ReplayBuffer.Core/Library/LibraryActor.cs` -- (a) set `Modified` wherever an item's `Size` is set from a `FileInfo`. (b) `public Task SetFavourite(LibrarySetFavourite args)` posts `SetFavouriteRequest(args, tcs)`; when the post fails, fault the task with `engine-disconnected`. (c) `OnSetFavourite`: key = the args' path normalised as the item keys are (relative, compared ignore-case); no item, or `Size`/`Modified` differ from the item → fault `library-item-changed`. For a non-written item, stat the file: missing → `Pend` it and fault; size or mtime differ → refresh the item (`Size`, `Modified`, and clear the length + re-queue it if the size changed, as `Reconcile` does), publish `itemChanged`, fault. Else if the flag differs set it, `_changed`, `_dirty`; complete. (d) `Entry`/`Load` save and restore `Favourite` -- R6 Favourite
- [x] `src/ReplayBuffer.Engine/EngineHost.cs` -- route `library.setFavourite` to the actor, returning `Empty.Value` -- wiring
- [x] `src/ReplayBuffer.App/Library/LibraryPage.cs`, `LibraryView.xaml` -- the star toggle: sends `SetFavourite` with the row's item `Path`, `Size`, `Modified` and `!Favourite`; swallows `ErrorCodeException` (the snapshot carries the truth). Row exposes `IsFavourite`/`Star` from `Show` -- R6 UI
- [x] `tests/ReplayBuffer.Core.Tests/LibraryActorTests.cs` -- (1) setFavourite then dispose, `Load` + new actor: the item is a Favourite; renaming the file in the folder moves it with `Favourite` true. (2) a command with an older `Modified` faults with `library-item-changed` and the item is unchanged. (3) `library.json` replaced by garbage with the `.bak` holding the Favourite: `Load` keeps it. (4) a Favourite set during `BeginWrite` survives `EndWrite`. (5) after the file's mtime is changed on disk (same size), a command with the item's old identity faults and an `itemChanged` with the new `Modified` follows -- persistence and matrix
- [x] `tests/ReplayBuffer.Engine.Tests/LibraryServeTests.cs` -- one test: a saved Clip, `library.setFavourite` over IPC, the client's snapshot shows it a Favourite -- wiring AC
- [x] `tests/ReplayBuffer.App.Tests/LibraryPageTests.cs` -- the star command sends `library.setFavourite` with the item's identity and `true`; the row's star stays unset until a snapshot with `Favourite: true` renders, then shows set -- UI AC
- [x] `tests/ReplayBuffer.Contracts.Tests/MessagesTests.cs` -- sample payload for `library.setFavourite`; extend the `LibraryItem` round-trip with `favourite` and `modified` -- contract

**Acceptance Criteria:**
- Given an indexed Clip, when `library.setFavourite` sets it, then `library.itemChanged` reports it a Favourite, and after an engine restart it is still one.
- Given a Favourite renamed outside the app, when the watcher reports the rename, then the item at the new path is a Favourite.
- Given a `library.json` that doesn't parse and a `.bak` holding a Favourite, when the engine starts, then the item is a Favourite.
- Given a command whose `modified` is older than the item's, when sent, then it is refused with `library-item-changed` and the item is unchanged.
- Given a Clip being written, when it is set as a Favourite, then it stays one after its `EndWrite`.
- Given a Library row, when its star is clicked, then `library.setFavourite` is sent with the row's identity, and the star changes only when the snapshot does.

## Implementation Notes

- `MessagesTests.Samples` covers events only, so the `library.setFavourite` payload is checked by a round-trip through `LibraryMessages.SetFavourite.ArgsType` in the `LibraryItem` test instead.
- An unexpected exception in `OnSetFavourite` can't hang the router: the `Handle` case completes the TCS with `command-failed` in a `finally` (a no-op once answered). After `Dispose` the post fails and the task faults with `engine-disconnected`; messages posted before it are drained as before.
- Matrix rows 2 and 5 share one Core test; row 3 extends `ACorruptIndexFallsBackToTheBakThenTheScan`. Two existing assertions that compared whole `LibraryItem`s now include `Modified`.

## Plan Change Log

## Review Triage Log

## Design Notes

Why `LibraryItem.Modified`: AD-3 says the identity is path + size + mtime, and only the engine stats files, so the App must learn the mtime from the snapshot. The actor compares the command against its item (what the App saw) and then against disk (what the file is now); a disk mismatch both refuses and publishes the refreshed item, so the next click works without a restart. A touch that leaves the size unchanged isn't otherwise noticed by `Reconcile`, and this keeps it from wedging the star.

Stat: `new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero)`; System.Text.Json round-trips its full tick precision.

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings, 0 errors
- `dotnet test --project tests/ReplayBuffer.Contracts.Tests -c Release --no-build`, then `ReplayBuffer.Core.Tests`, `ReplayBuffer.Engine.Tests`, `ReplayBuffer.App.Tests` -- expected: all pass
- `pwsh tools/publish-engine.ps1` -- expected: 0 IL/AOT warnings

## Auto Run Result

**Summary:** `library.setFavourite` (path, size, mtime, flag) sets or clears an item's Favourite through the LibraryActor, publishes `library.itemChanged` and saves it in `library.json`; a stale identity is refused with `library-item-changed` (a file changed on disk is refreshed and published first). `LibraryItem` gains `favourite` and `modified`. The Favourite survives a restart, an outside rename and a load from the `.bak`, and can be set while the file is being written. Each Library row has a star toggle that follows the snapshot.

**Verification:** `dotnet build ReplayBuffer.slnx -c Release`: 0 warnings. Contracts 116, Core 105, Engine 166 (20 explicit skipped), App 79 (7 explicit skipped) passed. `pwsh tools/publish-engine.ps1`: no IL/AOT warnings.

**Review:** The quick review lens wasn't run: the usage limit was reached. QA reviews the diff in its place.
