---
title: 'Rename and delete, locked while being written'
type: 'feature'
ticket: '7'
created: '2026-11-04'
status: 'in-review'
baseline_revision: 'd27ee22eed37256a2d51840ea381316c33c17758'
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

**Problem:** The Library can't rename or delete an item (R6 part). Both must go through the engine's LibraryActor (AD-17) by the item's identity (AD-3), never touch a file being written, and never look like an outside change to 7.2's reconciliation.

**Approach:** Two additive Contracts commands, `library.rename { path, size, modified, newName }` and `library.delete { path, size, modified }`, and four new error codes. The actor validates them, registers its own paths so the watcher echo is ignored, renames with `File.Move` (publishing exactly `itemRemoved` + `itemAdded` with the metadata kept), and recycles through an injected Recycle Bin port backed by `IFileOperation` + `FOFX_RECYCLEONDELETE`. The Library row gets an inline Rename and a confirmed Delete, both disabled while the item is locked.

## Boundaries & Constraints

**Always:**
- `newName` is the new base name; the engine appends `.mp4` and keeps the folder. The App trims the text and strips one trailing `.mp4` (ignore-case) before sending.
- Check order, both commands: item indexed and `BeingWritten` → `file-being-written`; identity (path, size, modified) not matching the index → `library-item-changed`; written item whose file is missing or differs on disk → refreshed/pended exactly like `OnSetFavourite`, then `library-item-changed`. Rename then: invalid name → `invalid-file-name` (empty; any `Path.GetInvalidFileNameChars()`; ends with `.` or space; reserved device name CON, PRN, AUX, NUL, COM1–9, LPT1–9, compared ignore-case on the part before the first `.`); same name (ordinal) → success, nothing changes; a different file or an indexed/being-written key at the target → `file-name-taken` (a case-only rename of the same file is allowed). Delete then: the port says the volume has no Recycle Bin → `recycle-bin-unavailable`, file and item untouched.
- Rename keeps profile, date, Favourite, length and modified time (`MoveItem`); delete removes the item (`Remove`) and saves. Both `Register` every path they touch before touching it.
- A sharing violation (HRESULT `0x80070020`/`0x80070021`, e.g. the length worker's FFmpeg read without `FILE_SHARE_DELETE`) is retried up to 10 times, 50 ms apart; any other failure, or the last retry, → `command-failed`, index unchanged.
- The Recycle Bin port: `Func<string, bool>? recycle` on the actor (false = no Recycle Bin; throws `IOException` with the HRESULT on failure). Without one, delete → `recycle-bin-unavailable`. The real one never deletes permanently: it requires `GetDriveType` = fixed and `SHQueryRecycleBin` success, and passes `FOF_WANTNUKEWARNING`; an aborted operation is reported as no Recycle Bin.
- Tests only touch files in a temp folder they create; the real-bin test restores its file and leaves nothing behind.

**Never:**
- No permanent delete (no `File.Delete` of an item); no rename of the folder or extension; no batch actions; no auto-retry of a refused command; no change to Cleanup, Favourite, grouping, thumbnails or the shell actions of 7.6; no breaking Contracts change.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Rename | written `Game\a.mp4`, Favourite, newName `b` | file `Game\b.mp4`; events exactly removed `a`, added `b` (Favourite kept) | — |
| Delete | written item, port recycles | port called with the absolute path; `itemRemoved` | — |
| Locked Clip / Recording | between BeginWrite and EndWrite (empty reservation; fMP4 growing in an open stream) | refused, file bytes unchanged | `file-being-written` |
| Bad name | `a:b`, `CON`, `x.` | refused | `invalid-file-name` |
| Taken | `b.mp4` exists | refused | `file-name-taken` |
| Stale | modified older than the index | refused | `library-item-changed` |
| No bin | port returns false | refused, file stays | `recycle-bin-unavailable` |

</intent-contract>

## Code Map

- `src/ReplayBuffer.Contracts/Messages/Library.cs` -- add `LibraryRename(string Path, long Size, DateTimeOffset? Modified, string NewName)`, `LibraryDelete(string Path, long Size, DateTimeOffset? Modified)`, `[JsonSerializable]` for both, `CommandType<_, Empty>` `Rename` ("library.rename") and `Delete` ("library.delete"), appended to `Commands`. Pattern: `SetFavourite`.
- `src/ReplayBuffer.Contracts/ErrorCodes.cs` -- add `FileBeingWritten` "file-being-written", `InvalidFileName` "invalid-file-name", `FileNameTaken` "file-name-taken", `RecycleBinUnavailable` "recycle-bin-unavailable", each with a doc comment and in `All` (ContractsTests check `All` matches the declared constants).
- `src/ReplayBuffer.Core/Library/LibraryActor.cs` -- add ctor param `Func<string, bool>? recycle = null` (last); public `Task Rename(LibraryRename)` / `Task Delete(LibraryDelete)` like `SetFavourite` (line ~289) with request messages handled in `Handle` with the same `finally` fallback; handlers beside `OnSetFavourite` (~995), factoring its identity/disk check into a shared helper. Reuse `Register` (~1234), `MoveItem` (~1179), `Remove` (~1201), `Refuse`, `TryKey`. The internal `Move`/`OnMove` (~891) stays as is. Update the class doc.
- `src/ReplayBuffer.Platform.Windows/RecycleBin.cs` (new) + `NativeMethods.txt` -- `public static unsafe class RecycleBin { public static bool Recycle(string path) }`: runs on a new STA thread (`CoInitializeEx(COINIT_APARTMENTTHREADED)`), pre-checks the volume (`GetDriveType`, `SHQueryRecycleBin`), `CoCreateInstance` of `FileOperation`, `SHCreateItemFromParsingName` → `IShellItem`, `SetOperationFlags(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOFX_EARLYFAILURE | FOFX_RECYCLEONDELETE | FOF_WANTNUKEWARNING)`, `DeleteItem`, `PerformOperations`, `GetAnyOperationsAborted`. CsWin32 style as `Platform.Desktop/ShellThumbnail.cs` (allowMarshaling false).
- `src/ReplayBuffer.Engine/EngineHost.cs` -- `new LibraryActor(...)` (~276) passes `recycle: RecycleBin.Recycle`; register `LibraryMessages.Rename`/`Delete` beside `SetFavourite` (~483).
- `src/ReplayBuffer.App/Library/LibraryPage.cs` -- ctor gains `Func<string, bool>? confirmDelete = null` last (default: Yes/No `MessageBox` "Move “<name>” to the Recycle Bin?" owned by the view's window). `LibraryRow` gains `IsRenaming`, `RenameText`, `RenameCommand` (starts editing with the base name; CanExecute `!IsLocked`), `CommitRenameCommand`, `CancelRenameCommand`, `DeleteCommand` (CanExecute `!IsLocked`); raise both CanExecute on lock change (as `_copyCommand`). Sends go through `_engine.SendAsync` with the row's `Item` identity; a refusal sets `ActionError` (row's path) with a message per code: being written "That file is still being written.", invalid "That name can't be used for a file.", taken "A file with that name already exists.", no bin "That drive has no Recycle Bin, so the file wasn't deleted.", changed "That file changed; try again.", else "Couldn't rename the file." / "Couldn't delete the file.". Success clears it.
- `src/ReplayBuffer.App/Library/LibraryView.xaml` (+ `.xaml.cs`) -- name cell: `TextBlock` hidden while `IsRenaming`, `TextBox` bound to `RenameText` shown, Enter → commit, Escape → cancel, LostFocus → commit; focus + select-all when shown (code-behind `IsVisibleChanged`). Context menu adds Rename and Delete after Copy.
- Tests: `tests/ReplayBuffer.Core.Tests/LibraryActorTests.cs` (`Harness`, `Mp4()`; extend `Harness` with a `recycle` arg), `tests/ReplayBuffer.App.Tests/LibraryActionsTests.cs` (`FakeEngineSession.Reply`/`Sent`), `tests/ReplayBuffer.Engine.Tests/LibraryServeTests.cs` (`SaveAsync`, `NextAsync`), `tests/ReplayBuffer.Platform.Windows.Tests/` (new `RecycleBinTests.cs`).

## Tasks & Acceptance

**Execution:**
- [x] `src/ReplayBuffer.Contracts/ErrorCodes.cs`, `Messages/Library.cs` -- codes, payloads, commands -- AD-22 additive
- [x] `src/ReplayBuffer.Core/Library/LibraryActor.cs` -- Rename/Delete handlers, recycle port, retry -- R6, AD-17
- [x] `src/ReplayBuffer.Platform.Windows/RecycleBin.cs`, `NativeMethods.txt` -- shell recycle -- R6
- [x] `src/ReplayBuffer.Engine/EngineHost.cs` -- wire port and router -- R6
- [x] `src/ReplayBuffer.App/Library/LibraryPage.cs`, `LibraryView.xaml(.cs)` -- inline rename, confirmed delete, errors, lock -- R6 UI
- [x] Tests -- Core: (1) rename moves the file, keeps the Favourite, events exactly removed+added (500 ms quiet, as `TheActorsOwnMoveIsPublishedOnce`); (2) delete with a fake port that moves the file into the test's own bin folder: item removed; port returning false: `recycle-bin-unavailable`, file kept; (3) a Clip reservation and a fake Recording writer growing a fragmented MP4 (`ftyp`, `moov`, then `moof`+`mdat` appended through a `FileShare.Read | FileShare.Delete` stream) between BeginWrite and EndWrite: rename and delete refused `file-being-written`, bytes unchanged; (4) `a:b`, an existing name and a stale modified time refused with their codes. App: rename sends `LibraryRename` with the identity and stripped base name and shows a refusal's message; Delete calls the confirmation and sends nothing on Cancel, `LibraryDelete` on OK; both disabled while locked; `TheViewRendersWithoutBindingErrors` still passes. Engine: rename over IPC updates the snapshot. Platform.Windows: `RecycleBin.Recycle` of a temp file returns true, the file is gone and its `$I`/`$R` pair is found in the user's `$Recycle.Bin` by original path; the test restores the `$R` to the temp folder and deletes the `$I`.

**Acceptance Criteria:**
- Given a written item, when `library.rename` is sent with its identity and a valid free name, then the file has the new name in the same folder, and the snapshot shows the item under the new path with its Favourite, and no other `library.*` event follows.
- Given a written item, when `library.delete` is sent, then the file is in the Recycle Bin (restorable to its original path) and the item leaves the snapshot.
- Given a Clip or a Recording between BeginWrite and EndWrite, when either command is sent, then it fails with `file-being-written`, and the Library row's Rename and Delete are disabled.
- Given the Library row's Delete, when the confirmation is cancelled, then nothing is sent.

## Implementation Notes

- `OnSetFavourite`'s identity and disk check is now `TryMatch`, shared by rename and delete; the three public commands post through one `Post` helper. Rename and delete check `BeingWritten` first (`RefuseBeingWritten`), then `TryMatch`.
- The sharing-violation retry (`Retried`) wraps both `File.Move` and the recycle port; it sleeps on the actor thread (at most ~500 ms).
- `RecycleBin.Recycle` runs on a new thread marked STA and also calls `CoInitializeEx(COINIT_APARTMENTTHREADED)` (S_FALSE is fine); any COM failure is rethrown as an `IOException` carrying its HRESULT.
- `RecycleBinTests` runs in the default suite (it is fast); it restores its `$R` and deletes its `$I`.

## Plan Change Log

## Review Triage Log

## Design Notes

- Registration covers the echo: the watcher's Renamed/Deleted for a registered path is `Ignored`, and the expiry `Reconcile` finds the index already matching disk, so it publishes nothing.
- Identity check before the being-written check would turn a locked item whose write just ended into `library-item-changed`; checking `BeingWritten` first keeps the more useful code for the common case.
- The Recycle Bin `$I` file (v2): 8-byte version (2), 8-byte size, 8-byte FILETIME, 4-byte char count, UTF-16 original path; its `$R` twin has the same suffix. The test reads `C:\$Recycle.Bin\<current user SID>\$I*` (SID from `WindowsIdentity.GetCurrent().User`).

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings, 0 errors
- `dotnet test --project tests/ReplayBuffer.<X>.Tests -c Release --no-build` for Contracts, Core, Platform.Windows, Engine, App -- expected: all pass
- `pwsh tools/publish-engine.ps1` -- expected: 0 IL/AOT warnings

**Manual checks (if no CLI):**
- Human Check Queue: rename a Clip inline and delete one through the confirmation in the real App, then restore it from the Recycle Bin; check how the prompts look and feel.
