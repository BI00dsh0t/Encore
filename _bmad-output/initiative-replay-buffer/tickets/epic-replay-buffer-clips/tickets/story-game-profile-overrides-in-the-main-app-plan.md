---
title: 'Game Profile overrides in the Main App'
type: 'feature'
ticket: '11'
created: '2026-11-03'
status: done
baseline_revision: '034ee329738174db36e66b4d603511501022880c'
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

**Problem:** The engine resolves a Game Profile's `trackLayout`, `video` and `bufferWindow` overrides (4.9), but the Main App can only edit Global values, so the builder can't give one game its own Track Layout (with its own Mic Devices and mute Tracks), Output Settings or Buffer Window (R7 UI, R8 per-profile marking).

**Approach:** Make the existing Track Layout editor (2.7/2.11) and Replay Buffer page (4.10) scope-aware: with a profile id they read the profile's override (or Global when there is none), send profile-scoped ops, and show a "Use Global" box per key. The Game Profiles page (3.6) gains an "Overrides for" picker that hosts a profile-scoped instance of each, below the list.

## Boundaries & Constraints

**Always:**
- Every change goes through `SettingsSender` (`settings.update`) as `SettingsOp.SetProfile` / `RemoveProfile` ops against the rendered revision.
- Unticking "Use Global" sends one `SetProfile` op whose value is the current Global value: for `trackLayout`, the Global layout as is, so the Track ids (and device keys) are kept.
- Ticking "Use Global" sends one `RemoveProfile` op for that key and drops any unsaved Track Layout draft.
- While a key uses Global, its fields show the Global value and can't be edited.
- The Global pages behave exactly as today (existing tests stay green, unchanged).

**Never:**
- No Contracts, Core or Engine change (the ops, keys and resolution already exist). No new settings keys.
- No per-profile storage estimate: the Replay Buffer page states its estimate is for the Global settings. Storage mode, ring folder and save cooldown are not overridable and are hidden in the profile editor.
- No new UX pass; the builder checks the page by hand (HITL).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Start a layout override | profile has no `trackLayout`; untick Use Global | one op `profile/<id>/trackLayout set` = Global layout (same Track ids) | refusal shows in the page Message |
| Edit and save override | override exists; tick a device's Mic, Save | one `SetProfile trackLayout` op with `isMic: true` | blank Track name refused as on the Global page |
| Video override | untick Use Global for Output, pick 1920×1080 | `SetProfile video` = Global copy, then `SetProfile video` with the new size | invalid entry not sent, Message shown |
| Use Global | override exists; tick Use Global | one `RemoveProfile <key>` op | — |
| Profile removed | selected profile vanishes from the document | picker clears, editors removed | — |

</intent-contract>

## Code Map

- `src/ReplayBuffer.App/Settings/SettingsSender.cs` -- `SettingsValues`: add `HasOverride<T>(doc, key, profileId)`, `Resolve<T>(doc, key, profileId?)` (valid profile override, else `Global`), `Set<T>(key, value, profileId?)` (Global or `SettingsOp.SetProfile(profileId, key.Name, key.ToJson(value))`). Update the class doc (it says the App doesn't resolve profiles).
- `src/ReplayBuffer.App/Settings/TrackLayoutPage.cs` -- add ctor param `string? profileId = null`; `ProfileId`, `IsProfile`, `UsesGlobal` (settable: true → `RemoveProfile`, false → `SetProfile` of the Global layout), `CanEdit` (`!IsProfile || !UsesGlobal`). `OnRender` loads `Resolve(...)`; when `UsesGlobal` it always reloads (no draft). `SaveAsync` uses `Set(..., ProfileId)` and does nothing when `!CanEdit`. Keep the meter watch logic as is.
- `src/ReplayBuffer.App/Settings/TrackLayoutView.xaml` -- a "Use Global Track Layout" checkbox at the top, visible only when `IsProfile`; the editor panel's `IsEnabled` bound to `CanEdit`.
- `src/ReplayBuffer.App/Settings/ReplayBufferSettingsPage.cs` -- primary ctor gains `string? profileId = null`; `IsGlobal`; `BufferWindowUsesGlobal` / `VideoUsesGlobal` (settable, same semantics as above) and `CanEditBufferWindow` / `CanEditVideo`. `OnRender` uses `Resolve`; `SendInt` (bufferWindow only) and `SendVideo` send via `Set(..., ProfileId)`. Storage/ring/cooldown sends remain Global (only reachable on the Global page).
- `src/ReplayBuffer.App/Settings/ReplayBufferSettingsView.xaml` -- Use Global checkboxes (visible when `!IsGlobal`) above Buffer Window and Output; storage/ring/cooldown rows and the estimate visible only when `IsGlobal`; fields' `IsEnabled` bound to the CanEdit flags. Add a grey line under the estimate: it covers the Global settings; a game's own Buffer Window or Output Settings change it. Use `BooleanToVisibilityConverter` (as TrackLayoutView does).
- `src/ReplayBuffer.App/Settings/GameProfilesPage.cs` -- store the `SettingsSender` and engine; `SelectedProfile` (GameProfileItem?) setter builds `OverrideTrackLayout = new TrackLayoutPage(sender, engine, id)` and `OverrideOutput = new ReplayBufferSettingsPage(sender, id)`, renders them with `Document`, replays the last devices/meters/state, and mirrors their `Message` into the page's `Message` (detach from the old children; set the old layout page `IsOpen = false`). `OnRender` forwards `Render` to the children and clears the selection when its profile is gone. New `ShowState(StateSnapshot)` (devices, meters + instance id, video options) and `IsOpen` (forwarded to `OverrideTrackLayout.IsOpen`).
- `src/ReplayBuffer.App/Settings/GameProfilesView.xaml` -- below the list: "Overrides for" `ComboBox` (`Profiles`, `DisplayMemberPath=Name`, `SelectedItem=SelectedProfile`), then `ContentControl Content="{Binding OverrideOutput.View}"` and `ContentControl Content="{Binding OverrideTrackLayout.View}"`.
- `src/ReplayBuffer.App/MainViewModel.cs` (lines ~164–180) -- call `GameProfiles.ShowState(state)` before the render loop; in `UpdateOpenPage` also set `GameProfiles.IsOpen = IsWindowShown && IsSettingsTabSelected && SelectedPage == GameProfiles`.
- `tests/ReplayBuffer.App.Tests/GameProfilesPageTests.cs` -- reuse `FakeEngineSession`, `SetProfiles`, `Create()`; extend the existing view test.
- Read-only: `ReplayBuffer.Contracts` (`SettingsOp`, `GameProfile.Overrides`, `Keys`), Core/Engine.

## Tasks & Acceptance

**Execution:**
- [x] `src/ReplayBuffer.App/Settings/SettingsSender.cs` -- add the scope helpers -- one place for profile reads and ops.
- [x] `src/ReplayBuffer.App/Settings/TrackLayoutPage.cs` + `TrackLayoutView.xaml` -- profile scope and Use Global -- reuse 2.7/2.11's editor.
- [x] `src/ReplayBuffer.App/Settings/ReplayBufferSettingsPage.cs` + `ReplayBufferSettingsView.xaml` -- profile scope, Use Global, Global-only rows hidden, estimate note -- reuse 4.10's video/bufferWindow editing.
- [x] `src/ReplayBuffer.App/Settings/GameProfilesPage.cs` + `GameProfilesView.xaml` -- picker and hosted editors -- 3.6's page is where a profile's settings live.
- [x] `src/ReplayBuffer.App/MainViewModel.cs` -- feed state and open flag to the Game Profiles page.
- [x] `tests/ReplayBuffer.App.Tests/GameProfilesPageTests.cs` -- two happy-path tests (layout override lifecycle; output + Buffer Window override lifecycle), and extend the view test to select a profile with an override, assert no binding errors mentioning the new types, and save `game-profile-overrides.png` to the test output folder (as `HotkeysPageTests` does).

**Acceptance Criteria:**
- Given a profile with no overrides, when the builder picks it under "Overrides for" and unticks Use Global for the Track Layout, then one `settings.update` carries `SetProfile(<id>, trackLayout, <Global layout>)` with the Global Track ids.
- Given that override rendered, when the builder marks an Input device as Mic in it and presses Save, then one `SetProfile(<id>, trackLayout, …)` op with that device `isMic: true` is sent, and the Global Track Layout page still shows the Global layout.
- Given a profile, when the builder unticks Use Global for Output and picks 1920 × 1080, and unticks Use Global for the Buffer Window and enters 60, then `SetProfile` ops for `video` (with 1920×1080) and `bufferWindow` (60) are sent, all profile-scoped.
- Given overrides, when the builder ticks Use Global for each, then one `RemoveProfile(<id>, <key>)` op per key is sent and the fields show the Global values, disabled.
- Given the Game Profiles page with a profile selected, when it renders, then there are no binding errors and the screenshot shows the picker, the Output and Track Layout editors with their Use Global boxes.

## Implementation Notes

- `SettingsPage.Embedded(UserControl)` drops a hosted view's root `ScrollViewer`: nested inside the Game Profiles page it never scrolls but would swallow the mouse wheel. The profile instances of both pages use it; the Global pages don't.
- `MainViewModel.UpdateOpenPage` closes before it opens, so leaving Game Profiles for Track Layout doesn't send the closing page's `meters.unwatch` after the opening page's `meters.watch`.
- `ReplayBufferSettingsPage.IsProfile` (= `!IsGlobal`) exists for the Use Global boxes' visibility binding; the Buffer Window and storage rows share their label column through `SharedSizeGroup`.
- The "Overrides for" section sits after "Add a game by hand" (still below the list), with a "Track Layout" heading above the hosted layout editor.

## Plan Change Log

## Review Triage Log

### 2026-11-03 — Review pass
- verdicts: 3 findings — high 0, medium 1, low 2, false 0, maybe-false 0
- findings:
  - `[low]` `[reject]` After a reconnect, a closed Global Track Layout page's deferred `meters.unwatch` (from `ShowMeters`) can follow the hosted override editor's `meters.watch` in `Refresh`, so the override's meters read zero until its draft changes — real, but needs a disconnect during a page switch, only zeroes the level bars, and the fix needs an ordering branch in `Refresh`.
  - `[medium]` `[patch]` A hosted editor that sets the same refusal twice raises no `Message` change, so `GameProfilesPage.ChildChanged` doesn't mirror it and the page shows nothing — fixed: `SettingsPage.Message`'s setter now raises on every set.
  - `[low]` `[reject]` The Output/Buffer Window test sends four updates at revision 3 with no render between them — a test-realism note, not a code defect: in the app each click follows the previous commit's `settings.changed`, which re-renders the page; the ops' scope and values are what the AC checks.

## Design Notes

Scope-aware pages instead of new editors: the profile editors are second instances of the Global pages' view models, constructed with a profile id, so validation, codec/resolution lists, the double-count warning, Mic and mute checkboxes and meters come for free. `SettingsPage.Render` skips repeat revisions, so a newly built child must be rendered once with the parent's `Document`.

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings, 0 errors.
- `dotnet test --project tests/ReplayBuffer.App.Tests -c Release --no-build` -- expected: all pass (only App changed; Engine publish not needed).

**Manual checks (if no CLI):**
- `tests/ReplayBuffer.App.Tests/bin/Release/**/game-profile-overrides.png` shows the editors; the end-to-end Clip check goes to the Human Check Queue.

## Auto Run Result

**Summary:** The Game Profiles page gains an "Overrides for" picker that hosts profile-scoped instances of the Replay Buffer page (Buffer Window and Output Settings) and the Track Layout editor (with Mic Devices and mute Tracks). Each key has a Use Global box: unticking sends `SetProfile` with a copy of the Global value (the layout keeps its Track ids), ticking sends `RemoveProfile`. Every edit is a profile-scoped `settings.update` op. The Global Replay Buffer page says its estimate covers the Global settings only. No Contracts, Core or Engine change.

**Files changed:**
- `src/ReplayBuffer.App/Settings/SettingsSender.cs` — `SettingsValues.HasOverride`, `Resolve`, `Set` (Global or profile op).
- `src/ReplayBuffer.App/Settings/SettingsPage.cs` — `Embedded` drops a hosted view's ScrollViewer; `Message` raises on every set.
- `src/ReplayBuffer.App/Settings/TrackLayoutPage.cs`, `TrackLayoutView.xaml` — optional profile scope, `UsesGlobal`, `CanEdit`.
- `src/ReplayBuffer.App/Settings/ReplayBufferSettingsPage.cs`, `ReplayBufferSettingsView.xaml` — optional profile scope, Use Global for Buffer Window and Output, Global-only rows hidden, estimate note.
- `src/ReplayBuffer.App/Settings/GameProfilesPage.cs`, `GameProfilesView.xaml` — picker, hosted editors, `ShowState`, `IsOpen`, message mirroring.
- `src/ReplayBuffer.App/MainViewModel.cs` — feeds the snapshot and open flag to Game Profiles; closes before opening so meter watches aren't stopped.
- `tests/ReplayBuffer.App.Tests/GameProfilesPageTests.cs` — layout override lifecycle, Output/Buffer Window override lifecycle, view test with `game-profile-overrides.png`.

**Review:** 1 patch applied (medium: repeated hosted-editor message), 0 deferred, 2 rejected (low: meter order after a reconnect mid-switch; test sends at one revision — see the triage log).

**Follow-up review recommended:** false (one medium patched, no high).

**Verification:** `dotnet build ReplayBuffer.slnx -c Release`: 0 warnings, 0 errors. `dotnet test --project tests/ReplayBuffer.App.Tests -c Release --no-build`: 104 total, 96 passed, 8 Heavy skipped, 0 failed. Other test projects not run: only App code changed. The engine publish was not needed. The screenshot shows the picker, the greyed Use Global Output and Buffer Window fields, and the editable Track Layout override.

**Residual risks / HITL:** The real app was not run. For the Human Check Queue: give one game a 1080p override and its own Track Layout with a different Mic Device, save a Clip in it and in another game, and check each file's resolution and Tracks; mute in the first game silences only its Mic Device; Use Global removes the override.
