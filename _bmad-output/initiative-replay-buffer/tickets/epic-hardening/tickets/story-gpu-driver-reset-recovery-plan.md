---
title: 'GPU driver reset recovery'
type: 'feature'
ticket: '3'
created: '2026-11-04'
status: 'built'
baseline_revision: '0694fb36c64bdc0713e39f184066f7d4eb7a4ce3'
route: 'full'
route_source: 'auto'
risk: 'high'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
followup_review_recommended: true
context:
  - '{project-root}/CLAUDE.md'
warnings: ['oversized']
deferred: []
---

<intent-contract>

## Intent

**Problem:** A GPU driver reset (PLAN.md T-01) today fails the capture session (`encode-failed` or a silent WGC stall), so the Replay Buffer stops and its content is lost until the next Target. 6.6 built the forced restart machinery (`CaptureSession.Restart`, `ChainRestarted`, the orchestrator's Recording handover) but nothing in production triggers it.

**Approach:** The session classifies `DXGI_ERROR_DEVICE_REMOVED`/`DXGI_ERROR_DEVICE_RESET` failures (and a removed-reason watchdog) as a device loss, stops feeding, and posts `ISessionEvents.DeviceLost`. The SessionOrchestrator (AD-19) raises `gpu-reset` and asks the session to `RecoverDevice()`, retrying every 2 s for up to 60 s. Recovery, on a thread of its own, tears down WGC, every chain and the D3D11 device, then rebuilds them on the Target's adapter into a new ring generation of the same ring (4.6) and posts `ChainRestarted`, so 6.6's handover continues a Recording into a new file (a Monitor Recording the same way). Giving up raises `gpu-reset-failed` and stops the Replay Buffer until the next Target. `dev.simulate gpuReset` marks every live device lost.

## Boundaries & Constraints

**Always:** AD-19: the orchestrator decides recovery, retries and give-up; the session only posts. AD-22 additive Contracts: `ErrorCodes.GpuReset = "gpu-reset"`, `ErrorCodes.GpuResetFailed = "gpu-reset-failed"` (both in `All`), `SimulatedEvent.GpuReset` appended last; nothing else. AD-7: the lost chain and device are gone before the new ones are built; no chain build, arm, switch, resize or Record while a device is lost. The ring (CPU memory or ring file) is never disposed by a loss or a failed attempt, so pre-reset content stays saveable: a save during the outage cuts the old generation to its last packet (no keyframe request), and one under 1 s past the new generation's first key falls back to it (4.6's rule). Fault seams exist only behind `CaptureSessionOptions` (tests) and `dev.simulate` (refused by the installed instance). Lean tests; Heavy tests `[Fact(Explicit = true)]` + `[Trait("Category","Heavy")]`.

**Never:** No real TDR, `dxcap`, registry or driver changes in code or tests. No new stop reason: a reset-split Recording uses `encoder-restart`. No change to `RingGenerations.Cut` or the retire rule. No recovery for file-mode (debug capture) sessions or before the anchor: those still fail as today. No App changes (the status line already lists warnings generically).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Loss, recovered | anchored game session; a device call fails with DEVICE_RESET | `gpu-reset` once; frames dropped; rebuilt; `ChainRestarted`; buffering again; `Recovered` callback | — |
| Loss mid-Recording | Recording A running | after recovery `Stopped(A, encoder-restart)`, `Started(B)`, `Restarted(A,B)`; state stays Running | B can't start → A stops, state Off (6.6) |
| Save during outage | press while lost | Clip of the old generation up to its last packet, playable | — |
| Attempt fails | rebuild throws | session posts `DeviceLost` again; orchestrator retries after `RetryEvery` | — |
| Keeps failing | `GiveUpAfter` passed since the loss | `gpu-reset-failed` (Error), session ended, Replay Off; a new window of the same Target doesn't restart it; another Target does | engine keeps running |
| Monitor session loss | Monitor Recording A | same recovery; `MonitorStopped(A, encoder-restart)`, `MonitorStarted(B)`, `Restarted(A,B)`; MonitorRecording stays Running | give-up ends the Monitor session |
| Repeated detection | watchdog + capture + encoder all fail | one `DeviceLost`; later failures of the lost chain are swallowed | — |
| Target exits during outage | drain | session drained (ring kept), retry timer stopped | — |
| Restart attempt before the new generation got a key | 2nd `Restart` on the ring | replaces the empty generation; the pre-reset one stays the predecessor | — |

</intent-contract>

## Code Map

- `src/ReplayBuffer.Contracts/ErrorCodes.cs` -- add `GpuReset`, `GpuResetFailed` with docs beside `ResetGap`/`EncoderBudget`, and in `All`. `src/ReplayBuffer.Contracts/Messages/Dev.cs` -- `SimulatedEvent.GpuReset` last (JSON `gpuReset`).
- `src/ReplayBuffer.Core/Replay/RingGenerations.cs` `Restart` (:126) -- when `_predecessor` is set and `_current.StartUs == long.MaxValue` (a restart generation with no keyframe yet), retire the current one and open the new one keeping `_predecessor` (note `Open` clears `_predecessor`; reassign after). Doc it.
- `src/ReplayBuffer.Core/Sessions/CaptureSessionPorts.cs` -- `ISessionEvents.DeviceLost(Exception failure) { }` (device removed or reset, or a recovery attempt failed; no frames until a recovery succeeds, which posts `ChainRestarted`); `ICaptureSession.RecoverDevice() => false` (starts one attempt off the calling thread; false when nothing is lost or one runs; never blocks).
- `src/ReplayBuffer.Core/Sessions/TargetGames.cs` -- new `public sealed record DeviceRecovery(Action<string>? Reset = null, Action<string>? Failed = null, Action<TimeSpan>? Recovered = null, TimeSpan? RetryEvery = null, TimeSpan? GiveUpAfter = null)` with `DefaultRetryEvery = 2 s`, `DefaultGiveUpAfter = 60 s`.
- `src/ReplayBuffer.Core/Sessions/SessionOrchestrator.cs`:
  - ctor: trailing optional `DeviceRecovery? deviceRecovery = null` (null = defaults, no callbacks).
  - `Active` gains `long? LostAt` and `Timer? Retry`; a helper stops/disposes the timer, called from `EndActive`, `DrainActive`, `EndMonitor`.
  - `Events.DeviceLost` → `Post(new SessionDeviceLost(id, failure))`; new private `RetryDeviceRecovery(long SessionId)`. `Handle` routes both for `_active` or `_monitor` by id.
  - `OnDeviceLost(Active s, Exception e, bool monitor)`: first loss (`LostAt` null) → set it, `Reset?.Invoke(message)`, call `TryRecover`; later (an attempt failed) → elapsed ≥ GiveUpAfter → give up, else a one-shot timer posting `RetryDeviceRecovery` after RetryEvery. `TryRecover`: `s.Session.RecoverDevice()` in try; a throw counts as a failed attempt (re-enter via Post).
  - On `SessionChainRestarted` for a session with `LostAt` set: clear it, stop the timer, `Recovered?.Invoke(elapsed)`, then the existing handling (`OnChainRestarted` for `_active`; new Monitor handover for `_monitor`).
  - Give up: game → `_gpuStoppedPid = active.Spec.Target.ProcessId`, `EndActive("gpu reset failed", new ErrorCodeException(GpuResetFailed, msg))`, `Failed?.Invoke(msg)`; `FollowTargetWindow`'s `_active is null` branch doesn't start on that pid; `CommitTarget` clears it when the Target's process changes. Monitor → `EndMonitor("gpu reset failed", SessionEnded)` + `Failed`.
  - Monitor handover (mirror 6.6's game path): `_monitorEnding`; on monitor `ChainRestarted` the running Monitor Recording gets `EncoderRestart`, moves to `_monitorEnding`, and a new one starts at once (extract the start part of `OnMonitorAnchored` into a helper returning the run) with `Follows` set; `OnMonitorRecordingWritten` handles `_monitorEnding` like `OnRecordingWritten` handles `_ending` (announce via `MonitorStarted ?? Started` then `Restarted`, Off and `EndMonitor` only when neither is left). Shutdown in `Run` waits for `_monitorEnding` too; `OnMonitorPress`'s "still finishing" check includes it. Update the class summary.
- `src/ReplayBuffer.Platform.Capture/D3D11Device.cs` -- consts `DeviceRemoved = unchecked((int)0x887A0005)`, `DeviceReset = unchecked((int)0x887A0007)`; `int RemovedReason` (simulated → `DeviceReset`, else `GetDeviceRemovedReason()`; S_OK = 0), `bool IsLost => RemovedReason < 0`; seam: instance `SimulateLoss()` and static `SimulateReset()` (a static counter; a device created before the latest call is lost); `ThrowIfSimulatedLoss()` throws `COMException(…, DeviceReset)` when simulated. `FrameScaler.Draw` (:78) calls it first ("the next device call fails").
- `src/ReplayBuffer.Engine/Sessions/CaptureSession.cs` (6.6's `Restart` at :746 is the model):
  - Options: `Func<Exception?>? RecoveryFault` (thrown at the start of each attempt, after the teardown), `TimeSpan DeviceWatchInterval = 500 ms`.
  - Fields under the chain lock: `_deviceLost`, `_recovering`, `_tornDown`, `_lostTap`, `_recoveryThread`. Treat `_deviceLost` like `_restarting` in the guards of `Record`, `TryArm`, `StartBuildLocked`, `ResizeAt`, `RestartChain`, `SwitchChain`, `PrepareChain`; `Chains` counts a lost session as 1.
  - `Fail(e)`: when `_deviceLost` already → log and return; when `IsDeviceLoss(e)` (HResult `DeviceRemoved`/`DeviceReset` anywhere in the inner chain, or `_device.IsLost`) and `OnDeviceLost(e)` takes it → return; else as today. `OnDeviceLost`: refuse (false) unless anchored, `HasRing || IsMonitor`, live (not stopped/drained/closed); under gate+chainLock set `_deviceLost`, `Fed = false` on current/armed/retiring; log Warning; `_events.DeviceLost(e)`. A watchdog `Timer` started at the anchor checks `_device.IsLost` under the gate every interval (skip while stopped/lost), disposed in `End`/`Abandon`.
  - `Save`: while `_deviceLost`, bind to `_current` even though disposed, with `video: null` (cuts to the end of the old generation).
  - `RecoverDevice()`: refuse unless lost and not recovering/stopped/closed; start the recovery thread. `Recover()`: join a running `_restartThread`; first attempt `TearDownLost()` (take `_capture` under the gate and dispose it; take armed/retiring/prepared; join `_workers`; take a `_prepared` a build finished meanwhile; `_lostTap = current.TakeTap()`; wait ≤3 s for `Writers == 0`; dispose lost chain `keepRing: true`, the others plainly; take `_device` under the gate and dispose it). The rebuild spec is the armed chain's when one was armed (the orchestrator took its switch), else the current's; `_wanted` is kept so a prepared chain is rebuilt after. Then `RecoveryFault`; new device (`D3D11Device.Create(_window.Handle)` / `CreateForMonitor(_monitor.Handle)`); new chain `Build(_anchorQpc, first: false, null, restartOf: lost.Ring, window: lost.WindowSeconds)` (Monitor: no ring); `NewCapture` with the new chain's fps (give `NewCapture` an fps parameter); start capture; `StartAudio(now)`; install under gate+chainLock unless stopped (clear `_deviceLost`, `_tornDown`; `StartBuildLocked`); log ms; `_events.ChainRestarted(now)`; then dispose `_lostTap`. On a throw: dispose the new chain with `keepRing: true`, capture, device; clear `_recovering` first, then `_events.DeviceLost(e)`. Stopped meanwhile: dispose what was built, post nothing.
  - 6.6's `Restart` catch: when `_deviceLost`, dispose `next` with `keepRing: true`, don't dispose the ring, don't `Fail`.
  - `End`: dispose the watchdog; join `_recoveryThread` with `_restartThread`; dispose `_lostTap` in the finally. `DisposeAll`: when the current chain is already disposed and the ring isn't kept, dispose its ring.
- `src/ReplayBuffer.Engine/Sessions/EncoderChain.cs` -- no logic change expected (`Dispose(keepRing)` and `Build(restartOf, window)` exist).
- `src/ReplayBuffer.Engine/Sessions/DevSimulator.cs` -- new `Action gpuReset` parameter, `GpuReset` case after the installed check. `EngineHost.cs` (:582) passes `D3D11Device.SimulateReset`; (:378) passes `new DeviceRecovery(Reset: m => warnings.Raise(GpuReset, Warning, "replay", m, overlay: false), Failed: m => warnings.Raise(GpuResetFailed, Error, "replay", m, overlay: false), Recovered: t => log)` with log lines. Fix `tests/ReplayBuffer.Engine.Tests/DevSimulateServeTests.cs:79`.
- Tests to reuse: `tests/ReplayBuffer.Core.Tests/SessionOrchestratorTests.cs` (`FakeSession`/`FakeFactory`/`NewRecordingOrchestrator`/`WaitUntil`, the 6.6 restart tests, the 6.9 Monitor tests); `tests/ReplayBuffer.Core.Tests/RingGenerationsTests.cs` (or wherever 4.6's tests live); `tests/ReplayBuffer.Engine.Tests/RecordingCaptureTests.cs` rig (its `CapturingFactory.Events` must forward `DeviceLost`, or the default member swallows it).

## Tasks & Acceptance

**Execution:**
- [x] `src/ReplayBuffer.Contracts/{ErrorCodes.cs,Messages/Dev.cs}` -- the two codes and the enum value -- AD-22.
- [x] `src/ReplayBuffer.Core/Replay/RingGenerations.cs` -- replace an empty restart generation in `Restart` -- a failed attempt must not retire the pre-reset content.
- [x] `src/ReplayBuffer.Core/Sessions/{CaptureSessionPorts,TargetGames,SessionOrchestrator}.cs` -- ports, `DeviceRecovery`, recovery, give-up, Monitor handover -- R3, AD-19.
- [x] `src/ReplayBuffer.Platform.Capture/{D3D11Device,FrameScaler}.cs` -- loss detection and the seam.
- [x] `src/ReplayBuffer.Engine/Sessions/{CaptureSession,DevSimulator}.cs`, `src/ReplayBuffer.Engine/EngineHost.cs` -- loss handling, `RecoverDevice`, guards, `Save` during the outage, End/DisposeAll, wiring.
- [x] Tests, default run: Core -- (1) a loss calls `Reset` once and `RecoverDevice` at once; a failed attempt is retried after `RetryEvery`; `ChainRestarted` then gives `Recovered` and, with a Recording running, 6.6's handover; (2) attempts that keep failing end after `GiveUpAfter` (short values) in `Failed`, the session ended, Replay Off, the same Target's new window not restarted, another Target started; (3) a Monitor session's loss and `ChainRestarted` give `MonitorStopped(A, encoder-restart)`, `MonitorStarted(B)`, `Restarted(A,B)` with MonitorRecording Running throughout; (4) `RingGenerations`: two restarts with no keyframe between keep generation 1 cuttable; (5) `DeviceRecovery` defaults are 2 s and 60 s. Engine -- `RecoverDevice` on a session with nothing lost returns false (no GPU); `dev.simulate gpuReset` reaches its action on a dev instance (extend the existing DevSimulator test).
- [x] Engine Heavy in `RecordingCaptureTests.cs`: `AGpuResetRebuildsTheSessionAndTheRecordingContinuesInASecondFile` -- rig with clips, `DeviceRecovery` capturing to the events, `RecoveryFault` failing the first attempt only; Recording A running ~3 s; `SimulateDeviceLoss()` (internal, calls `_device.SimulateLoss()`); expect `gpu-reset`; press Save during the outage → a playable Clip of ≥ 2 s; `Recovered` within 5 s of the loss; `Stopped(A, encoder-restart)`, `Started(B)`, `Restarted(A,B)`; 2 s later Save → a playable Clip; stop → `Stopped(B, stopped)`; A and B decode cleanly. Name it so `--filter-method "*GpuReset*"` selects it.

**Acceptance Criteria:**
- Given a running Replay Buffer with a Recording on a synthetic game, when the fault seam makes the device report a reset, then within 5 s the Replay Buffer is buffering again, a press during the outage saves the pre-reset content as a playable Clip, the Recording continues in a second playable file, and `gpu-reset` is raised.
- Given a recovery that keeps failing, when 60 s (the configured give-up) pass, then `gpu-reset-failed` is raised, the Replay Buffer is off until the next Target, and the engine keeps running.
- Given the installed instance, when `dev.simulate gpuReset` is sent, then it is refused with `not-dev-instance` (existing check).
- Given the default run, then the 0-warning Release build and the touched test projects pass, and the NativeAOT publish has no IL warnings.

## Implementation Notes

- Additions beyond the Code Map, each small: `ReplayRingSink.IsDisposed`, so a recovery after a Monitor-time `SwitchChain` that had already freed the ring builds a fresh ring instead of failing every attempt. A `Retarget` during the outage only records the new window, and an attempt whose window changed before its install fails so the next one captures the new window. A device loss under a 6.6 restart or switch hands over to the recovery (the restart's tap becomes `_lostTap`; no `Fail`). In the orchestrator, `RecoverDevice()` returning false (an attempt still running) is polled again after `RetryEvery`, and the give-up check runs there too, so a hung attempt can't wait forever.
- The Heavy test uses `RetryEvery: 1 s` to keep the 5 s bound from depending on the 2 s default plus teardown time. It is written and compiles; it was not run (GPU tests paused by the builder).

- Before review (the parent's diff read): a recovery that installed its chain and then threw while closing the old tap no longer posts `DeviceLost`, which would have had the orchestrator treat a healthy session as lost. A Core test for the "Target exits during the outage" row was added (`ATargetExitDuringTheOutageDrainsTheSessionAndStopsItsRetries`), and the two Target tests wait for the session instead of racing `targetChanged`.

- After QA (three non-blocking notes):
  - `GpuResetWarnings` (Engine) owns the status-line warnings. A recovery withdraws the oldest `gpu-reset` still shown; a give-up withdraws it and raises `gpu-reset-failed`. Test: `GpuResetWarningsTests`.
  - `TearDownLost` disposes each chain, and the device, in its own guarded step.
  - `Retarget` builds the new capture under the gate from the `_device` it reads there.
  - Verified: 0-warning build; Core 151/151 and Engine 178 pass (33 explicit skipped); the publish has no IL warnings. No GPU runs.

## Plan Change Log

## Review Triage Log

### 2026-11-04 — Review pass
- verdicts: 3 findings — high 0, medium 2, low 1, false 0, maybe-false 0
- findings:
  - `[medium]` `[patch]` `TryArm` lacked the `_deviceLost` guard, so a save during the outage could arm a `_prepared` chain built on the lost device. That chain survived the teardown and became current after recovery. Fixed: `TryArm` checks `!_deviceLost`, and a build that finishes while the device is lost is torn down rather than prepared (`_wanted` is kept for the rebuild).
  - `[low]` `[patch]` A loss during a Monitor-time `SwitchChain` rebuilt the old spec, although the orchestrator had already adopted the new one, so the settings change was silently lost. Fixed: `Restart`'s device-loss catch records `switchTo` as `_rebuildSpec`, and `TearDownLost` keeps it.
  - `[medium]` `[patch]` A hung recovery attempt never reached the give-up check, and `End`'s unbounded join would freeze the orchestrator on give-up. Fixed: `TryRecover` schedules the retry timer after a started attempt too, so the next retry is refused while the attempt runs and checks `GiveUpAfter`. `End` joins the recovery thread for at most 10 s, with a warning.

## Design Notes

- Why the orchestrator drives the retries: AD-19 and the ticket put the decision there, and Core tests can check the 2 s / 60 s policy with short values; the session only executes one attempt at a time.
- Why the old Recording's tap stays open during the outage: closing it early makes its post-back arrive before `ChainRestarted`, so 6.6's handover would see no Recording and not continue it. The cost: a Record stop pressed during the outage lands when recovery ends or the session ends.
- "A save afterwards still holds the content buffered before the reset" is met by 4.6's generations: during the outage and up to 1 s past the new generation's first keyframe, a press cuts the pre-reset generation; later presses cut the new one. The old generation is retired 6 s after the boundary, as for any restart.
- The real reset (`dxcap -forcetdr`, elevated) is a Human Check.

## Verification

**Commands:**
GPU lock: any run with real capture (Engine.Tests, Platform.FFmpeg.Tests) waits until `%USERPROFILE%\Documents\repos\ReplayBuffer\.bench-running` is absent, creates it for the run, and deletes it after, even on failure. One test project at a time.
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings
- `dotnet test --project tests/ReplayBuffer.Contracts.Tests -c Release --no-build` -- expected: pass
- `dotnet test --project tests/ReplayBuffer.Core.Tests -c Release --no-build` -- expected: pass
- `dotnet test --project tests/ReplayBuffer.Engine.Tests -c Release --no-build` -- expected: pass
- `dotnet test --project tests/ReplayBuffer.Engine.Tests -c Release --no-build --explicit only --filter-method "*GpuReset*"` (under the lock) -- expected: pass
- `pwsh tools/publish-engine.ps1` -- expected: no IL or AOT warnings

## Auto Run Result

- **Summary:** a GPU driver reset no longer ends the Replay Buffer.
  - **Detection:** the session takes `DXGI_ERROR_DEVICE_REMOVED`/`DEVICE_RESET` failures, and a 500 ms removed-reason watchdog, as a device loss. It stops feeding and posts `DeviceLost` once.
  - **Retries:** the orchestrator raises `gpu-reset` and asks for `RecoverDevice()` at once. It retries every 2 s; after 60 s it gives up, raising `gpu-reset-failed`, and the Replay Buffer stays off until the Target's process changes.
  - **Recovery:** each attempt tears down WGC, every chain and the device, keeping the ring. It rebuilds them on the Target's adapter into a new ring generation (4.6) and posts `ChainRestarted`, so 6.6's handover continues a Recording in a new file. A Monitor Recording now gets the same handover.
  - **Saves:** a save during the outage cuts the pre-reset generation to its last packet.
  - **Fault seam:** `dev.simulate gpuReset` marks every live device lost.
- **Files:**
  - Contracts: `ErrorCodes.GpuReset`, `GpuResetFailed` and `SimulatedEvent.GpuReset`, all additive (AD-22).
  - Core:
    - `RingGenerations.Restart` replaces an empty restart generation.
    - Ports: `ISessionEvents.DeviceLost` and `ICaptureSession.RecoverDevice`.
    - The `DeviceRecovery` record.
    - The orchestrator's recovery, give-up and Monitor handover.
  - Platform.Capture: `D3D11Device` gains `RemovedReason`/`IsLost` and the seam; `FrameScaler.Draw` runs the seam check.
  - Platform.FFmpeg: `ReplayRingSink.IsDisposed`.
  - Engine: `CaptureSession` loss handling and recovery, the `DevSimulator` case and the `EngineHost` wiring.
  - Tests:
    - Core: 4 orchestrator tests, 1 `RingGenerations` test and the defaults test.
    - Engine (default run): `ARecoveryIsRefusedWhenNothingIsLost` and the `dev.simulate gpuReset` case.
    - Engine Heavy: `AGpuResetRebuildsTheSessionAndTheRecordingContinuesInASecondFile`.
- **Review:** quick lens, 3 findings, all patched: 2 medium (an arm during the outage; a hung attempt never given up, plus the unbounded join) and 1 low (a switch's spec lost under a loss). None deferred, none rejected.
- **Follow-up review recommended:** true. Two mediums were patched in the threading between the orchestrator's retry timer, the recovery thread and `End`'s bounded join. That path is unverified: no test drives a hung attempt.
- **Verification (2026-11-04):**
  - Release build: 0 warnings.
  - Default runs:
    - Contracts: 121/121.
    - Core: 151/151.
    - Engine: 176 passed, 33 explicit skipped. `ReplayClipCaptureTests.ASavePressWritesAFaststartClipOfTheBufferWithOneTitledStreamPerTrack` failed once and passed on its rerun; it is an existing real-capture test this diff doesn't touch.
  - `tools/publish-engine.ps1`: published, no IL warnings.
  - **Not run (GPU tests paused by the builder):** the Heavy `AGpuResetRebuildsTheSessionAndTheRecordingContinuesInASecondFile`.
- **Matrix audit:** every row has a test. Two rows, "Save during outage" and "Repeated detection", are covered only by that Heavy test, which hasn't run.
- **Residual risks:**
  - Nothing has run on a real lost device. This covers the teardown and rebuild, NVENC disposal on a removed device, and the watchdog's `GetDeviceRemovedReason`.
  - A real `dxcap -forcetdr` (elevated) is a Human Check.
  - A Record stop pressed during the outage lands when the recovery ends.
  - A hung attempt that wakes after `End` has gone on may dispose chains at the same time as `End`.