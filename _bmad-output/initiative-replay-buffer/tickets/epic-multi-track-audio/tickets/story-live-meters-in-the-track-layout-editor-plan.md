---
title: 'Live meters in the Track Layout editor'
type: 'feature'
ticket: '8'
created: '2026-11-03'
status: done
baseline_revision: '778a9e78c56c1161c07ad89be08e7f809d9f1a21'
route: 'full'
route_source: 'auto'
risk: 'low'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
followup_review_recommended: false
context: ['{project-root}/CLAUDE.md']
warnings: []
deferred: []
---

<intent-contract>

## Intent

**Problem:** Story 2.6 gave the engine `meters.watch` / `meters.unwatch` and a coalesced `meters.levels` event that lands in `StateSnapshot.Meters`, but the Track Layout editor (2.7, 2.11) shows no levels, so the builder can't see which Tracks a playing device reaches (R6).

**Approach:** While the Track Layout page is open (Main App window shown, Settings tab selected, Track Layout the selected page), the page sends `meters.watch` with its current draft (saved or unsaved) and again whenever the draft's metered shape changes; when it closes it sends `meters.unwatch`. Each Track row shows a level bar fed from `State.Meters`, matched by Track id.

## Boundaries & Constraints

**Always:** Meters travel only as the existing Contracts messages (AD-3); the App changes no Contracts or engine code. The engine already coalesces to ≤ 20 Hz, so the App just renders the latest `State.Meters`. A watch failure (`ErrorCodeException`, e.g. disconnected or `invalid-arguments`) never throws out of the page; the next draft change or engine restart retries. A new engine run (`State.EngineInstanceId` changes) while the page is open gets a fresh watch. A Track the levels don't list reads 0.

**Never:** No settings writes for meters, no gain or peak-hold logic, no new abstraction over the session for meters (the `IEngineSession` is the feed; tests use `FakeEngineSession`), no Engine/Contracts changes, no Heavy tests.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Open | Window shown, Settings tab, Track Layout selected | One `MetersWatch` sent holding `ToLayout()` | No error expected |
| Unsaved edit | Page open, user adds a Track (not saved) | Another `MetersWatch` with the edited draft; a rename alone sends none | No error expected |
| Levels | `State.Meters` lists Track A at 0.5, Track B at 0, then `Changed` | A's level bar moves, B's stays 0 | No error expected |
| Close | Another page selected, the Status tab selected, or the window hidden | `meters.unwatch` sent once; all bars read 0 | Send failure ignored |
| Engine restart | Page open, `State.EngineInstanceId` changes | The draft is watched again | No error expected |

</intent-contract>

## Code Map

- `src/ReplayBuffer.App/Settings/TrackLayoutPage.cs` -- the page. Takes `IEngineSession` as well (constructor `(SettingsSender sender, IEngineSession engine)`). Add `IsOpen` (set via a method or setter from the view model): open → watch the draft; close → send `AudioMessages.Unwatch` (`Empty.Value`/the Contracts `Empty` instance), forget the watched layout, zero every Track's level. In `DraftChanged()` (runs after every edit and on `Load`), when open, watch again only if the metered shape changed: compare `Keys.TrackLayout.ToJson` of the draft with Track names blanked against the last watched JSON. Watch = fire-and-forget `engine.SendAsync(AudioMessages.Watch, new MetersWatch(ToLayout()))`, catching `ErrorCodeException` (then clear the remembered JSON so a later change retries). New `ShowMeters(MeterLevels levels, string engineInstanceId)`: stores the levels, sets each `TrackLayoutTrackItem.Level` (0 when absent), and if open and the instance differs from the one watched in, re-sends the watch. Apply stored levels to new Track items in `Load`/`AddTrack` too.
- `TrackLayoutTrackItem` (same file) -- add `double Level` (0..1, notifying). Map the linear peak to the bar on a dBFS scale: `peak <= 0 → 0`, else `Math.Clamp((20 * Math.Log10(peak) + 60) / 60, 0, 1)` (−60 dBFS..0 dBFS).
- `src/ReplayBuffer.App/Settings/SettingsPage.cs` -- `SettingsPages.Create(SettingsSender sender, IEngineSession engine)`; pass `engine` to `TrackLayoutPage`. Update the doc comment only if needed.
- `src/ReplayBuffer.App/Settings/TrackLayoutView.xaml` -- in the Tracks `ListBox` item template, a small `ProgressBar` (`Minimum=0`, `Maximum=1`, `Value="{Binding Level, Mode=OneWay}"`, ~80×8, before the name).
- `src/ReplayBuffer.App/MainViewModel.cs` -- pass `engine` to `SettingsPages.Create`. Add `IsWindowShown` and `IsSettingsTabSelected` (settable, default false) and recompute `TrackLayout.IsOpen = IsWindowShown && IsSettingsTabSelected && SelectedPage == TrackLayout` when any of the three changes. In `Refresh`, after rendering the pages, call `TrackLayout.ShowMeters(state.Meters, state.EngineInstanceId)`.
- `src/ReplayBuffer.App/MainWindow.xaml` -- Settings `TabItem`: `IsSelected="{Binding IsSettingsTabSelected, Mode=TwoWay}"`.
- `src/ReplayBuffer.App/MainWindow.xaml.cs` -- on `IsVisibleChanged`, set `((MainViewModel)DataContext).IsWindowShown = IsVisible` when the DataContext is a `MainViewModel`.
- Read-only: `src/ReplayBuffer.Contracts/Messages/Audio.cs` (`MetersWatch`, `TrackLevel(Guid TrackId, float Peak)`, `MeterLevels`, `AudioMessages.Watch`/`Unwatch`), `StateSnapshot.Meters`/`EngineInstanceId`. `tests/ReplayBuffer.App.Tests/FakeEngineSession.cs` is the fake feed: set `State` with `Meters`, `RaiseChanged()`; `Sent` records each command's args; set `Reply` to return the Contracts `Empty` for `meters.*`.
- Tests: `tests/ReplayBuffer.App.Tests/TrackLayoutPageTests.cs` -- existing tests never open the page, so they must not change; the existing view-render test covers the new `ProgressBar` binding.

## Tasks & Acceptance

**Execution:**
- [x] `src/ReplayBuffer.App/Settings/TrackLayoutPage.cs`, `SettingsPage.cs` -- open/close watch, draft re-watch, `ShowMeters`, `Level` -- the feature
- [x] `src/ReplayBuffer.App/Settings/TrackLayoutView.xaml` -- level bar per Track -- R6 UI
- [x] `src/ReplayBuffer.App/MainViewModel.cs`, `MainWindow.xaml`, `MainWindow.xaml.cs` -- open state, levels feed -- wiring
- [x] `tests/ReplayBuffer.App.Tests/TrackLayoutPageTests.cs` -- two tests through `MainViewModel` over `FakeEngineSession`: (1) open → watch; add a Track → watch with the draft; rename → no new watch; levels → only the fed Track's bar moves; new `EngineInstanceId` → watched again. (2) open, then select General → one unwatch and bars at 0; reopen then hide the window → unwatch -- matrix coverage

**Acceptance Criteria:**
- Given the Main App's view model over an engine session with a saved layout of two Tracks, when the Track Layout page opens, the user adds a Track without saving, and the session's state then carries levels with only the second Track at 0.5, then the session received `meters.watch` with the edited draft (three Tracks), and only the second Track's level is above 0.
- Given that open page, when the user selects another page or the window hides, then the session receives `meters.unwatch` and every Track's level reads 0.
- Given the Settings tab of the Main App, when the Track Layout view renders, then it builds without binding errors (the existing view test).

## Implementation Notes

- Implemented as planned. `TrackLevel` peaks show on a −60..0 dBFS bar (`TrackLayoutTrackItem.ShowPeak`); levels show only while the page is open.
- The implementer's first App.Tests run hung for 25+ minutes and was ended by the builder. Reproduced twice in seven full runs: `MainWindowTests`, `TrayTests` and `TrackLayoutPageTests` all stalled at once while every class passed alone, i.e. WPF on several STA threads in parallel deadlocked (pre-existing; no test injects input). Fix: the three classes share a non-parallel `WpfCollection` (`tests/ReplayBuffer.App.Tests/Sta.cs`). After it, seven full runs finished in ~26 s; one had an unrelated `SupervisionTests.AKilledAppIsRelaunchedByTheEngine` 10 s timing failure.

## Plan Change Log

## Review Triage Log

### 2026-11-03 — Review pass
- verdicts: 3 findings — high 0, medium 1, low 2, false 0, maybe-false 0
- findings:
  - `[medium]` `[patch]` An unwatch (or watch) that fails with `engine-disconnected` during a reconnect to the same engine run is never resent, so the engine keeps metering with the window in the tray — fixed: a disconnected unwatch sets `_unwatchPending`, sent again by the next snapshot while the page stays closed; a disconnected watch clears `_watchedInstanceId` so the next snapshot re-watches; the close test now covers the lost unwatch.
  - `[low]` `[patch]` `Refresh` rendered before `ShowMeters`, so a render-time watch recorded the previous engine id and `ShowMeters` watched the same draft again — fixed: `MainViewModel.Refresh` calls `ShowMeters` before rendering the pages.
  - `[low]` `[reject]` A minimized window still counts as open, so metering continues — the plan names hidden, not minimized; the builder rarely leaves the page minimized, and the fix adds a `StateChanged` path and another input to the open state.

## Design Notes

Why the window and tab count as "open": the Main App window only hides on close (AD-16), so watching on page selection alone would keep the engine's meter mixer running in the tray forever.

Why names are blanked in the re-watch check: renaming a Track changes nothing the engine meters, and the name `TextBox` updates per keystroke; each watch rebuilds the engine's meter subscriptions.

## Verification

**GPU lock:** before every build or test run, check that `%USERPROFILE%\Documents\repos\ReplayBuffer\.bench-running` doesn't exist; while it does, wait.

**Scoped tests:** App-only change. Do not run Engine, Platform.Capture, Media, Platform.Audio, `dotnet test --solution`, bench, or the NativeAOT publish.

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings, 0 errors
- `dotnet test --project tests/ReplayBuffer.App.Tests -c Release --no-build` -- expected: all pass

**Manual checks (if no CLI):**
- The builder's hand check (Human Check Queue): with no game running, play audio on the BEACN Chat output, open Settings → Track Layout and see only the Tracks holding it move, also after an unsaved edit; switch page and the meters stop.

## Auto Run Result

- **Summary:** The Track Layout page sends `meters.watch` with its draft (saved or unsaved) while it is on screen (window shown, Settings tab, Track Layout selected), re-watches when the draft's metered shape changes or the engine run changes, and sends `meters.unwatch` when it closes. Each Track row shows a level bar from `State.Meters`.
- **Files:**
  - `src/ReplayBuffer.App/Settings/TrackLayoutPage.cs` — open/close watch, re-watch, `ShowMeters`, Track `Level`.
  - `src/ReplayBuffer.App/Settings/SettingsPage.cs` — `SettingsPages.Create` passes the engine session.
  - `src/ReplayBuffer.App/Settings/TrackLayoutView.xaml` — level bar per Track.
  - `src/ReplayBuffer.App/MainViewModel.cs`, `MainWindow.xaml(.cs)` — open state from window visibility, tab and page; levels feed.
  - `tests/ReplayBuffer.App.Tests/TrackLayoutPageTests.cs` — two tests through the view model over the fake session.
  - `tests/ReplayBuffer.App.Tests/Sta.cs`, `MainWindowTests.cs`, `TrayTests.cs` — `WpfCollection` so WPF test classes don't run in parallel (fixes the hang).
- **Review:** 2 patches applied (1 medium, 1 low), 0 deferred, 1 low rejected (minimized window; outside the plan, extra state for little gain).
- **Follow-up review recommended:** false (patched: high 0, medium 1, low 1).
- **Verification:** `dotnet build ReplayBuffer.slnx -c Release` 0 warnings, 0 errors. `dotnet test --project tests/ReplayBuffer.App.Tests -c Release --no-build`: 62 passed, 5 Heavy skipped, ~26 s, in 6 of 7 runs; 1 run had the unrelated supervision timing failure above. Engine, Contracts, Media, Audio, Capture suites and the NativeAOT publish not run: App-only change.
- **Residual risks:** the WPF tab and window-visibility wiring and the real meters are untested in the running app (builder's hand check). `SupervisionTests.AKilledAppIsRelaunchedByTheEngine` is a real-time supervision test in the default run that can miss its 10 s window.
