---
title: 'Refactor sweep (epic 11)'
type: 'refactor'
ticket: '5'
created: '2026-11-05'
status: built
baseline_revision: '4f0de30'
route: 'quick'
risk: 'low'
---

# Story 11.5: refactor sweep

Cleanup only, with no behaviour change, scoped at the start from 11.1 to 11.4's plans, their deferred review findings and the epic Notes.

## Scope and outcome

| # | Item (source) | Outcome |
|---|---|---|
| 1 | One-monitor code beside the compositor (ticket) | **Changed.** `CaptureSession.Open` had two one-screen branches, a resolved single screen and the old `monitorRecordingMonitor`. They are now one pass-through: the resolved screen's device name, else the old key's, through `MonitorFinder.Find` as before. Today's one-screen output is unchanged, and 11.3 relies on it. |
| 2 | Unused spike members in `CompositeCapture` | **Removed:** `AnyDuplication` and `Compositor`. Neither had a caller; the session reads `Sources` and `LiveDevices`. |
| 3 | The spike's dev-only path (ticket): `CaptureSessionOptions.CompositeMonitors` / `CompositeDuplication`, `debug-capture --monitors` / `--monitor-api`, `measure-path-a.ps1 -mon<mode>` and `bench fps --conditions` | **Kept, recorded.** 11.3 didn't absorb it, because a Monitor session composites its resolved screens through the same `CompositeCapture`. The dev path is the only way to reproduce the NFR-1 cost figures (stage split included), and the builder's NFR-1 decision is still open (Decisions Awaiting Review). Its dead branch was removed: a Monitor session with the dev flag set, which no caller makes. Its doc now says what it's kept for. No later ticket (11.6, 8.9 to 8.11) names it. |
| 4 | Leftovers of the old single-screen picker (`monitorRecordingMonitor` in the App) | **None found.** 11.4 replaced 10.9's combo box. `MonitorPicker` only reads the old key, to pre-tick the legacy screen (11.4's Legacy row), and never writes it. The remaining test uses check the old key's compatibility (11.2, 11.4), and stay. |
| 5 | 11.1 follow-up: `measure-path-a.ps1` triple-counts video-encode | **Closed elsewhere:** bugfix `bench-videoencode-count`, merged as `4dc66d8`. |
| 6 | 11.2 QA: the monitor list's home; AD-25's H.264-fitted line; 11.3's two notes | **Closed** in 11.2's merge and in 11.3 (`monitor-recording-scaled`, the 0x0 skip). |
| 7 | 11.3 QA round 1: start, dispose and drop guards | **Closed** in 11.3 round 2. |
| 8 | 11.3: the mode-change scale path has no automated test | **Recorded:** a Human Check row (a screen's resolution change during a two-screen Monitor Recording). |
| 9 | 11.4 review #1: the App compiles `MonitorCanvasRule.cs` as a linked source, against AD-2's graph | **Recorded, unchanged:** 11.4's unattended design decision, for the builder or an AD-2 update. Moving it would be a design change, not a cleanup. |
| 10 | 11.4 review #7: tab order runs through the tiles before the check boxes | **Recorded, unchanged** (rejected at review: it follows the visual order). |

## Follow-ups

- **A test gap (QA, not a finding):** no default test covers the old `monitorRecordingMonitor` falling back to the primary when the screen it names isn't attached. `MonitorFinder.Find` does it against the real displays, and 11.2's `MonitorScreensTests` covers Core's resolver, but the engine's pass-through has no fake-display test. A small default test would need `MonitorFinder.Find` behind a display port.

## Verification

- `dotnet build ReplayBuffer.slnx -c Release`: 0 warnings.
- Default suites, 0 failed: Contracts 135, Core 169, Media 102 (4 explicit skipped), Platform.Capture 8 (1 skipped), Engine 196 (47 skipped), App 193 (20 skipped).
- `pwsh tools/publish-engine.ps1`: clean, 12.1 MB.
- **Pending, the Heavy part of the verify** (a one-screen and a two-screen Monitor Recording still record playable files). It can't run while 8.8's soaks hold the GPU. To run once they end, under the bench lock the tests take themselves (one project, nothing else running):

  `dotnet test --project tests/ReplayBuffer.Engine.Tests -c Release --no-build --explicit only --filter-class "*MonitorRecordingCaptureTests"`

  That's 13 tests. `ATogglePairWithNoGameWritesAPlayableFileWithEveryGlobalTrack` is the one-screen Recording through the merged pass-through; `ATwoScreenMonitorRecordingOf60SecondsPlaysToTheEndWithEveryTrackAndBothScreensPlaced` and `AScreenOnDesktopDuplicationStillComposites` are the composite.

## Log

- 2026-11-05 03:10: branch `feature/11.5-refactor-sweep` from develop `4f0de30`. Scoped, swept, verified (default only); develop had nothing new to sync.
