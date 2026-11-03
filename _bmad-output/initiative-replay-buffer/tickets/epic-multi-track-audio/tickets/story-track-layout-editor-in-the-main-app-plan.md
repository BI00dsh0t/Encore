---
title: 'Track Layout editor in the Main App'
type: 'feature'
ticket: '7'
created: '2026-11-02'
status: done
baseline_revision: 'd8ec9613890121d660210ddf7ae43db66b1aa680'
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

**Problem:** The Global Track Layout can only be changed by hand-editing `settings.json`; the Main App's Settings area has no page for it, so the builder can't pick devices, build Tracks or see an unplugged device (R1 picker part, R2 Track editing and Everything).

**Approach:** A "Track Layout" page in story 1.7's Settings host holds an unsaved draft of the Global `trackLayout`. It offers the snapshot's live `devices` list in a picker, shows layout devices that aren't present as unavailable, edits devices, Tracks and assignments in the draft, and Save sends one `settings.update` op (`SetGlobal trackLayout`) through the page's existing `SendAsync`.

## Boundaries & Constraints

**Always:** The App changes settings only through `SettingsPage.SendAsync` (AD-4), against the revision it rendered. The saved value is Contracts' `TrackLayout` shape, unchanged (AD-4): device keys are new GUIDs, Track ids stay stable across renames and moves, `IsMic` and `MuteAffected` are preserved for existing items, new devices get `IsMic: false`, new Tracks `MuteAffected: false`. A device added to the draft joins the Track whose id is `Keys.EverythingTrackId`, when the draft has it. Availability is computed from the snapshot's `devices` (an entry with the same endpoint id, ordinal-ignore-case, and flow, with `Available` true). A device's label carries its parent name when another listed or layout device has the same friendly name (ordinal-ignore-case). Save is refused in the page, with a message, while a Track name is blank. Members run on the UI thread.

**Never:** No Contracts, Core or Engine changes. No mic marking, mute-affected editing, double-count warning (2.8) or meters (2.11). No Game Profile layouts (epic 3). No new packages. No writes to settings outside Save.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Fresh document | no `trackLayout` in Global | draft shows the default: no devices, one Track "Everything"; not dirty | — |
| Add device | pick a listed device, Add | it joins the draft's devices and the Everything Track; leaves the picker; page dirty | — |
| Remove from Everything | uncheck the device on Everything | Everything no longer holds it; the device stays in the layout | — |
| Remove device | Remove a layout device | gone from the layout and from every Track | — |
| Track edits | add, rename, move up/down, delete Tracks | the saved layout's Tracks match in order, ids stable | moving the first up or last down does nothing |
| Assign | check a device on a Track | the Track's `DeviceKeys` gains its key | — |
| Same names | two listed devices both "Speakers", parents A and B | labels show each parent | — |
| Unplugged | a layout device leaves the snapshot's `devices`, or arrives `Available: false` | it shows as unavailable, stays in the layout; the draft is kept | — |
| Absent in picker | a listed device with `Available: false` not in the draft | offered, marked unavailable | — |
| Save | Save with a valid draft | one `settings.update` with `SetGlobal trackLayout` = the draft, against the rendered revision; after `settings.changed` with that value the page isn't dirty | — |
| Blank name | a Track renamed to blank, Save | nothing sent; a message says a Track needs a name | — |
| Refused / conflict | the engine refuses, or `settings-conflict` with an unchanged `trackLayout` | the draft is kept (base message shown) | — |
| Changed elsewhere | a new revision whose `trackLayout` differs from the draft's basis while dirty | the draft reloads from it, with a message | — |
| Other key changed | a new revision where only other keys changed while dirty | draft kept; the next Save uses the new revision | — |
| Revert | Revert | the draft returns to the rendered layout; not dirty | — |

</intent-contract>

## Code Map

- `src/ReplayBuffer.App/Settings/SettingsPage.cs` -- base page: `Render` → `Show` → abstract `OnRender(document)`; `SendAsync(ops)` handles Committed / Conflict (calls `Show(conflict.Current)`) / Failed (calls `Show(Document)`), so `OnRender` runs on those paths with the same or a newer document. `Message` has a private setter: make it `protected set` so the new page can set its own notes. Add `new TrackLayoutPage(sender)` to `SettingsPages.Create` after General.
- `src/ReplayBuffer.App/Settings/SettingsSender.cs` -- `SettingsValues.Global(document, Keys.TrackLayout)` reads the layout (default when missing), `SettingsValues.SetGlobal(Keys.TrackLayout, layout)` builds the op. Reuse; don't change.
- `src/ReplayBuffer.App/Settings/GeneralSettingsPage.cs`, `GeneralSettingsView.xaml(.cs)` -- the pattern to copy: page class with `CreateView()` returning a `UserControl` (`x:ClassModifier="internal"`, `DataContext = this`).
- `src/ReplayBuffer.App/Settings/TrackLayoutPage.cs` (new) -- `Title` "Track Layout". Draft state as `ObservableCollection`s of small `Observable` item classes in the same file: layout devices (the `LayoutDevice`, `Label`, `Available`), Tracks (`Id`, settable `Name`, `MuteAffected`, its ordered device keys, and one check item per layout device whose `IsChecked` setter adds/removes the key, appending to keep order), picker choices (the `AudioDevice`, `Label`). `SelectedDevice`, `SelectedTrack`, `SelectedChoice`; `RelayCommand`s Add device, Remove device, Add Track ("Track N"), Move up, Move down, Delete Track, Save, Revert; `IsDirty`. `ShowDevices(AudioDeviceList)` stores the list, refreshes `Available` and labels, and rebuilds the choices. `ToLayout()` builds the `TrackLayout` from the draft. Dirty = the draft's JSON (`Keys.TrackLayout.ToJson`) differs from the basis's (`JsonElement.DeepEquals`); records' list members compare by reference, so never compare `TrackLayout` with `==`. `OnRender`: if not dirty, or the new layout equals the basis, or equals the draft → take the new layout as basis (reload the draft only when not dirty or it differs); else reload with `Message = "The Track Layout changed elsewhere, so this page reloaded it."`.
- `src/ReplayBuffer.App/Settings/TrackLayoutView.xaml(.cs)` (new) -- in a `ScrollViewer`: layout devices list (label + "unavailable" when not `Available`) with Remove; a picker `ComboBox` of choices with Add; Tracks list with Add / Up / Down / Delete, a name `TextBox` (`UpdateSourceTrigger=PropertyChanged`) and the selected Track's device checkboxes; Save and Revert buttons enabled by `IsDirty`.
- `src/ReplayBuffer.App/MainViewModel.cs` -- add `TrackLayout` (`Pages.OfType<TrackLayoutPage>().Single()`, like `General`); in `Refresh`, call `TrackLayout.ShowDevices(state.Devices)` before rendering the pages.
- `tests/ReplayBuffer.App.Tests/FakeEngineSession.cs` -- the fake: set `State` (incl. `Devices` and `Settings`), `Reply`, `RaiseChanged()`; `Sent` records the `SettingsUpdate`. `MainViewModelTests.cs` shows building a `SettingsDocument` with a Global value (`SettingsMaps.EmptyValues.Add(...)`).
- `tests/ReplayBuffer.App.Tests/TrackLayoutPageTests.cs` (new) -- through `MainViewModel` over the fake session, `post: a => a()`. View-creating tests run in `Sta.Run`.

## Tasks & Acceptance

**Execution:**
- [x] `src/ReplayBuffer.App/Settings/SettingsPage.cs` -- `Message` protected setter; register the page -- the host lists it
- [x] `src/ReplayBuffer.App/Settings/TrackLayoutPage.cs`, `TrackLayoutView.xaml`, `TrackLayoutView.xaml.cs` -- the draft editor and its view -- R1 picker, R2 editing
- [x] `src/ReplayBuffer.App/MainViewModel.cs` -- `TrackLayout` property; pass `state.Devices` -- live availability
- [x] `tests/ReplayBuffer.App.Tests/TrackLayoutPageTests.cs` -- one test per matrix row or tight group, plus a test that creates the page's `View` in `Sta.Run` and renders it (`Measure`/`Arrange`, `RenderTargetBitmap`) without throwing -- matrix coverage and XAML bindings load

**Acceptance Criteria:**
- Given the Main App's view model over an engine session with three present devices, when a test adds all three, adds Tracks "Game", "Chat" and "Mic", assigns one device to each, and saves, then the sent `settings.update` holds a `trackLayout` with Everything first holding all three device keys, then the three named Tracks in order with one key each, against the rendered revision.
- Given that page with the layout shown, when the session's `devices` list changes so one layout device is no longer present, then that device's row reports unavailable and the unsaved draft is unchanged.
- Given the Settings tab of the Main App, when the user selects "Track Layout", then the page shows the layout's devices, the picker and the Tracks, and its view builds and renders without binding errors.

## Implementation Notes

- The page's item classes (`TrackLayoutDeviceItem`, `TrackLayoutTrackItem`, `TrackLayoutCheck`, `TrackLayoutChoice`) live in `TrackLayoutPage.cs`. A Track keeps its own ordered `DeviceKeys`, so loading a layout and saving it unchanged doesn't reorder keys or mark the page dirty.
- A layout device's label keeps the name stored in the layout, also while it's unplugged.
- The view test fails on any WPF binding error that names a `TrackLayout*` type; a deliberately broken binding made it fail.
- Review patch: Save does nothing while a save is in flight (`_saving`).
- Verification (2026-11-02): Release build 0 warnings, 0 errors; `ReplayBuffer.App.Tests` 71 of 71 passed. An earlier background run's test host (this worktree's) held the App dll and was stopped before the rerun.

## Plan Change Log

## Review Triage Log

### 2026-11-02 — Review pass
- verdicts: 3 findings — high 0, medium 1, low 2, false 0, maybe-false 0
- findings:
  - `[medium]` `[patch]` Save stays clickable while a save is in flight; a second click sends another `settings.update` against the old revision and shows a false "changed elsewhere" conflict — fixed: `SaveAsync` returns while `_saving` is set; new test `ASaveWhileOneIsInFlightDoesNothing`.
  - `[low]` `[reject]` The "changed elsewhere" and blank-name messages stay shown after they stop applying — real but cosmetic: the next committed save or Revert clears them, and clearing them on every render would also clear the base page's conflict note, which needs extra branching.
  - `[low]` `[patch]` The view test left `PresentationTraceSources.DataBindingSource.Switch.Level` raised for later tests, and only counts errors naming `TrackLayout` — fixed the level (restored in `finally`); the name filter stays, since other views' binding noise isn't this page's.

## Design Notes

A draft with an explicit Save (unlike General's send-on-change): a layout edit takes several steps (add a device, then assign it), and each intermediate `settings.update` would bump the revision and fire `settings.changed` for nothing; 2.11's meters also need "an unsaved draft". The `OnRender` rule keeps the draft across `settings.changed` for other keys (General's checkbox), across a refused save (base `Show(Document)`), and after the page's own save commits.

Labels: `"Speakers"`, or `"Speakers — USB DAC"` when another device is also named "Speakers". The flow shows as a suffix: `"(Output)"` / `"(Input)"`.

## Verification

**GPU lock:** before every build or test run, check that `%USERPROFILE%\Documents\repos\ReplayBuffer\.bench-running` doesn't exist; while it does, wait.

**Scoped tests (builder, 2026-11-02):** this is an App-only change. Do not run the Engine, Platform.Capture, Media or Platform.Audio test projects, `dotnet test --solution`, bench, or the NativeAOT publish.

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings, 0 errors
- `dotnet test --project tests/ReplayBuffer.App.Tests -c Release` -- expected: all pass

**Manual checks (if no CLI):**
- The builder's hand check (Human Check Queue): build a three-Track layout in the page, unplug a device while it's open and see it turn unavailable, save, and the next capture uses it with Everything holding all three devices.

## Auto Run Result

**Status:** built, 2026-11-02.

**Summary:** A "Track Layout" page in the Main App's Settings holds an unsaved draft of the Global `trackLayout`. It picks devices from the snapshot's live `devices` list (same-name devices labelled with their parent, absent ones as unavailable), adds and removes devices (a new one joins Everything), adds, renames, reorders and deletes Tracks, assigns devices with per-Track checkboxes, and Save sends one `settings.update` with `SetGlobal trackLayout`. Layout devices missing from the live list show "unavailable" while the draft is kept.

**Files:**
- `src/ReplayBuffer.App/Settings/TrackLayoutPage.cs` (new) — the draft view model and its item classes.
- `src/ReplayBuffer.App/Settings/TrackLayoutView.xaml(.cs)` (new) — the page's view.
- `src/ReplayBuffer.App/Settings/SettingsPage.cs` — `Message` protected setter; page registered after General.
- `src/ReplayBuffer.App/MainViewModel.cs` — `TrackLayout` page property; passes `state.Devices` on each refresh.
- `tests/ReplayBuffer.App.Tests/TrackLayoutPageTests.cs` (new) — 18 tests: the matrix rows, the three-Track save, unplugging, the in-flight save, and the rendered view.

**Review:** one quick pass, 3 findings. Patched: 1 medium, 1 low. Deferred: none. Rejected: 1 low (stale note, cosmetic). See the Review Triage Log.

**Follow-up review recommended:** false. One medium patched, covered by a new test.

**Verification:** `dotnet build ReplayBuffer.slnx -c Release` 0 warnings, 0 errors; `dotnet test --project tests/ReplayBuffer.App.Tests -c Release` 71 passed. Other test projects and the NativeAOT publish were not run (App-only change, scoped-tests rule).

**Residual risks:**
- The builder's hand check (a real unplug, and the next capture using the saved layout) isn't automated.
- No screenshot: the Main App window is excluded from capture; the view is rendered off-screen in a test instead.
