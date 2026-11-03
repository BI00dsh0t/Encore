---
title: 'Refactor sweep'
type: 'refactor'
ticket: '10'
created: '2026-11-03'
status: 'in-progress'
baseline_revision: '22b214e9daa66fdbeb7a8c4f236e1aebbdc1156b'
route: 'full'
route_source: 'auto'
risk: 'low'
review: ''
review_source: ''
lenses_ran: []
review_loop_iteration: 0
followup_review_recommended: false
context: []
warnings: []
deferred: []
---

<intent-contract>

## Intent

**Problem:** Epic 2 left its audio wiring written twice. The live meters (2.6) copied the capture session's device tap and its device-to-Track fan-out, with mic-mute gating. Core matches an endpoint and flow in three places, and the Mic-in-affected-Track walk exists twice. No epic 2 plan deferred an item: every `deferred:` is empty, and `deferred-work.md` holds only epic 1's items, which already have owners.

**Approach:** Pull each duplicate into one shared helper with its code unchanged, drop one dead member, and record in `deferred-work.md` that epic 2 deferred nothing.

## Boundaries & Constraints

**Always:**
- No behaviour change. Wiring, order, log text and the hot paths (`OnPcm`, the sinks) stay as they are. Helpers run at wiring time only, and the per-block path allocates nothing new.
- Keep to epic 2's code. New helpers are `internal` unless they need to cross an assembly; `SessionAudio` and `SessionTrack` methods may be public, as records in Core already are.

**Never:**
- Touch the files other stories are editing: `EngineHost.cs`, `Engine/Program.cs`, `CommandLine.cs`, `SettingsStore.cs`, `SessionOrchestrator.cs`, `MainViewModel.cs`, `MainWindow.xaml`, `Contracts/Messages/Engine.cs`, `Platform.Windows/*`, `Core/Output/*`.
- Change Contracts' public surface, or unify `AudioDeviceHub.KeyOf`. It is case-sensitive, so unifying it would change behaviour.
- Add tests beyond what the lean-tests rule needs. The existing suites are the proof.

</intent-contract>

## Code Map

- `src/ReplayBuffer.Engine/Sessions/SessionAudioTrack.cs`: a sealed `IAudioPcmSubscriber` with a volatile `_timeline` and `Publish`/`OnPcm`/`Timeline`, plus `Device` and `Subscription`.
- `src/ReplayBuffer.Engine/Audio/TrackMeters.cs`: `MeterTap` (~line 257) has the same body as `SessionAudioTrack`. The `ActiveWatch` constructor (~lines 150-180) loops each tapped device over each Track and uses `inputs[t].IndexOf(d)`, where `inputs` holds only the devices whose tap opened. It wraps a Mic Device in a `muteAffected` Track in `MicMuteGate(sink, Schedule, frequency, anchorQpc)`, and publishes `new AudioTimeline(freq, anchor, sinks.Count == 1 ? sinks[0] : new AudioFanOut(sinks))`.
- `src/ReplayBuffer.Engine/Sessions/CaptureSession.cs`:
  - ~lines 473-494: the same fan-out as `ActiveWatch`, over `tracks[t].DeviceIndexes`, with `_micMute` (a `MicMuteSchedule`).
  - `GatedInputs()` (~740), `FirstTrackOf()` (~755, used ~697) and `IndexOf()` (~769) are private helpers.
- `src/ReplayBuffer.Engine/TracerCapture.cs`: `LogMicMute` (~173) lists the same (muteAffected Track, Mic Device) pairs that `GatedInputs` counts, as `'{device}' in '{track}'`.
- `src/ReplayBuffer.Core/Sessions/SessionSpec.cs`: `SessionAudio.FromLayout` has two inline endpoint-and-flow lambdas, `IsMic` (~69) and `FindIndex` (~84).
- `src/ReplayBuffer.Core/Audio/AudioDeviceListBuilder.cs`: `KeyComparer.Equals` (~75) is the third copy of the match. Leave `GetHashCode` as it is.
- `src/ReplayBuffer.Engine/Settings/RuntimeStateStore.cs`: line 34, `public string FilePath` is unused in src, tests and bench (the tests define their own).
- Tests that cover all this: `tests/ReplayBuffer.Engine.Tests` (`TrackLayoutCaptureTests`, `TrackMixCaptureTests`, `MicMute*Tests`, the TrackMeters tests, `RuntimeStateStoreTests`, plus the Heavy `MicMuteCaptureTests`) and `tests/ReplayBuffer.Core.Tests` (`SessionAudioTests`, the device-list builder tests).

## Tasks & Acceptance

**Execution:**
- [ ] `src/ReplayBuffer.Engine/Audio/AudioTimelineTap.cs`: new `internal class AudioTimelineTap : IAudioPcmSubscriber` holding `SessionAudioTrack`'s tap body (`Timeline`, `Publish`, `OnPcm`) and its doc comment, reworded for both users. `SessionAudioTrack` becomes `internal sealed class SessionAudioTrack(SessionAudioDevice device) : AudioTimelineTap` and keeps `Device` and `Subscription`. `TrackMeters` deletes `MeterTap` and uses `AudioTimelineTap`.
- [ ] `src/ReplayBuffer.Engine/Audio/TrackWiring.cs`: new `internal static class TrackWiring` with `IAudioSampleSink DeviceSink(int device, bool isMic, IReadOnlyList<SessionTrack> tracks, IReadOnlyList<IReadOnlyList<int>> inputs, IReadOnlyList<TrackMixer> mixers, MicMuteSchedule schedule, long frequency, long anchorQpc)`. It is the loop body both callers share, and returns the single sink or an `AudioFanOut`. Both callers call it and keep building their own `AudioTimeline`. `CaptureSession` passes each Track's `DeviceIndexes` as `inputs`, and `ActiveWatch` passes its filtered `inputs`.
- [ ] `src/ReplayBuffer.Core/Sessions/SessionSpec.cs`:
  - Add `SessionAudio.FirstTrackOf(int device)` (-1 when none) and `SessionAudio.GatedMicInputs()`, which yields `(SessionTrack Track, SessionAudioDevice Device)` in Track then device-index order.
  - `CaptureSession` drops `GatedInputs`, `FirstTrackOf` and `IndexOf` and calls these instead; `GatedInputs` becomes `.Count()`. `TracerCapture.LogMicMute` maps the pairs to the same strings.
- [ ] `src/ReplayBuffer.Core/Audio/AudioEndpointMatch.cs`: new `internal static class AudioEndpointMatch` with `bool Same(string idA, AudioFlow flowA, string idB, AudioFlow flowB)` (same flow, `OrdinalIgnoreCase` id). `FromLayout`'s two lambdas and `KeyComparer.Equals` call it.
- [ ] `src/ReplayBuffer.Engine/Settings/RuntimeStateStore.cs`: delete the unused `FilePath`.
- [ ] `_bmad-output/deferred-work.md`: add a line saying that epic 2 (2.1–2.9, 2.11) deferred no items, and that story 2.10 closed the duplicates listed above.

**Acceptance Criteria:**
- Given the sweep, when `dotnet build ReplayBuffer.slnx -c Release` runs, then it reports 0 warnings and 0 errors.
- Given the sweep, when the default suites of `ReplayBuffer.Engine.Tests` and `ReplayBuffer.Core.Tests` run, then all pass. That includes the multi-Track capture tests, whose files carry each named Track in layout order with exactly its devices' tones.
- Given the sweep touches the audio wiring, when `ReplayBuffer.Engine.Tests` runs with `--explicit only`, then the mic-mute and Track-mix capture tests pass.
- Given the sweep changes engine code, when `pwsh tools/publish-engine.ps1` runs, then it succeeds with 0 IL2xxx/IL3xxx warnings.

## Implementation Notes

## Plan Change Log

## Review Triage Log

## Design Notes

- **Out of scope:**
  - `TrackLayoutPage.SameEndpoint` in the App: the App doesn't reference Core, so sharing it would add Contracts surface.
  - The `AudioFlow` conversions across assemblies.
  - `PeakLimiter.ReleaseSeconds` visibility.
- **The two fan-outs:** they differ only in their inputs list (filtered or not) and their schedule, frequency and anchor values, which is why the helper takes all of these as parameters.

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release`: expected 0 warnings.
- `dotnet test --project tests/ReplayBuffer.Engine.Tests -c Release --no-build`, then the same for `tests/ReplayBuffer.Core.Tests`: expected all pass.
- `dotnet test --project tests/ReplayBuffer.Engine.Tests -c Release --no-build --explicit only`: expected all pass. The accepted real-time flakes get one rerun.
- `pwsh tools/publish-engine.ps1`: expected 0 IL2xxx/IL3xxx warnings.
