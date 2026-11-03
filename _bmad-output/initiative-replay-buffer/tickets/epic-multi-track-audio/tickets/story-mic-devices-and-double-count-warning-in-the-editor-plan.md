---
title: 'Mic Devices and double-count warning in the editor'
type: 'feature'
ticket: '11'
created: '2026-11-03'
status: 'in-progress'
baseline_revision: '63d104d521d9c3af51c62a4308707424294a47a5'
route: 'full'
route_source: 'auto'
risk: 'low'
review: ''
review_source: ''
lenses_ran: []
review_loop_iteration: 0
followup_review_recommended: false
context: ['{project-root}/CLAUDE.md']
warnings: []
deferred: []
---

<intent-contract>

## Intent

**Problem:** 2.7's Track Layout page keeps each device's `IsMic` and each Track's `MuteAffected` but offers no way to set them, so the Mic Mute Hotkey (2.4) can only be set up by hand-editing `settings.json` (R4, marking). The page also doesn't warn when a Track may count the same audio twice (R2, warning).

**Approach:** The page's device rows get a "Mic" checkbox (Input Devices only), and each Track gets a "Mic Mute affects this Track" checkbox. Both edit the draft. Each Track shows a warning, and Save is never blocked by it, when its devices include Output Devices from two or more different parent devices.

## Boundaries & Constraints

**Always:** Edits go into 2.7's draft and are saved only by Save, through `SettingsPage.SendAsync`, in Contracts' `TrackLayout` shape unchanged (AD-4). A device's `Key` and a Track's `Id` never change when it is marked. The page marks the draft dirty, and Revert undoes the change. The warning is computed from the draft: a Track warns when the distinct `ParentName`s (ordinal, ignoring case) of its Output Devices number two or more. Input Devices never count toward the warning. It updates on every draft change, and loading a saved layout shows it as well. The warning never blocks Save. Members run on the UI thread.

**Never:** No Contracts, Core or Engine changes. No meters (2.8). No Game Profile layouts. No new packages. No mic marking on Output Devices. No other double-count heuristics.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Mark mic | check Mic on an Input Device | the saved device has `IsMic: true`, the same key; page dirty | — |
| Output device | an Output Device row | no Mic checkbox (`CanBeMic` false) | — |
| Mute affects | check the Track's mute box | the saved Track has `MuteAffected: true`, the same id | — |
| Double count | a Track holds outputs with parents "BEACN" and "Realtek" | that Track's `Warning` is non-empty and names both parents; other Tracks are empty | — |
| Same parent | outputs only from "BEACN" plus an input from "Realtek" | no warning | — |
| Warned save | Save while a Track warns | one `settings.update` sent as usual | — |
| Uncheck | remove one of the two outputs from the Track (uncheck or Remove device) | the warning clears | — |

</intent-contract>

## Code Map

- `src/ReplayBuffer.App/Settings/TrackLayoutPage.cs` -- the 2.7 page and its item classes, all in this file.
  - `TrackLayoutDeviceItem`: make `Device` `{ get; private set; }`. Add `CanBeMic => Device.Flow == AudioFlow.Input` and a settable `IsMic`, which sets `Device = Device with { IsMic = value }` and then calls a `changed` callback. To get that callback, add an `Action changed` ctor parameter, as `TrackLayoutTrackItem` has.
  - `TrackLayoutTrackItem.MuteAffected`: make it settable, calling `changed()`. Add a settable `Warning` string (empty means no warning).
  - Page: replace the `UpdateDirty` callback with `DraftChanged()`. It recomputes each Track's `Warning` from `Devices` and its `DeviceKeys`, then updates dirty. Use it everywhere `UpdateDirty` is called now: `Load`, add/remove device, add/move/delete Track, and the item callbacks.
  - Warning text: `$"May count audio twice: mixes outputs from {string.Join(", ", parents)}."`, with the parents in first-seen order. A blank `ParentName` counts as its own value.
  - `ToLayout()` already reads `d.Device` and `t.MuteAffected`, so it needs no change.
- `src/ReplayBuffer.App/Settings/TrackLayoutView.xaml` -- in `DeviceTemplate`, which the device ListBox, the picker and the per-Track checks all use, nothing changes. Instead, give the Devices ListBox its own `ItemTemplate`: the label, the note, and a `CheckBox Content="Mic" IsChecked="{Binding IsMic}"` with `Visibility` bound to `CanBeMic` through a `BooleanToVisibilityConverter` resource. In the selected-Track panel, add `CheckBox Content="Mic Mute affects this Track" IsChecked="{Binding MuteAffected}"` and a `TextBlock Foreground="DarkOrange" TextWrapping="Wrap" Text="{Binding Warning}"`. Give the Tracks ListBox an item template that shows `Name` and a "⚠" when `Warning` is non-empty, through a `DataTrigger` or a `HasWarning` bool on the item.
- `tests/ReplayBuffer.App.Tests/TrackLayoutPageTests.cs` -- add the tests here. Reuse the `Speakers` (USB DAC), `Headset` (Arctis) and `Mic` (Arctis, Input) fixtures and the `SetState`, `Create`, `Add` and `Document` helpers. In the existing view test, also check Mic on the input device, check mute-affected on a Track, and leave Everything holding Speakers and Headset (two parents) so the new bindings render.

## Tasks & Acceptance

**Execution:**
- [ ] `src/ReplayBuffer.App/Settings/TrackLayoutPage.cs` -- `IsMic`/`CanBeMic`, settable `MuteAffected`, `Warning`, `DraftChanged` -- R4 marking, R2 warning
- [ ] `src/ReplayBuffer.App/Settings/TrackLayoutView.xaml` -- the Mic checkbox, the mute checkbox, the warning text and its marker -- the editor UI
- [ ] `tests/ReplayBuffer.App.Tests/TrackLayoutPageTests.cs` -- lean tests: the two ACs below, and the existing view test extended -- verification

**Acceptance Criteria:**
- Given the page over three present devices (two outputs, one input), when a test adds them, marks the input as Mic, adds a "Chat" Track holding the mic with mute-affected checked, and saves, then the sent `trackLayout` has `IsMic` true only on the input device and `MuteAffected` true only on Chat (Everything stays as loaded), and the keys and ids are unchanged.
- Given the page with outputs from parents "USB DAC" and "Arctis" both on Everything, when it is shown, then Everything's `Warning` names both parents and a Track holding only the input has none. Save still sends one `settings.update`, and unchecking one output clears the warning.

## Implementation Notes

## Plan Change Log

## Review Triage Log

## Design Notes

Mic marking is offered only on Input Devices: a Mic Device is a microphone endpoint. AD-13 mutes it before the mix, and an Output Device can't be a mic. The warning is the epic's agreed heuristic (Decision 2026-10-25): it fires when outputs come from different parent devices, and it can miss a mixer output that already includes a separately captured mic.

## Verification

**GPU lock:** before every build or test run, check that `%USERPROFILE%\Documents\repos\ReplayBuffer\.bench-running` doesn't exist. While it does, wait.

**Scoped tests:** this is an App-only change. Don't run the Engine, Platform.Capture, Media, Platform.Audio, Contracts or Core test projects, `dotnet test --solution`, bench, or the NativeAOT publish.

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings, 0 errors
- `dotnet test --project tests/ReplayBuffer.App.Tests -c Release --no-build` -- expected: all pass

**Manual checks (if no CLI):**
- The builder's hand check (Human Check Queue): mark the BEACN Mic as a Mic Device affecting only Chat, then press the Mic Mute Hotkey during a capture and check the mic is silent only in Chat. Then add a physical output to a Track that has a BEACN output: the warning shows, and Save still works.
