---
title: 'One file however long, and forced encoder restarts'
type: 'feature'
ticket: '6'
created: '2026-11-04'
status: 'in-progress'
baseline_revision: '10a44f7a3b19e987a9bbace8eaec1359e1689e1b'
route: 'full'
route_source: 'auto'
risk: 'high'
review: ''
review_source: ''
lenses_ran: []
review_loop_iteration: 0
followup_review_recommended: false
context:
  - '{project-root}/CLAUDE.md'
warnings: ['oversized']
deferred: []
---

<intent-contract>

## Intent

**Problem:** A Recording must stay one file however long it runs and through a window resize (FR-13, FR-22), and a forced encoder restart must end it cleanly and start a new one at once (R4). Nothing proves the 4-hour case or the resize, and the engine has no forced restart at all: 4.6 built `ReplayRingSink.Restart` (a new ring generation) but no caller, and the orchestrator has no notion of a Recording surviving one.

**Approach:** Add a forced chain restart to `CaptureSession` (tear the current chain down, rebuild the same spec into a new generation of the same ring sink, post `ChainRestarted`). On it the orchestrator ends the running Recording with `encoder-restart` and starts a new one at once on the new chain in a new allocator path; when the old writer posts back it publishes `recording.stopped`, `recording.started` and a `recording-restarted` warning naming both files, with the Overlay dot staying solid. Prove the long file with a fast synthetic RecordingWriter test, and the resize and restart with one Heavy engine test.

## Boundaries & Constraints

**Always:** AD-19: the orchestrator alone starts and stops Recordings; the session only posts. AD-22: Contracts change additively — `RecordingStopReasons.EncoderRestart = "encoder-restart"` and `ErrorCodes.RecordingRestarted = "recording-restarted"` (in `All`), nothing else in Contracts. AD-7: during a restart the old chain is torn down before the new one is built, and no chain build starts while a restart runs. AD-5: the new Recording keeps the old one's chain and settings (it shares the session's chain), and the restart doesn't re-read `recordingsEnabled` (it continues a running Recording). Heavy tests are `[Fact(Explicit = true)]` + `[Trait("Category", "Heavy")]`; the GPU lock file is the session's job, not test code's. Lean tests.

**Never:** No continuing the same file across a restart (epic decision). No device-lost detection or other production trigger in this story: the restart is reached through `CaptureSession.RestartChain` only (tests), and that gap is reported. No change to `RecordingWriter`'s cutting, `RingGenerations` or the 4.9 switch logic beyond the guards below. No real-time multi-hour runs.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Restart mid-Recording | Recording A runs; session posts `ChainRestarted(qpc)` | A gets reason `encoder-restart`; B is reserved, Library `BeginWrite`, started via `Session.Record(qpc)`; state stays Running. On A's post-back: `Stopped(A, encoder-restart)`, `Started(B)`, `Restarted(A, B)`, in that order | none |
| B can't start | `Record` returns null or throws | A still finishes and publishes `Stopped(A, encoder-restart)`; state goes Off; Info/Failed says so | logged |
| B posts back before A | B stops fast | `Started(B)`+`Restarted` are published before `Stopped(B)` | none |
| Restart, no Recording | Replay Buffer only | nothing published; Clips continue from the new generation | none |
| Restart refused | not anchored, stopping, drained, a switch armed/retiring, a restart running | `RestartChain` returns false and logs | none |
| Rebuild fails | new chain throws while building | session fails with `encode-failed` (as any encode failure); ring freed | session ends |
| Shutdown mid-restart | Dispose while A still finishing | A and B are both waited for (bounded) and handled | as today |

</intent-contract>

## Code Map

- `src/ReplayBuffer.Contracts/Messages/Recording.cs` -- `RecordingStopReasons`: add `EncoderRestart`. `src/ReplayBuffer.Contracts/ErrorCodes.cs` -- add `RecordingRestarted` beside `RecordingStopped` (doc: a warning, `recordingStarted` sound, names both files) and in `All`. `ContractsTests` checks `All` matches the declared consts.
- `src/ReplayBuffer.Core/Sessions/CaptureSessionPorts.cs` -- `ISessionEvents`: add default member `void ChainRestarted(long qpc) { }` ("the session's encoder chain was forcibly rebuilt; frames from `qpc` on are the new chain's").
- `src/ReplayBuffer.Core/Sessions/TargetGames.cs:112` -- `Recordings` record: add trailing optional `Action<RecordingRestart>? Restarted = null`; add `public sealed record RecordingRestart(string StoppedPath, string StartedPath)` beside it.
- `src/ReplayBuffer.Core/Sessions/SessionOrchestrator.cs`:
  - `Events` (line ~1283) implements `ChainRestarted` → `Post(new ChainRestarted(id, qpc))`; handle it in `Handle` when `_active?.Id` matches.
  - `StartRecording` (1048): split the part after the `recordingsEnabled` check (Record, snapshot/Info, allocate, BeginWrite, Open, `write.Start`, pending-chain drop, `ApplySettings`) into a helper returning the `RecordingRun?`, so the restart reuses it without the switch check and without publishing `Started`.
  - New `_ending` (`RecordingRun?`): the restarted Recording awaiting its post-back. `RecordingRun` gains `Follows` (the run it replaced, until announced).
  - `OnRecordingWritten` (1164): a post-back for `_ending` does the Library/Open/Failed part, publishes `Stopped(old, reason)`, then announces the replacement (`Started(new)`, `Restarted(old,new)`) or, with none running, sets state Off and commits a held Target. A post-back of a run whose `Follows` is still set announces it first. Keep the existing path otherwise.
  - `Run` (250): on shutdown wait for `_ending` too (same bounded wait).
  - Class summary: one sentence on restarts.
- `src/ReplayBuffer.Engine/Sessions/CaptureSession.cs`:
  - `internal bool RestartChain(string reason)`: refuse (log, false) unless Replay, anchored, live and no `_armed`/`_retiring`/restart; set `_restarting` and start a dedicated worker thread kept in a field (`_restartThread`) under `_chainLock`.
  - The worker: under `_gate` set `old.Fed = false` (abort if `_stopped`); `old.Tap?.Close()` (the Recording ends at the old chain's last packet); wait for `old.Writers == 0` (bounded, ~3 s: their key never comes, so each cuts at its press); `old.Dispose(keepRing: true)`; build `new EncoderChain(old.Spec, old.EncoderName, old.Codec, null, ...)`, `Subscribe`, `Build(_anchorQpc, first: false, null, restartOf: old.Ring, window: old.WindowSeconds)`; `StartAudio(now)`; under `_gate`+`_chainLock` make it `_current`, clear `_restarting`, `StartBuildLocked()`; log the gap; `_events.ChainRestarted(now)`. On a build failure: dispose the new chain and the ring, clear `_restarting`, `Fail(new ErrorCodeException(EncodeFailed, ...))`.
  - Guards: `OnFrame` draws `_current` only while `_current.Fed`; `Save`/`Record` return null while `_restarting` or the chain is disposed; `TryArm`, `ResizeAt` and `StartBuildLocked` do nothing while `_restarting`; `End` joins `_restartThread` right after its gate block and skips `CompleteVideo`..`WriteStats` when the current chain is disposed.
  - Class summary: a sentence on the restart.
- `src/ReplayBuffer.Engine/Sessions/EncoderChain.cs` -- `Build` gains optional `ReplayRingSink? restartOf` and `int window`: when given, `restartOf.Restart(NewRing(window))` and use it as `_ring` (its `WindowSeconds = window`) instead of a new sink. Add `public bool IsDisposed`.
- `src/ReplayBuffer.Core/Feedback/FeedbackRouter.cs` -- `RecordingRestarted(RecordingRestart restart)`: `RaiseInfo`-style (withdrawn after `InfoLifetime`) `FeedbackNotice(ErrorCodes.RecordingRestarted, WarningSeverity.Warning, "recordings", "Recording restarted after an encoder restart: <old file name> → <new file name>", "recordingStarted")`.
- `src/ReplayBuffer.Engine/EngineHost.cs:409` -- pass `Restarted: r => { log; notices.RecordingRestarted(r); }` to `Recordings` (named argument).
- `src/ReplayBuffer.Platform.FFmpeg/RecordingWriter.cs`, `FragmentedMp4Muxer.cs`, `DurableOutput.cs` -- test seams only: `internal bool FlushToDisk { get; init; } = true` on the writer, threaded to the durable muxer and `DurableOutput.Flush` (`_file.Flush(flushToDisk)`); `internal int Queued => _queue.Count` on the writer (the test throttles on it). 28,800 fragments × ~3 ms `FlushFileBuffers` would take ~80 s.
- Tests to reuse: `tests/ReplayBuffer.Core.Tests/SessionOrchestratorTests.cs` (`NewRecordingOrchestrator`, `FakeRecording`, `FakeSession`, `FakeFactory { Record = ... }`, `SetRecordingsEnabled`, `Press`, `WaitUntil`; the fake session's `ISessionEvents` is how a test posts `ChainRestarted`). `tests/ReplayBuffer.Engine.Tests/RecordingCaptureTests.cs` (`StartRecordingAsync`, `Rig`, `WindowScript`, `AssertFinished`, `SessionTestKit.Probe`/`AssertDecodesCleanly`). `tests/ReplayBuffer.Core.Tests/FeedbackRouterTests.cs` if present.

## Tasks & Acceptance

**Execution:**
- [x] Contracts (`Recording.cs`, `ErrorCodes.cs`) -- the two additive constants -- AD-22.
- [x] `src/ReplayBuffer.Core/Sessions/{CaptureSessionPorts,TargetGames,SessionOrchestrator}.cs` -- `ChainRestarted` port, `Restarted` callback and `RecordingRestart`, the restart handling per the matrix -- R4.
- [x] `src/ReplayBuffer.Core/Feedback/FeedbackRouter.cs` + `src/ReplayBuffer.Engine/EngineHost.cs` -- the `recording-restarted` notice and its wiring.
- [x] `src/ReplayBuffer.Engine/Sessions/{CaptureSession,EncoderChain}.cs` -- `RestartChain` and its guards -- 4.6 generations, AD-7. Add `internal Func<Exception?>? RestartFault` to `CaptureSessionOptions` (like `ClipWriteFault`): when it returns an exception, the rebuild throws it before building, for the rebuild-fails test.
- [x] Matrix coverage, small tests each: Core (fakes) -- B posts back before A (announce before its stop); restart with no Recording publishes nothing; Dispose while A is still ending handles A and B. Engine default run -- `RestartChain` on a `CaptureSession` that hasn't opened/anchored returns false (no GPU needed). Engine Heavy -- with `RestartFault` set, a restart fails the session with `encode-failed` (`TargetCapture`'s failure callback or `SessionFailed`); keep its name matching `*Restart*`.
- [x] `src/ReplayBuffer.Platform.FFmpeg/{RecordingWriter,FragmentedMp4Muxer,DurableOutput}.cs` -- the two test seams.
- [x] `tests/ReplayBuffer.Core.Tests/SessionOrchestratorTests.cs` -- one test: restart mid-Recording gives the order `Started(A)`, then on A's post-back `Stopped(A, encoder-restart)`, `Started(B)`, `Restarted(A,B)`; two distinct paths; state stays Running throughout until B stops; Library begin/end for both. One test: B can't start (fake `Record` returns null) → `Stopped(A, encoder-restart)` and state Off.
- [x] `tests/ReplayBuffer.Core.Tests/FeedbackRouterTests.cs` (or the existing router tests) -- one test: `RecordingRestarted` raises `recording-restarted`, Warning, sound `recordingStarted`, message naming both file names.
- [x] New `tests/ReplayBuffer.Platform.FFmpeg.Tests/LongRecordingTests.cs` (default run) -- a `RecordingWriter` with `FlushToDisk = false`, synthetic streams (an H.264-free video stream such as MPEG-4 Part 2 or MJPEG params with a 90 kHz time base at 30 fps, keyframe every 60 frames; three AAC 48 kHz stereo streams with a valid 2-byte AudioSpecificConfig, 1024-sample packets) fed 4 h of packets in time order through `Offer` faster than real time (throttle on `Queued`), then `OutputEnded`. Assert: one file; ffprobe reads the container and the video as 4 h (±1 frame); each AAC stream's duration equals the video's within one AAC frame. Target under ~30 s.
- [x] `tests/ReplayBuffer.Engine.Tests/RecordingCaptureTests.cs` -- one Heavy test: window script that resizes the form (e.g. 641×481 → 1000×700 at ~2 s, and back); the rig exposes the `CaptureSession` (wrap the factory); Recording runs ~5 s through the resize, `RestartChain("simulated")`, ~4 s more, stop. Assert: two files in the game's folder; events `Started(A)`, `Stopped(A, encoder-restart)`, `Started(B)`, (`Restarted` via the rig's callback), `Stopped(B, stopped)`; A is 640×480, one file spanning the resize, decodes cleanly; B decodes cleanly; A's last video PTS (anchor time = A's K + duration) to B's first is no larger than the measured restart time plus two frames; Library begin/end for both.

**Acceptance Criteria:**
- Given 4 hours of synthetic encoded packets with three Tracks, when fed through the RecordingWriter faster than real time, then one file results that ffprobe reads as 4 hours, every Track the same length within one AAC frame.
- Given a Recording on the synthetic game window, when the window's resolution changes mid-Recording, then the Recording stays one file at the output size and decodes cleanly.
- Given a running Recording, when a simulated encoder restart happens, then two playable files result whose stamps meet with no gap larger than the restart, `recording.stopped { reason: encoder-restart }` and `recording.started` are published for them, and one `recording-restarted` notice names both files.
- Given the default run, when the 0-warning Release build and the touched test projects run, then all pass, and the NativeAOT publish has no IL warnings.

## Implementation Notes

- The orchestrator's start after the `recordingsEnabled` check is `BeginRecording` (sets `_recording`, publishes nothing); `StartRecording` publishes `Running` + `Started`, the restart sets `Follows`. `Announce` publishes `Started(B)` then `Restarted(A,B)` once. `CaptureState.Recording` goes Off only when neither `_recording` nor `_ending` is left, and a held Target switch commits only then; a switch is held while `_ending` is set too. A second restart while `_ending` is still set stops the running Recording with no replacement (logged through Info).
- Shutdown waits for `_ending` then `_recording` within one `ShutdownWait`.
- The notice message is `Recording restarted after an encoder restart: <old> → <new>` (file names only).
- `Save` returns null for a chain disposed while the output still runs (a failed restart), not for a drained session's disposed chain, which keeps its ring.
- Verified: 0-warning Release build; Contracts, Core, Platform.FFmpeg (the 4 h test runs in ~7 s) and Engine default suites pass; the two Heavy `*Restart*` Engine tests pass (three runs); `tools/publish-engine.ps1` has no IL/AOT warnings.

## Plan Change Log

- The old chain's Recording is ended after `ChainRestarted` is posted, not with `old.Tap?.Close()` before the teardown: the writer finishes in milliseconds while the rebuild takes far longer, so its post-back would reach the orchestrator before `ChainRestarted` and be handled as `session-ended`. `EncoderChain.TakeTap()` keeps the tap out of `Dispose`; the restart thread disposes it (ending the Recording) in a `finally` after the post, or after `Fail` on a failed rebuild. The Recording so also keeps the old chain's packets up to its teardown.

## Review Triage Log

## Design Notes

The session tears the old chain down before building the new one, because `ReplayRingSink.Restart` needs the old encoders to have stopped writing into the ring, and AD-7 caps live chains. The capture keeps running; frames during the rebuild are dropped, so the gap between the files is the rebuild's length (plus up to a frame). The new Recording attaches on the orchestrator thread as soon as `ChainRestarted` arrives and asks for a keyframe after the restart QPC, so the first frame encoded after it (or the next, if the post was slow) starts file B.

Publishing waits for A's post-back so `recording.stopped` keeps its contract (the file is complete) and comes before B's `recording.started`; `CaptureState.Recording` never leaves Running, so the FeedbackRouter's started/stopped notices don't fire and the user hears one `recordingStarted` sound with the `recording-restarted` notice.

## Verification

**Commands:**
GPU lock: any command that runs real capture or encode (the Platform.FFmpeg.Tests and Engine.Tests runs, the Heavy run) first waits until `%USERPROFILE%\Documents\repos\ReplayBuffer\.bench-running` doesn't exist, creates it for the run's duration, and deletes it after, even on failure; other sessions share the GPU. Run one test project at a time.

- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings
- `dotnet test --project tests/ReplayBuffer.Contracts.Tests -c Release --no-build` -- expected: pass
- `dotnet test --project tests/ReplayBuffer.Core.Tests -c Release --no-build` -- expected: pass
- `dotnet test --project tests/ReplayBuffer.Platform.FFmpeg.Tests -c Release --no-build` -- expected: pass
- `dotnet test --project tests/ReplayBuffer.Engine.Tests -c Release --no-build` -- expected: pass
- `dotnet test --project tests/ReplayBuffer.Engine.Tests -c Release --no-build --explicit only --filter-method "*Restart*"` (session holds the GPU lock) -- expected: pass
- `pwsh tools/publish-engine.ps1` -- expected: no IL or AOT warnings
