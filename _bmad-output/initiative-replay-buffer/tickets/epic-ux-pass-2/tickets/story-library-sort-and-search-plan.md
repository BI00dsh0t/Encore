---
title: 'Library sort and search'
type: 'feature'
ticket: '3'
created: '2026-11-05'
status: done
baseline_revision: '33686fa'
route: 'oneshot'
route_source: 'auto'
risk: 'low'
review: 'quick'
review_source: 'pinned'
lenses_ran: [quick]
review_loop_iteration: 0
followup_review_recommended: false
context: []
warnings: []
deferred: []
---

<intent-contract>

## Intent

**Problem:** The Library can only be read newest first and grouped by game or day; with many Clips the builder can't find one by name or see the largest (epic 10 R4, R8).

**Approach:** `LibraryPage` gains a Sort choice, Group by None and a search text, applied in `Apply` on top of the existing filters; `LibraryViewState` keeps Sort (with Group by) and reads an older file with the defaults. The view's filter row gains the Search box (first) and the Sort combo (after Group by), Ctrl+F and Esc, and a "No items match" message distinct from the empty Library's.

</intent-contract>

## Implementation Notes

- **Route:** about 250 lines across the Library's files, so `full` by size, but implemented in the session that built 10.2 and holds its context (the selection model this builds on); a fresh implementer would only re-derive it. Reviewed with the quick lens as usual.
- **Spec:** the ticket and EXPERIENCE.md "Sort and search". Sorts: Newest first (default; date ↓, path), Oldest first (date ↑, path), Name (natural, case-insensitive), Largest first, Longest first (unknown length last) and Game (name A–Z); the last four tie by date ↓ then path. Group by Day lists its days oldest first under Oldest first. Search: a case-insensitive substring of the file name, on top of the other filters; the view's binding applies it at most every 150 ms (`Delay=150`), the page at once.
- **Selection:** 10.2's set of paths is untouched; sort and search only change `Entries`, so a searched-out item stays selected and is counted "hidden by filters", and Ctrl+A and Shift ranges follow the shown order.

- **Filter row:** each caption sits with its combo in a small StackPanel, so a wrap never strands a caption at a line's end.
- **Found, not fixed (outside the Library):** the Gaming dark TextBox template applies a box's Padding twice (on `PART_ContentHost` and again inside its ScrollViewer), so the search text starts about 60 px in under Gaming dark (30 px under Fluent); every Gaming text box has the same doubled inset. Reported to the coordinator; the fix belongs in `GamingDarkTheme.xaml`.

## Verification

- `dotnet build ReplayBuffer.slnx -c Release` -- 0 warnings
- `dotnet test --project tests/ReplayBuffer.App.Tests -c Release --no-build`, and Core and Contracts -- all pass
- `library-search-fluent.png` and `library-search-gaming-dark.png` beside the App test binary show the search box with text, Sort and Group by None.

## Plan Change Log

## Review Triage Log

### 2026-11-05 — Review pass
- verdicts: 2 findings — high 0, medium 1, low 1, false 0, maybe-false 0
- findings:
  - `[medium]` `[patch]` Esc and the clear button read the delayed `SearchText`, so text typed in the last 150 ms couldn't be cleared — fixed: the view clears the box and pushes the binding at once; the button shows from the box's own text.
  - `[low]` `[patch]` Ctrl+F from an open rename box moved focus and committed the rename; any modifier mix with Ctrl matched — fixed: Ctrl alone, and not from a TextBox other than the search box.

## Auto Run Result

- **Summary:** Sort (six choices, remembered), Group by None, search by name (150 ms delay, clear button, Esc, Ctrl+F) and a "No items match" message; 10.2's selection follows the shown order and keeps searched-out items.
- **Files:** `LibraryPage.cs` (sort, search, `LibraryRowComparer`), `LibraryViewState.cs` (`LibrarySort`, `Group by None`), `LibraryView.xaml(.cs)`, `LibraryTheme.xaml` (search glyph, hint, clear button), tests `LibrarySortSearchTests.cs` (new) and `LibraryThemeTests.cs` (search PNGs, no-results).
- **Review:** 2 patched (1 medium, 1 low), none deferred or rejected. Follow-up review: false.
- **Verification:** 0-warning Release build; App 173 passed (20 explicit skipped), Core 161, Contracts 128; `library-search-*.png` checked by eye.
- **Residual risks:** the Gaming dark text-box inset (above); the delayed binding and keys are checked by inspection, not by a test.
