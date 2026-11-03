---
title: 'Refactor sweep'
type: 'refactor'
ticket: '10'
created: '2026-11-03'
status: 'built'
baseline_revision: '22b214e9daa66fdbeb7a8c4f236e1aebbdc1156b'
route: 'full'
route_source: 'auto'
risk: 'low'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
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
- [x] `src/ReplayBuffer.Engine/Audio/AudioTimelineTap.cs`: new `internal class AudioTimelineTap : IAudioPcmSubscriber` holding `SessionAudioTrack`'s tap body (`Timeline`, `Publish`, `OnPcm`) and its doc comment, reworded for both users. `SessionAudioTrack` becomes `internal sealed class SessionAudioTrack(SessionAudioDevice device) : AudioTimelineTap` and keeps `Device` and `Subscription`. `TrackMeters` deletes `MeterTap` and uses `AudioTimelineTap`.
- [x] `src/ReplayBuffer.Engine/Audio/TrackWiring.cs`: new `internal static class TrackWiring` with `IAudioSampleSink DeviceSink(int device, bool isMic, IReadOnlyList<SessionTrack> tracks, IReadOnlyList<IReadOnlyList<int>> inputs, IReadOnlyList<TrackMixer> mixers, MicMuteSchedule schedule, long frequency, long anchorQpc)`. It is the loop body both callers share, and returns the single sink or an `AudioFanOut`. Both callers call it and keep building their own `AudioTimeline`. `CaptureSession` passes each Track's `DeviceIndexes` as `inputs`, and `ActiveWatch` passes its filtered `inputs`.
- [x] `src/ReplayBuffer.Core/Sessions/SessionSpec.cs`:
  - Add `SessionAudio.FirstTrackOf(int device)` (-1 when none) and `SessionAudio.GatedMicInputs()`, which yields `(SessionTrack Track, SessionAudioDevice Device)` in Track then device-index order.
  - `CaptureSession` drops `GatedInputs`, `FirstTrackOf` and `IndexOf` and calls these instead; `GatedInputs` becomes `.Count()`. `TracerCapture.LogMicMute` maps the pairs to the same strings.
- [x] `src/ReplayBuffer.Core/Audio/AudioEndpointMatch.cs`: new `internal static class AudioEndpointMatch` with `bool Same(string idA, AudioFlow flowA, string idB, AudioFlow flowB)` (same flow, `OrdinalIgnoreCase` id). `FromLayout`'s two lambdas and `KeyComparer.Equals` call it.
- [x] `src/ReplayBuffer.Engine/Settings/RuntimeStateStore.cs`: delete the unused `FilePath`.
- [x] `_bmad-output/deferred-work.md`: add a line saying that epic 2 (2.1–2.9, 2.11) deferred no items, and that story 2.10 closed the duplicates listed above.

**Acceptance Criteria:**
- Given the sweep, when `dotnet build ReplayBuffer.slnx -c Release` runs, then it reports 0 warnings and 0 errors.
- Given the sweep, when the default suites of `ReplayBuffer.Engine.Tests` and `ReplayBuffer.Core.Tests` run, then all pass. That includes the multi-Track capture tests, whose files carry each named Track in layout order with exactly its devices' tones.
- Given the sweep touches the audio wiring, when `ReplayBuffer.Engine.Tests` runs with `--explicit only`, then the mic-mute and Track-mix capture tests pass.
- Given the sweep changes engine code, when `pwsh tools/publish-engine.ps1` runs, then it succeeds with 0 IL2xxx/IL3xxx warnings.

## Implementation Notes

- `CaptureSession` passes `tracks.Select(t => t.DeviceIndexes).ToList()` as `inputs`, once at `StartOutput`; `TrackWiring` keeps the old ordinal `IndexOf` loop privately.
- Heavy run (`--explicit only`): 16 passed, 2 failed twice: `CaptureEndTests.AStatsRunWritesAReportThatMatchesTheFile` (`nonIncreasingStamps` 1, video) and `OutputResolutionTests.AStatsRunOnAFastWindowMeetsTheLatencyGcAndAllocationBounds`. Both fail the same way on the baseline (22b214e/8857c3c) built in a separate worktree, so they are not from this sweep. The mic-mute and Track-mix capture tests pass.

## Plan Change Log

## Review Triage Log

### 2026-11-03 — Review pass
- verdicts: 2 findings — high 0, medium 0, low 1, false 1, maybe-false 0
- findings:
  - `[low]` `[patch]` The `GatedMicInputs` doc comment says the pairs come "in Track then device-index order", but each Track's `DeviceIndexes` follows `DeviceKeys` order, not sorted index order. Confirmed; the behaviour matches the old code. Fix: the comment now says "in Track order, then each Track's device order".
  - `[false]` `[reject]` The Implementation Notes record no result for the build, default-suite and publish ACs. The implementer's run met all three, and this session re-ran the build and suites on the final tree (see Auto Run Result). The finding's fix would be a plan edit, so it is rejected as a review finding.
- The patch is one comment line, applied in the orchestrating session, not by the implementation subagent.

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

## Auto Run Result

**2026-11-03, bmad-build-auto (full route, quick review).**

- **Summary:** Epic 2's duplicated audio wiring now lives in one place, with no behaviour change:
  - the capture session and the meter watch share one device tap (`AudioTimelineTap`);
  - they also share one device-to-Track fan-out with mic-mute gating (`TrackWiring.DeviceSink`);
  - Core matches an endpoint and flow in one place (`AudioEndpointMatch.Same`);
  - the Mic-in-affected-Track walk and the first-Track lookup are `SessionAudio.GatedMicInputs`/`FirstTrackOf`;
  - the unused `RuntimeStateStore.FilePath` is deleted.

  Epic 2 deferred no items, and `deferred-work.md` records that.
- **Files:**
  - `src/ReplayBuffer.Engine/Audio/AudioTimelineTap.cs` (new): the shared tap. `SessionAudioTrack.cs` derives from it, and `TrackMeters.cs` drops `MeterTap`.
  - `src/ReplayBuffer.Engine/Audio/TrackWiring.cs` (new): the shared fan-out, called from `CaptureSession.cs` and `TrackMeters.cs`.
  - `src/ReplayBuffer.Core/Audio/AudioEndpointMatch.cs` (new): the endpoint match, used by `SessionSpec.cs` and `AudioDeviceListBuilder.cs`.
  - `src/ReplayBuffer.Core/Sessions/SessionSpec.cs`: `FirstTrackOf` and `GatedMicInputs`. `CaptureSession.cs` and `TracerCapture.cs` lose their private copies.
  - `src/ReplayBuffer.Engine/Settings/RuntimeStateStore.cs`: deletes `FilePath`.
  - `_bmad-output/deferred-work.md`: the epic 2 entry.
- **Review:** 2 findings. 1 low was patched (a comment), 1 false was rejected, 0 were deferred. Follow-up review recommended: false (no high or medium patched).
- **Verification:**
  - Release build: 0 warnings.
  - `ReplayBuffer.Core.Tests`: 58/58.
  - `ReplayBuffer.Engine.Tests` default run: 143 passed, 18 Heavy skipped.
  - Engine `--explicit only`: 16/18. The mic-mute and Track-mix capture tests passed. The two failures are 1.14's accepted real-time flakes (`CaptureEndTests.AStatsRunWritesAReportThatMatchesTheFile`, `OutputResolutionTests.AStatsRunOnAFastWindowMeetsTheLatencyGcAndAllocationBounds`), and they failed the same way on the baseline.
  - `pwsh tools/publish-engine.ps1`: 0 IL2xxx/IL3xxx warnings (10.3 MB).
  - Other projects were skipped: the sweep touches only Core and Engine code, and no App, Media, Platform.Audio or Contracts code.
- **Line endings:** the implementer had rewritten `TracerCapture.cs` from CRLF to LF, which made the whole file one diff. CRLF is restored, so the diff holds only the real change and won't conflict with epic 3's work.
- **Residual risks:** none new. The two Heavy stats tests failing on the baseline are worth a look in epic 8's sweep if they keep failing alone.

