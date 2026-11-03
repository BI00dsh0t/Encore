---
title: 'Per-Game-Profile settings at the next Reset'
type: 'feature'
ticket: '9'
created: '2026-11-03'
status: 'in-progress'
baseline_revision: 'e83a7f8fe440e8f4274d1c7a960dcacd269e6bbe'
route: 'full'
route_source: 'auto'
risk: 'high'
review: ''
review_source: ''
lenses_ran: []
review_loop_iteration: 0
followup_review_recommended: false
context: ['{project-root}/CLAUDE.md']
warnings: ['oversized']
deferred: []
---

<intent-contract>

## Intent

**Problem:** A session snapshots `EffectiveSettings` once at Start, and nothing reaches a running session but `bufferWindow` (4.6). An edit to the Target's profile `trackLayout` or `video` (or its Mic Devices and mute Tracks) never applies until a new session. R7 and R8 need it from the next Clip, with no gap.

**Approach:**
- The orchestrator re-resolves the active game session's settings on each settings change (a new `SettingsUpdated` message the engine posts on every commit) and after each Reset. When the result needs another encoder chain, it calls `ICaptureSession.PrepareChain(next)`.
- The session builds that chain **in advance** on a background thread: its own device taps, AAC encoders and mixers, video encoder, frame pool and scaler at the new size, and its own `ReplayRingSink`. The chain stays idle.
- At the next save press whose write starts, the session **arms the switch**. The new chain's audio starts at the press QPC. Its video starts with the first captured frame after the press, which is also the frame the old chain's forced key lands on. From then on, saves cut from the new chain's sink.
- The old chain keeps encoding only until every write on it has cut, then it is torn down. At most two chains exist at any time (AD-7's budget).
- The old Clip ends at its key `K`, and the new chain's first keyframe `B` starts the next Clip. When `B` is more than one frame after `K`, the session raises `warning.raised { code: reset-gap }`. That happens when no frame arrived after the press, e.g. a static window.

## Boundaries & Constraints

**Always:**
- AD-19: only the orchestrator decides. The session switches only inside a started `IClipWrite.Write` on the orchestrator thread, and `IClipWrite.SwitchesTo` tells the orchestrator, which then updates `active.Spec` (its `Snapshot` and `Audio`; the `Target` is kept).
- Every chain's PTS are relative to the one session anchor, so µs-since-anchor, `_claim` and `_resetUs` stay session-wide and comparable across chains.
- A Clip comes from one chain's sink. A save binds its chain (sink and encoder) in `Save`, before any claim. That settles both of 4.6's deferred items without `RingGenerations.Restart`: there is no cross-chain fallback, and each sink converts its own PTS. Leave `Restart` and its fallback untouched; record it for the 4.13 sweep.
- Budget: at most two chains live (current plus prepared, building or retiring). A `PrepareChain` while a chain is retiring or already armed is remembered and built once retirement ends. A newer spec replaces an un-armed prepared chain, which is disposed first.
- No switch while a Recording is attached to the current chain (`RecordingTap.HasWriter`): it waits for a later press (AD-5: Recordings change at the next Recording). `Record` returns null while a switch is armed.
- AD-7: nothing managed is allocated per frame or per packet. Building, tearing down and retiring run off the orchestrator thread and off the capture thread. The D3D11 device is multithread-protected, so building on a background thread is safe.
- Lean tests (CLAUDE.md "Tests"). GPU capture tests are `[Fact(Explicit = true)]` with `[Trait("Category", "Heavy")]`.

**Never:**
- A Contracts settings-shape change. The only Contracts change is `ErrorCodes.ResetGap = "reset-gap"`, additive (AD-22), added to `All`.
- Chain switching in file mode (`!options.Replay`, debug-capture), on a drained session, or before the session's anchor. Before the anchor, remember the request and build once anchored.
- Disposing an encoder, sink or tap that a waiting `ClipWrite` still uses before that write has cut.
- A cold rebuild (tearing the old chain down, then building). When the in-advance build fails, log a warning and clear the request. The orchestrator asks again at the next Reset or settings change, and the session retries.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Profile video at start | Profile A overrides `video` 1920×1080; game B has no profile overrides | A's session spec and output are 1920×1080; B's are Global | — |
| Layout change mid-session | Global 2 Tracks at 640×480; then profile override: 3 Tracks at 1280×720; ~1.5 s, press, ~3 s, press | Clip 1: 2 Tracks, 640×480. Clip 2: the 3 new Tracks, 1280×720, starting within one frame of Clip 1's end. No `reset-gap` | — |
| Own Mic Device | The profile's layout marks only its own mic `isMic` in a `muteAffected` Track; mic muted | That mic is silent in its Track; the Global layout's mic (not `isMic` here) stays audible | — |
| Revert before the press | A change, then a change back to the current chain | `PrepareChain(null)`: the prepared chain is dropped; no switch | — |
| Mute-only edit | Only `isMic` or `muteAffected` changes | Counts as another chain (`SameChain` false), so it applies from the next Clip | — |

Untested by design (lean tests; engine-internal timing), but required behaviour:
- A press while the build still runs doesn't switch; a later press does.
- A failed build logs a Warning, the session keeps its chain, and the next Reset or settings change retries it. It is no session failure.
- With no frame after the press (a static window, where the old key comes from a repeat), the switch happens at the next captured frame, and `reset-gap` is raised with the gap length.
- When the switching press's write fails, the switch stands (it counts as a Reset), and the retry cuts from the new chain only.

</intent-contract>

## Code Map

- `src/ReplayBuffer.Core/Sessions/TargetGames.cs` (beside `ChainKey`): `public static bool SameChain(SessionSpec a, SessionSpec b)`. True when the `ChainKey`s are equal and both give the same `GatedMicInputs()` (each pair as Track name plus device endpoint, ignoring case, and flow, in order). The session uses it too.
- `src/ReplayBuffer.Core/Sessions/CaptureSessionPorts.cs`:
  - `ICaptureSession.PrepareChain(SessionSpec? next) { }`. Default member, documented: build `next`'s chain in advance for the next save press, or drop any prepared one when null; orchestrator thread; never blocks.
  - `IClipWrite.SwitchesTo => null`: the spec this write's press switched the session to, set once `Write` has armed a switch.
- `src/ReplayBuffer.Core/Sessions/SessionOrchestrator.cs`:
  - A public `sealed record SettingsUpdated : SessionMessage`, documented ("the settings document was committed; any thread").
  - `Active` gains `Func<EffectiveSettings, SessionAudio> Audio` (from `Start`: `StartOnTarget` passes `_targetCapture.Audio`; `StartSession` passes `_ => start.Audio`) and `bool Pending`.
  - A new `ApplySettings(Active)`. Only for `_active` of kind Game with `_clips` set. It resolves for the profile and builds `next = active.Spec with { Snapshot, Audio = active.Audio(snapshot) }`, catching a throwing `Audio` (an invalid layout: skip).
    - If `SameChain(next, active.Spec)`: when `Pending`, call `PrepareChain(null)` and clear `Pending`.
    - Otherwise call `PrepareChain(next)` and set `Pending`.
    - A throwing session call is swallowed, like `SetMicMuted`.
  - Call it on `SettingsUpdated`, and in `OnClipWritten` after `written.Write.Reset()` for `_active`.
  - In `SaveClip`, after `write.Write(...)` succeeds: if `write.SwitchesTo is { } s`, then `active.Spec = active.Spec with { Snapshot = s.Snapshot, Audio = s.Audio }` and `Pending = false`.
  - Update the class doc.
- `src/ReplayBuffer.Media/FramePacer.cs`: an optional constructor parameter `bool firstFrameTakesNearestSlot = false`. When set, the first `Offer` (nothing emitted or held yet) first sets `NextSlot = Math.Max(NextSlot, NearestSlot(captureQpc))`. A chain that starts mid-session then emits no repeats back to slot 0. The default is unchanged. Doc it.
- `src/ReplayBuffer.Platform.FFmpeg/AacEncoder.cs`: an optional `long startSample = 0`. `_samplesSent` starts there, and `ReceivePackets` unrefs and skips packets with `pts + duration <= startSample` (the priming, as `PacketSink.BeforeStart` drops at 0).
- `src/ReplayBuffer.Platform.FFmpeg/ReplayRingSink.cs`: `public long FirstKeyUs` is the first video keyframe ever written, in µs since the anchor; `long.MaxValue` until then. Set once in `Write`: check the unset field first, then `isKey`, then the video stream, so the steady state costs one field read.
- `src/ReplayBuffer.Platform.Capture/WindowCapture.cs`: `public void SetFps(int fps)` sets `MinUpdateInterval` as the constructor does. Log and keep the old interval on failure.
- `src/ReplayBuffer.Engine/Sessions/EncoderChain.cs` (new, internal sealed, `IDisposable`): everything per chain that `CaptureSession` now holds in fields.
  - The spec, output size, encoder name and codec.
  - Device taps (`SessionAudioTrack` and subscriptions), `AudioTimeline`s, `AacEncoder`s, `TrackMixer`s.
  - `D3D11VAFrames`, `FrameScaler`, `VideoEncoder`, `PacedVideoEncoder`.
  - The `ReplayRingSink` and `RecordingTap` in Replay mode, or the `FragmentedMp4Muxer` in file mode, and `WindowSeconds`.
  - Its audio anchor QPC (the session anchor for the first chain, the switching press for later ones) and `StartSample` (that QPC as frames since the session anchor).
  - Its writer count, and `Retiring`/`SwitchEndUs`.
  - Move `StartOutput`'s build code, `Finish`, the silence padding, `ReportAudioFaults`, `DisposeAll`'s per-chain part and `NewRing`/`RingCapacity` (per chain spec) into it.
  - A later chain uses:
    - `FramePacer(..., session anchor, firstFrameTakesNearestSlot: true)`;
    - `AudioTimeline` and `TrackWiring.DeviceSink` anchored at its audio anchor (so `MicMuteGate` positions are relative to it);
    - `AacEncoder(startSample: StartSample)`;
    - mixer positions relative to its audio anchor: the silence clock and `Finish` pad to frames-since-audio-anchor.
  - The first chain is built exactly as today. Keep the existing log lines.
  - `FrameUs` = 1e6 / fps. `SnapStart(fromUs)`: if `FirstKeyUs < fromUs <= FirstKeyUs + FrameUs`, return `FirstKeyUs`. Otherwise return `fromUs`. A one-frame overlap then never drops the next Clip to the next GOP.
- `src/ReplayBuffer.Engine/Sessions/CaptureSession.cs`: holds `_current`, `_prepared`, `_armed` (the chain plus the press QPC), `_retiring`, `_wanted` and the build thread, guarded by a `_chainLock` (the chain fields `OnFrame` reads stay under `_gate`).
  - `OnFrame`: build the first chain at the anchor as today. If `_armed` and `qpc > press`, it becomes `_current` and the old one `_retiring` (start its retirement if it has no writers). Then draw and post to `_current` and to `_retiring` while it is fed.
  - `Save`: binds `_current` (null when none), registers a writer on it, and passes the chain to `ClipWrite`.
  - `ClipWrite.Write`: after the keyframe request, if `_prepared` is ready, nothing is armed, no Recording is attached and the session is live, arm it: publish its taps with timelines at the press QPC and set `SwitchesTo`.
  - `ClipWrite.Run`:
    - Uses its chain's sink and encoder throughout. `fromUs = chain.SnapStart(...)` before the cut.
    - After the cut (or on failure before it), it ends its writer registration exactly once, in `finally`. The switching write first sets `oldChain.SwitchEndUs = toUs`.
  - Retirement runs on a background thread when `Retiring` and the writers are 0:
    - Take the chain out of `OnFrame`'s feed under `_gate`, then dispose it with no `Finish`.
    - Check the gap: wait up to `SaveWait` for the new chain's `FirstKeyUs`. When it exceeds `SwitchEndUs + new FrameUs`, call `options.ResetGap(message)` with the gap in ms and log it at Warning.
    - If the fps changed, `_capture.SetFps(new fps)`.
    - Then start a remembered `_wanted` build.
  - `PrepareChain` (orchestrator thread):
    - No-op when `!Replay`, drained, `_outputFinished` or stopped.
    - `SameChain` with the chain that is or will be current (`_armed ?? _current`; before the anchor, `_spec`): drop an un-armed prepared chain and clear `_wanted`.
    - The same as `_wanted`: nothing.
    - Otherwise `_wanted = next`, dispose an un-armed prepared chain, and start a build unless it isn't anchored yet, a chain is retiring or armed, or a build is running. The anchor, retirement and build completion each start the pending build.
    - The build thread hands its chain over under the lock only if `_wanted` still matches and the session is live; otherwise it disposes it. On failure it logs a Warning and clears `_wanted`.
  - `ResizeAt` acts on `_current`, with `SnapStart(us)`.
  - `NewCapture` uses `_current`'s fps once a chain exists.
  - `End`/`Drain`:
    - Wait for a running build.
    - Dispose `_prepared`/`_armed` and `_retiring`.
    - `Finish` and dispose `_current` as today; a drain keeps `_current`'s ring.
    - `_outputFinished` releases waiting writers as today.
  - `Record` uses `_current` and returns null while armed.
  - `WriteStats` (file mode) reads `_current`.
  - Update the class, `Save` and `ClipWrite` docs.
- `CaptureSessionOptions`: `public Action<string>? ResetGap { get; init; }`.
- `src/ReplayBuffer.Engine/EngineHost.cs`:
  - Add `load.Store.Changed += _ => settingsTo?.Post(new SettingsUpdated())`, with a `SessionOrchestrator? settingsTo` assigned once `sessions` exists, the way `proposeTo` is.
  - Pass `ResetGap = message => warnings.Raise(ErrorCodes.ResetGap, WarningSeverity.Warning, "replay", message, overlay: false)` in `CaptureSessionOptions` (line ~324).
- `src/ReplayBuffer.Contracts/ErrorCodes.cs`: `ResetGap`, with a doc comment in the `MemoryMargin` warning style.
- Test reuse:
  - `tests/ReplayBuffer.Core.Tests/SessionOrchestratorTests.cs`: `FakeFactory`, `FakeSession` (~line 1247; add a recorded `PrepareChain`), `FakeWrite` (add an init `SwitchesTo`), `Revision`, `InFolder`, `NextAsync`, `ProfileId`.
  - `tests/ReplayBuffer.Engine.Tests/ReplayClipCaptureTests.cs` and `MicMuteCaptureTests.cs` (the window script, `ToneHub`, `SessionTestKit`, ffprobe and volume helpers, log parsing).

## Tasks & Acceptance

**Execution:**
- [ ] `src/ReplayBuffer.Contracts/ErrorCodes.cs` -- `ResetGap` -- the AD-7 warning code
- [ ] `src/ReplayBuffer.Core/Sessions/{TargetGames,CaptureSessionPorts,SessionOrchestrator}.cs` -- `SameChain`, `PrepareChain`, `SwitchesTo`, `SettingsUpdated`, `ApplySettings` -- the re-resolve and the orchestrator's view of the switch
- [ ] `src/ReplayBuffer.Media/FramePacer.cs`, `src/ReplayBuffer.Platform.FFmpeg/{AacEncoder,ReplayRingSink}.cs`, `src/ReplayBuffer.Platform.Capture/WindowCapture.cs` -- mid-session start points, the first key, the capture rate
- [ ] `src/ReplayBuffer.Engine/Sessions/{EncoderChain,CaptureSession}.cs`, `EngineHost.cs` -- the chain extraction, the build in advance, the switch at the press, retirement, reset-gap
- [ ] `tests/ReplayBuffer.Core.Tests/SessionOrchestratorTests.cs` -- two tests:
  - Profile at start: a profile overriding `video` 1920×1080 and a layout with only its own mic `isMic` in a `muteAffected` Track, started through `ProposeTarget` with a `TargetCapture` from the layout. The factory's spec has 1920×1080 and `GatedMicInputs` holding only that mic. After its exit, a game with no profile overrides gets the Global video and the Global layout's mic.
  - Change: `SettingsUpdated` after a profile `trackLayout` override gives one `PrepareChain` with the new Track names. Reverting it gives `PrepareChain(null)`. A mute-only edit gives another `PrepareChain`. A save whose `FakeWrite.SwitchesTo` is the prepared spec makes the next `SettingsUpdated` with no change call nothing, and its Reset re-resolves (no call while it is the same chain).
- [ ] `tests/ReplayBuffer.Media.Tests/FramePacerTests.cs` -- one test: with `firstFrameTakesNearestSlot`, a first frame at 5 s (30 fps) emits slot 150 with no repeats before it
- [ ] New `tests/ReplayBuffer.Engine.Tests/ChainSwitchCaptureTests.cs` -- one Heavy test (Explicit, `Category=Heavy`), in process, `saveCooldown` 0, muted at start (no store):
  - Global is 640×480 with Tracks Game and Mic. In the Global layout, the Global mic device `mic` is `isMic` in Mic (`muteAffected`).
  - ~3 s after the anchor: the profile overrides `video` to 1280×720 and the layout to three Tracks: Game; Voice (`mic`, not `isMic` here); and Mic2 (`mic2`, the profile's own mic, `isMic`, `muteAffected`). Then post `SettingsUpdated`.
  - After 1.5 s, save; 3 s later, save. Expected:
    - Clip 1: 2 audio streams, 640×480.
    - Clip 2: Game/Voice/Mic2, 1280×720, with Voice audible and Mic2 silent (< −60 dBFS).
    - Clip 2's start (from the session log's "Clip of … s, a s to b s after the anchor") is within one frame of Clip 1's end.
    - No `ResetGap` call.

**Acceptance Criteria:**
- Given a profile that overrides `video` to 1080p, when its game becomes the Target, then its Replay Buffer encodes 1080p, while another game's encodes the Global resolution.
- Given a running session, when the profile's Track Layout and video change and two saves follow, then the first Clip keeps the old Tracks and size, and the second has the new ones and starts within one frame of the first one's end, with no `reset-gap`.
- Given a profile layout that marks only its own mic as a Mic Device in a `muteAffected` Track, when mic mute is on, then that mic is silent there and the Global layout's mic is not silenced.
- Given the default run, when the 0-warning Release build, the Contracts, Core, Media, Platform.FFmpeg, Platform.Capture and Engine suites, Engine's Heavy category and the NativeAOT publish run, then all pass.

## Implementation Notes

## Plan Change Log

## Review Triage Log

## Design Notes

**Why a sink per chain, not `RingGenerations.Restart`.** During the switch both chains append at once: the old one until its Clip's key and audio are in, the new one from the press. `RingGenerations` routes every append to one current ring. 4.6 deferred two hazards to here: a waiting save converting PTS with the wrong generation, and a fallback cut that its claim doesn't match. Both come from sharing one sink across encoders. A save bound to its chain's sink at the press has neither.

**Why switch at the press, not keep two chains running until a Reset.** Running both encoders from the edit until a successful save would double the GPU encode cost under the game for as long as the user waits. Building in advance and switching at the press keeps the overlap to about a second. The cost: a failed switching write can't retry the old chain's span (a Clip holds one chain), which AD-8's "a boundary counts as a Reset" already accepts.

**Why `SnapStart`.** A fresh pacer gives the press frame its nearest slot. The old pacer may already hold that slot and take the next one, so `B` can be one frame before `K`. Cutting from `K` would then miss `B` and start at the next keyframe, 1 s later.

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings, 0 errors
- `dotnet test --project tests/ReplayBuffer.Contracts.Tests -c Release --no-build` -- expected: all pass (the code registry)
- `dotnet test --project tests/ReplayBuffer.Core.Tests -c Release --no-build` -- expected: all pass
- `dotnet test --project tests/ReplayBuffer.Media.Tests -c Release --no-build` -- expected: all pass
- `dotnet test --project tests/ReplayBuffer.Platform.FFmpeg.Tests -c Release --no-build` -- expected: all pass
- `dotnet test --project tests/ReplayBuffer.Platform.Capture.Tests -c Release --no-build` -- expected: all pass
- `dotnet test --project tests/ReplayBuffer.Engine.Tests -c Release --no-build` -- expected: all pass
- `dotnet test --project tests/ReplayBuffer.Engine.Tests -c Release --no-build --explicit only` -- expected: all pass, `ChainSwitchCaptureTests` included (the capture and encode path changed); a real-time test failing only under GPU load gets one rerun
- `pwsh tools/publish-engine.ps1` -- expected: 0 IL/AOT warnings
