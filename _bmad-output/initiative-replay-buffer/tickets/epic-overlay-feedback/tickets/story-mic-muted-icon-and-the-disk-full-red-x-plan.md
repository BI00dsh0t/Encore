---
title: 'Mic-muted icon and the disk-full red X'
type: 'feature'
ticket: '7'
created: '2026-11-03'
status: done
baseline_revision: '8f3c755800b3e6c3a68df75bc97b4f40723bd1a3'
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

**Problem:** The Overlay shows the dot, the saving icon and queued messages, but not the mic mute (`capture.stateChanged.micMuted`, story 2.4) or a lasting disk-full mark (R3). The builder can't tell at a glance that the mic is muted or that the disk filled.

**Approach:** The Overlay view model gains `MicMuted` and `DiskFull`, read from the snapshot. The window's strip draws a mic-muted icon right beside the dot and a red X further left, each in a fixed slot, so nothing moves as they come and go.

## Boundaries & Constraints

**Always:**
- Read only `IEngineSession.State` and `Status` (AD-3, AD-15). Disconnected: both false.
- `MicMuted` = connected && `Dot != None` && `Capture.MicMuted`. The icon sits "beside the dot", so it shows only while the dot does. `CaptureState.Initial` is muted, so an always-on icon would keep the Overlay on the desktop with nothing running, against R1's "nothing when all are off".
- `DiskFull` = connected && some active `Warning` has `Code == ErrorCodes.DiskFull` and `Overlay == true`, whatever the dot. It goes when the engine dismisses the warning: on `warning.dismiss`, or when a Recording starts (`FeedbackRouter.StateChanged` withdraws every `disk-full`).
- The disk-full warning still queues as a message (5.5). The X is in addition to it.
- Strip, right to left: dot, gap, mic icon, gap, saving icon, gap, X. Every slot is fixed and uses `Visibility.Hidden` when off, so the dot stays exactly at `Monitors.TopRight(...)`. The strip's width becomes `3 * (IconSizeDip + GapDip) + DotSizeDip`. The message area (260 DIP) is still wider and stays right-aligned below.
- Window `Showing` = `Dot != None || Saving || Message != null || DiskFull`.
- The X is drawn in the dot's red (`#E53935`). The mic icon is light (`#EEEEEE`) with a red slash.
- Lean tests (CLAUDE.md "Tests"). View-model tests use `FakeEngineSession` and change state at once, with no waits.

**Never:** No engine, Core or Contracts change. No new setting. No `Platform.Desktop` change. Don't move the dot or change the saving-icon or message-queue logic. No sound in the App.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Mute on, buffering | Replay Buffering, `MicMuted` true | `MicMuted` true; false after `MicMuted` false | none |
| Mute on, nothing running | `CaptureState.Initial` (muted) | `MicMuted` false, window hidden | none |
| Disk full | an active `disk-full` overlay warning, no dot | `DiskFull` true, window visible | none |
| Dismissed | the `disk-full` warning removed from the snapshot | `DiskFull` false | none |
| Simulator | real dev engine: `dev.simulate diskFull`, then `recordingStarted` | `DiskFull` true, then false and `Dot` Solid | none |

</intent-contract>

## Code Map

- `src/ReplayBuffer.App/Overlay/OverlayViewModel.cs` -- add `MicMuted` and `DiskFull` bool properties (`Set(ref ...)`, like `Saving`). Compute them in `Refresh` beside `dot`/`saving`. Add `DiskFull` to the "nothing to show" condition. In that branch, set `MicMuted`/`DiskFull` before `TargetProcessId`; in the other, after it, keeping the existing hide-before-move ordering. `ErrorCodes` is in namespace `ReplayBuffer.Contracts`. Update the class summary.
- `src/ReplayBuffer.App/Overlay/OverlayWindow.cs` -- add `_mic` (a 16x16 `Grid` holding a mic `Path`, filled light, plus a red slash `Path`) and `_cross` (a `Path`, `M3,3 L13,13 M13,3 L3,13`, red stroke ~2.5, round caps), both `IsHitTestVisible = false`, each with a right margin `GapDip`. Strip children: `_cross, _icon, _mic, _dot`. Change `WidthDip` to `3 * (IconSizeDip + GapDip) + DotSizeDip`. `Update` sets each one's Visibility to Visible or Hidden. `Showing` adds `_model.DiskFull`. `Place` needs no change beyond `WidthDip`. Update the class summary.
- `src/ReplayBuffer.Core/Feedback/FeedbackRouter.cs:82-120` -- read only: `disk-full` is raised as an Error with `Overlay` true and withdrawn when a Recording turns on.
- `src/ReplayBuffer.Engine/Sessions/DevSimulator.cs` -- read only: `dev.simulate` works on test instances (`Instance.TestPrefix`).
- `tests/ReplayBuffer.App.Tests/OverlayTests.cs` -- `FakeEngineSession`, `Shift`, `Notice`, `Raise`, `Dismiss`, `ManualTime` and `Buffering` already exist. Extend the window test.
- `tests/ReplayBuffer.App.Tests/EngineConnectionTests.cs` -- launches the real engine on a throwaway test instance. Reuse `ConnectAsync()`, `Eventually(...)` and `Token`. Send through `connection.SendAsync(DevMessages.Simulate, new DevSimulate(...), Token)`. Build `new OverlayViewModel(connection, action => action())`.

## Tasks & Acceptance

**Execution:**
- [x] `src/ReplayBuffer.App/Overlay/OverlayViewModel.cs` -- `MicMuted` and `DiskFull`, the ordering and the summary -- R3's icons.
- [x] `src/ReplayBuffer.App/Overlay/OverlayWindow.cs` -- draw the mic icon and the X in fixed strip slots, the wider strip, `Showing` and the summary.
- [x] `tests/ReplayBuffer.App.Tests/OverlayTests.cs` -- (1) `TheMicIconFollowsTheMuteWhileTheDotShows`: buffering with mute on shows it, mute off hides it, and the muted `Initial` doesn't show it; (2) `TheRedXShowsWhileADiskFullWarningIsActive`: `Raise` a `disk-full` notice, then `Dismiss` it; (3) the window test: a disk-full warning with no dot shows the window, and dismissing it hides it.
- [x] `tests/ReplayBuffer.App.Tests/EngineConnectionTests.cs` -- `TheSimulatorsDiskFullShowsTheRedXUntilARecordingStarts`: `DiskFull` → `Eventually(model.DiskFull)`; `RecordingStarted` → `Eventually(!model.DiskFull && model.Dot == OverlayDot.Solid)`.

**Acceptance Criteria:**
- Given the Replay Buffer running, when `micMuted` turns true, then the view model's `MicMuted` is true at once (well within 1 s), and when it turns false, `MicMuted` is false at once.
- Given an active disk-full warning, then `DiskFull` is true and the window shows even with no dot. When the warning is dismissed, `DiskFull` is false.
- Given a dev engine, when `dev.simulate diskFull` runs and then `dev.simulate recordingStarted`, then the view model shows the X and later clears it, once the engine withdraws the warning.

## Implementation Notes

## Plan Change Log

## Review Triage Log

### 2026-11-03 — Review pass
- verdicts: 1 findings — high 0, medium 0, low 1, false 0, maybe-false 0
- findings:
  - `[low]` `[patch]` `TheSimulatorsDiskFullShowsTheRedXUntilARecordingStarts` posts inline on `TimeProvider.System`, so the connection's event thread and the 4 s message timer can run `Refresh` at once over the unlocked queue — fixed: the test's `post` runs one action at a time under a lock, as the UI thread would.

## Design Notes

The mic icon follows the dot, not just the flag: AD-5 restores "muted" by default, so an icon tied to the flag alone would keep a lone Overlay window on the desktop all the time. The X doesn't follow the dot, because a full disk can stop the capture that the dot reports. Fixed slots and `Hidden` keep 5.3's rule that the dot never moves. The strip grows by two transparent 22 DIP slots. Story 12 re-checks the borderless independent flip with the final window.

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings, 0 errors
- `dotnet test --project tests/ReplayBuffer.App.Tests -c Release --no-build` -- expected: all pass

**Manual checks (if no CLI):**
- Builder (Human Check Queue): with the Dev engine and App, the Replay Buffer on and the mic muted, the mic icon shows left of the dot. `dev.simulate diskFull` shows the red X, and `dev.simulate recordingStarted` clears it.

## Auto Run Result

- **Summary:** The Overlay view model gains `MicMuted` (connected, the dot shows, and `capture.micMuted`) and `DiskFull` (an active `disk-full` Overlay warning, whatever the dot). The window's strip draws, right to left, the dot, a light mic icon with a red slash, the saving icon and a red X. Each sits in a fixed slot that is `Hidden` when off, so the dot never moves. The window also shows while `DiskFull` is true. The X goes when the engine dismisses the warning, which it does on `warning.dismiss` or when a Recording starts.
- **Files:**
  - `src/ReplayBuffer.App/Overlay/OverlayViewModel.cs`: `MicMuted`, `DiskFull`, and the show and hide ordering.
  - `src/ReplayBuffer.App/Overlay/OverlayWindow.cs`: the mic and X glyphs, the wider strip, and `Showing`.
  - `tests/ReplayBuffer.App.Tests/OverlayTests.cs`: the mic and red X view-model tests, and the extended window test.
  - `tests/ReplayBuffer.App.Tests/EngineConnectionTests.cs`: the real dev engine runs `dev.simulate diskFull`, then `recordingStarted`; the X shows, then clears.
- **Review:** quick lens, 1 low finding, patched by the parent session (a serialising `post` in the real-engine test). Nothing deferred or rejected.
- **Follow-up review recommended:** false (0 high and 0 medium patched).
- **Verification:** Release build with 0 warnings. App.Tests: 78 passed, 7 Heavy skipped. Every matrix row is covered. No engine code changed, so there was no NativeAOT publish.
- **Residual risks:** The glyphs' look is checked only by eye, through the Human Check. The strip is 44 DIP wider, all of it transparent, and story 12 re-checks independent flip.
