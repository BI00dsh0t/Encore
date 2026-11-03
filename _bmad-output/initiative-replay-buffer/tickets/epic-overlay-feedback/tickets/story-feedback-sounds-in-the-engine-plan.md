---
title: 'Feedback Sounds in the engine'
type: 'feature'
ticket: '6'
created: '2026-11-03'
status: 'built'
baseline_revision: '7a9506cdf07062318716ff265facbf119f70709a'
route: 'full'
route_source: 'auto'
risk: 'medium'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
followup_review_recommended: false
context: ['{project-root}/CLAUDE.md']
warnings: ['oversized']
deferred: []
---

<intent-contract>

## Intent

**Problem:** The FeedbackRouter (5.4) names a Feedback Sound for each notice, but nothing plays it, so over an exclusive-fullscreen game (where the Overlay can't be seen, spike 5.2) the user can't tell a saved Clip from a failed save. Nothing warns when a Track would record the sounds.

**Approach:** A Core `FeedbackSoundPlayer` behind an `IFeedbackSoundOutput` port plays five built-in synthesized sounds on a worker thread, on `feedbackSoundDevice` or the default communications endpoint, gated by `feedbackSounds`. Platform.Audio's WASAPI shared-mode render client implements the port. The engine checks the active Track Layout against the sound endpoint and raises `feedback-device-captured`, and serves `feedback.preview`.

## Boundaries & Constraints

**Always:**
- Sounds (48 kHz stereo float, peak exactly -12 dBFS, each < 600 ms, short fade in/out): `clipSaved` rising two-note chime (E5 → A5), `saveFailed` low descending double buzz (220 → 165 Hz, odd harmonics), `recordingStarted` one high note (C6), `recordingStopped` one low note (E4), `diskFull` three short 196 Hz pulses.
- The player only enqueues: the router's and orchestrator's calls never block on audio. One worker plays in order. The device is resolved at each play (`feedbackSoundDevice`, else the port's default communications render endpoint), so it follows a default-endpoint change. The flag is read at each enqueue, from the current settings (applied immediately).
- A failing play (missing or removed device, no default endpoint) logs a warning and is skipped. The next sound still plays, and nothing throws to the caller.
- A sound that's switched off is not played, and its notice's `Sound` is `null` (Contracts: "the name the engine played").
- `feedback-device-captured` (severity warning, source `feedback`, `overlay: false`, dismissable): raised when a Track of the active layout (resolved for the Target Game's profile, else global) mixes an Output Device whose endpoint is the sound endpoint. Re-checked on settings change, endpoint change (including a default communications change) and target change. Raised once per distinct condition, and withdrawn when it clears.
- `feedback.preview { sound }` (camelCase `FeedbackSound` enum) in `Contracts/Messages/Feedback.cs` plays the sound even when it's switched off. New code `feedback-device-captured` in `ErrorCodes` and `All`.
- Test instances get a silent output unless a test injects one. Only non-test instances open WASAPI.
- Lean tests with fake outputs. The real-device check is `Explicit` + `Heavy`.

**Never:** No Overlay or App changes (5.11 adds the Play buttons). No custom sound files, no volume setting. No change to capture, mixing or the hub's loopback (sounds stay out of Tracks by warning, as AD-15 says). No change to save, cooldown or Recording logic. Don't change existing notice codes or messages.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Each event | saved, not saved, Recording on/off, disk full; all on, device null | each sound plays once on the default communications endpoint | none |
| Switched off | `feedbackSounds.clipSaved = false`, Clip saved | nothing plays; notice `Sound` null | none |
| Device gone | output throws for the device | logged and skipped; next sound plays | caught on the worker |
| Preview | `feedback.preview {clipSaved}` while switched off | plays `clipSaved` | none |
| Captured | layout Track with an Output Device = sound endpoint | `feedback-device-captured` warning | none |
| Not captured | same id as an Input Device, or a device no Track uses | no warning | none |

</intent-contract>

## Code Map

- `src/ReplayBuffer.Core/Feedback/FeedbackRouter.cs` -- add an optional `FeedbackSoundPlayer? sounds = null` constructor parameter. Before each `notices.Raise`, call `sounds.Play(notice.Sound)`. When it returns false, raise `notice with { Sound = null }`. Play only enqueues, so it's safe to call under the router's lock. Keep the existing tests passing unchanged.
- `src/ReplayBuffer.Core/Feedback/` (new files) -- `FeedbackSoundSynth` (pure PCM render, cached), `IFeedbackSoundOutput` (`string? DefaultEndpointId()`, `void Play(string endpointId, ReadOnlyMemory<float> pcm)`, which blocks until played), `FeedbackSoundPlayer`, and `FeedbackCapture.CapturingTracks(TrackLayout, string endpointId)`, which uses `AudioEndpointMatch.Same` (internal, in Core). Core has no logging package: the player takes an `Action<string, Exception>` for failures.
- `src/ReplayBuffer.Contracts/Settings/SettingValues.cs:125` -- the `FeedbackSounds` record (flags per sound). `Keys.FeedbackSoundDevice` holds an endpoint id or null. Read with `SettingsResolver.Resolve(doc, null)`; neither key is overridable. Read-only.
- `src/ReplayBuffer.Contracts/Messages/Feedback.cs` -- add the `FeedbackSound` enum (`CamelCaseEnumConverter`), `FeedbackPreview(FeedbackSound Sound)` and `FeedbackMessages.Preview` (`feedback.preview`, result `Empty`). Add it to `Commands` and `[JsonSerializable(typeof(FeedbackPreview))]`.
- `src/ReplayBuffer.Contracts/ErrorCodes.cs` -- add `FeedbackDeviceCaptured`, and add it to `All`.
- `src/ReplayBuffer.Platform.Audio/AudioEndpoints.cs` -- add `DefaultCommunications()`: `GetDefaultAudioEndpoint(eRender, eCommunications)`, returning null when there is none.
- `src/ReplayBuffer.Platform.Audio/EndpointWatcher.cs:66` -- `OnDefaultDeviceChanged` signals for render `eCommunications`, so `EndpointsChanged` fires (coalesced) and the check re-runs.
- `src/ReplayBuffer.Platform.Audio/TonePlayer.cs` -- the WASAPI render pattern (format, Initialize, GetBuffer). The new `FeedbackSoundOutput` opens shared mode without an event callback and a 1 s buffer. It writes the whole sound, Starts, polls `GetCurrentPadding` every 20 ms until it is 0 (cap: length + 500 ms), then Stops and releases. It wraps failures in `ErrorCodeException(AudioDeviceFailed)`.
- `src/ReplayBuffer.Engine/EngineHost.cs` -- `EngineHostOptions.FeedbackSounds` (`IFeedbackSoundOutput?`). When it's null: `FeedbackSoundOutput` for a non-test instance, a silent output for a test one. Create the player before the router and pass it in. Add a `FeedbackDeviceCheck` (Engine/Audio, new) fed by `load.Store.Changed`, `(hostOptions.AudioEndpoints ?? audio).EndpointsChanged` and the target-changed lambda; it reads `hub.State.TargetGame?.ProfileId`. Register `feedback.preview`. Dispose the check, then the player, after the router. Update the class summary.
- `src/ReplayBuffer.Engine/Ipc/Warnings.cs` -- `Raise(...)` / `Withdraw(id)` are reused by the check. Read-only.
- `tests/ReplayBuffer.Engine.Tests/DevSimulateServeTests.cs` -- the golden serve test (host start, client, `UntilAsync`). `FakeEndpointSource` lives in the Engine tests.

## Tasks & Acceptance

**Execution:**
- [x] `src/ReplayBuffer.Contracts/ErrorCodes.cs`, `Messages/Feedback.cs` -- the code, the enum, the preview command.
- [x] `src/ReplayBuffer.Core/Feedback/FeedbackSoundSynth.cs`, `FeedbackSoundPlayer.cs`, `FeedbackCapture.cs` (new) -- synth, port, player (`bool Play(string name)`, `void Preview(FeedbackSound)`, `IDisposable`, which drains and joins), capture check logic.
- [x] `src/ReplayBuffer.Core/Feedback/FeedbackRouter.cs` -- optional player.
- [x] `src/ReplayBuffer.Platform.Audio/FeedbackSoundOutput.cs` (new), `AudioEndpoints.cs`, `EndpointWatcher.cs` -- the WASAPI port and the default communications endpoint.
- [x] `src/ReplayBuffer.Engine/Audio/FeedbackDeviceCheck.cs` (new), `EngineHost.cs` -- composition, `feedback.preview`.
- [x] `tests/ReplayBuffer.Core.Tests/FeedbackSoundsTests.cs` (new) -- matrix rows 1 to 4 and 6 (player and router over a fake output), plus the synth check.
- [x] `tests/ReplayBuffer.Engine.Tests/FeedbackSoundServeTests.cs` (new) -- over the pipe with a fake output: a layout capturing the fake's default endpoint raises `feedback-device-captured`, and `feedback.preview` plays a switched-off sound.
- [x] `tests/ReplayBuffer.Platform.Audio.Tests/FeedbackSoundOutputTests.cs` (new, Explicit + Heavy) -- plays each sound on the PC's default communications endpoint.

**Acceptance Criteria:**
- Given every sound on and no device set, when a Clip is saved, a save fails, a Recording starts and stops, and the disk fills, then each matching sound plays once, on the default communications endpoint.
- Given `clipSaved` switched off, when a Clip is saved, then no sound plays and the notice's `Sound` is null; when `feedback.preview {clipSaved}` is sent, then it plays.
- Given the output fails for the device, when a sound plays, then no exception reaches the caller and the next sound plays.
- Given an active layout whose Track loopback-captures the sound endpoint, when the settings apply, then a `feedback-device-captured` warning is active.
- Given the five rendered sounds, then each is under 600 ms at 48 kHz, peaks at -12 dBFS (±0.1 dB), and `clipSaved` rises in pitch while `saveFailed` falls.

## Implementation Notes

- Review patches were applied in the main session, two one-line edits: the silent output's placeholder endpoint and the check's condition key.

- `FeedbackNotice.Sound` is now `string?`, so the router can raise a switched-off notice with no sound. Existing router tests pass unchanged.
- `IFeedbackSoundOutput` and `SilentFeedbackSoundOutput` live in `FeedbackSoundPlayer.cs`. The player also exposes `Endpoint()` (device, else default communications), which the engine check reuses.
- `FeedbackCapture.CapturingTracks` returns each capturing Track with its matching device, so the warning can name the device.
- `FeedbackDeviceCheck` subscribes to `SettingsStore.Changed` and `EndpointsChanged` itself and unsubscribes on dispose; the host calls `Check()` once before the pipe serves and after each `target.changed`. The condition key is the endpoint plus the capturing Track ids; a changed condition withdraws the old warning and raises a new one.
- The real-device check measured about 480 ms for the 450 ms `clipSaved`, so playback ends on the padding drain, not the cap.

## Plan Change Log

## Review Triage Log

### 2026-11-03 — Review pass
- verdicts: 2 findings — high 0, medium 0, low 2, false 0, maybe-false 0
- findings:
  - `[low]` `[patch]` On a test instance, the silent output had no default endpoint, so every sound logged a "didn't play" warning and its notice still named the sound — fixed: `SilentFeedbackSoundOutput.DefaultEndpointId()` returns the placeholder `silent`, so the play succeeds silently. A real instance with no communications endpoint still keeps `Sound` on its notice; the plan nulls it only for a switched-off sound.
  - `[low]` `[patch]` The `feedback-device-captured` message kept old names after a Track or device rename, because the condition key held only Track ids — fixed: the key also holds each Track's name and device's friendly name, so a rename re-raises with the current names.

## Design Notes

- "Synthesized at build time into embedded WAVs": the sounds are rendered by deterministic code compiled into Core and cached on first use, rather than by an MSBuild tool step. Nothing ships as a file and the samples are identical every run. This avoids a build-time tool project and keeps the NativeAOT publish simple. The test renders them and checks length, peak and pitch.
- Pitch check: zero-crossing rate over the first and last 100 ms. The chime adds 0.3 × 2nd harmonic (which adds no zero crossings) and the buzz adds odd harmonics at 1/n, so crossings track the fundamental.
- The player resolves the device at each play instead of caching it. Following the default communications endpoint needs no notification. The watcher change only re-runs the capture check.

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings
- `dotnet test --project tests/ReplayBuffer.Contracts.Tests -c Release --no-build`, and the same for `ReplayBuffer.Core.Tests`, `ReplayBuffer.Engine.Tests` and `ReplayBuffer.Platform.Audio.Tests` -- expected: all pass
- `dotnet test --project tests/ReplayBuffer.Platform.Audio.Tests -c Release --no-build --explicit only --filter-method "*FeedbackSoundOutput*"` -- expected: plays the five sounds without error
- `pwsh tools/publish-engine.ps1` -- expected: 0 IL warnings

## Auto Run Result

**Summary:** The engine plays five built-in synthesized Feedback Sounds (48 kHz stereo, peak -12 dBFS, under 600 ms) on a worker thread through a Core `IFeedbackSoundOutput` port. The port is backed by a WASAPI shared-mode client in Platform.Audio. Each plays on `feedbackSoundDevice` or the default communications endpoint, resolved at each play, and only when its `feedbackSounds` flag is on. A switched-off sound's notice carries no sound. `feedback.preview` plays a sound even when it is switched off. `FeedbackDeviceCheck` raises `feedback-device-captured` while a Track of the active layout loopback-captures the sound endpoint; it re-checks on settings, endpoint (including a default communications change) and target changes. Test instances use a silent output.

**Files changed:**
- `src/ReplayBuffer.Contracts/ErrorCodes.cs`: adds `feedback-device-captured`.
- `src/ReplayBuffer.Contracts/Messages/Feedback.cs`: adds the `FeedbackSound` enum and `feedback.preview`.
- `src/ReplayBuffer.Core/Feedback/FeedbackSoundSynth.cs`: new, the five sounds.
- `src/ReplayBuffer.Core/Feedback/FeedbackSoundPlayer.cs`: new, the port, the silent output and the queued player.
- `src/ReplayBuffer.Core/Feedback/FeedbackCapture.cs`: new, the Tracks that capture the sound endpoint.
- `src/ReplayBuffer.Core/Feedback/FeedbackRouter.cs`: queues each notice's sound.
- `src/ReplayBuffer.Core/Audio/AudioDeviceHub.cs`: the `EndpointsChanged` doc.
- `src/ReplayBuffer.Platform.Audio/FeedbackSoundOutput.cs`: new, WASAPI render.
- `src/ReplayBuffer.Platform.Audio/AudioEndpoints.cs`: adds `DefaultCommunications()`.
- `src/ReplayBuffer.Platform.Audio/EndpointWatcher.cs`: signals on a default communications change.
- `src/ReplayBuffer.Engine/Audio/FeedbackDeviceCheck.cs`: new, the warning.
- `src/ReplayBuffer.Engine/EngineHost.cs`: wires these in.
- Tests: `Core.Tests/FeedbackSoundsTests.cs`, `Engine.Tests/FeedbackSoundServeTests.cs`, and `Platform.Audio.Tests/FeedbackSoundOutputTests.cs` (Explicit, Heavy).

**Review:** quick lens, 2 findings. Both were low and patched (see the triage log). None were deferred or rejected.

**Follow-up review recommended:** false. Both patches were low, at 0 high and 0 medium.

**Verification:**
- Release build: 0 warnings.
- Contracts: 99/99 passed.
- Core: 79/79 passed.
- Platform.Audio: 24 passed, 3 explicit skipped.
- Engine: 154 passed, 18 explicit skipped. One run before the patches had a single failure that couldn't be identified; two reruns and the run after the patches were clean.
- Heavy `FeedbackSoundOutput` test: passed on the default communications endpoint, run by the implementing subagent.
- `tools/publish-engine.ps1`: no IL warnings.
- Matrix: rows 1 to 4 and 6 are covered by `FeedbackSoundsTests`, and row 5 by `FeedbackSoundServeTests`.

**Residual risks:**
- On a target change, the capture check makes one COM call on the orchestrator thread to read the default endpoint.
- Nobody has listened to the sounds yet (queued for the builder).
- No test covers a default communications change re-running the check.
