---
title: 'Feedback Sounds settings page'
type: 'feature'
ticket: '11'
created: '2026-11-03'
status: done
baseline_revision: 'f2ff2fe2a20af05401b41727ae43dc7c37a58249'
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

**Problem:** The engine plays five Feedback Sounds (5.6) gated by `feedbackSounds` on `feedbackSoundDevice`, but the Main App has no way to switch a sound off, pick the device, or hear a sound, so R4's "each can be switched off" isn't reachable by the builder.

**Approach:** A `FeedbackSoundsPage` in the Settings host, after Game Profiles: one on/off switch and one Play button per sound, a sound-device combo box (from the snapshot's live `devices` list, Output Devices only, "Default communications device" first), and the active `feedback-device-captured` warning's message. Edits go through `SettingsPage.SendAsync`; Play sends `feedback.preview`.

## Boundaries & Constraints

**Always:**
- Sound rows in this order and labels: Clip saved (`ClipSaved`), Save failed (`SaveFailed`), Recording started (`RecordingStarted`), Recording stopped (`RecordingStopped`), Disk full (`DiskFull`).
- Switching one sound sends exactly one Global op: `SettingsValues.SetGlobal(Keys.FeedbackSounds, current with { <flag> = value })`. No op when the value is unchanged.
- Device list: first `(null, "Default communications device")`, then every `AudioFlow.Output` device with `Available` true, labelled by `FriendlyName` (with " — ParentName" when two listed devices share a friendly name and the parent is non-empty). If the set `feedbackSoundDevice` isn't in the list, append it labelled "<friendly name or id> (not connected)". Endpoint ids compare `OrdinalIgnoreCase`.
- Choosing a device sends one Global op `SetGlobal(Keys.FeedbackSoundDevice, endpointId)` (null for the default); none when unchanged.
- Play sends `FeedbackMessages.Preview` with `new FeedbackPreview(sound)` whether the sound is on or off. An `ErrorCodeException` is caught and shown in `Message` ("Couldn't play (<code>): <message>").
- `CapturedWarning`: the `Message` of the first active warning in the snapshot with `Code == ErrorCodes.FeedbackDeviceCaptured`, else null; shown in dark red above the switches.
- Lean tests: a few `[Fact]`s on the view model through `FakeEngineSession`; nothing plays audio (the fake never reaches an engine).

**Never:** No Contracts, Core or Engine changes (5.6 already added `feedback.preview`, the keys, the code and the engine-side gating). No sounds played by the App (AD-15). No volume or custom sound file. Don't touch other pages beyond the one-line registration.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Switch off | all on, rev 3; Save failed switched off | one `settings.update` at rev 3: Global `feedbackSounds` with `saveFailed:false`, others true | none |
| Pick device | devices: Speakers (out), Mic (in); choose Speakers | `feedbackSoundDevice` = Speakers' id; list is Default, Speakers (no Mic) | none |
| Back to default | device set; choose Default | `feedbackSoundDevice` = null | none |
| Missing device | device set to an id not in `devices` | that id appended as "(not connected)" and selected | none |
| Play | Clip saved off; Play on its row | `FeedbackPreview(ClipSaved)` sent | `ErrorCodeException` → `Message` |
| Captured | snapshot has a `feedback-device-captured` warning | `CapturedWarning` is its message | none |

</intent-contract>

## Code Map

- `src/ReplayBuffer.App/Settings/SettingsPage.cs` -- base class (`Render`/`OnRender`/`SendAsync`/`Message`) and `SettingsPages.Create`: add `new FeedbackSoundsPage(sender, engine)` last.
- `src/ReplayBuffer.App/Settings/SettingsSender.cs` -- `SettingsValues.Global` / `SetGlobal`; reuse.
- `src/ReplayBuffer.App/Settings/ReplayBufferSettingsPage.cs` + `ReplayBufferSettingsView.xaml(.cs)` -- golden pattern for a page, `ShowState` called from `MainViewModel.Refresh` before `Render`, and the view (`UserControl`, `x:ClassModifier="internal"`, ScrollViewer + StackPanel, SemiBold headings, DarkRed warning text).
- `src/ReplayBuffer.App/Settings/TrackLayoutPage.cs:322-331` -- shared-friendly-name labelling pattern.
- `src/ReplayBuffer.App/Status/StatusLine.cs` -- how a `StatusWarning` row binds a `RelayCommand` and swallows `ErrorCodeException` on send; mirror it for the Play command (async void-safe pattern).
- `src/ReplayBuffer.App/Observable.cs` -- `Observable.Set`, `RelayCommand(Action)`.
- `src/ReplayBuffer.App/MainViewModel.cs` -- add `public FeedbackSoundsPage FeedbackSounds { get; }` (`Pages.OfType<...>().Single()`), and in `Refresh` before the page loop: `FeedbackSounds.ShowState(state)` (devices + warnings).
- `src/ReplayBuffer.Contracts/Settings/Keys.cs:182-189`, `SettingValues.cs` `FeedbackSounds` record, `Messages/Feedback.cs` (`FeedbackSound`, `FeedbackPreview`, `FeedbackMessages.Preview`, `Warning`), `Messages/Audio.cs` (`AudioDevice`, `AudioDeviceList`), `ErrorCodes.FeedbackDeviceCaptured` -- read only.
- `tests/ReplayBuffer.App.Tests/ReplayBufferSettingsPageTests.cs` -- test pattern (`FakeEngineSession`, `Reply`, `Sent`, `new MainViewModel(_engine, a => a())`, the `Sta.Run` + `BindingErrors` view test).
- `tests/ReplayBuffer.App.Tests/TrackLayoutPageTests.cs:472` -- page-title list; add "Feedback Sounds".

## Tasks & Acceptance

**Execution:**
- [x] `src/ReplayBuffer.App/Settings/FeedbackSoundsPage.cs` (new) -- page (`Title` "Feedback Sounds"), `FeedbackSoundRow` (Sound, Label, `IsOn` get/set, `PlayCommand`), `FeedbackDeviceItem(string? EndpointId, string Label)`, `Devices`, `SelectedDevice`, `CapturedWarning`, `ShowState(StateSnapshot)`.
- [x] `src/ReplayBuffer.App/Settings/FeedbackSoundsView.xaml(.cs)` (new) -- heading, warning, device combo box, an `ItemsControl` of rows (CheckBox with the label bound to `IsOn`, a "Play" button), then `Message`.
- [x] `src/ReplayBuffer.App/Settings/SettingsPage.cs`, `src/ReplayBuffer.App/MainViewModel.cs` -- registration and refresh, as in the Code Map.
- [x] `tests/ReplayBuffer.App.Tests/FeedbackSoundsPageTests.cs` (new) -- the matrix as three or four `[Fact]`s, plus one `Sta.Run` view test asserting no binding errors and writing `feedback-sounds.png` beside the test binary (like the Replay Buffer page's).
- [x] `tests/ReplayBuffer.App.Tests/TrackLayoutPageTests.cs` -- add the title.

**Acceptance Criteria:**
- Given all sounds on, when the builder switches Save failed off on the page, then one `settings.update` with Global `feedbackSounds` (`saveFailed: false`) is sent; the engine then doesn't play it (5.6's `FeedbackSoundsTests` covers the engine side).
- Given the live Output Device list, when the builder picks a device, then `feedbackSoundDevice` is set to its endpoint id, and "Default communications device" is first and sets it to null.
- Given a sound switched off, when Play is clicked on its row, then `feedback.preview` is sent for that sound.
- Given an active `feedback-device-captured` warning, when the page shows, then its message is on the page.
- Given the Settings tab, when it opens, then "Feedback Sounds" is the last page and its view renders without binding errors.

## Implementation Notes

- The view leaves out its own `Message` text: `MainWindow.xaml` already shows `SelectedPage.Message` below every page, so a second copy would show it twice. Play errors still reach the builder through that line.
- `ShowState` runs after `Hotkeys.ShowStatus` in `MainViewModel.Refresh`, before the page loop. The device list is built again on each render too, because the "(not connected)" entry depends on `feedbackSoundDevice`.

## Plan Change Log

## Review Triage Log

### 2026-11-04 — Review pass
- verdicts: 5 findings — high 0, medium 0, low 5, false 0, maybe-false 0
- findings:
  - `[low]` `[patch]` `CapturedWarning` kept the last snapshot's warning while the engine was disconnected or restarting, unlike the status line — fixed: `ShowState` sets it only while `EngineStatus.Connected`.
  - `[low]` `[patch]` A failed Play's message stayed after later Plays succeeded — fixed: a successful preview clears `Message`.
  - `[low]` `[reject]` The shared-name check ignores the appended "(not connected)" entry, and an Input endpoint set by hand reads "(not connected)" — the suffix already tells the two apart, and an Input id in `feedbackSoundDevice` can't come from this page; fixing it adds branches for a case nobody meets.
  - `[low]` `[reject]` A "(not connected)" entry stays in the list after a new pick until `settings.changed` re-renders — self-heals within the round trip, and a failed send restores the old selection.
  - `[low]` `[reject]` AC 4 is checked on the view model, not the rendered text — the binding-error check covers the binding; lean-tests rule.

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings
- `dotnet test --project tests/ReplayBuffer.App.Tests -c Release --no-build` -- expected: all pass

**Manual checks (if no CLI):**
- Inspect `feedback-sounds.png` written by the view test. Hearing each sound on the chosen device is queued for the builder (Human Check Queue).

## Auto Run Result

**Summary:** A Feedback Sounds page, last in Settings: a switch and a Play button per sound (Clip saved, Save failed, Recording started, Recording stopped, Disk full), the sound device from the live Output Device list with "Default communications device" first (a set device that's gone shows "(not connected)"), and the active `feedback-device-captured` warning. Switches send one Global `feedbackSounds` op, the device one `feedbackSoundDevice` op, and Play sends `feedback.preview`. The App plays nothing itself.

**Files changed:**
- `src/ReplayBuffer.App/Settings/FeedbackSoundsPage.cs`: new page, `FeedbackSoundRow`, `FeedbackDeviceItem`.
- `src/ReplayBuffer.App/Settings/FeedbackSoundsView.xaml(.cs)`: new view.
- `src/ReplayBuffer.App/Settings/SettingsPage.cs`: registers the page last.
- `src/ReplayBuffer.App/MainViewModel.cs`: `FeedbackSounds` property and `ShowState` in `Refresh`.
- `tests/ReplayBuffer.App.Tests/FeedbackSoundsPageTests.cs`: new, three view-model tests and a view test that writes `feedback-sounds.png`.
- `tests/ReplayBuffer.App.Tests/TrackLayoutPageTests.cs`: page-title list.

**Review:** quick lens, 5 low findings: 2 patched (applied in the main session), 3 rejected (reasons in the triage log), none deferred.

**Follow-up review recommended:** false (2 low patched, 0 high, 0 medium).

**Verification:** Release build 0 warnings. App.Tests 101 passed, 9 explicit skipped, 0 failed, run four times; one run after the patches had one failure that couldn't be identified, and the next three were clean. No Contracts, Core or Engine change, so no other projects and no NativeAOT publish. The view's PNG was inspected by the implementer. Matrix: every row is covered by `FeedbackSoundsPageTests`, all in the default run.

**Residual risks:** Nobody has heard the sounds through the page or looked at it on screen (Human Check Queue, added at merge). The engine side of "switched off isn't played" is 5.6's test. Parallel stories adding Settings pages will conflict on `SettingsPages.Create` and the title-list test.

