---
title: 'Monitor changes'
type: 'feature'
ticket: '5'
created: '2026-11-04'
status: done
baseline_revision: '76cbba8'
route: 'full'
route_source: 'auto'
risk: 'medium'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
followup_review_recommended: false
context:
  - '{project-root}/CLAUDE.md'
warnings: []
deferred: []
---

<intent-contract>

## Intent

**Problem:** Display changes (PLAN.md T-03) aren't handled on purpose. A Monitor Recording whose monitor is unplugged ends only by accident: WGC's item closes and it stops as `session-ended`, or Desktop Duplication fails. Nobody is warned. The Recordings page's monitor list goes stale until Refresh. The Overlay isn't re-placed after a display change unless something it shows also changes: `OverlayViewModel.DisplaysChanged` re-asks the plane query, but `Refresh` raises no property change when nothing else moved, so `OverlayWindow.Place` never runs.

**Approach:**
- A Core display port, `IDisplayMonitors` (`List()` and `Subscribe(Action)`), sits beside 8.4's power port. The orchestrator posts `DisplaysChanged` on each change.
- When the Monitor session's monitor (`ICaptureSession.MonitorDevice`) is no longer in the list, the orchestrator ends the session. The Monitor Recording stops at its last frame with `monitor-removed`, its file kept, and `warning.raised { code: monitor-removed }` is raised.
- A Monitor session that closes or fails while its monitor is gone ends the same way. That covers WGC seeing the monitor go before the display change arrives.
- The game session is never touched, because game capture is of a window.
- The engine publishes `monitors.changed { monitors }`, a new snapshot field, and `monitors.list` reads the same port. The Recordings page follows the event.
- The Overlay re-places itself on every display change through a new `DisplayChanges` counter on its view model. Placement still uses 5.1's rule (the Target Game's monitor, else the primary) at that monitor's DPI, now read through an injectable locator.

## Boundaries & Constraints

**Always:**
- AD-19: the orchestrator decides; the port only reports.
- AD-22 additive Contracts: `ErrorCodes.MonitorRemoved = "monitor-removed"` (in `All`), `RecordingStopReasons.MonitorRemoved`, `RecordingMessages.MonitorsChanged` ("monitors.changed", in `Events`), and `StateSnapshot.Monitors` (null before a change).
- The displays are only read: `EnumDisplayMonitors`, DisplayConfig queries, and `WM_DISPLAYCHANGE` received by a hidden window that is never shown.
- Lean tests. Heavy tests are `[Fact(Explicit = true)]` + `[Trait("Category","Heavy")]`. Engine-hosting tests use a Guid test instance.

**Never:**
- Never change the display configuration: no `ChangeDisplaySettings`, `SetDisplayConfig`, `DisplaySwitch`, monitor enable or disable, or driver settings. Monitor changes in tests come from the fake port. The one real-window test posts `WM_DISPLAYCHANGE` to its own hidden window only, never broadcasts it, and changes nothing.
- A removed monitor never restarts the Monitor Recording on another monitor.
- No change to the game session's capture.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Recorded monitor removed | Monitor Recording of DISPLAY2; the list drops it | Monitor session ended "monitor removed"; `Stopped(M, monitor-removed)` once its writer posts; one `monitor-removed` Warn; MonitorRecording Off | — |
| Another monitor removed | Monitor Recording of DISPLAY1; DISPLAY2 dropped | nothing ends; no warning | — |
| Primary changes | either session running | nothing ends | — |
| WGC closes first | the session's `Closed` arrives while the list lacks its monitor | ends as removed, not `session-ended` | — |
| Monitor session fails, monitor present | `Failed` | as before: `recordings.Failed`, `session-ended` | — |
| Game capture through changes | game session (and Recording) running | untouched; buffering continues | — |
| Empty list | a read mid-change returns no monitors | treated as unknown; nothing ends | — |
| Monitor unknown | `MonitorDevice` null or empty | nothing ends | — |
| Port can't subscribe | window creation fails | logged at Warning; no changes reported | — |
| `monitors.changed` | a burst of `WM_DISPLAYCHANGE` | one report, `Settle` (300 ms) after the last; published only when the list differs from the last published | — |
| Recordings page | `monitors.changed` while connected | list replaced without a fetch; a chosen monitor that's gone shows "(not connected)" | — |
| Overlay | display change while it shows | re-placed on the Target's monitor (else primary) at its DPI | hidden: nothing to place |

</intent-contract>

## Code Map

- `src/ReplayBuffer.Contracts/ErrorCodes.cs`: `MonitorRemoved`, also in `All`.
- `src/ReplayBuffer.Contracts/Messages/Recording.cs`: `RecordingStopReasons.MonitorRemoved` and `RecordingMessages.MonitorsChanged`.
- `src/ReplayBuffer.Contracts/Messages/Engine.cs`: `StateSnapshot.Monitors`.
- `src/ReplayBuffer.Core/Sessions/CaptureSessionPorts.cs`: `ICaptureSession.MonitorDevice => null` and `IDisplayMonitors`.
- `src/ReplayBuffer.Core/Sessions/SessionOrchestrator.cs`:
  - the `DisplaysChanged` message;
  - the ctor's trailing `IDisplayMonitors? displays`, subscribed after the thread starts and disposed beside the power subscription;
  - `EndMonitorIfRemoved`, used by `DisplaysChanged` and by the Monitor session's `SessionClosed` and `SessionFailed`.
- `src/ReplayBuffer.Core/Feedback/FeedbackRouter.cs`: knows `monitor-removed` (an Info notice, "the monitor was removed").
- `src/ReplayBuffer.Platform.Capture/DisplayMonitors.cs` (new): the Windows adapter.
  - `List()` is `MonitorFinder.Describe()`.
  - While anyone is subscribed, a `DisplayChanges` thread owns a hidden `WS_POPUP`/`WS_EX_TOOLWINDOW` top-level window. `WM_DISPLAYCHANGE` reaches top-level windows only.
  - A `SetTimer` settle coalesces a burst of changes.
  - `LibraryImport` and an `[UnmanagedCallersOnly]` window procedure keep it AOT-safe.
  - `SimulateDisplayChange` is the test hook: it posts to its own window only.
- `src/ReplayBuffer.Engine/Sessions/CaptureSession.cs`: `MonitorDevice` is the resolved `MonitorFinder` device name.
- `src/ReplayBuffer.Engine/EngineHost.cs`:
  - `EngineHostOptions.Displays`;
  - the port is passed to the orchestrator;
  - `monitors.list` reads `displays.List()`;
  - a subscription publishes `monitors.changed` when the list differs, disposed first in `DisposeAsync`.
- `src/ReplayBuffer.App/Overlay/OverlayViewModel.cs`: `DisplaysChanged` bumps `DisplayChanges`. It is already wired to `SystemEvents.DisplaySettingsChanged` in `ShellApp`.
- `src/ReplayBuffer.App/Overlay/OverlayWindow.cs`: an optional `Func<int?, MonitorArea> locate`, defaulting to `Monitors.ForProcess`.
- `src/ReplayBuffer.App/Settings/RecordingsSettingsPage.cs`: `ShowState` takes a new `Monitors` list without fetching.
- `src/ReplayBuffer.Platform.Desktop/OverlayWindowStyle.cs`: `Bounds(hwnd)`, for the test.

## Tasks & Acceptance

**Execution:**
- [x] Contracts: the code, the reason, the event and the snapshot field (AD-22).
- [x] Core: the port, `MonitorDevice`, and the orchestrator's handling. `FeedbackRouter` knows the reason.
- [x] Platform.Capture: `DisplayMonitors`.
- [x] Engine: `MonitorDevice`, the wiring, `monitors.list` and `monitors.changed`.
- [x] App: Overlay re-placement and the Recordings page.
- [x] Tests, default run:
  - Core:
    - `ARemovedMonitorEndsItsMonitorRecordingWithMonitorRemovedAndGameCaptureContinues`: a primary change first, then the recorded monitor removed. The game session runs on.
    - `AnotherMonitorsRemovalChangesNothingAndACloseAfterItsOwnRemovalEndsAsRemoved`.
  - Engine: `ADisplayChangePublishesTheNewListAndMonitorsListReadsTheDisplayPort`, a Guid test instance with the fake port.
  - Platform.Capture: `ItListsThePrimaryFirstAndReportsABurstOfDisplayChangesOnce`, the real adapter with a self-posted message.
  - App:
    - `ADisplayChangePlacesTheWindowOnTheNewPrimaryAtItsDpi`;
    - `AMonitorsChangedEventUpdatesTheListWithoutAFetch`.
  - Contracts: a sample payload for `monitors.changed`. The registry tests cover the new field and event.
- [x] Heavy, in `MonitorRecordingCaptureTests.cs`, written and not run (only the GPU runner runs Heavy GPU tests). Both take `BenchLock`.
  - `ARemovedMonitorEndsItsMonitorRecordingPlayableWithMonitorRemoved`:
    - A Monitor Recording of the real primary runs for about 3 s. The fake port then drops that monitor; the real one stays.
    - Expected: `Stopped(M, monitor-removed)` and one `monitor-removed` warning. The file decodes cleanly and holds at least 2.5 s at a constant 30 fps with both Tracks in step.
  - `AGameRecordingRunsOnThroughEachDisplayChange`:
    - A game Recording runs on a synthetic window while the port adds a monitor, moves the primary to it, and removes it.
    - Expected: one session throughout, Replay still Buffering, and a file that decodes cleanly at a constant 30 fps.

**Acceptance Criteria:**
- Given a Monitor Recording, when the display port reports its monitor removed, then it ends with a playable file, `recording.stopped { reason: monitor-removed }` and `warning.raised { code: monitor-removed }`.
- Given a removed monitor that isn't being recorded, then only the list changes: `monitors.changed` carries it and the picker shows it.
- Given a primary change, then the Overlay is placed again on the Target's monitor, else the new primary, at that monitor's DPI.
- Given a game session, then display changes never end or restart it.
- Given the default run, then the 0-warning Release build and the touched test projects pass, and the NativeAOT publish has no IL warnings.

## Design Notes

- **Why Platform.Capture, not Platform.Desktop:** the ticket names a Platform.Desktop adapter. Platform.Desktop is the App's Win32 layer: it references neither Core nor the engine, and the engine doesn't reference it. Platform.Capture already owns monitor enumeration (`MonitorFinder`) and references Core, so the adapter lives beside it. The App keeps its own existing signal, `SystemEvents.DisplaySettingsChanged`, which is the same `WM_DISPLAYCHANGE`, for the Overlay.
- **Why a hidden top-level window:** `WM_DISPLAYCHANGE` is broadcast to top-level windows, and the Hotkey thread's message-only window never receives it. The new window is never shown, has no taskbar entry, and only its own thread touches it.
- **Why check removal on close and fail too:** WGC's monitor item can close before the broadcast arrives, so the reason must not depend on which comes first. An empty list is treated as a read that failed mid-change, never as every monitor gone.
- **What "ends at its last frame" means:** `EndMonitor` ends the Monitor session, which finishes the output; the writer then finishes the file. Desktop Duplication's `ACCESS_LOST` handling (6.10) keeps a still-present monitor's Recording in one file, and nothing here changes it.
- **Human Checks for the builder:**
  1. During a game and a Monitor Recording of the second monitor, unplug the second monitor. The Recording should end with the `monitor-removed` warning and a playable file; the game's Replay Buffer should keep buffering, and a Clip saved afterwards should play.
  2. Plug it back in. The Recordings page should list it again without Refresh.
  3. Change the primary monitor in Windows Settings while a game runs. The Overlay dot should stay on the game's monitor, or move to the new primary when there's no game, at the right size for that monitor's scaling. A Clip saved afterwards should play.
  4. Change the resolution or refresh rate of the monitor being Monitor-Recorded. The Recording should continue in one file (6.10's `ACCESS_LOST` path on Desktop Duplication; WGC resizes).

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` (expected: 0 warnings).
- `dotnet test --project tests/ReplayBuffer.<P>.Tests -c Release --no-build`, for each of Contracts, Core, Platform.Capture, Platform.Desktop, Engine and App (expected: pass).
- `pwsh tools/publish-engine.ps1` (expected: no IL or AOT warnings).
- Heavy, for the GPU runner, one project:

  `dotnet test --project tests/ReplayBuffer.Engine.Tests -c Release --no-build --explicit only --filter-method "ReplayBuffer.Engine.Tests.MonitorRecordingCaptureTests.ARemovedMonitorEndsItsMonitorRecordingPlayableWithMonitorRemoved" --filter-method "ReplayBuffer.Engine.Tests.MonitorRecordingCaptureTests.AGameRecordingRunsOnThroughEachDisplayChange"`

## Review Triage Log

### 2026-11-04 — Self-review pass (quick)
- verdicts: 3 findings — high 0, medium 1, low 2.
- findings:
  - `[medium]` `[patch]` The Overlay wasn't re-placed on a display change when nothing it showed changed: `DisplaysChanged` → `Refresh` raised no property change. Fixed with the `DisplayChanges` counter, and covered by the App test.
  - `[low]` `[patch]` A WGC close that arrives before the display change would have ended the Recording as `session-ended`. Now `EndMonitorIfRemoved` is checked on close and on fail, and the second Core test covers it.
  - `[low]` `[defer]` If `server.Start` throws during `EngineHost.Start`, the `monitors.changed` subscription isn't disposed; its background thread and hidden window live until the process exits. The engine exits on a start failure anyway.

## Auto Run Result

- **Summary:** monitor changes (T-03) are handled.
  - A Core display port, `IDisplayMonitors`, posts `DisplaysChanged` to the orchestrator. In Windows, `DisplayMonitors` (Platform.Capture) is a hidden top-level window listening for `WM_DISPLAYCHANGE`, with a 300 ms settle.
  - A Monitor Recording whose monitor leaves the list ends at its last frame with `monitor-removed` and the `monitor-removed` warning, and its file is kept. A close or failure while its monitor is gone ends it the same way.
  - Game capture is never touched.
  - The engine publishes `monitors.changed`, a new snapshot field, `monitors`. `monitors.list` reads the same port, and the Recordings page follows the event.
  - The Overlay is re-placed on every display change, on the Target's monitor (else the primary) at its DPI.
- **Verification (2026-11-04):**
  - Release build: 0 warnings.
  - Default runs:
    - Contracts: 124/124.
    - Core: 159/159.
    - Platform.Capture: 4/4.
    - Platform.Desktop: 13/13.
    - Engine: 180 passed, 41 explicit skipped.
    - App: 128 passed, 18 explicit skipped.
  - `tools/publish-engine.ps1`: published, no IL warnings.
  - Heavy: written, not run. They are listed under Verification for the GPU runner.
- **Matrix audit:** every row has a default-run test except "Port can't subscribe" (logged no-op, by inspection) and "Monitor unknown" (guard, by inspection). "Ends with a playable file" is proven only by the Heavy test.
- **Residual risks:** no real hot-plug has happened. Two things are open:
  - The order and timing of WGC's item close, Desktop Duplication's `ACCESS_LOST` and the broadcast on a real unplug. Both orders are handled.
  - Whether `MonitorFinder.Describe` lists a monitor that is mid-removal. An empty list is ignored.
- **Human Checks:** the four in Design Notes.
