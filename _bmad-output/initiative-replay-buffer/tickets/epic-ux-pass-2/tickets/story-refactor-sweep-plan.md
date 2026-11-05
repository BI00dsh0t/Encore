---
title: "Refactor sweep"
ticket: 7
status: review
---

# Plan: 10.7 Refactor sweep

**Intent:** cleanup only, with no behaviour change, scoped at the start from the epic's plans, their deferred review findings and the leftovers the ticket names. Before removing anything, I grepped the ticket tree for later tickets (epic 11's 11.5 and 11.6, epic 8's 8.9 to 8.11). Nothing removed is named there. 11.3's files (the orchestrator's monitor paths, the Media compositor, `Platform.Capture`) weren't touched; they're left to 11.5.

## Scope and how each item ended

| Item | Source | Outcome |
|---|---|---|
| The old Track Layout view's styles and view-model members | ticket | **Closed:** 10.5 rewrote the page and view whole. The scan found one unused member, `TrackLayoutPage.AppliesHint` (the save bar has its own text), now removed. No orphaned style keys remain. |
| The Recordings page's leftovers | ticket | **Closed:** the page and view went in 10.9. Stale comments naming it (in `FakeEngineSession` and `RecordingsEndToEndTests`) now name the Hotkeys page's screen picker. |
| The old Game Profiles view | ticket | **Closed:** 10.10 replaced it. Its last helper, `SettingsPage.Embedded`, which stripped a hosted page's ScrollViewer, is removed: a game's page no longer hosts the Clips view (it has its own editors), so `ReplayBufferSettingsPage.CreateView` builds the main view only. `MainViewModel`'s comments now say Audio Tracks and Games. |
| Per-item action code beside the bulk path | ticket | **Closed, nothing to remove:** a single Favourite and the bulk Favourite share `SendFavouriteAsync`, and every delete goes through `DeleteAsync(list)`. |
| Unused App members | scan (members named only once) | **Closed:** `MainWindow.IsNavNarrow` removed. The other hits are framework overrides, attached-property accessors and design-token keys, all kept. |
| The glossary over strings set in C# | 10.11's QA, the note on this ticket | **Closed:** `GlossaryTests.NoUserFacingStringSetInCodeUsesAnInternalTerm` scans the string literals of the user-facing sources: status, tray, Overlay, welcome card, the settings pages and pickers, the engine's warning reporters, and Core's Feedback router. It skips log, exception and comment lines. It found one miss, the interrupted-Recording text's "fragment", now reworded. |
| The Fluent selected fill may be faint in Windows light mode | 10.2 review, deferred (unverified) | **Recorded:** it needs a light-mode render on the builder's PC; it's added to the epic's closing check (10.8). |
| Rows don't expose `SelectionItemPattern` | 10.2 review, rejected | **Recorded:** a later accessibility pass. |
| The folder picker's path has no middle ellipsis | 10.9 deviation | **Recorded:** a read-only TextBox keeps the path selectable, with the full path in the tooltip; a custom trimming control is a later polish. |
| The card name is a plain TextBox, not edit-on-click | 10.5 deviation | **Recorded:** later polish; F2 and Rename work. |
| Per-game folders checked against the cache folder | 10.9 follow-up | **Closed in 10.10:** no per-game folder is exposed (AD-21, AD-4). |

## Verification

- 0-warning Release build, and every default suite: Contracts, Core, Media, the Platform suites, Engine, App and Bench. The NativeAOT publish (the engine's interrupted-Recording text changed).
- The smoke list, through the existing default tests:
  - a batch delete: `LibraryActionsTests`;
  - a sort and a search: `LibrarySortSearchTests`;
  - a preset saved: `TrackLayoutPageTests`' preset and save tests;
  - a game's own Clip length set and reset: `GameProfilesPageTests.ChangeForThisGameCopiesTheMainValueAndResetAsksThenRemovesIt`;
  - a cache folder picked: `ReplayBufferSettingsPageTests.TheCacheFolderIsEnabledOnlyUnderDiskAndSendsRingPathAndTimeBetweenSaves`.

## Log

- 2026-11-05: swept and verified; READY FOR QA.
