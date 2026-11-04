---
title: 'Library and Cleanup settings page'
type: 'feature'
ticket: '9'
created: '2026-11-04'
status: done
baseline_revision: 'e4c8eab65bd50cb3afbec44e79fea6af1276d8af'
route: 'full'
route_source: 'auto'
risk: 'low'
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

**Problem:** Nothing in the App sets `outputFolder` or the `cleanup` limits (R8), and Cleanup (7.8) deletes files permanently, so turning a limit on must first show what it would delete and ask (R7's confirmation).

**Approach:** A "Library and Cleanup" Settings page (last in `SettingsPages.Create`) edits the Global `outputFolder` and `cleanup { maxBytes, maxAgeSeconds }` keys through `SettingsPage.SendAsync`. Turning a limit on, or lowering one, first calls `cleanup.preview` with the new limits and shows a Yes/No confirmation with its count and size; only Yes sends the `settings.update`.

## Boundaries & Constraints

**Always:**
- Size limit in GB (10^9 bytes, decimals allowed, invariant or current culture), > 0 and ≤ 1 000 000 GB; age limit in whole days, 1 to 36 500. Anything else (empty, zero, negative, non-numeric) sends nothing and shows the rendered value again with a `Message`.
- Each limit is a checkbox (off by default: the key's null) plus a text box holding its value. The text box keeps a draft while the limit is off (defaults 100 GB, 30 days, or the last saved value); ticking sends the draft; editing the text while on sends the new value.
- Confirmation needed when the new value of a limit is set and the current one is null or larger (bytes) / longer (age). Raising a limit or turning it off sends at once, no preview, no prompt.
- The confirmation text names the count and size from `LibraryCleanupPlan` (count 0 says nothing would be deleted now), says deletion is permanent (not the Recycle Bin), and that Favourites and files being written are never deleted. Cancel (or a failed preview, shown as a `Message`) sends nothing and re-shows the saved values.
- The page always shows "Cleanup never deletes a Favourite or a file being written." and, when `state.CleanupBlocked`, that Cleanup is off until the rebuilt index is acknowledged on the Library tab.
- Output Folder: shows the effective folder (`state.LibraryFolder`, else the setting, else "the default folder under Videos") and its current size and file count (sum of `state.Library` sizes). Browse opens a folder picker; choosing a full path different from the current setting asks (Yes/No) with "The Library will list only the new folder. Existing files aren't moved." and sends `outputFolder` on Yes. "Use default" sends null with the same prompt; hidden/disabled when already null.
- The confirm prompt is a port (`Func<string, bool>`, settable for tests); default is a Yes/No `MessageBox` with a warning icon over the view's window, as `LibraryPage.ConfirmDelete`.
- Lean tests; tests never touch real folders (no files at all are needed: the engine is faked).

**Never:** Engine or Contracts changes (they already hold `cleanup.preview`, the keys and the snapshot fields); moving files; Per-Game-Profile overrides (both keys are not overridable); sending a limit op before the user confirms.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Enable size | cleanup null, tick size, draft "50" | preview(50e9, null) sent; prompt shows its count and size; Yes → one op `cleanup {maxBytes:50e9, maxAgeSeconds:null}` | — |
| Cancel | as above, prompt No | no settings.update; checkbox unticked again; cleanup stays null | — |
| Lower | maxBytes 50 GB saved, type "40" | preview + prompt again | — |
| Raise / off | maxBytes 50 GB, type "60", or untick | op sent, no preview, no prompt | — |
| Bad value | "0", "-1", "abc", "" for size; "0", "1.5" for days | nothing sent, `Message` set, text shows saved value | — |
| Folder | Browse → `D:\Clips` | prompt; Yes → op `outputFolder "D:\\Clips"` | non-full path → `Message`, nothing sent |
| Preview fails | engine throws | nothing sent, `Message` | — |

</intent-contract>

## Code Map

- `src/ReplayBuffer.App/Settings/LibrarySettingsPage.cs` (new) -- `internal sealed class LibrarySettingsPage(SettingsSender sender, IEngineSession engine) : SettingsPage(sender)`, Title "Library and Cleanup". Pattern: `ReplayBufferSettingsPage` (text props with validation + `Refuse`-style re-show, `ShowState(StateSnapshot)` called from `MainViewModel.Refresh`, `OnRender` reads `SettingsValues.Global(document, Keys.Cleanup / Keys.OutputFolder)`). Ops: `SettingsValues.SetGlobal(Keys.Cleanup, new CleanupSettings(...))`, `SettingsValues.SetGlobal(Keys.OutputFolder, path)`. Preview: `engine.SendAsync(LibraryMessages.CleanupPreview, new LibraryCleanupPreview(maxBytes, maxAge))`, catch `ErrorCodeException` and other exceptions → `Message`. Sizes via `ReplayBufferSettingsPage.FormatBytes`. Properties: `OutputFolderText`, `FolderSizeText`, `CanUseDefaultFolder`, `ChooseOutputFolder(string)`, `UseDefaultFolderCommand` (`RelayCommand` in `Observable.cs`), `SizeLimitEnabled`, `SizeLimitText`, `AgeLimitEnabled`, `AgeLimitText`, `CleanupBlockedText` (null when not blocked), `internal Func<string, bool> Confirm { get; set; }`. While a preview is awaited the page ignores further limit edits (a `_busy` flag).
- `src/ReplayBuffer.App/Settings/LibrarySettingsView.xaml(.cs)` (new) -- modelled on `ReplayBufferSettingsView.xaml` (ScrollViewer root, Label style, `BooleanToVisibilityConverter`); Browse handler in code-behind uses `Microsoft.Win32.OpenFolderDialog` (`ShowDialog(Window.GetWindow(this))`) then `page.ChooseOutputFolder(dialog.FolderName)`, as `GameProfilesView.Browse`. Show `Message` like the other pages.
- `src/ReplayBuffer.App/Settings/SettingsPage.cs` -- append `new LibrarySettingsPage(sender, engine)` to `SettingsPages.Create`; mention it in the doc.
- `src/ReplayBuffer.App/MainViewModel.cs` -- `public LibrarySettingsPage LibrarySettings { get; }` (`Pages.OfType<...>().Single()`), call `LibrarySettings.ShowState(state)` in `Refresh` before the page render loop.
- `tests/ReplayBuffer.App.Tests/TrackLayoutPageTests.cs:472` -- page-order list gains "Library and Cleanup" at the end.
- `tests/ReplayBuffer.App.Tests/LibrarySettingsPageTests.cs` (new) -- pattern `ReplayBufferSettingsPageTests` (`FakeEngineSession`, `Reply` switching on the command: `SettingsMessages.Update` → `SettingsUpdateResult`, `LibraryMessages.CleanupPreview` → a `LibraryCleanupPlan`; `new MainViewModel(_engine, a => a())`; binding-error render that saves `library-cleanup-settings.png`).

## Tasks & Acceptance

**Execution:**
- [x] `src/ReplayBuffer.App/Settings/LibrarySettingsPage.cs` -- the view model -- R7 confirmation, R8
- [x] `src/ReplayBuffer.App/Settings/LibrarySettingsView.xaml(.cs)` -- the view and folder picker -- R8
- [x] `src/ReplayBuffer.App/Settings/SettingsPage.cs`, `src/ReplayBuffer.App/MainViewModel.cs` -- register and feed the page
- [x] `tests/ReplayBuffer.App.Tests/LibrarySettingsPageTests.cs`, `TrackLayoutPageTests.cs` -- (1) enable size → preview with the new limit, prompt text holds count and size, update only on Yes; Cancel sends no update and cleanup stays null; (2) lowering asks again, raising and unticking don't; (3) bad values (size 0, -1, abc; days 0, 1.5) send nothing; (4) choosing a folder sends `outputFolder`, folder size text sums the snapshot's items; (5) the view renders without binding errors and saves a PNG

**Acceptance Criteria:**
- Given no cleanup limit, when the size limit is ticked, then `cleanup.preview` is sent and the prompt shows its count and size, and `settings.update` is sent only after Yes; after No, `cleanup` is still null and nothing was sent.
- Given a saved limit, when it is lowered, then the prompt shows again; when it is raised or turned off, then the update is sent with no prompt.
- Given a zero, negative or non-numeric limit, when entered, then nothing is sent and the page says why.
- Given the Browse picker returns a folder, when the prompt is confirmed, then `settings.update` sets `outputFolder` to it.
- Given the 0-warning Release build, when App.Tests runs, then all pass.

## Implementation Notes

- Saved (`_cleanup`) and shown (`_shown`) limits are kept apart, so Cancel restores the saved ones (the first test run caught this).
- `FeedbackSoundsPageTests` assumed Feedback Sounds was the last page; it now only checks the page is present.
- Test: `LibrarySettingsPageTests` (6 tests), writing `library-cleanup-settings.png` to the App test output folder.

## Plan Change Log

## Review Triage Log

### 2026-11-04 — Review pass
- verdicts: 2 findings — high 0, medium 1, low 1, false 0, maybe-false 0
- findings:
  - `[medium]` `[patch]` A confirmed update the engine refused or conflicted on left the page showing the unsaved limits: `OnRender` skipped `ShowLimits` while busy and nothing re-rendered. — `finally` now clears busy and shows the saved limits; test `AConfirmedLimitTheEngineRefusesShowsTheSavedLimitsAgain`.
  - `[low]` `[patch]` A `cleanup` change arriving while the prompt was open would be overwritten by the whole `next` value sent at the new revision. — The confirmed path now refuses with a reload note when the saved limits changed since the preview.

## Design Notes

- The App can't know if a lower limit deletes anything without asking the engine, so every enable/lower previews; count 0 still prompts (deletion becomes permanent from then on).
- Raising a limit can't delete more than the current one, so no prompt (the ticket).
- Confirming the folder change too is a small unattended default: the ticket says the page "says" it, and a prompt is the cheapest way to make sure it is read.

## Verification

**Before any build or test:** `%USERPROFILE%\Documents\repos\ReplayBuffer\.bench-running` must not exist.

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings, 0 errors
- `dotnet test --project tests/ReplayBuffer.App.Tests -c Release --no-build` -- expected: all pass

## Auto Run Result

**Summary:** A "Library and Cleanup" Settings page (last in the list) edits the Global `outputFolder` and `cleanup` limits. It shows the Library's folder and its size and file count, a Browse folder picker and Use default, each behind a prompt saying the Library will list only the new folder and existing files aren't moved. Each limit is a checkbox and a text box (GB above 0 up to 1 000 000; whole days 1 to 36 500). Turning a limit on or lowering one calls `cleanup.preview` and asks Yes/No with the count and size, that deletion is permanent and that Favourites and files being written are never deleted; only Yes sends. Raising or turning off sends at once. Bad values, Cancel, a failed preview or a refused send leave the saved limits shown. No Contracts or engine change.

**Files:** `src/ReplayBuffer.App/Settings/LibrarySettingsPage.cs`, `LibrarySettingsView.xaml(.cs)` (new); `SettingsPage.cs`, `MainViewModel.cs` (register, feed state); tests `LibrarySettingsPageTests.cs` (new), `TrackLayoutPageTests.cs`, `FeedbackSoundsPageTests.cs` (page list).

**Review:** quick lens, 2 findings, both patched (1 medium, 1 low); nothing deferred or rejected. Follow-up review not recommended (no high, one medium).

**Verification:** `dotnet build ReplayBuffer.slnx -c Release` 0 warnings; App.Tests 121 passed, 12 explicit skipped. Other projects untouched, so not run; no engine change, so no NativeAOT publish.

**HITL (Human Check Queue):** 7.9 — Settings > Library and Cleanup by hand: Browse to a folder and confirm (the Library lists only it), set a size limit just under the folder's size, read the prompt's count and size, confirm, and see the oldest non-Favourite Clip go; check how the page and prompts read. Proxy: `LibrarySettingsPageTests` through the view model with a fake engine, and the rendered `library-cleanup-settings.png`.

**Residual risks:** the real folder picker and message box weren't driven by hand.

