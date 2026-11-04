---
title: 'Rename and delete, locked while being written'
type: 'feature'
ticket: '7'
created: '2026-11-04'
status: 'built'
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
- Verify (step 3): a probe showed `IFileOperation` reports a file held open without delete sharing as `0x80270027` (the copy engine's sharing violation), so `RecycleBin` maps `0x80270027`/`0x80270028` to `0x80070020` for the actor's retry; `RecycleBinTests` checks it with a held `FileStream` (`83d8ee3`).
- Review patch (`7893a12`): `FOF_WANTNUKEWARNING` is replaced by a native `IFileOperationProgressSink` whose `PreDeleteItem` aborts a delete that the shell won't recycle (`TSF_DELETE_RECYCLE_IF_POSSIBLE` clear), reported as no Recycle Bin. This deviates from the contract's named flag in order to keep its rule (never delete permanently) without UI or a blocked actor. The refusal path is untested: it needs a drive whose Recycle Bin is turned off.

## Plan Change Log

## Review Triage Log

### 2026-11-04 — Review pass
- verdicts: 6 findings — high 0, medium 1, low 4, false 1, maybe-false 0
- findings:
  - `[medium]` `[patch]` A disabled Recycle Bin, or a file over its quota, made `FOF_WANTNUKEWARNING` show an ownerless "permanently delete?" dialog from the engine, which blocked the actor; answering Yes deleted the file permanently. — Replaced by a progress sink whose `PreDeleteItem` aborts when `TSF_DELETE_RECYCLE_IF_POSSIBLE` is clear, returning false (`recycle-bin-unavailable`); no UI.
  - `[low]` `[patch]` An unchanged commit on `clip.MP4` renamed it to `clip.mp4`, because the engine appended a lowercase `.mp4`. — The new name now keeps the item's own extension.
  - `[low]` `[patch]` A non-I/O exception from the injected recycle port escaped `Run` and ended the actor thread. — `Retried` now catches any exception, reports it, and refuses with `command-failed`.
  - `[low]` `[patch]` A failing `RecycleBinTests` run left its file in the user's real Recycle Bin. — The restore now runs in a `finally`.
  - `[low]` `[reject]` A rename or delete that fails after `Register` hides that path's watcher events for 5 s. — Unlikely in everyday use (it needs an outside same-size rewrite inside that window), and the fix adds unregister logic.
  - `[false]` `[reject]` The rename test's 500 ms wait doesn't cover the 5 s registration expiry, so extra events could follow. — `Reconcile` at expiry publishes nothing: the source key is no longer indexed, so `Remove` is a no-op, and the target is indexed at the same size, so it returns early.

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

## Auto Run Result

**Summary:** The engine's LibraryActor now has `library.rename` (base name only; the folder and the extension's own spelling stay; metadata kept; exactly `itemRemoved` + `itemAdded`) and `library.delete` (Recycle Bin only, through `IFileOperation` + `FOFX_RECYCLEONDELETE` and a sink that refuses any permanent delete). Both register their paths so the watcher echo is ignored. Both are refused with `file-being-written` while a Clip or Recording is being written, with `library-item-changed` on a stale identity, and with `invalid-file-name` / `file-name-taken` / `recycle-bin-unavailable`. A sharing violation (e.g. the length read) is retried for up to about 500 ms. The Library row has an inline Rename and a Delete that asks first; both are disabled while the item is locked, and a refusal shows a short message.

**Files:** Contracts `ErrorCodes.cs` (4 codes) and `Messages/Library.cs` (2 commands and their payloads, additive); Core `LibraryActor.cs` (handlers, `TryMatch`, retry, recycle port); Platform.Windows `RecycleBin.cs` (new) and `NativeMethods.txt`; Engine `EngineHost.cs` (wiring and router); App `LibraryPage.cs`, `LibraryView.xaml(.cs)` (inline rename, confirmed delete); tests in Core (4), App (2), Engine (1) and Platform.Windows (`RecycleBinTests`, real bin, self-restoring).

**Review:** quick lens, 6 findings: 4 patched (1 medium, 3 low), 1 low rejected (register-before-failed-op: unlikely, the fix adds complexity), 1 false rejected (expiry publishes nothing). Nothing deferred. Follow-up review not recommended (no high patched, and only one medium).

**Verification:** `dotnet build ReplayBuffer.slnx -c Release` 0 warnings, 0 errors. Default runs: Contracts 118/118, Core 115/115, Platform.Windows 26/26, Engine 168 passed / 24 explicit skipped, App 104 passed / 10 explicit skipped, 0 failed. `pwsh tools/publish-engine.ps1` clean (no IL/AOT warnings). Heavy not run (no capture, encode, mux or audio change).

**Residual risks:** The sink's refusal path (Recycle Bin off, or a file over its quota) is untested by automation. The inline editor's focus and LostFocus behaviour, and the look of the prompts, are for the builder to check by hand. A locked file blocks the actor for up to about 500 ms.
