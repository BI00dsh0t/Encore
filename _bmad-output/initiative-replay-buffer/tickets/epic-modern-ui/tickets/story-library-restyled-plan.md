---
title: 'Library restyled'
type: 'feature'
ticket: '4'
created: '2026-11-04'
status: done
baseline_revision: '995f9df6d87c55f1d93cff545e8071873f826d27'
route: 'full'
route_source: 'auto'
risk: 'medium'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
followup_review_recommended: false
context: ['{project-root}/CLAUDE.md']
warnings: ['oversized']
deferred: []
---

<intent-contract>

## Intent

**Problem:** The Library view (`src/ReplayBuffer.App/Library/LibraryView.xaml`) still has 9.2-era hard-coded colours (`#E6E6E6`, `#A0A0A0`, `Gray`, `#D4A017`, `#FFF4E5`, `#E0A040`, `#C42B1C`), its own "Library" heading under the shell's page title, a plain CheckBox for Favourites and flat rows. EXPERIENCE.md and DESIGN.md (story 4: Library row, Group header, Star toggle, filters, banner, empty state) want it restyled in both themes (R5 Library part, R6).

**Approach:** Restyle only the view: one new theme-independent dictionary `Theme/LibraryTheme.xaml` whose styles take every colour, corner and type from the existing token keys by `DynamicResource`, merged by one line in `ThemeService.Apply`; rewrite `LibraryView.xaml` on it. The only view-model-adjacent addition is a view converter for the row's "game · date · size" line.

## Boundaries & Constraints

**Always:**
- Every colour, brush, corner and type comes from a token key with `DynamicResource` (the keys in `Theme/GamingDarkTheme.xaml` lines 13–64, which Fluent also defines). No hex, no named colour, anywhere in the Library XAML.
- Keep every behaviour and binding: double-click Open; context menu Open, Show in Explorer, Copy, Rename, Delete (same commands, same headers); inline rename (Enter/leaving commits, Escape cancels, `OnRenameVisibleChanged`/`OnRenameLostFocus` unchanged); `FavouriteCommand` (the star only sends `library.setFavourite`; `IsChecked` bound **OneWay** to `IsFavourite`); Group by / Game / Date combos with their labels; `FavouritesOnly` binding with the text "Favourites only"; `ActionError` text; corrupt banner text and `AcknowledgeCommand` shown while `IsCleanupBlocked`; path tooltip on the name; the list stays one `ItemsControl` with `VirtualizingStackPanel`, Recycling, Pixel scroll, `CanContentScroll=True`.
- Existing Library tests (`LibraryPageTests`, `LibraryActionsTests`, `SurfacesThemeTests`, `ThemeTests`) pass unchanged.

**Never:**
- No change to `LibraryPage.cs`, `LibraryViewState.cs`, Contracts, the engine, `MainWindow.xaml`, the shell dictionaries or any settings view (9.5 runs in parallel on those). No card grid. No new wording except the empty-state line "No Clips or Recordings yet.".

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Empty Library | no items, no filter (Game all, Date All, Favourites off) | centred "No Clips or Recordings yet." in `text-secondary`; filters still shown | — |
| Filter matches nothing | items exist, filter on, no matches | empty list, no message | — |
| Missing thumbnail | `HasThumbnail` false | placeholder `fill-control` with ▶ in `text-secondary` | — |
| Being written | `IsLocked` true | "Saving…" caution pill; no length badge | — |
| Corrupt index | `IsCleanupBlocked` true | caution info bar with existing text and Acknowledge | — |

</intent-contract>

## Code Map

- `src/ReplayBuffer.App/Library/LibraryView.xaml` -- the view to rewrite. Its row `DataTemplate` for `LibraryRow` and header template for `LibraryGroupHeader` live in `UserControl.Resources`; keep the `BooleanToVisibilityConverter` keyed `Visible`.
- `src/ReplayBuffer.App/Library/LibraryView.xaml.cs` -- rename focus/commit handlers; keep as is.
- `src/ReplayBuffer.App/Library/LibraryPage.cs` -- read only. `LibraryPage`: `IsCleanupBlocked`, `ActionError`/`HasActionError`, `AcknowledgeCommand`, `Entries` (headers and rows), `GroupOptions`/`GroupBy`, `Games`/`SelectedGame`, `GameFilter` (null = all), `DateOptions`/`DateFilter` (`LibraryDateFilter.All`), `FavouritesOnly`. `LibraryRow` (line ~643): `Name`, `Path`, `Game`, `Date`, `Size` (empty while locked), `Length` (empty while unknown/locked), `Thumbnail`/`HasThumbnail`, `IsLocked`, `Lock`, `IsFavourite`, `FavouriteCommand`, `Open/ShowInExplorer/Copy/Rename/Delete/CommitRename/CancelRenameCommand`, `IsRenaming`, `RenameText`. `LibraryGroupHeader.Name`.
- `src/ReplayBuffer.App/Theme/SharedTheme.xaml` -- already has `RbCardStyle`, `RbInfoBarCautionStyle` (ContentControl; content is the message plus optional action) and `RbStarToggleStyle` (ToggleButton, ★ glyph, off `text-secondary`, on accent text). Reuse them.
- `src/ReplayBuffer.App/Theme/FluentTheme.xaml`, `GamingDarkTheme.xaml` -- both define `RbSectionHeaderStyle` (Gaming uppercases), `RbSmallCornerRadius`, `ControlCornerRadius`, `OverlayCornerRadius`. Gaming styles ToggleButton, ComboBox, TextBox, ContextMenu and MenuItem implicitly; Fluent uses its built-ins. Token → key map: surface-card `CardBackgroundFillColorDefaultBrush`, stroke-card `CardStrokeColorDefaultBrush`, fill-control `ControlFillColorDefaultBrush`, fill-control-hover `ControlFillColorSecondaryBrush`, fill-on-image `ControlOnImageFillColorDefaultBrush`, stroke-divider `DividerStrokeColorDefaultBrush`, text-primary/secondary `TextFillColorPrimaryBrush`/`TextFillColorSecondaryBrush`, caution/caution-background `SystemFillColorCautionBrush`/`SystemFillColorCautionBackgroundBrush`, danger `SystemFillColorCriticalBrush`.
- `src/ReplayBuffer.App/Theme/ThemeService.cs` -- `Apply` (line ~108) builds the merged list; `ShellTheme.Place(merged, theme)` is 9.3's line. Add **one** line beside it for the Library dictionary (9.5 adds its own; keep the diff to that line).
- `src/ReplayBuffer.App/Theme/ShellTheme.cs` -- pattern for a dictionary class with code-behind constructor (`partial class ... { ctor => InitializeComponent(); }`) and a static `Place`.
- `tests/ReplayBuffer.App.Tests/ShellTests.cs` -- pattern for an off-screen `MainWindow` in both themes via `ThemeService.ForWindow(window).Apply(...)`, `Sta.Run`, `Sta.Pump`, `window.CloseForQuit()`, and `Save(window, name)` writing a PNG beside the test binary. `BindingErrors.cs` collects binding trace output. `ThemeTests` already checks every `DynamicResource` key in the App's XAML resolves in both themes.

## Tasks & Acceptance

**Execution:**
- [ ] `src/ReplayBuffer.App/Theme/LibraryTheme.xaml` (+ `LibraryTheme.cs` with ctor and `Place(IList<ResourceDictionary>)` that adds one instance if absent) -- styles: `RbLibraryRowStyle` (Border: surface-card, 1 px stroke-card, `OverlayCornerRadius`, padding 8, bottom margin 6; hover fill-control-hover; stays at hover while its ContextMenu is open), `RbLibraryGroupHeaderStyle` (header text via `RbSectionHeaderStyle` in text-secondary, 12 top gap, then a 1 px stroke-divider rule filling the row, vertically centred), thumbnail 128×72 clipped to `RbSmallCornerRadius` with placeholder fill-control and ▶ `Path` in text-secondary, length badge (fill-on-image, text-primary 12, `RbSmallCornerRadius`, padding 6,1, bottom-right, hidden when `Length` empty), lock pill (caution-background, caution text, 12, `RbSmallCornerRadius`, padding 6,1), name in 14 SemiBold text-primary, meta line in 12 text-secondary, empty-state text, action error in danger. -- one place for the Library's look.
- [ ] `src/ReplayBuffer.App/Theme/ThemeService.cs` -- one line in `Apply` after `ShellTheme.Place`: `LibraryTheme.Place(merged);`.
- [ ] `src/ReplayBuffer.App/Library/LibraryMetaConverter.cs` -- `IMultiValueConverter` joining the non-empty strings among Game, Date, Size with " · ". -- the row's second line.
- [ ] `src/ReplayBuffer.App/Library/LibraryView.xaml` -- rewrite on those styles: drop the "Library" heading (the shell's page title is it); filters WrapPanel first (captions in text-secondary, combos as today, then a `ToggleButton` "★ Favourites only" with `AutomationProperties.Name="Favourites only"` bound TwoWay to `FavouritesOnly`); the corrupt banner as `RbInfoBarCautionStyle` with Acknowledge on the right; `ActionError`; then the list (row = card: thumbnail with badge | name or rename TextBox, meta line, lock pill | `RbStarToggleStyle` with `Command=FavouriteCommand`, `IsChecked={Binding IsFavourite, Mode=OneWay}`, `ToolTip="Favourite"`); the empty-state TextBlock (`x:Name="EmptyMessage"`) centred over the list area, visible only when `Entries.Count` is 0, `GameFilter` null, `DateFilter` All and `FavouritesOnly` false (MultiDataTrigger). Name the list `x:Name="List"`.
- [ ] `tests/ReplayBuffer.App.Tests/LibraryThemeTests.cs` -- the three Acceptance Criteria below, as one or two Sta tests (lean tests); the empty-state check can use a bare `LibraryView` with a `LibraryPage` built as `LibraryPageTests` does.

**Acceptance Criteria:**
- Given the Main App on the Library page with fabricated items (a Favourite, a being-written item, an item with no thumbnail) and `CleanupBlocked`, when it is shown under Fluent and then Gaming dark, then the Library view loads with no binding error or missing resource, the banner is visible, the being-written row shows "Saving…", the Favourite's star is checked, the list's items panel is a `VirtualizingStackPanel` with virtualization on, and a PNG of the window is saved beside the test binary for each theme (`library-fluent.png`, `library-gaming-dark.png`).
- Given an empty Library with no filter, when the view loads, then "No Clips or Recordings yet." is visible; given Favourites only on, it is collapsed.
- Given a row, when its star is clicked, then `FavouriteCommand` runs and the star's checked state still follows only `IsFavourite`.

## Implementation Notes

- Built by a subagent from this plan; files: `Theme/LibraryTheme.xaml` + `.cs` (one dictionary for both themes, placed by `LibraryTheme.Place(merged)`, the one line in `ThemeService.Apply`), `Library/LibraryMetaConverter.cs`, `Library/LibraryView.xaml`, `tests/.../LibraryThemeTests.cs`.
- Deviation: the star is `Library/StarToggle.cs`, a ToggleButton whose `OnToggle` does nothing, so its checked state follows only `IsFavourite` (a plain ToggleButton flips itself on click, and a UI Automation Toggle would flip it without running the command). `LibraryView.xaml.cs` is unchanged.
- The view references every Library style by `DynamicResource`, because `LibraryPageTests` builds a bare `LibraryView` with no theme loaded; the empty-state line's look is inline on its TextBlock beside its visibility trigger.
- Thumbnails fill the 128×72 box (`UniformToFill` ImageBrush, so they follow the rounded corner); the old view letterboxed them.

## Plan Change Log

## Review Triage Log

### 2026-11-04 — Review pass
- verdicts: 4 findings — high 0, medium 1, low 3, false 0, maybe-false 0
- findings:
  - `[medium]` `[patch]` A UI Automation Toggle on the star flips `IsChecked` without `Click` or the command — replaced the Click handler with `StarToggle` (no-op `OnToggle`); the test now toggles through the automation peer and checks the star stays unchecked.
  - `[low]` `[patch]` The `LibraryView.xaml.cs` change wasn't recorded — the handler is gone and the deviation is in Implementation Notes.
  - `[low]` `[reject]` The empty-state look is inline, not in `LibraryTheme.xaml` — it uses token keys only; moving it is cosmetic with no user or developer harm.
  - `[low]` `[patch]` Unused `x:Name="FavouritesOnly"` on the filter toggle — removed.

## Design Notes

- One dictionary, not one per theme: every value the Library needs is a token key both themes define, and Gaming's uppercase comes from `RbSectionHeaderStyle` and its ToggleButton template. Body-strong is SemiBold in both (Gaming's 700 differs slightly; not worth a second dictionary).
- The row's hover while its context menu is open: a `MultiTrigger`/`DataTrigger` on `ContextMenu.IsOpen` of the row's border (`{Binding ContextMenu.IsOpen, RelativeSource={RelativeSource Self}}`).

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings, 0 errors.
- `dotnet test --project tests/ReplayBuffer.App.Tests -c Release --no-build` -- expected: all pass, except the known order bug `ThemeTests.MicaIsBehindTheMainAppUnderFluentOnlyAndNeverBehindOtherWindows` (fails after `SurfacesThemeTests.TheDialogAnswersYesNo...`; a separate bugfix). Engine, Media, capture and audio projects are not touched, so they are skipped.

**Manual checks (if no CLI):**
- Look at `library-fluent.png` and `library-gaming-dark.png` in `tests/ReplayBuffer.App.Tests/bin/Release/<tfm>/` against `ux-modern-ui/renders/fluent-dark-library.png` and `gaming-library.png`.

## Auto Run Result

- **Change:** the Library restyled to DESIGN.md and EXPERIENCE.md in both themes: filters row under the shell's title with a "★ Favourites only" toggle, caution info bar banner with Acknowledge, action error in `danger`, group headers with a divider rule, card rows (128×72 thumbnail or ▶ placeholder, length badge, name, "game · date · size", "Saving…" pill, star toggle), the empty state "No Clips or Recordings yet.". The list stays one virtualized ItemsControl; every command, binding and wording is kept.
- **Files:** `src/ReplayBuffer.App/Theme/LibraryTheme.xaml`/`.cs` (styles), `Theme/ThemeService.cs` (one line), `Library/LibraryView.xaml` (rewritten), `Library/LibraryMetaConverter.cs`, `Library/StarToggle.cs`, `tests/ReplayBuffer.App.Tests/LibraryThemeTests.cs` (2 tests; saves `library-fluent.png` and `library-gaming-dark.png` beside the test binary).
- **Review:** quick lens, 4 findings: 3 patched (1 medium, 2 low), 1 low rejected (see the triage log). Nothing deferred.
- **Follow-up review recommended:** false (one medium patched, no high).
- **Verification:** `dotnet build ReplayBuffer.slnx -c Release` 0 warnings, 0 errors; `dotnet test --project tests/ReplayBuffer.App.Tests -c Release --no-build` 145 passed, 18 explicit skipped, 1 failed: the known order bug `ThemeTests.MicaIsBehindTheMainAppUnderFluentOnlyAndNeverBehindOtherWindows` (separate bugfix). Other projects untouched, so not run; no engine change, so no NativeAOT publish; no Heavy tests.
- **Residual risks / HITL:** the builder browses real Clips in both themes, uses each filter and grouping and every context-menu action, and renames one inline. A screen reader's Toggle on the star now does nothing (the mouse and keyboard click still send the Favourite).

