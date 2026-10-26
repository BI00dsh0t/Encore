---
title: 'SessionOrchestrator and the capture-session API'
type: 'feature'
ticket: '6'
created: '2026-10-26'
status: 'done'
baseline_revision: '8c4cffaa131b38e0e46e1b84db790c596c2eac48'
route: 'full'
route_source: 'auto'
risk: 'medium'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/initiative-replay-buffer/architecture-replay-buffer/architecture-replay-buffer.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** `TracerCapture` wires WGC → NVENC and WASAPI → AAC → fMP4 directly, under a lock shared by capture threads, Ctrl+C and window-close callbacks. Nothing owns a session, so epics 2 and 3 have no `SessionSpec`, no orchestrator `Channel` and no session API to build on (R1 retarget part, R6, AD-19, AD-22).

**Approach:** Core gains `SessionSpec { Kind, Target, Snapshot, Audio }`, the capture-session ports, and `SessionOrchestrator`: one dedicated thread reading a single-reader `Channel`, the only place that starts, retargets and ends sessions and publishes `CaptureState`. The tracer's media wiring moves into an Engine `CaptureSession` behind the port, and `debug-capture` drives it only through orchestrator messages. `serve` composes an orchestrator too, publishing `capture.stateChanged` through the hub.

## Boundaries & Constraints

**Always:**
- The snapshot is resolved once, by the orchestrator at Start: `SettingsResolver.Resolve(currentDocument(), target.ProfileId)`. Retarget keeps it; a settings change mid-session never touches it (AD-5). `debug-capture` resolves against a default document (it doesn't open the store).
- Retarget is allowed only to another window of the target's process; a different process is refused with `retarget-other-process` (AD-19: that is end + new session). The session stops the old WGC capture, then starts the new one, keeping the device, encoders and muxer, so the file has one unbroken set of streams.
- Media threads never block on the orchestrator: window-closed, capture failure and first-frame timeout are posted back as messages, and the orchestrator ends the session.
- `capture.stateChanged` is published only from the orchestrator thread, once per change.
- Decision (user, 2026-10-26): a running game session publishes `replay: buffering`; no session is `replay: off`. Epic 3 later swaps the file sink for the ring without changing what is published. `recording`, `monitorRecording`, `micMuted` and `cooldownUntil` keep `CaptureState.Initial`'s values.
- New codes in `ErrorCodes` (constants + `All`): `session-running`, `session-not-running`, `retarget-other-process`.
- `debug-capture`'s options, output file, stats report, exit codes and console output stay as they are; every existing `CaptureEndTests` case passes unchanged.
- 0 warnings; the engine stays AOT-clean (no reflection, no new suppressions); no P/Invoke outside `Platform.*`.

**Never:** The frame pacer, GPU scaling, Output Settings applied to the encoder, codec probing (stories 10, 11). The AudioDeviceHub or moving audio encoding off the WASAPI threads (12). IPC commands to start sessions, Game Detection, Target switching policy, Recordings, Hotkeys (1.9, epics 3, 6). Story 13's publish scripts and the App's startup toggle.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Start | no session; `Start(game, target, audio)` | session started with the resolved snapshot; one `CaptureState` published | No error expected |
| Start twice | a session running | refused | `session-running` |
| Retarget same process | running; window of the same pid | session's capture moves to the new window; snapshot revision unchanged; no state event | No error expected |
| Retarget other process | running; window of another pid | session untouched | `retarget-other-process` |
| Retarget / End idle | no session | nothing changes | `session-not-running` |
| End | running | session finished, outcome (file path) returned; state back to off | No error expected |
| Window closes / capture fails | running | session posts back; orchestrator ends it with that reason or failure; state off | failure carried in the outcome |
| Posts from many threads | 8 threads posting concurrently | processed one at a time, on one thread, in channel arrival order | No error expected |

</frozen-after-approval>

## Code Map

- `src/ReplayBuffer.Engine/TracerCapture.cs` -- source of the media wiring to move: `StartSession` (anchor, muxer, `D3D11VAFrames`, `VideoEncoder`, `EncoderThread`, AAC, `AudioTimeline`), `OnFrame`, `Finish`, `WriteStats`, `ReportAudioFaults`, `DisposeAll` order. `ResolveWindow`/`ResolveEndpoint`/`Describe` stay with the debug driver.
- `src/ReplayBuffer.Engine/Program.cs` -- `debug-capture` case calls the driver; `Serve` unchanged.
- `src/ReplayBuffer.Engine/EngineHost.cs` -- compose the orchestrator; publish via `Hub.Publish(CaptureMessages.StateChanged, …)`; end any session and stop the thread in `DisposeAsync`.
- `src/ReplayBuffer.Core/Settings/SettingsResolver.cs` -- the one `Resolve`; reuse.
- `src/ReplayBuffer.Contracts/Messages/Capture.cs` -- `CaptureState`, `CaptureState.Initial` (mic muted); reuse, no Contracts message changes.
- `src/ReplayBuffer.Contracts/ErrorCodes.cs` -- add the three codes.
- `src/ReplayBuffer.Platform.Capture/WindowCapture.cs` -- one per window; `Dispose` waits for the frame in flight, so stop-then-start never overlaps frames. `CopyFrame` clips a differently sized window (scaling is 11).
- `src/ReplayBuffer.Platform.Windows/WindowFinder.cs` -- `WindowInfo.ProcessId` gives the target's pid.
- `tests/ReplayBuffer.Engine.Tests/CaptureEndTests.cs` -- the end-to-end pattern (pwsh test window, ffprobe); don't change its cases.

## Tasks & Acceptance

**Execution:**
- [x] `src/ReplayBuffer.Contracts/ErrorCodes.cs` -- add the three codes.
- [x] `src/ReplayBuffer.Core/Sessions/SessionSpec.cs` -- `SessionKind { Game, Monitor }`, `CaptureTarget(int ProcessId, nint Window, string? ProfileId)`, `SessionAudio(IReadOnlyList<SessionAudioDevice>)` with `SessionAudioDevice(string EndpointId, AudioFlow Flow, string FriendlyName)`, `SessionSpec`.
- [x] `src/ReplayBuffer.Core/Sessions/CaptureSessionPorts.cs` -- `ICaptureSessionFactory.Start(SessionSpec, ISessionEvents)`, `ICaptureSession { Retarget(CaptureTarget); SessionOutcome End(); }`, `ISessionEvents` (posts back: closed, failed), `SessionOutcome(string? Path, string Reason, Exception? Failure)`.
- [x] `src/ReplayBuffer.Core/Sessions/SessionOrchestrator.cs` -- unbounded `Channel` with `SingleReader = true`, a dedicated named thread, `Post` for any thread, awaitable `StartAsync`/`RetargetAsync`/`EndAsync` (reply through a `TaskCompletionSource` carried in the message), `Ended` task per session for the closed/failed path, `Action<CaptureState>` publisher, `Dispose` ends a running session and joins.
- [x] `src/ReplayBuffer.Engine/Sessions/CaptureSession.cs` (+ factory) -- the moved wiring; debug-only options (stages, preset, tune, colour, encode thread, stats, GPU priority) in an engine-internal `CaptureSessionOptions`, not in `SessionSpec`; first-frame timeout posts a failure.
- [x] `src/ReplayBuffer.Engine/TracerCapture.cs` -- becomes the `debug-capture` driver: resolve window and devices, Start → wait (seconds from anchor, Ctrl+C, or `Ended`) → End; same failures and codes.
- [x] `src/ReplayBuffer.Engine/EngineHost.cs` -- compose the orchestrator.
- [x] `tests/ReplayBuffer.Core.Tests/SessionOrchestratorTests.cs` -- the matrix with a fake factory; the arrival-order test (8 threads, ticket taken under a lock with each `Post`, processed tickets strictly increasing, one thread id); snapshot unchanged after a settings change and a retarget; state published only on the orchestrator thread.
- [x] `tests/ReplayBuffer.Engine.Tests/SessionCaptureTests.cs` -- in-process: one pwsh process opens two windows; Start on the first, Retarget to the second, End; ffprobe shows h264 + aac + aac with strictly increasing video PTS across the switch; retarget to a window of another process is refused.

**Acceptance Criteria:**
- Given a test window, when `debug-capture` runs, then it writes the same 3-stream fMP4 as the tracer did, and every `CaptureEndTests` case passes.
- Given two windows of one process, when a session is started, retargeted and ended through orchestrator messages, then one playable file results, holding frames from both windows.
- Given the solution, when `dotnet build ReplayBuffer.slnx` and `dotnet test --solution ReplayBuffer.slnx` run, and `pwsh tools/publish-engine.ps1` publishes, then 0 warnings (no IL2xxx/IL3xxx) and all tests pass.

## Implementation Notes

- `ICaptureSession.End` takes a `reason` (`End(string reason)`, and `EndAsync(string reason = "ended")` on the orchestrator), so `debug-capture`'s stats report keeps its stop reasons (`30 s elapsed`, `Ctrl+C`). A session that already stopped itself (closed, failed) keeps its own reason.
- `StartAsync` replies with `RunningSession(Spec, Ended)`; `RetargetAsync` replies with the updated `SessionSpec` (same `Snapshot` reference). Session post-backs carry the session's id, so a late one from an ended session is ignored.
- On every end path the orchestrator publishes `replay: off` before completing `Ended`, so an awaiter of `Ended` sees the state already published.
- A throw from `ICaptureSession.Retarget` (old capture already stopped) ends the session with that failure and fails the retarget.
- `CaptureSession` lives in `src/ReplayBuffer.Engine/Sessions/CaptureSession.cs` with `CaptureSessionFactory` and the engine-internal `CaptureSessionOptions` (output path/folder, stats, GPU priority, stages, preset, tune, colour, encode thread, an `Anchored` callback, first-frame timeout). `serve` allocates files in the snapshot's Output Folder, else `%LOCALAPPDATA%\ReplayBuffer[-<instance>]\sessions`; nothing starts a session there yet.
- `SessionAudioDevice.Flow` is `Contracts.Settings.AudioFlow` (Core can't see `Platform.Audio`'s).

## Plan Change Log

## Review Triage Log

**Pass 1 (quick, 2026-10-26):** high 0, medium 3, low 5, false 0, maybe-false 0; plus 1 deferred as pre-existing.

| # | Finding | Verdict | Route | Evidence / action |
|---|---------|---------|-------|-------------------|
| 1 | A retarget handled after the window closed throws `InvalidOperationException`, and the orchestrator marks a clean "window closed" outcome as failed | medium | patch | `CaptureSession.Retarget` throws on `_stopped`, and `EndActive` sets `Failure` because the session's own outcome had none. Fix: throw `session-not-running`, and end cleanly on that code. |
| 2 | The same-process rule trusts the caller's pid, not the window's real owner | medium | patch | `Retarget` never compares `WindowFinder.FromHandle(...).ProcessId`. Fix: check before stopping the old capture; `retarget-other-process` leaves the session running. |
| 3 | A retarget's `ProfileId` replaces the session's, so it can disagree with the snapshot | low | patch | `active.Spec with { Target = target }`. Fix: keep the session's existing `ProfileId`. |
| 4 | An exception from `_publish` escapes the orchestrator thread | low | reject | `EventHub.Publish` only serializes source-generated records under its own lock and isn't expected to throw. Guarding it would add complexity for a path not seen in use. |
| 5 | `_window` stays on the first window after a retarget, so the stats report shows the wrong window | low | patch | Only `_capture` is swapped. Fix: update `_window` on a successful retarget. |
| 6 | The serve path allocates its output file with `OutputNaming` and `File.Exists`, not AD-21's allocator | low | defer | Inherited from the tracer. Nothing starts a session in serve yet, and `OutputPathAllocator` doesn't exist yet. |
| 7 | No test covers `Retarget` or `End` throwing | medium | patch | The fake session can't throw. Add tests for the generic, `session-not-running`, `retarget-other-process` and `End`-throw paths. |
| 8 | `Assert.False(running.Ended.IsFaulted)` can never fail | low | patch | `Ended` is only ever completed with `TrySetResult`. Fix: assert that `(await Ended).Failure` is null. |
| 9 | "Frames from both windows" is checked by timestamps, not pixels | low | reject | The old capture is disposed before the new one starts, so frames more than 1 s after the switch can only come from the second window. A pixel comparison would add a decoder to the test. |

## Design Notes

**What this story improves, and what it leaves.** Spike 1.2 found capture at ≈ 55 fps against a 78 fps game and encoding on the capture threads. This story changes the control plane, not the frame path: session lifecycle moves off the tracer's shared lock onto one orchestrator thread, and the capture threads only post back. Frame rate and video threading are story 11's: its pacer must emit frames on its own clock (a static window still gets the configured rate), which is where encoding leaves the WGC thread and where `MinUpdateInterval` stops capping arrivals. Audio encoding on the WASAPI threads moves with the AudioDeviceHub's fan-out in story 12. (The epic's spike note says "stories 6 and 12" own the pacer; the tree gives it to 11.)

**Why messages carry a reply.** `StartAsync` returns a `Task` completed by the orchestrator thread, so callers (the debug driver now, IPC and Hotkeys later) get `session-running` and friends as `ErrorCodeException`, without ever touching session state themselves.

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx` -- expected: 0 warnings, 0 errors
- `dotnet test --solution ReplayBuffer.slnx` -- expected: all pass, no new skips
- `pwsh tools/publish-engine.ps1` -- expected: publishes with 0 IL2xxx/IL3xxx warnings
</content>
</invoke>
