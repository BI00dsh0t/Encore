---
title: 'Main App status line'
type: 'feature'
ticket: '10'
created: '2026-11-03'
status: 'in-progress'
baseline_revision: '143484bf848bc958bbbe74a457a769fa27c3b272'
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

**Problem:** The Main App shows capture state and warnings only as raw text on its Status tab (story 1.7), with no times, no ordering and no way to dismiss, so the builder can't see at a glance what the engine is doing or clear a warning (R5).

**Approach:** A `StatusLine` view model, rendered from the snapshot alone (AD-3), drives a status line docked above the tabs on every page: one line of capture state, then the active warnings newest first, each with its local time and a Dismiss button (`warning.dismiss`) when dismissable. The Status tab goes.

## Boundaries & Constraints

**Always:**
- Everything shown comes from `IEngineSession.Status`/`Problem`/`State`; the row goes only when the snapshot drops the warning (`warning.dismissed`), never optimistically. A reconnect's snapshot re-renders the whole list.
- State text, parts joined by " · ":
  - Engine not connected → the existing connection text (e.g. "Restarting the engine… <problem>") replaces the capture parts.
  - Replay `Off` → "Replay Buffer off"; `Buffering` → "Buffering on <TargetGame.Name>" or "Buffering" with no Target Game; `Draining` → "Draining".
  - "Recording" while `Recording` is running, "Monitor Recording" while `MonitorRecording` is running, "Mic muted" while `MicMuted`.
- Every active warning shows (device-dropout, engine-restarted, app-restarted, memory-margin, reset-gap, clip-failed, disk-full, feedback-device-captured and any other), sorted by `At` descending; row text is its `Message`, time `At.ToLocalTime()` as `HH:mm:ss` (invariant culture), severity shown.
- Dismiss sends `FeedbackMessages.Dismiss` with the warning's `Id`; an `ErrorCodeException` (already gone, disconnected) is swallowed.
- Lean tests: one or two view-model tests per AC; one explicit `Heavy` test that renders the window to a PNG for the HITL screenshot.

**Never:** No Contracts, Core or Engine changes. No new warnings raised by the App. Don't touch the Overlay, the Library banner, or Settings pages. No sounds.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Buffering with a game, mic muted | Replay Buffering, TargetGame "Game.exe", MicMuted | "Buffering on Game.exe · Mic muted" | none |
| Everything off | `CaptureState.Initial` with MicMuted false | "Replay Buffer off" | none |
| Two warnings | At 10:00 and 10:05 | the 10:05 row first | none |
| Dismiss | Dismissable warning, Dismiss clicked | `WarningDismiss(id)` sent; row stays until the snapshot drops it | swallowed `ErrorCodeException` |
| Not dismissable | `Dismissable: false` | row has no Dismiss button (`CanDismiss` false) | none |
| Reconnect | new snapshot with a different list | list equals the new snapshot's | none |

</intent-contract>

## Code Map

- `src/ReplayBuffer.App/MainViewModel.cs` -- Status-tab properties `Status`, `EngineDetails`, `Capture`, `TargetGame`, `SettingsRevision`, `Warnings` and their `Refresh` lines go; add `public StatusLine StatusLine { get; }` and call `StatusLine.Render(_engine.Status, _engine.Problem, _engine.State)` at the top of `Refresh`. Keep the rest of `Refresh` unchanged (other stories edit the page lines: 4.11 runs in parallel).
- `src/ReplayBuffer.App/MainWindow.xaml` -- the Status `TabItem` goes; wrap the `TabControl` in a `DockPanel` with the status line (`DockPanel.Dock="Top"`) above it. Library becomes the first tab.
- `src/ReplayBuffer.App/Observable.cs` -- `Observable.Set`, `RelayCommand(Action)`; reuse.
- `src/ReplayBuffer.App/Library/LibraryPage.cs:436-450` -- golden pattern for sending `FeedbackMessages.Dismiss` and swallowing `ErrorCodeException`.
- `src/ReplayBuffer.App/Library/LibraryView.xaml(.cs)` -- pattern for a `UserControl` view bound to its page.
- `src/ReplayBuffer.Contracts/Messages/Feedback.cs` -- `Warning(Id, Code, Severity, Source, Message, At, Overlay, Sound, Dismissable)`, `WarningDismiss`; read only.
- `src/ReplayBuffer.Contracts/Messages/Capture.cs` -- `CaptureState`, `ReplayState`, `RecordingState`; read only.
- `tests/ReplayBuffer.App.Tests/MainViewModelTests.cs` -- `ARefreshRendersTheSnapshotAndTheSettingsPages`, `TheStatusSays...` assert the removed properties; move those asserts to `model.StatusLine`.
- `tests/ReplayBuffer.App.Tests/FakeEngineSession.cs` -- fake with `Sent`, `Reply`, `RaiseChanged`.
- `tests/ReplayBuffer.App.Tests/MainWindowTests.cs`, `Sta.cs` -- how to host `MainWindow` on an STA thread off-screen.

## Tasks & Acceptance

**Execution:**
- [ ] `src/ReplayBuffer.App/Status/StatusLine.cs` (new) -- `StatusLine(IEngineSession)` : `Observable` with `string State`, `IReadOnlyList<StatusWarning> Warnings`, `Render(EngineStatus, string?, StateSnapshot?)`; nested/sibling `StatusWarning` row (`Id`, `Time`, `Severity`, `Message`, `CanDismiss`, `DismissCommand`). Keep the connection-text switch from `MainViewModel` here.
- [ ] `src/ReplayBuffer.App/Status/StatusLineView.xaml(.cs)` (new) -- `UserControl`: the state line (SemiBold), then an `ItemsControl` of rows (time, severity, message wrapping, Dismiss button visible only when `CanDismiss`, via `BooleanToVisibilityConverter`). Cap the list's height (e.g. `MaxHeight` 120 with a `ScrollViewer`) so it doesn't push the pages away.
- [ ] `src/ReplayBuffer.App/MainViewModel.cs`, `src/ReplayBuffer.App/MainWindow.xaml` -- as in the Code Map.
- [ ] `tests/ReplayBuffer.App.Tests/StatusLineTests.cs` (new) -- the matrix rows as a few `[Fact]`s; plus an explicit `[Fact(Explicit = true)] [Trait("Category","Heavy")]` that renders `MainWindow` with a sample snapshot to `status-line.png` in `Path.GetTempPath()` (or `RB_SCREENSHOT` env path) via `RenderTargetBitmap`, for the HITL screenshot.
- [ ] `tests/ReplayBuffer.App.Tests/MainViewModelTests.cs` -- update the asserts for the removed properties.

**Acceptance Criteria:**
- Given each capture state (off, buffering on a Target Game, draining, Recording, Monitor Recording, mic muted), when the snapshot renders, then the status line shows its text.
- Given several active warnings, when the snapshot renders, then they list newest first, each with its time and message.
- Given a dismissable warning, when Dismiss is clicked, then `warning.dismiss` is sent with its id, and the row goes when `warning.dismissed` reaches the snapshot; a non-dismissable warning has no Dismiss button.
- Given the App reconnects, when the new snapshot arrives, then the list is the new snapshot's.
- Given the Main App window on any tab, when it is open, then the status line is visible above the page.

## Implementation Notes

## Plan Change Log

## Review Triage Log

## Design Notes

- The status line is a separate view model and view so `MainViewModel` changes stay small while 4.11 edits it.
- The window is capture-excluded (R6), so a desktop screenshot shows black; the HITL screenshot renders the WPF tree in-process instead.

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings, 0 errors
- `dotnet test --project tests/ReplayBuffer.App.Tests -c Release --no-build` -- expected: all pass
- `dotnet test --project tests/ReplayBuffer.App.Tests -c Release --no-build --explicit only --filter-method "*StatusLine*"` (or the MTP equivalent) -- expected: PNG written
