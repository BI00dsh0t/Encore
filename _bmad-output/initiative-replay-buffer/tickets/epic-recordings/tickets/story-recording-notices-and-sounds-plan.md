---
title: 'Recording notices and sounds'
type: 'feature'
ticket: '8'
created: '2026-11-04'
status: done
baseline_revision: 'b932e5aef70574fc52449988a7ea7e3667d0954e'
route: 'full'
route_source: 'auto'
risk: 'low'
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

**Problem:** The FeedbackRouter raises `recording-started` and `recording-stopped` from `CaptureState.Recording` transitions, so a stop never says why, a disk-full stop plays both the stopped and the disk-full sound, and an encoder restart can't be told apart from a plain start.

**Approach:** Drive the two notices from the real `recording.started` and `recording.stopped` events the SessionOrchestrator publishes (through the `Recordings` callbacks in `EngineHost`): the stop's message names its reason, and only disk-full and encoder-restart stops stay silent, since their own notices play their sounds. `CaptureState` keeps driving only the solid dot. dev.simulate's stub Recording calls the same two router methods.

## Boundaries & Constraints

**Always:** each real start plays `recordingStarted` once and raises `recording-started` (info, 10 s), withdrawing every `disk-full` notice; each stop raises `recording-stopped` (info, 10 s) whose message names the reason; the router stays thread-safe and never blocks; sounds go through `FeedbackSoundPlayer` as now, so switched-off sounds still yield a notice with `Sound = null`.

**Never:** no Contracts change; no change to the orchestrator's event order or state publishing; no sound through the speakers in the default test run (tests use a fake `IFeedbackSoundOutput`); no Monitor Recording work (stories 9–11).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Start | `recording.started` | `recording-started` notice, `recordingStarted` sound once | — |
| Hotkey stop | `recording.stopped` reason `stopped` | `recording-stopped` "Recording stopped", `recordingStopped` sound | — |
| Game exit / Ignored / session ended / shutdown | stop with that reason | `recording-stopped` naming the reason, `recordingStopped` sound | — |
| Disk full | stop `disk-full`, then `DiskFull()` | silent `recording-stopped` "…the disk is full", then `disk-full` with `diskFull` sound: only one sound | — |
| Encoder restart with a replacement | state still Running; stop `encoder-restart`, start, `RecordingRestarted` | silent `recording-stopped` "…encoder restart", no `recording-started` for the replacement, `recording-restarted` with `recordingStarted`: one sound | — |
| Encoder restart without a replacement | state already Off before the stop | silent `recording-stopped`; the next real start raises its notice and sound as usual | — |
| Writer failure | stop with an error code reason | `recording-stopped` Warning "…the write failed (code)", `recordingStopped` sound | — |

</intent-contract>

## Code Map

- `src/ReplayBuffer.Core/Feedback/FeedbackRouter.cs` -- `StateChanged` raises started/stopped from `CaptureState.Recording`; `RecordingRestarted`, `DiskFull`, `RaiseInfo` (10 s), `Raise` (plays sound, nulls it when off), `_diskFulls`. Reuse these.
- `src/ReplayBuffer.Core/Sessions/SessionOrchestrator.cs` -- read-only. Start: `SetState(Running)` then `Started` (~1132). Restart: `OnChainRestarted` keeps state Running; `OnRecordingWritten` publishes `Stopped(old, encoder-restart)` while state is still Running, then `Announce` publishes `Started(new)` then `Restarted`. Without a replacement, `SetState(Off)` comes before that `Stopped`. Disk full: `SetState(Off)`, `Stopped(disk-full)`, then `OnDiskFull` → `Recordings.DiskFull`. All callbacks run in order on the orchestrator thread.
- `src/ReplayBuffer.Contracts/Messages/Recording.cs` -- `RecordingStarted`, `RecordingStopped`, `RecordingStopReasons` (stopped, session-ended, game-exit, ignored, shutdown, encoder-restart, disk-full). Read-only.
- `src/ReplayBuffer.Engine/EngineHost.cs` (~360–450, ~532) -- the `Recordings` `started`/`stopped` lambdas publish and log; add the router calls there. The state lambda keeps calling `notices.StateChanged`. `DevSimulator` is built at ~532.
- `src/ReplayBuffer.Engine/Sessions/DevSimulator.cs` -- `recording(bool)` posts the stub state; the simulated start/stop must also raise the notices (DevSimulateServeTests expects them).
- `tests/ReplayBuffer.Core.Tests/FeedbackSoundsTests.cs` -- `FakeOutput` identifies played sounds, `Notices` records raised notices; the first test drives `StateChanged` for start/stop and must move to the new methods.
- `tests/ReplayBuffer.Core.Tests/FeedbackRouterTests.cs` -- `ADiskFull…` ends with a `StateChanged(Running)`; move it to `RecordingStarted`.
- `tests/ReplayBuffer.Core.Tests/SessionOrchestratorTests.cs` (~507, ~1723–1730) -- already assert `capture.stateChanged` goes Off → Running → Off around a Recording (the solid dot).

## Tasks & Acceptance

**Execution:**
- [x] `src/ReplayBuffer.Core/Feedback/FeedbackRouter.cs` -- add `RecordingStarted(RecordingStarted)` and `RecordingStopped(RecordingStopped)` per the matrix; `StateChanged` now only records the state (for the restart check) and raises nothing; add a public static `StopMessage(string reason)`; update the class doc -- notices come from the real events.
- [x] `src/ReplayBuffer.Engine/EngineHost.cs` -- call `notices.RecordingStarted(started)` / `notices.RecordingStopped(stopped)` in the `Recordings` lambdas; pass the simulator start/stop callbacks that also call the router.
- [x] `src/ReplayBuffer.Engine/Sessions/DevSimulator.cs` -- the simulated start/stop post the stub state and raise the notices (path `dev.simulate`, reason `stopped`); update the doc.
- [x] `tests/ReplayBuffer.Core.Tests/FeedbackSoundsTests.cs` -- move the first test to the new methods; add one test with the fake output: a start and each plain stop reason play `recordingStarted` once and `recordingStopped` per stop, each stop's message names its reason; a disk-full stop plays only `diskFull`; a restart (state Running, stop encoder-restart, start, restarted) plays only `recording-restarted`'s `recordingStarted` and raises no extra `recording-started`.
- [x] `tests/ReplayBuffer.Core.Tests/FeedbackRouterTests.cs` -- use `RecordingStarted` in place of `StateChanged(Running)`.

**Acceptance Criteria:**
- Given the engine's FeedbackRouter with a fake render port, when a Recording starts and stops for each reason, then the notices and sounds match the I/O matrix.
- Given a running Recording, when the orchestrator publishes `capture.stateChanged`, then it reports Running from start to stop, including across an encoder restart (existing SessionOrchestratorTests cover it).
- Given a dev or test instance, when dev.simulate injects RecordingStarted then RecordingStopped, then the snapshot shows the Recording state and both notices (DevSimulateServeTests still passes).
- Given the builder in a game with Recordings on, when they start and stop a Recording, then they hear both sounds and see both notices (Human Check Queue).

## Implementation Notes

- `DevSimulator` takes two more callbacks (`started`, `stopped`) beside the stub-state one; `DevSimulateServeTests.TheInstalledInstanceRefusesIt` passes two more no-ops.
- The router tracks `_replacing`: set by an `encoder-restart` stop while its last `StateChanged` said Running, cleared by the next start or any non-Running state. Every start withdraws the `disk-full` notices, the replacement's included.
- A stop reason outside `RecordingStopReasons` raises a Warning; the known reasons are Info.
- Verification pending: `.bench-running` was present, so no build or test ran.

## Plan Change Log

## Review Triage Log

### 2026-11-04 — Review pass
- verdicts: 4 findings — high 0, medium 2, low 2, false 0, maybe-false 0
- findings:
  - `[medium]` `[patch]` A writer failure on the Recording an encoder restart replaced leaves `_replacing` false, so its replacement raises `recording-started` with a sound beside the restart's — confirmed in `OnRecordingWritten` (the error replaces the reason, state stays Running). Fixed: any stop while the last state was Running marks the next start as the replacement.
  - `[medium]` `[patch]` A disk full on that replaced Recording: the replacement's start withdrew the new `disk-full` notice and played a start sound. Fixed by the same rule, and the replacement now withdraws nothing; covered by `ADiskFullOnARecordingAnEncoderRestartReplacedKeepsItsNoticeAndRaisesNoStart`.
  - `[low]` `[reject]` A replacement that ends before the Recording it replaced is announced before the old stop, so it gets a start notice and sound — real but needs the old writer to outlast the new Recording; the fix needs the follow relation in the router, more complexity than the edge is worth.
  - `[low]` `[reject]` dev.simulate raises its notices even when a real Recording keeps the state Running — dev and test instances only, and the stub exists only to exercise notices.

## Design Notes

Decision: the encoder restart's replacement raises no `recording-started` notice; `recording-restarted` already names the new file and plays the start sound, so three notices for one restart would be noise. The router tells a replacement apart by the stop arriving while its last `StateChanged` said Running: the orchestrator turns the state Off before the stop only when no replacement follows.

Stop messages: `stopped` "Recording stopped"; `game-exit` "Recording stopped: the game exited"; `ignored` "Recording stopped: the game was set Ignored"; `session-ended` "Recording stopped: the game session ended"; `shutdown` "Recording stopped: ReplayBuffer shut down"; `encoder-restart` "Recording stopped: encoder restart"; `disk-full` "Recording stopped: the disk is full"; any other code "Recording stopped: the write failed ({code})", severity Warning.

## Verification

**Guard:** a benchmark may be running on this PC. Before any build or test, check that `%USERPROFILE%\Documents\repos\ReplayBuffer\.bench-running` doesn't exist; while it does, run no build or test (implement, then report that verification is pending).

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings, 0 errors
- `dotnet test --project tests/ReplayBuffer.Core.Tests -c Release --no-build` -- expected: all pass
- `dotnet test --project tests/ReplayBuffer.Engine.Tests -c Release --no-build` -- expected: all pass
- `pwsh tools/publish-engine.ps1` -- expected: 0 IL warnings (engine code changes)

## Auto Run Result

- **Summary:** the FeedbackRouter raises `recording-started` and `recording-stopped` from the real `recording.started` and `recording.stopped` events; each stop names its reason, a disk-full or encoder-restart stop is silent, and a restart's replacement raises no start notice. `CaptureState` drives only the solid dot. dev.simulate calls the same two methods.
- **Files:** `src/ReplayBuffer.Core/Feedback/FeedbackRouter.cs` (new event methods, `StopMessage`); `src/ReplayBuffer.Engine/EngineHost.cs` (wires the Recordings callbacks and the simulator); `src/ReplayBuffer.Engine/Sessions/DevSimulator.cs` (stub raises the notices); `tests/ReplayBuffer.Core.Tests/FeedbackSoundsTests.cs` and `FeedbackRouterTests.cs` (matrix test with a fake render port, the restart disk-full test); `tests/ReplayBuffer.Engine.Tests/DevSimulateServeTests.cs` (constructor arguments).
- **Review:** 2 medium patched (one root cause), 0 deferred, 2 low rejected (reasons in the triage log). Follow-up review: false (two medium patches share one root cause and one small fix, covered by a test).
- **Verification:** `dotnet build ReplayBuffer.slnx -c Release` 0 warnings; Core.Tests 125/125; Engine.Tests 170 passed, 28 explicit skipped; `pwsh tools/publish-engine.ps1` published with no warnings.
- **Residual risks:** the rejected restart ordering edge above; hearing the sounds is in the Human Check Queue.
