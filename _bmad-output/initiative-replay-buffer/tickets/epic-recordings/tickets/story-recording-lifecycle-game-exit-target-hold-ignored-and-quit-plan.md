---
title: 'Recording lifecycle: game exit, Target hold, Ignored and Quit'
type: 'feature'
ticket: '4'
created: '2026-11-03'
status: 'built'
baseline_revision: '4d66ab597c7648dd8ff3747bfa3c9287a6b46ab3'
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

**Problem:** A Recording ends with `session-ended` whatever ended it, the Target hold reads 3.3's stub `RecordingRunning` flag instead of the real Recording, and on Quit (`engine.shutdown`) the writer's post-back arrives after the orchestrator's channel has closed: the Library never gets `EndWrite`, `recording.stopped` is never raised, and 6.2's open-Recordings entry stays, so the next start reports a clean quit as `recording-interrupted`.

**Approach:** In `SessionOrchestrator`, stop a running Recording with the reason of what ended its session (`game-exit` for the Target's exit or its window closing, `ignored` for an Ignored handover, `shutdown` for the orchestrator stopping), hold Target switches on the real `_recording`, re-evaluate a held switch the moment the Recording's writer posts back, and at shutdown wait (bounded) for the writer's result and handle it inline. Add the three reasons to `RecordingStopReasons` (additive, AD-22).

## Boundaries & Constraints

**Always:** The orchestrator alone decides stop reasons (AD-19); writers still post back. Contracts change additively only (three new `const`s). The shutdown wait is bounded so the whole quit stays inside the App's 5 s `QuitTimeout`. Reuse 4.3's drain on exit: a drained session still finishes the Recording through `Drain` → `End`.

**Never:** No change to the engine's `CaptureSession`/`RecordingWriter`, Game Detection's 10 s focus rule, or the `dev.simulate` contract. No new IPC messages. No real-time waits in default tests beyond what existing capture tests use.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Game exit | Recording runs; `ProposeTarget(null or other, Exit)` or the session's window closes | session drained/ended, `recording.stopped { reason: game-exit }`, file in the game's folder | none |
| Ignored | Recording runs; `ProposeTarget(_, Ignored)` | session ends, `recording.stopped { reason: ignored }` | none |
| Hold | Recording runs; `ProposeTarget(b, Focus)` | no `target.changed`; after the Recording's writer posts back, `target.changed` to b at once | none |
| Quit | Recording runs; orchestrator `Dispose` | file finished, `EndWrite(Written)`, open entry closed, `recording.stopped { reason: shutdown }` before `Dispose` returns | writer not done within the wait: left open (next start reports it interrupted) |
| Stub | `dev.simulate` `RecordingRunning` | only sets `CaptureState.Recording` while no real Recording runs; never holds the Target | none |

</intent-contract>

## Code Map

- `src/ReplayBuffer.Contracts/Messages/Recording.cs` -- `RecordingStopReasons`: add `GameExit = "game-exit"`, `Ignored = "ignored"`, `Shutdown = "shutdown"`.
- `src/ReplayBuffer.Core/Sessions/SessionOrchestrator.cs` -- `Run()` (end-of-loop `EndActive("engine stopping")`), `Handle` (`RecordingRunning`, `SessionClosed` cases), `CommitTarget` (hold uses `_recordingRunning`; exit/Ignored branches call `DrainActive`/`EndActive`), `EndActive`/`DrainActive` → `MarkSessionEnded(active)` (sets `SessionEnded`), `StartRecording` (`write.Start(path, error => Post(new RecordingWritten(...)))`), `OnRecordingWritten`, `RecordingRun`. Remove `_recordingRunning`.
- `src/ReplayBuffer.Core/Sessions/TargetGames.cs` -- `RecordingRunning` doc comment: now dev-only display state.
- `src/ReplayBuffer.Engine/EngineHost.cs:546,581` -- `Sessions.Dispose()` runs before `_library.Dispose()` and the server; read-only, confirms the shutdown order works.
- `src/ReplayBuffer.Core/Sessions/CaptureSessionPorts.cs` -- `IRecordingWrite` contract: `done` is called once from the writer's thread after the session's output ends. Read-only.
- `tests/ReplayBuffer.Core.Tests/SessionOrchestratorTests.cs` -- `ARunningRecordingHoldsASwitchUntilItClearsThenItCommitsAtOnce` and `AnExitOrIgnoredHandoverCommitsThroughARunningRecording` use the stub; rework them onto a real fake Recording (`FakeFactory.Record`, `FakeRecording`, `NewRecordingOrchestrator`, `SetRecordingsEnabled`, `FakeLibraryWrites`). `FakeSession.Ended` lets a test see End was called.
- `tests/ReplayBuffer.Engine.Tests/RecordingCaptureTests.cs` -- the real WGC + ToneHub Recording setup (`RecordAsync`) to reuse for the engine tests.

## Tasks & Acceptance

**Execution:**
- [x] `src/ReplayBuffer.Contracts/Messages/Recording.cs` -- add the three reasons with doc comments -- AD-22 additive.
- [x] `src/ReplayBuffer.Core/Sessions/SessionOrchestrator.cs` -- `MarkSessionEnded(active, reason)`; pass `GameExit` from the exit drain/end and `SessionClosed`, `Ignored` from the Ignored branch, `Shutdown` from `Run`'s final `EndActive`; others keep `SessionEnded`. `RecordingRun` gets a `TaskCompletionSource<Exception?>` the writer callback completes before posting. After `Run`'s loop and final `EndActive`, if `_recording` is set, wait up to a `ShutdownWait` (3 s) on it and call `OnRecordingWritten` inline. Hold on `_recording is not null`; `OnRecordingWritten` commits `_pendingTarget` after `Stopped`. `RecordingRunning` only sets state when `_recording` is null. Update the class summary.
- [x] `src/ReplayBuffer.Core/Sessions/TargetGames.cs` -- fix the `RecordingRunning` doc.
- [x] `tests/ReplayBuffer.Core.Tests/SessionOrchestratorTests.cs` -- rework the two stub tests onto a real Recording; add one test for the stop reasons (exit → game-exit, Ignored → ignored, window closed → game-exit) and one for Quit (`Dispose` mid-Recording → `shutdown`, Library end and open closed, before `Dispose` returns).
- [x] `tests/ReplayBuffer.Engine.Tests/RecordingCaptureTests.cs` -- one default test: kill the target process mid-Recording (then post `ProposeTarget(null, Exit)` as Game Detection would) → one file in the game's folder that decodes to its end, `recording.stopped` with `game-exit`; one default test: `Dispose` the orchestrator mid-Recording → done within 5 s, `shutdown`, file decodes. Share setup with `RecordAsync` by extraction, not copy.

**Acceptance Criteria:**
- Given a Recording runs on the Target, when the Target's process is killed, then a complete file that ffprobe decodes to its end is in the game's folder and `recording.stopped` carries `game-exit`.
- Given a Recording runs, when another game is proposed by focus, then the Target doesn't change; when the Recording is stopped, then `target.changed` to that game follows at once.
- Given a Recording runs, when the Target is set Ignored, then the Recording stops with `ignored`.
- Given a Recording runs, when the engine shuts down, then the file is finished, the Library and open-Recordings are closed, and `recording.stopped` carries `shutdown`, all within 5 s.

## Implementation Notes

- `EndActive` takes an optional `recordingReason` (default `session-ended`); `DrainActive` takes it explicitly. `CommitTarget` maps `Exit` to `game-exit`, `Ignored` to `ignored`, anything else to `session-ended`.
- `OnRecordingWritten(written, commitPending)`: the posted path commits a held switch; the shutdown path passes false so Quit never starts a session on a held Target. A shutdown wait that times out logs through `Recordings.Info` and leaves the open entry.
- Core tests: `NewRecordingOrchestrator` now takes an optional `targetChanged` (which also wires a `TargetCapture`) and `library`, and returns the factory. The two stub tests became `ARunningRecordingHoldsASwitchUntilItsWriterPostsBackThenItCommitsAtOnce` and `ARecordingStopsWithTheReasonOfWhatEndedItsSession`; Quit is `DisposeMidRecordingFinishesItAsShutdownBeforeItReturns`.
- Engine tests: setup extracted into `StartRecordingAsync()` returning a `Rig`; `RecordAsync` and the two new tests (`KillingTheTargetMidRecording...`, `DisposingTheOrchestratorMidRecording...`) share it.

- Review fixes: the two new engine tests are Heavy (`Explicit = true`), as CLAUDE.md asks for GPU capture; the Core stop-reason test runs with `ReplayClips`, so exit and window-close take the drain path; a post-back handled after `Dispose` commits no held Target; a timed-out shutdown wait also publishes `Recording = Off`.

## Plan Change Log

## Review Triage Log

### 2026-11-03 — Review pass
- verdicts: 5 findings — high 0, medium 1, low 3, false 0, maybe-false 1
- findings:
  - `[medium]` `[patch]` The two new engine tests are WGC/GPU capture tests in the default run, against CLAUDE.md's Tests rules — fixed: both marked `[Fact(Explicit = true)]` + `[Trait("Category", "Heavy")]`; they ran and passed with `--explicit only`.
  - `[low]` `[patch]` No test took the drain path production uses for a Target exit (no `clips`) — fixed: the Core stop-reason test passes `ReplayClips`, asserting `game-exit` and one drain for exit and window-close.
  - `[low]` `[patch]` A post-back already queued when `Dispose` completes the channel could commit a held Target and start a session during Quit — fixed: `commitPending` only while not disposed.
  - `[maybe-false]` `[reject]` The 5 s Quit budget isn't bounded across `CaptureSession.End`'s flush and the host's other teardown — what would settle it is a timed real `engine.shutdown` mid-Recording on the builder's PC; if true it would be low (the flush is one fragment), so rejected with this note.
  - `[low]` `[patch]` A shutdown wait that times out left `capture.stateChanged` at recording running — fixed: it publishes `Recording = Off`, leaving the open entry for the next start.

## Design Notes

The shutdown wait is inline because the channel is complete by then: `Post` fails, so the posted `RecordingWritten` is lost. Completing the TCS before `Post` lets `Run` see the result either way; `OnRecordingWritten`'s `ReferenceEquals` guard makes a duplicate harmless.

Minimizing, alt-tab and windowed mode already don't end the session (alt-tab is a focus proposal, now held), so nothing changes for them.

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings
- `dotnet test --project tests/ReplayBuffer.Core.Tests -c Release --no-build` -- expected: all pass
- `dotnet test --project tests/ReplayBuffer.Engine.Tests -c Release --no-build` -- expected: all pass
- `pwsh tools/publish-engine.ps1` -- expected: no IL or AOT warnings

## Auto Run Result

- **Summary:** A Recording now stops with the reason of what ended its session: `game-exit` (Target exit or window closed, drained or ended), `ignored` (Ignored handover), `shutdown` (Quit), else `session-ended`. The real Recording replaces 3.3's stub as the Target hold, and a held switch commits the moment the writer posts back. On Quit the orchestrator waits up to 3 s for the writer and handles its result inline, so the Library is told, the open-Recordings entry closes and a clean quit is no longer reported as interrupted at the next start. `dev.simulate`'s stub is display state only.
- **Files:** `src/ReplayBuffer.Contracts/Messages/Recording.cs` (three additive reasons, AD-22); `src/ReplayBuffer.Core/Sessions/SessionOrchestrator.cs` (reasons, hold, shutdown wait); `src/ReplayBuffer.Core/Sessions/TargetGames.cs` (stub doc); `tests/ReplayBuffer.Core.Tests/SessionOrchestratorTests.cs` (hold, reasons, Quit); `tests/ReplayBuffer.Engine.Tests/RecordingCaptureTests.cs` (shared rig; two Heavy tests: kill the Target, Dispose mid-Recording).
- **Review:** 5 findings: 4 patched (1 medium, 3 low), 1 rejected (maybe-false, low if true: the whole-host 5 s Quit bound). None deferred.
- **Follow-up review recommended:** false. One medium and no high were patched.
- **Verification:** see the story commit's report: Release build 0 warnings; Core.Tests and Engine.Tests default runs; the two Heavy tests with `--explicit only`; `tools/publish-engine.ps1`.
- **Residual risks:** the 15 s real-focus hold with two game processes is covered at the orchestrator, not through Game Detection's timer. A writer slower than 3 s at Quit leaves the Recording to be reported interrupted. The whole-host Quit time is unmeasured.
