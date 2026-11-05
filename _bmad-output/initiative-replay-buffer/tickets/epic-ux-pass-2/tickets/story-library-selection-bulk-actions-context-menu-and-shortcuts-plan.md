---
title: 'Library selection, bulk actions, context menu and shortcuts'
type: 'feature'
ticket: '2'
created: '2026-11-04'
status: 'in-progress'
baseline_revision: 'bea6f2131967325f9ee01cb7246efb3898e29ee0'
route: 'full'
route_source: 'auto'
risk: 'high'
review: ''
review_source: ''
lenses_ran: []
review_loop_iteration: 0
followup_review_recommended: false
context:
  - '{project-root}/CLAUDE.md'
  - '{project-root}/_bmad-output/initiative-replay-buffer/tickets/epic-ux-pass-2/ux-pass-2/EXPERIENCE.md'
warnings: [oversized]
deferred: []
---

<intent-contract>

## Intent

**Problem:** The Library list (story 7.x, restyled in 9.4) acts on one row at a time through a per-row context menu. The builder wants Windows Explorer's multi-select and bulk actions (epic 10 R1–R3, R8), with Favourites never deleted, single or many (the builder approved this at 10.1, changing 7.7).

**Approach:** `LibraryPage` owns the selection as a set of item paths plus an anchor and a focused path, and exposes page-level commands that act on the *shown* selected rows by sending today's per-item engine commands one at a time (AD-24, AD-3). `LibraryView` becomes a `ListBox` whose own selection is unused: the view routes mouse and keys to the page, draws selection and focus from the rows' flags, and adds the command bar and one context menu bound to the page.

## Boundaries & Constraints

**Always:**
- Spec: EXPERIENCE.md "Library list" (Selection model, Empty-selection state and the command bar, Delete, Context menu, Keyboard shortcuts) and DESIGN.md addendum "Library row, selected" and "Library command bar". Where the plan is silent, they rule.
- Engine commands stay one per item (`library.setFavourite`, `library.delete`, `library.rename`), each with the identity last shown (path, size, modified). No Contracts or engine change (AD-22, AD-24).
- The App never opens media (AD-1): Open, Show in Explorer, Copy and Copy to folder go through `ILibraryShell` by absolute path, after checking only that each file exists.
- Every colour, corner and type through `DynamicResource` on epic 9 token keys, working in both themes and a live switch (AD-23). Dialogs via `Prompts.YesNo` (themed), never `MessageBox`.
- The list stays one virtualized (`VirtualizingStackPanel`, Recycling, pixel scroll) list of group headers and rows; headers are never selectable or focusable.
- Lean tests (CLAUDE.md): happy paths per the ticket's verify list; nothing slow in the default run. 0-warning Release build.

**Never:**
- No bulk engine message, no change to `LibraryActor` or any engine/Core code.
- Don't touch Audio Tracks, settings pages, `MainWindow`, `Prompts`/`PromptDialog`, or nav code (story 10.4 runs in parallel). Theme dictionaries may only gain `RbLibrary*` keys.
- No search box, sort, Ctrl+F, welcome card or kind badge (stories 10.3 / 10.6). No checkboxes on rows, no drag and drop.
- Don't rely on `ListBox.SelectedItems`; with virtualization it desyncs. The page's set is the only selection.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Ranges across a header | Grouped by game: header A, a1, a2, header B, b1. Click a2, Shift+click b1; then Ctrl+click a1 | {a2, b1}; then {a1, a2, b1}, anchor a1 | — |
| Keyboard range | Focus a1, Shift+Down twice | {a1, a2, b1}, focus b1, anchor a1 | — |
| Filter keeps selection | a1, b1 selected; Game filter = A | a1 and b1 still selected; bar reads "2 selected (1 hidden by filters)"; actions act on a1 only | — |
| New item / rename / removal | Snapshot adds an item; the App renames selected a1 to "best"; a snapshot drops selected a2 | Selection unchanged by the add; `A\best.mp4` selected (and focused if a1 was); a2 leaves the selection, focus moves to the next shown row, else the previous | Rename refused → selection stays on the old path |
| Bulk Favourite | 3 shown selected, 1 already a Favourite | Bar reads "Favourite"; 2 `library.setFavourite(…, true)` sent; with all 3 Favourites it reads "Remove from Favourites" and sends 3 with false | A refused send is ignored (the snapshot is the truth), as today |
| Bulk delete | 5 shown selected: 1 Favourite, 1 being written, 3 others | One confirmation: "Move 3 items ({size}) to the Recycle Bin?" + a line "1 Favourite and 1 file being saved are kept."; on Yes three `library.delete` in shown order | One refused (e.g. `file-being-written`) → ActionError "Deleted 2 of 3. That file is still being written." (today's refusal text, most common code wins) |
| Single Favourite delete | Only a Favourite selected; Delete key, command or menu | Nothing asked, nothing sent; Delete is disabled with a tooltip; the key sets ActionError "Favourites can't be deleted. Remove it from Favourites first." | — |
| Copy | 3 shown selected, 1 being written, 1 hidden by a filter | One `ILibraryShell.Copy` call with the 2 shown, written files' absolute paths | Missing files are left out; all missing → "That file no longer exists." |
| Show in Explorer | Selected files in 2 folders | One `ShowInExplorer(folder, files)` per folder; with more than 3 folders, one `OpenFolder(LibraryFolder)` instead | Shell throws → "Couldn't show the files." |
| Copy to folder | ≥1 copyable shown selected | `pickFolder()`; on a folder, one `CopyToFolder(paths, folder)`; on cancel (null) nothing | Shell throws → "Couldn't copy the files." |
| Long delete | 12 deletable | Bar reads "Deleting k of 12…" during the sends; commands' CanExecute false until done | — |

</intent-contract>

## Code Map

- `src/ReplayBuffer.App/Library/LibraryPage.cs` -- the page view model (862 lines). Keep `Render`, `Apply`, `Sync` (in-place collection sync: a new item never resets the list), grouping/filters, `GameKeyOf`, `RefusalText`, `SizeText`, `SendActionAsync`/`Fail`/`ClearActionError`. Today each `LibraryRow` carries Open/ShowInExplorer/Copy/Delete commands wired to `Act`/`DeleteAsync`; replace those four with page-level commands. Keep the row's `FavouriteCommand` (the star), `RenameCommand`/`CommitRenameCommand`/`CancelRenameCommand` (the inline editor) and `Item` (identity). `confirmDelete` is `Func<string,bool>` given the file name; change it to receive the whole question text.
- `src/ReplayBuffer.App/Library/LibraryView.xaml(.cs)` -- one `ItemsControl x:Name="List"` of `Entries` with DataTemplates for `LibraryGroupHeader` and `LibraryRow`; the row card (`RbLibraryRowStyle` Border) has the per-row ContextMenu and a double-click MouseBinding; rename TextBox handlers in code-behind. Names `List`, `Banner`, `EmptyMessage` are used by tests; keep them.
- `src/ReplayBuffer.App/Library/LibraryShell.cs` -- `ILibraryShell` (Open, ShowInExplorer(path), Copy(path)), `WindowsLibraryShell`, `LibraryClipboard.Create(path)`.
- `src/ReplayBuffer.App/Library/StarToggle.cs` -- the star; a click only runs its command. Must not change selection.
- `src/ReplayBuffer.App/Theme/LibraryTheme.xaml` -- the Library's styles for both themes (`RbLibraryRowStyle`, etc.). Add the new styles here.
- `src/ReplayBuffer.App/Theme/FluentTheme.xaml` / `GamingDarkTheme.xaml` -- token dictionaries. Fluent's `RbAccentSubtleBrush` is `SubtleFillColorSecondary` (grey, ~6 %), which is why selected rows look faint under Fluent with a grey accent (10.1 renders). Gaming's is `#1A2DD4BF`. Keys: `AccentFillColorDefaultBrush`, `FocusStrokeColorOuterBrush`, `SubtleFillColorSecondaryBrush`, `CardBackgroundFillColorDefaultBrush`, `CardStrokeColorDefaultBrush`, `ControlCornerRadius`, `OverlayCornerRadius`, `RbNavItemCornerRadius` (0 in Gaming: square bar), `BodyStrongTextBlockStyle`, `CaptionTextBlockStyle`, `TextFillColorSecondaryBrush`.
- `src/ReplayBuffer.Platform.Desktop/ShellFolder.cs` + `NativeMethods.txt` -- CsWin32 (`allowMarshaling: false`, raw pointers). `SelectInExplorer(path)` uses `SHParseDisplayName` + `SHOpenFolderAndSelectItems`.
- `src/ReplayBuffer.App/Settings/LibrarySettingsView.xaml.cs` -- example of `OpenFolderDialog` use (`Microsoft.Win32.OpenFolderDialog`).
- `src/ReplayBuffer.App/Prompts.cs` -- `Prompts.YesNo(view, message, caption, image)`; read only.
- `src/ReplayBuffer.App/MainViewModel.cs:40` -- constructs `LibraryPage(engine, thumbnails, viewFile)`; optional parameters keep it unchanged.
- Tests: `tests/ReplayBuffer.App.Tests/LibraryActionsTests.cs` (row actions, FakeShell, clipboard), `LibraryPageTests.cs`, `LibraryThemeTests.cs` (loads MainWindow in both themes, binding-error listener, `Save` PNG helper, `RowCard` assumes `ContentPresenter` containers), `LibraryEndToEndTests.cs` (Explicit/Heavy, uses row commands and a `RecordingShell : ILibraryShell`; must still compile). `FakeEngineSession` (`Sent`, `Reply`, `State`), `Sta.Run`, `Sta.Pump`, `BindingErrors`, `WpfCollection`.

## Tasks & Acceptance

**Execution:**
- [ ] `src/ReplayBuffer.App/Library/LibraryShell.cs` -- `ILibraryShell` becomes: `Open(path)`, `ShowInExplorer(string folder, IReadOnlyList<string> files)`, `OpenFolder(string folder)`, `Copy(IReadOnlyList<string> paths)`, `CopyToFolder(IReadOnlyList<string> paths, string folder)`. `LibraryClipboard.Create(IReadOnlyList<string>)` puts every path in one file drop with `DROPEFFECT_COPY`. `WindowsLibraryShell.CopyToFolder` runs the shell's copy on a background STA thread so the App stays responsive (owner = the main window's handle, captured on the UI thread); the shell shows its own progress, conflict and error dialogs. `OpenFolder` is ShellExecute on the folder.
- [ ] `src/ReplayBuffer.Platform.Desktop/ShellFolder.cs` (+ `NativeMethods.txt`) -- add `SelectInExplorer(string folder, IReadOnlyList<string> files)` (folder pidl + `ILFindLastID` children → `SHOpenFolderAndSelectItems`) and a `ShellFileCopy.Copy(IReadOnlyList<string> files, string folder, nint owner)` over `SHFileOperationW` `FO_COPY` with `FOF_ALLOWUNDO` (double-null-terminated lists); a user cancel is not an error.
- [ ] `src/ReplayBuffer.App/Library/LibraryPage.cs` -- the selection model, bar state and page commands, per the Design Notes. New optional ctor parameter `Func<string?>? pickFolder` (default: `OpenFolderDialog` titled "Copy to folder" over the view's window); `confirmDelete` now receives the full question (default `Prompts.YesNo(_view, text, "Delete", Question)`). `LibraryRow` gains `IsSelected`, `IsFocused` (observable, set only by the page) and `IsRow => true`; `LibraryGroupHeader` gains `IsRow => false`. Remove the row's Open/ShowInExplorer/Copy/Delete commands and `LibraryAction`. Update the class summary for 10.2.
- [ ] `src/ReplayBuffer.App/Library/LibraryOverflowPanel.cs` (new) -- a horizontal Panel for the bar's actions: measures every child; when they don't fit, children marked with attached `Overflows="True"` (Copy to folder…, Show in Explorer) and the child marked `IsMore="True"` swap: overflowing ones get `Visibility.Hidden` (Hidden still measures, so no layout loop) and are arranged at zero width, and More is shown; otherwise More is Hidden. Collapsed children (Open/Rename when not single) take no space.
- [ ] `src/ReplayBuffer.App/Library/LibraryView.xaml(.cs)` -- per Design Notes "View": the command bar, the `ListBox`, one ContextMenu, input routing.
- [ ] `src/ReplayBuffer.App/Theme/LibraryTheme.xaml` (+ the two theme dictionaries for one `RbLibraryRowSelectedBrush` each) -- selected row fill and 3 px accent bar, focus ring, `ListBoxItem` container style, command bar card style. Under Gaming `RbLibraryRowSelectedBrush` = the accent-subtle colour `#1A2DD4BF`; under Fluent a brush on the Windows accent colour at about 25 % opacity (a `DynamicResource` colour, as `RbAccentSubtleBrush` does), so a grey accent still gives a visibly stronger fill than the 6 % grey (DESIGN's grey-accent rule, made stronger; record it in Implementation Notes).
- [ ] `tests/ReplayBuffer.App.Tests/LibrarySelectionTests.cs` (new) -- the ticket's verify, one test each: Shift/Ctrl ranges across a header (+ Shift+Down); selection survives a filter change, a new item and the App's own rename, and a removed item leaves it (focus moves on); bulk Favourite sends one per changing item; bulk delete of five → one confirmation naming 3 and the kept line, three deletes, then a refusal reported "Deleted 2 of 3. …"; a single Favourite's delete sends and asks nothing; Copy is one file drop of the shown written files; Show in Explorer groups by folder.
- [ ] `tests/ReplayBuffer.App.Tests/LibraryActionsTests.cs`, `LibraryEndToEndTests.cs`, `LibraryPageTests.cs` -- move to the page-level API (select the row, then run the page command) and the new shell shape; `TheClipboardData…` takes a list; keep each test's intent.
- [ ] `tests/ReplayBuffer.App.Tests/LibraryThemeTests.cs` -- `RowCard` finds the card inside a `ListBoxItem`. In both themes: select two rows and focus one; assert the command bar is visible with "2 selected", a selected card's background is `RbLibraryRowSelectedBrush` and differs from an unselected card's, the list is still virtualized, and no Library binding error; save `library-selected-fluent.png` and `library-selected-gaming-dark.png` beside the test binary (keep the existing PNGs).

**Acceptance Criteria:**
- Given the Library view loaded under Fluent and under Gaming dark with two rows selected, when it renders, then the selected rows show the selected fill and accent bar, the focused row shows the focus ring, the bar shows the count and actions in 40 px, and no binding or resource error is logged.
- Given rows selected with nothing hidden, when the user right-clicks a row outside the selection, then only that row is selected and the menu lists Open (Enter, single only), Favourite/Remove from Favourites, Copy (Ctrl+C), Copy to folder…, Show in Explorer, a separator, Rename (F2, single only, disabled while being written) and Delete (Del).
- Given focus in the list, when the user presses Enter, Delete, F2, Ctrl+C, Ctrl+A, Space, Ctrl+Space or Esc, then each does what EXPERIENCE.md's shortcut table says, and typing a letter doesn't jump (text search off); keys typed in the rename box reach only the box.
- Given nothing selected, when the bar renders, then it shows "{n} items · {size}" of the shown list and "Click to select. Ctrl or Shift selects more." with no action buttons.

## Implementation Notes

## Plan Change Log

## Review Triage Log

## Design Notes

**Page state.** `_selected` (`HashSet<string>`, OrdinalIgnoreCase), `_anchor`, `_focus` (paths). `ShownRows`: the rows of `Entries` in order (rebuilt in `Apply`). "Shown selected" = shown rows in `_selected`, in shown order; every action uses only these. After any change, set each row's `IsSelected`/`IsFocused`, raise the bar properties and the commands' `CanExecuteChanged`.

**Selection API** (public/internal methods the view calls, all unit-testable):
- `Click(row, ctrl, shift)`: plain → only it, anchor = focus = it; Ctrl → toggle, anchor = focus = it; Shift → selection = range anchor..row in shown order (headers skipped), focus = row; Ctrl+Shift → add that range. No shown anchor → the row is the anchor.
- `MoveFocus(LibraryMove move, int pageRows, ctrl, shift)` for Up/Down/PageUp/PageDown/Home/End: plain → select only the target (anchor = focus); Shift → range from anchor; Ctrl → focus only; clamp to the shown rows.
- `SelectAll()` (selection = every shown row), `ToggleFocusedSelection()` (Ctrl+Space), `ClearSelection()`, `PrepareContextMenu(row)` (row not selected → `Click(row)` plain), `FocusedRow`.
- In `Render`: paths gone from the Library leave `_selected`; a pending own rename (recorded before sending: old → `dir\NewName.mp4`) moves selection, anchor and focus to the new path when it appears, and is dropped on a refusal; a removed focused row → the next row of the previous shown order still present, else the previous. A filter change hides but keeps.

**Bar properties:** `HasSelection`; `SelectionText` ("{n} selected", n = every selected item still in the Library; while deleting, "Deleting {k} of {n}…"); `HiddenText` ("({m} hidden by filters)" or empty); `SummaryText` ("{n} items · {size}", "1 item"); `FavouriteLabel`; `IsSingle`; `DeleteToolTip` (why Delete is disabled). Commands: `OpenCommand` (exactly one shown selected; opens even while being written, as today), `FavouriteSelectionCommand`, `CopyCommand`, `CopyToFolderCommand`, `ShowInExplorerCommand` (being-written items included), `RenameCommand` (one, not being written → that row's `RenameCommand`), `DeleteCommand` (≥1 deletable), `ClearSelectionCommand`. All are disabled while a delete batch runs. Also `OpenFocused()` (Enter, double-click) and `ToggleFocusedFavourite()` (Space).

**Delete text.** One deletable → `Move “{name}” to the Recycle Bin?`; more → `Move {n} items ({SizeText(sum)}) to the Recycle Bin?`. When some were left out, a second line: `{f} Favourite(s) and {w} file(s) being saved are kept.` (drop the empty half; "is kept" for a single one). Send in shown order, awaiting each; tally failures by error code; on any failure ActionError = `Deleted {ok} of {n}. {RefusalText(most common code)}` (for n = 1, today's single refusal text alone).

**View.** Under the action-error line, a 40 px bar card (`RbLibraryCommandBarStyle`: `CardBackgroundFillColorDefaultBrush`, `CardStrokeColorDefaultBrush`, `ControlCornerRadius`, padding 8,0 / 12,0), always visible:
- nothing selected → the summary (body) and the how-to caption (caption, text-secondary);
- selected → the count (body-strong), the hidden text (caption), the actions in `LibraryOverflowPanel` (Open, Favourite, Copy, Copy to folder…, Show in Explorer, Rename, Delete, More ⋯ with a menu holding Copy to folder… and Show in Explorer), and a subtle ✕ (`AutomationProperties.Name` "Clear selection") docked right.

The list: `ListBox x:Name="List"`, transparent, no border, `IsTextSearchEnabled="False"`, Recycling virtualization, pixel scroll. Its item container style is the Library's `ListBoxItem` style: a template of a 2 px border (transparent; `FocusStrokeColorOuterBrush` when the row's `IsFocused` and the list `IsKeyboardFocusWithin`), around the content presenter, with `FocusVisualStyle` null. Headers' containers (`IsRow` false) aren't focusable or hit-testable. The row card's selected trigger sets `RbLibraryRowSelectedBrush` and shows a 3 px `AccentFillColorDefaultBrush` bar on the left (corner `RbNavItemCornerRadius`); hover over a selected row keeps the selected fill.

Input (code-behind):
- `PreviewMouseLeftButtonDown` on the list: from a `ButtonBase` or `TextBox` inside a row → do nothing (the star and the rename box work as today); on a row → `Click`, or `OpenFocused` on `ClickCount == 2`, then focus its container, handled; on a header → handled; on a ScrollBar → nothing; elsewhere → `ClearSelection`.
- `PreviewMouseRightButtonDown` on a row → `PrepareContextMenu`.
- `ContextMenuOpening`: no shown selection → cancel; from the keyboard (Menu key, Shift+F10) → `PrepareContextMenu(FocusedRow)` first.
- `PreviewKeyDown` (skipped when the source is a TextBox): the arrows, Page Up/Down (rows per page from the viewport and a realized row's height), Home and End (with Shift/Ctrl) → `MoveFocus`, then scroll the focused row into view and focus its container; Ctrl+A, Ctrl+Space, Space, Enter, Delete, F2, Ctrl+C and Esc (clears the selection) as the shortcut table says.
- When the list itself gets keyboard focus (Tab), move it to the focused row's container, else the first row's.
- The rename box keeps its own Enter/Esc bindings and LostFocus commit.

The ContextMenu items use `InputGestureText` for the keys shown.

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings, 0 errors
- `dotnet test --project tests/ReplayBuffer.App.Tests -c Release --no-build` -- expected: all pass (failure names visible)
- `dotnet test --project tests/ReplayBuffer.Core.Tests -c Release --no-build` and `--project tests/ReplayBuffer.Contracts.Tests` -- expected: all pass (unchanged code; the coordinator's brief asks for them)

**Manual checks (if no CLI):**
- Open `library-selected-fluent.png` and `library-selected-gaming-dark.png` in `tests/ReplayBuffer.App.Tests/bin/Release/net10.0-windows10.0.26100.0/`: selected rows visibly differ from the others in both, the focused row has the ring, the bar shows the count and actions.
