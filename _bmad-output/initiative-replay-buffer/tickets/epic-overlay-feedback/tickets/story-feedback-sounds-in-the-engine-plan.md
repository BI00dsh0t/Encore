---
title: 'Feedback Sounds in the engine'
type: 'feature'
ticket: '6'
created: '2026-11-03'
status: 'in-progress'
baseline_revision: '7a9506cdf07062318716ff265facbf119f70709a'
route: 'full'
route_source: 'auto'
risk: 'medium'
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
- [ ] `src/ReplayBuffer.Contracts/ErrorCodes.cs`, `Messages/Feedback.cs` -- the code, the enum, the preview command.
- [ ] `src/ReplayBuffer.Core/Feedback/FeedbackSoundSynth.cs`, `FeedbackSoundPlayer.cs`, `FeedbackCapture.cs` (new) -- synth, port, player (`bool Play(string name)`, `void Preview(FeedbackSound)`, `IDisposable`, which drains and joins), capture check logic.
- [ ] `src/ReplayBuffer.Core/Feedback/FeedbackRouter.cs` -- optional player.
- [ ] `src/ReplayBuffer.Platform.Audio/FeedbackSoundOutput.cs` (new), `AudioEndpoints.cs`, `EndpointWatcher.cs` -- the WASAPI port and the default communications endpoint.
- [ ] `src/ReplayBuffer.Engine/Audio/FeedbackDeviceCheck.cs` (new), `EngineHost.cs` -- composition, `feedback.preview`.
- [ ] `tests/ReplayBuffer.Core.Tests/FeedbackSoundsTests.cs` (new) -- matrix rows 1 to 4 and 6 (player and router over a fake output), plus the synth check.
- [ ] `tests/ReplayBuffer.Engine.Tests/FeedbackSoundServeTests.cs` (new) -- over the pipe with a fake output: a layout capturing the fake's default endpoint raises `feedback-device-captured`, and `feedback.preview` plays a switched-off sound.
- [ ] `tests/ReplayBuffer.Platform.Audio.Tests/FeedbackSoundOutputTests.cs` (new, Explicit + Heavy) -- plays each sound on the PC's default communications endpoint.

**Acceptance Criteria:**
- Given every sound on and no device set, when a Clip is saved, a save fails, a Recording starts and stops, and the disk fills, then each matching sound plays once, on the default communications endpoint.
- Given `clipSaved` switched off, when a Clip is saved, then no sound plays and the notice's `Sound` is null; when `feedback.preview {clipSaved}` is sent, then it plays.
- Given the output fails for the device, when a sound plays, then no exception reaches the caller and the next sound plays.
- Given an active layout whose Track loopback-captures the sound endpoint, when the settings apply, then a `feedback-device-captured` warning is active.
- Given the five rendered sounds, then each is under 600 ms at 48 kHz, peaks at -12 dBFS (±0.1 dB), and `clipSaved` rises in pitch while `saveFailed` falls.

## Implementation Notes

## Plan Change Log

## Review Triage Log

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
