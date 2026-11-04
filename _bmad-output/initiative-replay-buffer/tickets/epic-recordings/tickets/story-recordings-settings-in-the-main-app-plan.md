---
title: 'Recordings settings in the Main App'
type: 'feature'
ticket: '11'
created: '2026-11-04'
status: 'ready-for-dev'
baseline_revision: '20e6b640214d6c766cbaef652e0ebb6e7eeb2b26'
route: 'full'
route_source: 'auto'
risk: 'low'
review: ''
review_source: ''
lenses_ran: []
review_loop_iteration: 0
followup_review_recommended: false
context: ['{project-root}/CLAUDE.md']
warnings: ['oversized']
deferred: []
---

<intent-contract>

## Intent

**Problem:** A Monitor Recording captures `global.monitorRecordingMonitor` (6.9/6.10), but nothing in the Main App lists the monitors or sets it, and the Hotkeys page shows every Record and Monitor Record row whatever `recordHotkeyMode` is (R6, choosing the monitor).

**Approach:** An additive `monitors.list` command (Contracts `Recording.cs`) that the engine answers from Platform.Capture's monitor enumeration; a "Recordings" Settings page (after Feedback Sounds) whose monitor combo box comes from it and sends one Global `monitorRecordingMonitor` op; and the Hotkeys page shows each mode-dependent row only in the mode that uses it.

## Boundaries & Constraints

**Always:**
- Contracts additive only (AD-22): `DisplayMonitor(string DeviceName, string FriendlyName, int Width, int Height, bool Primary)`, `MonitorList(IReadOnlyList<DisplayMonitor> Monitors)`, `RecordingMessages.MonitorsList` = `CommandType<Empty, MonitorList>("monitors.list", …)` added to `Commands`, the types in `RecordingJsonContext`.
- The engine's reply lists every attached monitor, the primary first, then `EnumDisplayMonitors` order. `FriendlyName` is the monitor's friendly name from DisplayConfig (`QueryDisplayConfig` + `DisplayConfigGetDeviceInfo` target name, matched to the source's GDI device name), else `EnumDisplayDevices`' monitor `DeviceString`, else the device name. Width/Height are the current mode (`EnumDisplaySettings`, `ENUM_CURRENT_SETTINGS`), so DPI awareness doesn't matter.
- Page list: first `(null, "Primary monitor")`, then each monitor labelled `"<FriendlyName> — <W>×<H>"` with `" (primary)"` on the primary; when two share a friendly name, append the device name short form (`DISPLAY2`). A set `monitorRecordingMonitor` not in the list is appended as `"<device name> (not connected)"` and selected. Device names compare `OrdinalIgnoreCase`.
- Choosing an item sends exactly one `SettingsValues.SetGlobal(Keys.MonitorRecordingMonitor, deviceName)` (null for Primary monitor); none when unchanged or when a combo box clears its selection.
- The page fetches the list when it first sees a Connected engine and again whenever `EngineInstanceId` changes, plus a "Refresh" button. An `ErrorCodeException` from the fetch is shown in `Message`; the list keeps its last value.
- Hotkeys page: a `HotkeyRow.IsShown` that is false for a row the selected mode doesn't use (the engine's `HotkeyPlan.IsActive` rule mirrored privately: toggle rows in Toggle, start/stop rows in Separate, Save and Mic mute always); the view collapses hidden rows. The duplicate check still covers hidden rows (the engine refuses duplicates across modes).
- Lean tests: one or two `[Fact]`s per AC.

**Never:** No Core settings, orchestrator or capture-path change (the session already reads the snapshot's `monitorRecordingMonitor`). App doesn't reference Platform.Capture or Core. No GPU or Heavy test runs (GPU tests paused by the builder). Don't touch other pages beyond the registration line.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| List | engine connected; reply DISPLAY1 "DELL" 2560×1440 primary, DISPLAY2 "LG" 1920×1080 | items: Primary monitor, "DELL — 2560×1440 (primary)", "LG — 1920×1080"; Primary monitor selected | none |
| Pick | rev 3; choose LG | one `settings.update` at rev 3: Global `monitorRecordingMonitor` = `\\.\DISPLAY2` | none |
| Back to primary | set DISPLAY2; choose Primary monitor | `monitorRecordingMonitor` = null | none |
| Missing | set `\\.\DISPLAY3`, not in reply | `"\\.\DISPLAY3 (not connected)"` appended and selected | none |
| Fetch fails | reply throws `ErrorCodeException` | `Message` "Couldn't list monitors (<code>): <message>" | caught |
| Modes | Toggle | Record (toggle), Monitor Recording (toggle) shown; the four start/stop rows hidden; Separate is the reverse | none |

</intent-contract>

## Code Map

- `src/ReplayBuffer.Contracts/Messages/Recording.cs` -- add the records, the command (`Empty` from `ReplayBuffer.Contracts.Ipc`; see `FeedbackMessages.Preview` in `Messages/Feedback.cs:66` for a `CommandType` with `Empty`), `[JsonSerializable]` for `DisplayMonitor`, `MonitorList`, `Empty`.
- `src/ReplayBuffer.Platform.Capture/MonitorFinder.cs` -- `All()` gives `MonitorInfo(Handle, DeviceName, IsPrimary)`; add `public static IReadOnlyList<MonitorDescription> Describe()` (new record in the same file: DeviceName, FriendlyName, Width, Height, IsPrimary), primary first. `NativeMethods.txt` gains the DisplayConfig, `EnumDisplayDevices` and `EnumDisplaySettings` names (`QueryDisplayConfig`, `GetDisplayConfigBufferSizes`, `DisplayConfigGetDeviceInfo`, `DISPLAYCONFIG_SOURCE_DEVICE_NAME`, `DISPLAYCONFIG_TARGET_DEVICE_NAME`, `EnumDisplayDevices`, `EnumDisplaySettings`, `ENUM_CURRENT_SETTINGS`). Failures of the friendly-name lookup fall back, never throw.
- `src/ReplayBuffer.Engine/EngineHost.cs:576` -- beside `FeedbackMessages.Preview`, register `RecordingMessages.MonitorsList` mapping `MonitorFinder.Describe()` to `DisplayMonitor`s.
- `src/ReplayBuffer.App/Settings/RecordingsSettingsPage.cs` (new) -- `internal sealed class RecordingsSettingsPage(SettingsSender sender, IEngineSession engine) : SettingsPage`, `Title` "Recordings"; `MonitorItem(string? DeviceName, string Label)` record; `Monitors`, `SelectedMonitor`, `RefreshCommand`, `ShowState(StateSnapshot)`, `OnRender` reads `SettingsValues.Global(document, Keys.MonitorRecordingMonitor)`. Golden pattern: `FeedbackSoundsPage.cs` (device list, "(not connected)", `SelectedDevice` setter, async send with `ErrorCodeException` caught, `ShowState` only acting while `EngineStatus.Connected`).
- `src/ReplayBuffer.App/Settings/RecordingsSettingsView.xaml(.cs)` (new) -- styled like `FeedbackSoundsView.xaml`: heading "Recordings", a "Monitor Recording captures" label with the combo box (`DisplayMemberPath="Label"`) and a Refresh button, and a gray note that game Recordings are switched on per game on the Game Profiles page and their keys are on the Hotkeys page. No `Message` text (MainWindow shows it).
- `src/ReplayBuffer.App/Settings/SettingsPage.cs:109` -- add `new RecordingsSettingsPage(sender, engine)` after `FeedbackSoundsPage`.
- `src/ReplayBuffer.App/MainViewModel.cs` -- `Recordings` property (`Pages.OfType<…>().Single()`), `Recordings.ShowState(state)` after `FeedbackSounds.ShowState(state)` in `Refresh`.
- `src/ReplayBuffer.App/Settings/HotkeysPage.cs` -- `HotkeyRow.IsShown` (observable), set in `ShowRows()` from `_mode`; `SendMode` and `OnRender` already call `ShowRows` or must. `HotkeysView.xaml` -- the row `Grid` gets a `Visibility` bound to `IsShown` via `BooleanToVisibilityConverter` (as `Library/LibraryView.xaml:7`).
- Tests: `tests/ReplayBuffer.App.Tests/FeedbackSoundsPageTests.cs` (pattern: `FakeEngineSession.Reply`, `Sent`, `new MainViewModel(_engine, a => a())`, the `Sta.Run` view test writing a PNG); `HotkeysPageTests.cs`; `TrackLayoutPageTests.cs:472` page-title list (add "Recordings" after "Feedback Sounds"; check where LibrarySettings sits and keep the real order). Engine: `tests/ReplayBuffer.Engine.Tests/FeedbackSoundServeTests.cs` (EngineHost over a real pipe with `ProbeEncoders = false`). Core: `tests/ReplayBuffer.Core.Tests/SessionOrchestratorMonitorTests.cs:15` (Monitor session spec via `factory.Sessions`, `session.Spec.Snapshot`).

## Tasks & Acceptance

**Execution:**
- [ ] `src/ReplayBuffer.Contracts/Messages/Recording.cs` -- the command and payloads -- the App's only way to learn the monitors (AD-22 additive)
- [ ] `src/ReplayBuffer.Platform.Capture/MonitorFinder.cs`, `NativeMethods.txt` -- `Describe()` -- friendly names and resolutions
- [ ] `src/ReplayBuffer.Engine/EngineHost.cs` -- register the handler
- [ ] `src/ReplayBuffer.App/Settings/RecordingsSettingsPage.cs`, `RecordingsSettingsView.xaml(.cs)`, `SettingsPage.cs`, `MainViewModel.cs` -- the page and its registration
- [ ] `src/ReplayBuffer.App/Settings/HotkeysPage.cs`, `HotkeysView.xaml` -- rows shown per mode
- [ ] `tests/ReplayBuffer.App.Tests/RecordingsSettingsPageTests.cs` (new) -- the matrix's List/Pick/Back/Missing rows in two or three `[Fact]`s, plus a `Sta.Run` view test (no binding errors, writes `recordings-settings.png`)
- [ ] `tests/ReplayBuffer.App.Tests/HotkeysPageTests.cs` -- one `[Fact]`: Toggle shows the toggle rows and hides the four start/stop rows; after Separate is chosen, the reverse; `TrackLayoutPageTests.cs` title list
- [ ] `tests/ReplayBuffer.Engine.Tests/MonitorListServeTests.cs` (new) -- `monitors.list` over a test instance's pipe equals `MonitorFinder.All()`'s device names (as a set), exactly one primary, listed first, every size positive
- [ ] `tests/ReplayBuffer.Core.Tests/SessionOrchestratorMonitorTests.cs` -- one `[Fact]`: with Global `monitorRecordingMonitor` = `\\.\DISPLAY2`, a Monitor toggle opens a session whose `Spec.Snapshot.MonitorRecordingMonitor` is it

**Acceptance Criteria:**
- Given the engine running, when the App sends `monitors.list`, then every attached monitor comes back with its device name, a friendly name, a positive resolution, and exactly one marked primary, listed first.
- Given the Recordings page, when the builder picks a monitor, then one `settings.update` sets `monitorRecordingMonitor` to its device name, and the next Monitor Recording's session is opened with that monitor (the capture session's `MonitorFinder.Find` of the snapshot value is 6.9's, unchanged).
- Given the Hotkeys page in Toggle mode, when it renders, then the Monitor Recording (start) and (stop) rows are hidden, and in Separate mode they show and the Monitor Recording (toggle) row is hidden.
- Given the Settings tab, when it opens, then "Recordings" is listed after "Feedback Sounds" and its view renders without binding errors.

## Implementation Notes

## Plan Change Log

## Review Triage Log

## Design Notes

- The Hotkeys page hides the Record rows by mode too, not only the Monitor ones: both follow the one `recordHotkeyMode` radio, and showing "Not used in this Record mode" for half of them beside hidden Monitor rows would read inconsistently. The status text stays for a binding the engine reports inactive.
- "Primary monitor" (null) follows whichever monitor Windows makes primary; picking the primary's own entry pins its device name. Both are offered because they behave differently after a display change.

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings
- `dotnet test --project tests/ReplayBuffer.App.Tests -c Release --no-build` -- all pass
- `dotnet test --project tests/ReplayBuffer.Engine.Tests -c Release --no-build --filter-class "*MonitorListServeTests"` -- passes (default, non-Heavy)
- `dotnet test --project tests/ReplayBuffer.Core.Tests -c Release --no-build` -- all pass
- `pwsh tools/publish-engine.ps1` -- 0 IL warnings (engine code changed)

**Manual checks (if no CLI):**
- Inspect `recordings-settings.png` and `hotkeys-settings.png` from the view tests. Human Check row: the builder picks their second monitor, binds the Monitor Record keys in each mode, records it, and checks the pages by eye.
