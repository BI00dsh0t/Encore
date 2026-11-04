---
title: 'Open, show in Explorer and copy for Discord'
type: 'feature'
ticket: '6'
created: '2026-11-03'
status: 'in-progress'
baseline_revision: 'f2ff2fe2a20af05401b41727ae43dc7c37a58249'
route: 'full'
route_source: 'auto'
risk: 'medium'
review: ''
review_source: ''
lenses_ran: []
review_loop_iteration: 0
followup_review_recommended: false
context: []
warnings: []
deferred: []
---

<intent-contract>

## Intent

**Problem:** The Library lists items but can't act on them; R5 needs Open (default player), Show in Explorer (folder with the file selected) and Copy (a file drop that pastes into Discord as an attachment).

**Approach:** App-side shell actions on the item's absolute path (AD-1) behind a small `ILibraryShell` port, exposed as row commands from a context menu and a double-click (Open). A file that no longer exists shows a short error on the page instead of calling the shell.

## Boundaries & Constraints

**Always:**
- Absolute path = `Path.Combine(state.LibraryFolder, item.Path)` from the last snapshot.
- Open: ShellExecute verb `open` (`Process.Start` with `UseShellExecute = true`, `Verb = "open"`). Show in Explorer: `SHParseDisplayName` + `SHOpenFolderAndSelectItems(pidl, 0, null, 0)` + `ILFree`. Copy: WPF `Clipboard.SetDataObject(data, copy: true)` where `data` holds `DataFormats.FileDrop` (CF_HDROP) with exactly the one absolute path and `"Preferred DropEffect"` as a 4-byte `MemoryStream` of `DROPEFFECT_COPY` (1).
- Open and Show in Explorer work on an item being written; Copy is disabled (`CanExecute` false, `Execute` a no-op) until `EndWrite`, and its `CanExecuteChanged` fires when the lock changes.
- Before any action the page checks `File.Exists` (no contents read); missing → `ActionError` = "That file no longer exists." and no shell call. A shell failure (any exception from the port) → `ActionError` = "Couldn't <open|show|copy> the file." The error clears on the next successful action, or when a Render no longer has that row.
- Lean tests; nothing in the default run launches a player, Explorer, or touches the real clipboard.

**Never:**
- No engine, Core or Contracts change; the App never opens the file's contents.
- No rename/delete/Cleanup; one item per action.
- No change to grouping, filters, star, thumbnails or banner.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Open written | existing file, double-click or menu | shell Open(abs path) | — |
| Show written | existing file | shell ShowInExplorer(abs path) | — |
| Copy written | existing file | shell Copy(abs path) | — |
| Being written | `BeingWritten = true` | Open/Show work; Copy CanExecute false, no call | — |
| Missing file | path not on disk | no shell call; `ActionError` set | short error |
| Shell throws | port throws | `ActionError` set | caught |

</intent-contract>

## Code Map

- `src/ReplayBuffer.App/Observable.cs:30` -- `RelayCommand(Action execute)` always executable; extend additively with an optional `Func<bool>? canExecute` and a `RaiseCanExecuteChanged()` that fires a real `CanExecuteChanged` (existing callers unchanged).
- `src/ReplayBuffer.App/Library/LibraryPage.cs` -- `LibraryPage` ctor `(engine, thumbnails?, viewStateFile?, time?)`: add `ILibraryShell? shell = null` last (null → `WindowsLibraryShell`). `Render` keeps `state.LibraryFolder` in a field. Rows are created at line ~161 `new LibraryRow(item.Path, r => _ = SetFavouriteAsync(r))`; pass an action callback. `LibraryRow` (line ~482): add `OpenCommand`, `ShowInExplorerCommand`, `CopyCommand` (CanExecute `!IsLocked`; raise when `IsLocked` changes in its setter). Add `ActionError` (string?, bindable) and `HasActionError` on the page.
- `src/ReplayBuffer.App/Library/LibraryShell.cs` (new) -- `internal interface ILibraryShell { void Open(string path); void ShowInExplorer(string path); void Copy(string path); }`, `WindowsLibraryShell` (the real one), and `static DataObject LibraryClipboard.Create(string absolutePath)` building the FileDrop + Preferred DropEffect data object (pure, testable).
- `src/ReplayBuffer.Platform.Desktop/ShellFolder.cs` (new) -- `public static unsafe class ShellFolder { public static bool SelectInExplorer(string path) }`, CsWin32 style as `ShellThumbnail.cs` (pointer signatures, `allowMarshaling: false`); add `SHParseDisplayName`, `SHOpenFolderAndSelectItems`, `ILFree` to `NativeMethods.txt`. Returns false on a failed HRESULT; the shell port turns false into an exception.
- `src/ReplayBuffer.App/Library/LibraryView.xaml` -- the `LibraryRow` DataTemplate's root `Grid`: add `Background="Transparent"` (hit-testing), `<Grid.InputBindings><MouseBinding Gesture="LeftDoubleClick" Command="{Binding OpenCommand}"/></Grid.InputBindings>`, and a `ContextMenu` with Open, Show in Explorer, Copy (bound to the row commands). Below the filter bar (DockPanel.Dock Top) a red `TextBlock` bound to `ActionError`, visible by `HasActionError`.
- `src/ReplayBuffer.App/MainViewModel.cs:33` -- unchanged call is fine (default shell = Windows).
- Tests: `tests/ReplayBuffer.App.Tests/LibraryPageTests.cs` (`FakeEngineSession`, `_engine.State`, `Sta.Run`, `[Collection(nameof(WpfCollection))]`). `TheViewRendersWithoutBindingErrors` must keep passing with the new bindings.

## Tasks & Acceptance

**Execution:**
- [ ] `src/ReplayBuffer.App/Observable.cs` -- optional canExecute on `RelayCommand` -- Copy disabled while written
- [ ] `src/ReplayBuffer.Platform.Desktop/NativeMethods.txt`, `ShellFolder.cs` -- Explorer select -- R5
- [ ] `src/ReplayBuffer.App/Library/LibraryShell.cs` -- port, Windows impl, clipboard data object -- R5, AD-1
- [ ] `src/ReplayBuffer.App/Library/LibraryPage.cs` -- row commands, existence check, `ActionError` -- R5
- [ ] `src/ReplayBuffer.App/Library/LibraryView.xaml` -- context menu, double-click, error line -- R5 UI
- [ ] `tests/ReplayBuffer.App.Tests/LibraryPageTests.cs` (or a new `LibraryActionsTests.cs`) -- with a temp folder and a fake shell: (1) Open, Show and Copy pass the absolute path; while being written Open works and Copy is disabled and makes no call; (2) a missing file sets `ActionError` and calls nothing; (3) `LibraryClipboard.Create` holds exactly the path as FileDrop and Preferred DropEffect bytes `01 00 00 00`; (4) Heavy explicit: `WindowsLibraryShell.Copy` on the real clipboard, read back with `Clipboard.GetFileDropList()` and `"Preferred DropEffect"` -- verify

**Acceptance Criteria:**
- Given a written item, when Open, Show in Explorer or Copy is chosen, then the shell port gets the item's absolute path.
- Given an item being written, when the row renders, then Copy is disabled and sends nothing, and Open still calls the shell; once written, Copy is enabled.
- Given an item whose file is gone, when an action is chosen, then the page shows a short error and the shell is not called.
- Given an absolute path, when the clipboard data is built, then it holds exactly that path as CF_HDROP and Preferred DropEffect is DROPEFFECT_COPY.

## Implementation Notes

## Plan Change Log

## Review Triage Log

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings, 0 errors
- `dotnet test --project tests/ReplayBuffer.App.Tests -c Release --no-build` -- expected: all pass
- `pwsh tools/publish-engine.ps1` -- expected: 0 IL warnings (Platform.Desktop is compiled into the engine)

**Manual checks (if no CLI):**
- Human Check Queue: double-click opens the default player, Show in Explorer selects the file, a copied Clip pastes into Discord as a playable attachment.
