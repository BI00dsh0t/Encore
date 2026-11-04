---
title: 'Sleep, resume and the secure desktop'
type: 'feature'
ticket: '4'
created: '2026-11-04'
status: done
baseline_revision: '9815d81fef8d3e8bfc5128b465818da5bee1ca48'
route: 'full'
route_source: 'auto'
risk: 'medium'
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

**Problem:** Sleep and resume (PLAN.md T-02) aren't handled. Capture and audio die or stall across a sleep, and a running Recording or the ring can get timestamps that span the sleep. Nothing proves the secure desktop (lock, UAC, Ctrl+Alt+Del; T-04) is safe: there WGC delivers no frames.

**Approach:** A Core power port (`IPowerNotifications`) posts `PowerSuspending`/`PowerResumed` to the SessionOrchestrator. Platform.Windows implements it with `RegisterSuspendResumeNotification` (`DEVICE_NOTIFY_CALLBACK`). On suspend, the orchestrator stops a running Recording with reason `suspend` and warns `recording-suspended`. It ends the Monitor session, and asks the game session to `Suspend()`. That reuses 8.3's device-loss teardown: capture, chains (the ring is kept, so the generation stays saveable), audio subscriptions and the device are torn down, and the Recording tap is closed. On resume, the orchestrator drives 8.3's recovery (`RecoverDevice`, with its retries) without the `gpu-reset` warning. That builds a new device, chain and capture into a new ring generation, and audio resubscribes through the hub, which rebinds devices by id, then name (2.5). The secure desktop needs no new code: the pacer already repeats the last frame. This is proven by a frame-starve seam.

## Boundaries & Constraints

**Always:**
- AD-19: the orchestrator decides. The session only executes and posts.
- AD-22 additive Contracts: `ErrorCodes.RecordingSuspended = "recording-suspended"` (in `All`) and `RecordingStopReasons.Suspend = "suspend"`.
- AD-7: the old chain and device are gone before the new ones are built (8.3's order).
- The ring is never disposed by a suspend or a failed resume.
- Only the Replay Buffer resumes. A Recording stopped by suspend is never handed over or restarted.
- Seams exist only behind `CaptureSessionOptions` (tests) and the injectable port.
- Lean tests. Heavy tests are `[Fact(Explicit = true)]` + `[Trait("Category","Heavy")]`. Engine-hosting tests use a Guid test instance.

**Never:**
- No real sleep, lock, UAC or power-setting change in code or tests.
- No change to `RingGenerations` or to the pacer's logic.
- No App changes.
- No new warning on resume. On give-up, the existing `gpu-reset-failed` is reused with a resume message.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Suspend, Replay only | anchored game session | `Suspend()` called; no warning; state stays Running | — |
| Suspend with Recording | Recording A running | `Stopped(A, suspend)` once its writer posts; `Warn(recording-suspended)` once; Recording state Off | Stop throws → `recordings.Failed`, writer still finishes |
| Suspend with Monitor Recording | Monitor session running | `EndMonitor(..., Suspend)` → `MonitorStopped(M, suspend)`; one warning in total | — |
| Resume | session suspended | `RecoverDevice()` asked; on `ChainRestarted` no `Recovered`/`Reset`; no Recording starts | attempt fails → 8.3 retries every `RetryEvery`; give-up → `Failed` (resume message), session ended |
| Resume twice | `PBT_APMRESUMEAUTOMATIC` then `PBT_APMRESUMESUSPEND` | second is ignored (not suspended) | — |
| Suspend during a GPU-reset outage | `LostAt` set | retry timer stopped; resume restarts the wait from now | — |
| Save while suspended / after resume | press | while suspended: the old generation cut to its last packet (8.3's lost-save path); after resume: the new generation | — |
| Session can't suspend | not anchored / no ring | `Suspend()` false; nothing else | its first-frame timeout fails it as today |
| Secure desktop | capture delivers no frames | pacer repeats; constant fps; no new generation | — |

</intent-contract>

## Code Map

- `src/ReplayBuffer.Contracts/ErrorCodes.cs` -- `RecordingSuspended` beside `RecordingInterrupted` (doc: a sleep ended the Recording at its last fragment; it isn't restarted), and in `All`. `Messages/Recording.cs` `RecordingStopReasons.Suspend = "suspend"`. Contracts tests that pin codes or reasons may need the new values.
- `src/ReplayBuffer.Core/Sessions/CaptureSessionPorts.cs`:
  - `ICaptureSession.Suspend() => false`. Doc: tears capture, chains and device down off the calling thread, keeping the Replay Buffer saveable. No frames until `RecoverDevice` succeeds; false when it can't (not anchored, no ring, stopped). Never blocks.
  - New port `public interface IPowerNotifications { IDisposable Subscribe(Action<PowerChange> changed); }` and `public enum PowerChange { Suspending, Resumed }`, documented as AD-19's input (called on any thread).
- `src/ReplayBuffer.Core/Sessions/SessionOrchestrator.cs`:
  - Public messages beside `HotkeyPressed` (:33): `PowerSuspending(long Qpc)` and `PowerResumed()`.
  - Ctor: trailing optional `IPowerNotifications? power = null`. It subscribes, posting `PowerSuspending(Stopwatch.GetTimestamp())`/`PowerResumed`, and the subscription is disposed in `Dispose` before the channel completes.
  - `Active` gains `bool Suspended` and `bool Resuming`.
  - `OnSuspend(qpc)`:
    1. When `_recording` is `{ Reason: null }`: set `Reason = Suspend`, then `Write.Stop(qpc)` in try (catch → `recordings.Failed`), as at :1215.
    2. When `_monitor` is set: note whether its recording ran, then `EndMonitor("suspend", RecordingStopReasons.Suspend)`.
    3. If either ran: `recordings.Warn?.Invoke(ErrorCodes.RecordingSuspended, "The PC went to sleep; the Recording ended at its last fragment and isn't restarted")`.
    4. When `_active` is set and not `Suspended`: `StopRecovery`-like (dispose `Retry`; keep it lost), then `Suspended = Session.Suspend()` in try. A throw is logged through `recordings?.Info`; nothing else.
  - `OnResume()`: when `_active is { Suspended: true } s`: clear `Suspended`, set `Resuming = true`, `LostAt = now`, then `TryRecover(s, false)`.
  - `OnDeviceLost`'s first-loss branch is unchanged (a suspended session doesn't post `DeviceLost`; a failed resume attempt arrives with `LostAt` set, so it retries).
  - `OnRecovered`: when `Resuming`, clear it and skip `Recovered`.
  - `GiveUpRecovery`: when `Resuming`, use the message "The Replay Buffer couldn't restart capture after the PC woke within {after} s; it stays off until the next game". The rest is unchanged.
  - Route both messages in `Handle`. Add a sentence to the class summary.
  - `OnChainRestarted` (:1340) already skips a stopping Recording (`Reason is not null`), so a suspended Recording is never handed over. Keep that.
- `src/ReplayBuffer.Engine/Sessions/CaptureSession.cs`:
  - `Suspend()`: under gate+chainLock, refuse unless `TakeDeviceLoss`'s guards hold (anchored, `HasRing || IsMonitor`, not stopped/outputFinished/keepRing/chainsClosed). Already `_deviceLost` → true and nothing more. Otherwise set `_deviceLost`, set `Fed = false` on current/armed/retiring, log Information, and start a thread stored in `_recoveryThread` (joining the previous one) that joins `_restartThread`, runs `TearDownLost()` unless `_tornDown`, then takes `_lostTap` and disposes it (the Recording ends at its last fragment; wrap it in try and log). Don't set `_recovering`, so `RecoverDevice()` on resume is accepted, and its `Recover(previous)` joins this thread.
  - The watchdog and `Fail` already skip while `_deviceLost`. `Save` already cuts a lost chain.
  - Options: `Func<bool>? StarveFrames`. For tests: while it returns true, `OnFrame` drops each frame after the anchor and the `_deviceLost` check, as a secure desktop does. Doc it.
  - `internal void SimulateSuspend()` isn't needed: tests call `Suspend()`.
- `src/ReplayBuffer.Platform.Windows/PowerNotifications.cs` (new) -- `SuspendResumeNotifications : IPowerNotifications`:
  - `Subscribe` registers `RegisterSuspendResumeNotification(&DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS, DEVICE_NOTIFY_CALLBACK)` with an `[UnmanagedCallersOnly]` static callback and a `GCHandle` context. Add the APIs to `NativeMethods.txt`, or use `LibraryImport` for user32. It must be AOT-safe.
  - The returned `IDisposable` calls `UnregisterSuspendResumeNotification` and frees the handle. A failed registration is logged at Warning and returns a no-op.
  - The pure `internal static PowerChange? Map(uint type)`: `PBT_APMSUSPEND` (4) → Suspending; `PBT_APMRESUMEAUTOMATIC` (0x12) and `PBT_APMRESUMESUSPEND` (7) → Resumed; else null.
- `src/ReplayBuffer.Engine/EngineHost.cs` (:379, :503) -- pass `hostOptions.Power ?? new SuspendResumeNotifications(logger)` as the orchestrator's new argument. Add `IPowerNotifications? Power` to the host options record. Log each power change at Information.
- Tests to reuse: `tests/ReplayBuffer.Core.Tests/SessionOrchestratorTests.cs` (`FakeSession`/`FakeFactory`/`NewRecordingOrchestrator`/`WaitUntil`, and 8.3's recovery tests). `FakeSession` gains `Suspend` (count) and `RecoverDevice`. `tests/ReplayBuffer.Media.Tests/FramePacerTests.cs` (`AStaticWindowGetsEverySlotByRepeatingItsOneFrame` shows the pattern). `tests/ReplayBuffer.Engine.Tests/RecordingCaptureTests.cs` rig (8.3's Heavy GPU-reset test is the model).

## Tasks & Acceptance

**Execution:**
- [x] `src/ReplayBuffer.Contracts/{ErrorCodes.cs,Messages/Recording.cs}` -- the code and the reason -- AD-22.
- [x] `src/ReplayBuffer.Core/Sessions/{CaptureSessionPorts,SessionOrchestrator}.cs` -- the port, the messages, suspend and resume handling -- R4, AD-19.
- [x] `src/ReplayBuffer.Engine/Sessions/CaptureSession.cs` -- `Suspend()` and `StarveFrames`.
- [x] `src/ReplayBuffer.Platform.Windows/{PowerNotifications.cs,NativeMethods.txt}` -- the Windows source.
- [x] `src/ReplayBuffer.Engine/EngineHost.cs` -- the wiring.
- [x] Tests, default run:
  - Core:
    1. A fake port's Suspending with Recording A running gives `Stopped(A, suspend)` and one `recording-suspended` Warn. `Suspend` is called once; Recording is Off and Replay is Running.
    2. Resumed asks `RecoverDevice`. A `ChainRestarted` then starts no Recording and doesn't call `Recovered` or `Reset`.
    3. A resume whose attempts keep failing gives up after `GiveUpAfter` (short values) with `Failed` and the session ended.
    4. A Monitor Recording on suspend gives `MonitorStopped(M, suspend)`.
  - Media: a pacer offered one frame and then none for 60 s of simulated QPC emits exactly `60 × fps` slots after it, all `Repeat`, with PTS stepping by one slot.
  - Platform.Windows: `Map` for the three PBT codes and an unrelated one.
- [x] Heavy, in `RecordingCaptureTests.cs`:
  - `ASuspendAndResumeOpenANewGenerationAndEndTheRecordingPlayable`: rig with clips, a Recording running ~3 s. `Suspend()` (posted through the orchestrator by a fake power port) → `Stopped(A, suspend)`, and A decodes cleanly. A Save while suspended gives a playable Clip. Resume → buffering again within 5 s. A Save 2 s later gives a playable Clip whose streams all have monotonic, non-negative PTS (ffprobe).
  - `AFrameStarvedSourceKeepsTheFrameRateWithAudioInSync`: `StarveFrames` true for 60 s, then a Save → the Clip's video frame count is within one frame of `fps × duration`, audio and video durations are within one frame, and no `ChainRestarted` was posted.
  - Name both so `--filter-method "*Suspend*|*Starved*"` selects them.

**Acceptance Criteria:**
- Given a running Replay Buffer with a Recording, when the injected power port reports suspend and then resume, then the Recording ends playable with `recording.stopped { reason: suspend }` and `recording-suspended`, no Recording starts after resume, and the Replay Buffer buffers again in a new generation.
- Given a resume whose rebuild keeps failing, when `GiveUpAfter` passes, then `gpu-reset-failed` is raised and the engine keeps running.
- Given a source delivering no frames, when the pacer runs for 60 s, then it emits every slot at the configured rate with no PTS break.
- Given the default run, then the 0-warning Release build and the touched test projects pass, and the NativeAOT publish has no IL warnings.

## Implementation Notes

- Guards beyond the Code Map, for an attempt in flight across a sleep: `OnDeviceLost` and `TryRecover` do nothing while `Suspended` (a failed attempt or a stale retry timer waits for the resume), and `OnRecovered` clears `Suspended` as well as `Resuming` and skips `Recovered` (an attempt that finished just as the PC slept leaves the session running, so the resume is ignored rather than retrying a session that has nothing lost).
- `CaptureSession.Suspend()` runs its teardown on `SuspendTearDown`, stored in `_recoveryThread`; `End` already joins that thread (bounded), and the next `Recover` joins it as `previous`.
- The Windows source logs each change at Information itself (`SuspendResumeNotifications`), so `EngineHost` only wires it. It uses `LibraryImport` for user32 (no `NativeMethods.txt` entry) and a natively allocated `DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS`; a failed unregistration leaks its GCHandle rather than free it under a possible callback.
- Core tests 1 and 2 are one test (suspend, then resume, on the same rig); a double resume is covered there too.
- Heavy tests written but not run (GPU tests paused).

- Parent, after the matrix audit: two Core tests for rows with none, `ASessionThatCantSuspendIsLeftAsItIsAndTheResumeAsksNothing` and `ASuspendDuringAGpuResetOutageStopsItsRetriesAndTheResumeRecovers` (`FakeSession.CanSuspend`).
- Parent, after review: superseded the `OnRecovered` rule above. `CaptureSession` gains a private `_suspended` flag, set by `Suspend()` (also when already lost) and cleared by `RecoverDevice()`. An attempt still running at a sleep installs nothing and posts nothing. `Suspend()` on a lost session still tears down once that attempt ends. `OnRecovered` leaves `Suspended` set (a `ChainRestarted` that raced the sleep is torn down by the suspend; the resume rebuilds). `OnSuspend` drops the retry timer only when the suspend succeeded. `FeedbackRouter` knows `suspend` (an Info notice, "the PC went to sleep").
- After QA (1 finding): a `gpu-reset` raised before a sleep stayed shown, because the resume's rebuild skipped `Recovered`, and the warning queue drifted. `Active.ResetRaised` is set when `Reset` fires and answered by the next successful rebuild, the resume's included. A pure sleep still raises neither. Test: `ASuspendDuringAGpuResetOutageStopsItsRetriesAndTheResumeRecovers` asserts one `Reset` and one `Recovered`. Verified: build 0 warnings, Core 157/157.

## Plan Change Log

## Review Triage Log

### 2026-11-04 — Review pass
- verdicts: 7 findings — high 0, medium 3, low 4, false 0, maybe-false 0
- findings:
  - `[medium]` `[patch]` `FeedbackRouter` didn't know `suspend`, so a sleep gave a Warning "the write failed (suspend)" notice — added `Suspend` to `IsKnownReason`, `StopMessage` and `MonitorStopMessage`.
  - `[medium]` `[patch]` A suspend between a recovery's install and its `ChainRestarted` post-back: `OnRecovered` cleared `Suspended`, so the resume was ignored and the torn-down session stayed dead — `OnRecovered` now keeps `Suspended`; the resume rebuilds.
  - `[medium]` `[patch]` A suspend during an in-flight recovery attempt returned early, and the attempt could install a capture and chain that ran across the sleep — the private `_suspended` flag stops it installing; the suspend teardown runs after it.
  - `[low]` `[patch]` `OnSuspend` disposed the retry timer before `Suspend()`, so a refused or throwing suspend during an outage lost its retries — the timer is dropped only on success.
  - `[low]` `[reject]` Two extra Core tests exceed lean tests — they cover matrix rows that had no test, which the matrix audit requires; the 500 ms wait is short.
  - `[low]` `[reject]` The Heavy suspend test doesn't assert `recording-suspended` — the Core test asserts it; the Heavy test can't run now (GPU tests paused), and wiring `Warn` into the rig adds rig complexity for a duplicate check.
  - `[low]` `[patch]` A redundant PTS-step assertion in the Media pacer test — deleted (`AssertUniform` covers it).

## Design Notes

- Why reuse 8.3's lost state for sleep: it already tears down in AD-7's order, keeps the ring saveable (a save cuts the old generation to its last packet), swallows failures and the watchdog, and rebuilds into a new ring generation with orchestrator-driven retries. A resume right after waking often finds the GPU or the audio endpoints not ready, and the retries cover that.
- A new generation per resume means no Clip spans the sleep. The new chain's pacer starts mid-session (`firstFrameTakesNearestSlot`), so its PTS start past the sleep and stay monotonic, and Clips rebase at their first key.
- The secure desktop is already covered by the pacer's repeat. This story only proves it (Media test, Heavy engine test).
- Human Checks for the builder: a real sleep and wake during a game, a lock (Win+L), a UAC prompt and Ctrl+Alt+Del, each followed by a Clip save that plays.

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings
- `dotnet test --project tests/ReplayBuffer.Contracts.Tests -c Release --no-build` -- expected: pass
- `dotnet test --project tests/ReplayBuffer.Core.Tests -c Release --no-build` -- expected: pass
- `dotnet test --project tests/ReplayBuffer.Media.Tests -c Release --no-build` -- expected: pass
- `dotnet test --project tests/ReplayBuffer.Platform.Windows.Tests -c Release --no-build` -- expected: pass
- `dotnet test --project tests/ReplayBuffer.Engine.Tests -c Release --no-build` -- expected: pass
- `pwsh tools/publish-engine.ps1` -- expected: no IL or AOT warnings
- Heavy tests are not run (GPU tests paused by the builder).

## Auto Run Result

- **Summary:** sleep and resume (T-02) are now handled. `IPowerNotifications` (Windows: `RegisterSuspendResumeNotification`, `DEVICE_NOTIFY_CALLBACK`) posts `PowerSuspending`/`PowerResumed` to the orchestrator.
  - On suspend, a running Recording stops with `suspend` and `recording-suspended` is warned once. The Monitor session ends. The game session takes 8.3's lost state: WGC, chains, audio subscriptions and the device are torn down, and the ring is kept, so the old generation stays saveable.
  - On resume, 8.3's recovery (with retries and give-up, without `gpu-reset`) rebuilds into a new ring generation. No Recording restarts.
  - The secure desktop is already handled by the pacer repeating the last frame. A 60 s frame-starved pacer test now proves it.
- **Files:**
  - Contracts: `ErrorCodes.RecordingSuspended` and `RecordingStopReasons.Suspend`.
  - Core: the port, `ICaptureSession.Suspend`, and the orchestrator's suspend and resume handling. `FeedbackRouter` knows `suspend`.
  - Engine: `CaptureSession.Suspend`, `_suspended` and the `StarveFrames` seam. `EngineHost` wires the power source (`EngineHostOptions.Power`).
  - Platform.Windows: `SuspendResumeNotifications`.
  - Tests:
    - Core: 5 power tests.
    - Media: the frame-starved pacer.
    - Platform.Windows: `Map`.
    - Engine Heavy: `ASuspendAndResumeOpenANewGenerationAndEndTheRecordingPlayable` and `AFrameStarvedSourceKeepsTheFrameRateWithAudioInSync`.
- **Review:** quick lens, 7 findings. Five were patched: 3 medium (the FeedbackRouter notice, the `OnRecovered` race, an in-flight attempt installing across the sleep) and 2 low. Two low ones were rejected: the extra matrix tests, and the Heavy test's missing warning check. None were deferred.
- **Follow-up review recommended:** true. Three mediums were patched in the threading between `Suspend`, an in-flight recovery attempt, and the orchestrator's `Suspended`/`Resuming` flags. No test drives a real attempt racing a suspend: the Core fake's attempts never complete.
- **Verification (2026-11-04):**
  - Release build: 0 warnings.
  - Default runs:
    - Contracts: 122/122.
    - Core: 157/157.
    - Media: 100 passed, 4 skipped.
    - Platform.Windows: 30/30.
    - Engine: 179 passed, 39 explicit skipped. On the first full run one Engine test failed once; its name wasn't captured. It passed on the rerun and on the run after the patches.
  - `tools/publish-engine.ps1`: published, no IL warnings.
  - **Heavy: passed on the GPU runner at 7629641:** `ASuspendAndResumeOpenANewGenerationAndEndTheRecordingPlayable` (14 s) and `AFrameStarvedSourceKeepsTheFrameRateWithAudioInSync` (1 min 4 s).
- **Matrix audit:** every row has a test that ran, except "Save while suspended / after resume". That row is covered only by the Heavy suspend test, which hasn't run. Its while-suspended half is 8.3's lost-save path.
- **Residual risks:**
  - Nothing has run on a real sleep. The teardown and rebuild after a real resume, the Windows callback and its threading, and the GPU or audio endpoints not being ready at wake are all untested. The resume's retries are meant to cover the last of these.
  - **Human Checks:** sleep and wake the PC during a game, lock it (Win+L), trigger a UAC prompt, and press Ctrl+Alt+Del, then save a Clip after each and check that it plays.
