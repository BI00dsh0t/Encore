---
title: 'Group and filter by game, date and Favourite'
type: 'feature'
ticket: '5'
created: '2026-11-03'
status: 'in-progress'
baseline_revision: '6d4b48f3754c546bae04e11517079dfaac90db05'
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

**Problem:** The Library page is one flat newest-first list (7.1–7.4); R4 needs it grouped by game or by day and filtered by game, date and Favourite.

**Approach:** App-only. `LibraryPage` keeps view state (group-by, game filter, date filter, Favourites only), builds a flat, virtualized list of group headers and rows from each snapshot, and saves the view state to a small JSON file in the App instance's roaming data folder so it is remembered per instance. No engine or Contracts change.

## Boundaries & Constraints

**Always:**
- An item's game: its `ProfileId`'s live profile name when the snapshot has that profile; else the profile whose `Folder` equals the item's top-level folder (case-insensitive; scanned files carry no `ProfileId`, epic Decision); else the top-level folder name (so `Monitor Recordings` is its own group); else "Other" at the Output Folder's root. The group key is `profile:<id>` for a profile, `folder:<name>` (case-insensitive) for a folder, `other` for the root, so a renamed profile renames its group and keeps the game filter.
- Group by game: groups ordered by name (ordinal ignore case), then `Monitor Recordings`, then `Other`; rows newest first in each. Group by day: local day, newest first, headed "Today", "Yesterday", else `yyyy-MM-dd`.
- Date filter on the item's local date against the clock's local today: Today; Last 7 days = today and the 6 days before; Last 30 days = today and the 29 before; All. Filters combine (AND).
- The game filter's options are "All games" plus every game present, by group order. A selected game no longer present resets to All only when the snapshot has items (a persisted filter survives the empty first render).
- The clock is a `TimeProvider` (default `TimeProvider.System`) so tests fix "today".
- Rendering 5 000 items with grouping and filters takes under 200 ms; collections update in place (no O(n²) `IndexOf` per item), so scroll position survives snapshots.
- The list virtualizes: no outer `ScrollViewer`/`StackPanel` around the items; an `ItemsControl` (or `ListBox`) with `VirtualizingStackPanel`, `CanContentScroll`, its own `ScrollViewer`.
- View-state file failures (missing, unreadable, unparsable, unwritable) are ignored: defaults load, saves are dropped.
- Lean tests (CLAUDE.md "Tests"): one or two happy-path tests per AC.

**Never:**
- No engine, Core or Contracts change; no new `state.snapshot` field.
- No rename/delete/actions (7.6), Cleanup (7.7), settings page (7.9).
- No change to the star, thumbnail or banner behaviour.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| By game | 3 profiles' items, `Monitor Recordings\x.mp4`, `root.mp4` | groups Alpha, Beta, Gamma, Monitor Recordings, Other | — |
| Scanned in profile folder | `Alpha\s.mp4`, no ProfileId, profile Alpha has `Folder = "Alpha"` | in Alpha's group | — |
| By day | items today, yesterday, 2026-10-01 | Today, Yesterday, 2026-10-01 | — |
| Combined | game Alpha + Last 7 days + Favourites only | only Alpha's Favourites from the last 7 days | — |
| Rename | profile Alpha renamed Alpha 2, filter on Alpha | header "Alpha 2", filter kept | — |
| New item | a later snapshot adds a Beta item | it appears under Beta | — |
| Bad view file | `library-view.json` holds garbage | defaults (by game, all, all, off) | ignored |

</intent-contract>

## Code Map

- `src/ReplayBuffer.App/Library/LibraryPage.cs` -- `Render(StateSnapshot)` syncs `Rows` (all items, newest first; existing tests read `Rows`, keep it). `GameOf(item, settings)` is the game-name rule; extend it with the profile-`Folder` match and add a key function beside it. `LibraryRow.Show(item, game)` sets `Game`. Add: `LibraryGrouping { Game, Day }`, `LibraryDateFilter { All, Today, Last7Days, Last30Days }`, bindable properties `GroupBy`, `GameFilter` (string? key, null = all), `DateFilter`, `FavouritesOnly`; `Games` (`ObservableCollection<LibraryGameOption(string? Key, string Name)>`); `Entries` (`ObservableCollection<object>` of `LibraryGroupHeader(string Key){ Name }` and `LibraryRow`, reusing header instances by key); option lists for the two enums with display names. Changing any view property re-applies from the last snapshot and saves. Constructor gains optional `string? viewStateFile = null, TimeProvider? time = null`.
- `src/ReplayBuffer.App/Library/LibraryViewState.cs` (new) -- record `LibraryViewState(LibraryGrouping GroupBy, string? Game, LibraryDateFilter Date, bool FavouritesOnly)` + `Load(path)`/`Save(path)` with System.Text.Json (a `JsonSerializerContext`, enums as strings), write-temp-then-replace; any exception → defaults / no-op.
- `src/ReplayBuffer.App/Library/LibraryView.xaml` -- replace the outer `ScrollViewer`/`StackPanel` with a `DockPanel` (banner, title + a filter bar on top: Group by combo, Game combo bound to `Games` with `SelectedValuePath=Key`, Date combo, "Favourites only" check box) and the items list filling the rest, bound to `Entries`, virtualized, with `DataTemplate DataType` for `LibraryRow` (the existing row grid, unchanged) and `LibraryGroupHeader` (a SemiBold header). `xmlns:local="clr-namespace:ReplayBuffer.App.Library"`.
- `src/ReplayBuffer.App/MainViewModel.cs:27,36` -- the constructor builds `new LibraryPage(engine, LibraryThumbnails.Shell(post))`; add an optional `string? libraryViewFile = null` parameter passed through.
- `src/ReplayBuffer.App/ShellApp.cs:54` -- `new MainViewModel(_engine, ...)`: pass `Path.Combine(instance.RoamingDataFolder, "library-view.json")`.
- `src/ReplayBuffer.Contracts/Settings/SettingsDocument.cs:33` -- `GameProfile.Name`, `.Folder` (read only).
- The literal `"Monitor Recordings"` (Core's `ProfileFolders.MonitorRecordings`; App doesn't reference Core).
- Tests: `tests/ReplayBuffer.App.Tests/LibraryPageTests.cs` (`FakeEngineSession`, `SetLibrary`, `TheViewRendersWithoutBindingErrors` must still pass with the new view). Use `Microsoft.Extensions.Time.Testing.FakeTimeProvider` only if already referenced; else a tiny `TimeProvider` subclass in the test.

## Tasks & Acceptance

**Execution:**
- [ ] `src/ReplayBuffer.App/Library/LibraryViewState.cs` -- the record, load and save -- remembered per instance
- [ ] `src/ReplayBuffer.App/Library/LibraryPage.cs` -- game key/name rule, view properties, `Games`, `Entries` built in group order and synced in place, re-apply on change, persistence -- R4
- [ ] `src/ReplayBuffer.App/Library/LibraryView.xaml` -- filter bar and virtualized grouped list -- R4 UI
- [ ] `src/ReplayBuffer.App/MainViewModel.cs`, `src/ReplayBuffer.App/ShellApp.cs` -- pass the instance's view-state file -- wiring
- [ ] `tests/ReplayBuffer.App.Tests/LibraryPageTests.cs` -- fabricated items across three games, Monitor Recordings and the root, fixed clock: (1) by game gives the expected headers and rows, (2) by day gives Today/Yesterday/date, (3) game + date + Favourites combined, (4) a new item lands in its group and a renamed profile renames its group, (5) view state saved to a temp file is restored by a new page, (6) 5 000 items group and filter in under 200 ms (one `Stopwatch` around a `Render` and a filter change, after one warm-up render) -- verify

**Acceptance Criteria:**
- Given items across three games, Monitor Recordings and the root, when grouped by game, then the headers are the three game names, Monitor Recordings and Other, each over its items newest first.
- Given items on several days, when grouped by day, then the headers are Today, Yesterday and each older date, newest first.
- Given a game, a date filter and Favourites only all set, when rendered, then only that game's Favourites in that range show.
- Given a rendered Library, when a snapshot adds an item, then it shows in its game's group; when a profile is renamed, then its group header shows the new name.
- Given a view state chosen in one page, when a new page is made with the same view-state file, then it starts with that view state.
- Given 5 000 items, when rendered with grouping and filters, then it takes under 200 ms.

## Implementation Notes

## Plan Change Log

## Review Triage Log

## Design Notes

A flat `Entries` list of headers and rows (rather than `CollectionView` grouping) keeps the grouping testable on the view model and lets WPF virtualize one `VirtualizingStackPanel` without `IsVirtualizingWhenGrouping` quirks. In-place sync, O(n) when little changed:

```csharp
for (var i = 0; i < wanted.Count; i++)
{
    if (i < target.Count && ReferenceEquals(target[i], wanted[i])) continue;
    var at = IndexFrom(target, wanted[i], i + 1); // -1 when absent
    if (at >= 0) target.Move(at, i); else target.Insert(i, wanted[i]);
}
while (target.Count > wanted.Count) target.RemoveAt(target.Count - 1);
```

Use it for both `Rows` and `Entries` (replacing the per-item `Rows.IndexOf`).

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings, 0 errors
- `dotnet test --project tests/ReplayBuffer.App.Tests -c Release --no-build` -- expected: all pass (App-only change: Engine, Core, Contracts and pipeline suites are not touched; no NativeAOT publish since no engine code changes)
