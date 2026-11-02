---
title: 'Tracer: Global Track Layout to named Track streams in a capture'
type: 'feature'
ticket: '1'
created: '2026-11-01'
status: done
baseline_revision: 'bc4d4a12bb672a1efd392281b3c7c254d959c9f7'
route: 'full'
route_source: 'auto'
risk: 'high'
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

**Problem:** A capture writes one AAC stream per device, named after the device. The Global `trackLayout` in settings is never read, so a capture can't carry the user's named Tracks (R2, R7).

**Approach:** `debug-capture --track-layout` reads the instance's Global `trackLayout` from its settings.json (read-only) and turns it into a `SessionAudio` of Tracks. The capture session subscribes once per distinct device through AudioDeviceHub, cuts each device's stream at the anchor with its own `AudioTimeline`, fans it out to every Track that holds the device, and sums each Track's devices by sample position (QPC-derived) in a thin `TrackMixer`. Each Track is written as one AAC stream titled with the Track's name, in layout order. `debug-list` also prints each endpoint's parent name and flow. `debug-capture --instance ReplayBuffer-Test-*` lets tests and parallel sessions use their own instance folder.

## Boundaries & Constraints

**Always:** All audio goes through `IAudioDeviceHub`, with one subscription per distinct device per session (AD-20). Device threads never block beyond the short locks already used by `AacEncoder`. A single-device Track is passed through bit-identically. Stream order is the layout's Track order, and the first Track is the default stream. Without `--track-layout`, `debug-capture` behaves exactly as before: the default or named output and input device, one stream each, named after the device. That keeps story 1.14's bench and the existing tests unchanged. Reading settings never writes, moves or deletes anything in the instance folder.

**Never:** No limiter, gain or clipping handling: story 2.2 owns them. No silent Track for a Track with no devices: story 2.2 owns that too, so such a Track is refused here. No dropout or rebind (2.5), no device notifications or IPC device list (2.3), no App UI. `serve` still starts no sessions. No change to Contracts message areas. The `trackLayout` shape is untouched (AD-4).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Three-Track layout | Tracks Game, Chat and Mic, each with one present device | 3 AAC streams titled Game, Chat, Mic in that order; Game is the default | — |
| Shared device | Track A = {out}, Track B = {out, in} | 2 streams; out is subscribed once; B = out + in, aligned by sample | — |
| Track with no devices | the default layout (Everything, no devices) | nothing captured | exit 1, `error invalid-arguments: Track 'Everything' has no devices…` |
| Absent device | the layout names an endpoint id that isn't present, or has the wrong flow | nothing captured | exit 1, `audio-device-not-found`, naming the device, with the present endpoints as candidates |
| Invalid layout in file | `trackLayout` present but failing validation | nothing captured | exit 1, `setting-invalid-value` naming `trackLayout` |
| Unreadable settings.json | invalid JSON | nothing captured; the file is left in place | exit 1, `settings-unreadable` |
| Flags mixed | `--track-layout` together with `--output-device` or `--input-device` | — | exit 2, `invalid-arguments` |
| Non-test instance | `--instance Foo` | — | exit 2, `invalid-arguments` (test names only, as for `serve`) |
| Inputs arrive unevenly | device A writes 480-frame chunks, device B 441-frame chunks | the mix holds frame k = A[k] + B[k] for every k | — |
| Input stalls | one input is more than 2 s behind the leader | the mixer emits up to leader − 2 s, filling the laggard with silence; its late frames are dropped | memory stays bounded |
| End | inputs at different lengths on Flush | the mix is padded to the longest input | — |

</intent-contract>

## Code Map

- `src/ReplayBuffer.Core/Sessions/SessionSpec.cs` -- `SessionAudio(Devices)` becomes `SessionAudio(Devices, Tracks)` with a new `SessionTrack(string Name, IReadOnlyList<int> DeviceIndexes)`. Add `SessionAudio.OnePerDevice(devices)` (the old behaviour: one Track per device, named FriendlyName) and `SessionAudio.FromLayout(TrackLayout)`. FromLayout keeps the Tracks in layout order. Its devices are the distinct layout devices some Track references, in first-reference order, as `SessionAudioDevice(EndpointId, Flow, FriendlyName)`. It throws `ErrorCodeException(invalid-arguments)` for a Track with no devices.
- `src/ReplayBuffer.Core/Sessions/SessionOrchestrator.cs` -- passes `SessionAudio` through; check only that it still compiles.
- `src/ReplayBuffer.Media/TrackMixer.cs` (new) -- `TrackMixer(int inputs, IAudioSampleSink output)`. `Input(i)` returns an `IAudioSampleSink`. It's thread-safe (one `Lock`), and with one input it passes through. Inputs are summed by frame position since the anchor. `MaxLagFrames = 2 * 48_000`. `Flush()` pads to the longest input. `AudioFanOut : IAudioSampleSink` writes one stream to several sinks. Follow the style of `AudioTimeline.cs` and `BoundedHandoff.cs`: XML doc comments, no allocation per block once warmed (reuse the pooled buffers).
- `src/ReplayBuffer.Engine/Sessions/CaptureSession.cs` -- `Open` subscribes once per `spec.Audio.Devices` entry (unchanged). `StartOutput` creates one `AacEncoder` per Track (title = Track name, `isDefault: i == 0`) and a `TrackMixer` per Track. Each device gets one `AudioTimeline` whose sink is the matching mixer input, or an `AudioFanOut` when the device is in several Tracks. `Finish` flushes the mixers, then the encoders. `WriteStats` keeps one `AudioReport` per device and passes the encoder samples of the device's first Track. Keep `DisposeAll`'s order: unsubscribe before freeing encoders. The `Finish`/`WriteStats` guards compare encoder count to Track count.
- `src/ReplayBuffer.Engine/Sessions/SessionAudioTrack.cs` -- per-device tap; unchanged apart from naming, if needed.
- `src/ReplayBuffer.Engine/CommandLine.cs` -- `DebugCaptureCommand` gains `bool TrackLayout` (`--track-layout`) and `string? Instance` (`--instance`, test names only, via `Instance.IsTestName`). `--track-layout` with `--output-device`/`--input-device` is an error. Update `Usage`.
- `src/ReplayBuffer.Engine/Program.cs` -- the instance comes from `ServeCommand.Instance` or `DebugCaptureCommand.Instance`. `DebugList` prints `flow`, the friendly name, `parent:` and the id for each endpoint.
- `src/ReplayBuffer.Engine/TracerCapture.cs` -- with `--track-layout`: read the document read-only from `instance.RoamingDataFolder` and take Global's `trackLayout` (`SettingsResolver.Resolve(doc, null).TrackLayout`). Refuse an invalid stored value, check that every device is present (`AudioEndpoints.List` of its flow, matched by id), and build `SessionAudio.FromLayout`. The session's document is the read document with the command line's `video` set. Without the flag: `SessionAudio.OnePerDevice` over output and input, as today.
- `src/ReplayBuffer.Engine/Settings/SettingsStore.cs` -- add `static SettingsDocument ReadDocument(string folder)`, read-only. A missing file gives `SettingsDocument.Empty`; an unparseable one throws `ErrorCodeException(settings-unreadable)` and is left in place. Reuse `TryParse`.
- `src/ReplayBuffer.Platform.Audio/AudioEndpoints.cs` + `NativeMethods.txt` -- `AudioEndpoint` gains `string ParentName = ""`, read from `PKEY_DeviceInterface_FriendlyName` (empty when missing). Existing constructors in tests keep compiling.
- Tests: `tests/ReplayBuffer.Media.Tests`, `tests/ReplayBuffer.Core.Tests`, `tests/ReplayBuffer.Engine.Tests` (`CommandLineTests`, `SettingsStoreTests`, `EngineProcessTests`, and a new capture test using `EngineBinaries` and ffprobe like `CaptureEndTests`), `tests/ReplayBuffer.Core.Tests/SessionOrchestratorTests.cs` (`NoAudio`), `tests/ReplayBuffer.Engine.Tests/SessionTestKit.cs` and `SessionCaptureTests.cs` (`DefaultAudio` → `OnePerDevice`).

## Tasks & Acceptance

**Execution:**
- [x] `src/ReplayBuffer.Media/TrackMixer.cs` -- TrackMixer + AudioFanOut -- the QPC-aligned per-Track sum
- [x] `tests/ReplayBuffer.Media.Tests/TrackMixerTests.cs` -- passthrough bit-identical; uneven chunking sums aligned; silence inputs; 2 s lag bound; Flush pads; fan-out -- the matrix's mixer rows
- [x] `src/ReplayBuffer.Core/Sessions/SessionSpec.cs` -- SessionTrack, FromLayout, OnePerDevice -- the layout-to-session mapping
- [x] `tests/ReplayBuffer.Core.Tests/SessionAudioTests.cs` -- order, shared-device dedupe, unreferenced device skipped, empty Track refused
- [x] `src/ReplayBuffer.Engine/Sessions/CaptureSession.cs` -- per-Track encoders, mixers, fan-out; stats per device
- [x] `src/ReplayBuffer.Platform.Audio/AudioEndpoints.cs`, `NativeMethods.txt` -- ParentName
- [x] `src/ReplayBuffer.Engine/Settings/SettingsStore.cs` -- ReadDocument + tests in `SettingsStoreTests.cs`
- [x] `src/ReplayBuffer.Engine/CommandLine.cs`, `Program.cs`, `TracerCapture.cs` -- `--track-layout`, `--instance`, debug-list fields + `CommandLineTests`
- [x] `tests/ReplayBuffer.Engine.Tests/TrackLayoutCaptureTests.cs` -- runs the published engine as a process: `debug-capture --instance ReplayBuffer-Test-<guid> --track-layout --seconds 3` on a throwaway pwsh window. settings.json holds a layout from the default output and input endpoints, with Tracks `Game`={out}, `Both`={out,in}, `Mic`={in}. ffprobe shows 3 AAC streams with those titles in order and the first is default. Then the empty-Track and absent-device error cases. The test instance's folders are deleted afterwards.
- [x] Update the existing tests for the SessionAudio shape; extend the debug-list process test.

**Acceptance Criteria:**
- Given the instance's settings.json holds a Global trackLayout with three Tracks of one present device each, when `debug-capture --track-layout --seconds 30` runs, then ffprobe on the file shows one video stream and three AAC streams whose `title` tags are the Track names in layout order, and each stream lasts as long as the video to within 0.1 s.
- Given a Track that mixes two devices, when the capture runs, then its stream holds both devices' audio and the device shared with another Track also appears in that Track's stream.
- Given `debug-list`, when it runs, then every present endpoint's line shows its flow, friendly name, parent name and endpoint id.
- Given no `--track-layout`, when `debug-capture` runs, then the file has the same streams as before (video plus the output and input device, named after the devices).

## Implementation Notes

- 2026-11-01, paused: the builder took the PC (the `.bench-running` pause). The implementing subagent was stopped during its own verification; its report never came back. All the plan's source and test files are written and committed as WIP. To resume: read the WIP diff against `baseline_revision`, then run the Verification commands. The build and tests have not been confirmed, and the matrix audit, the review (step 4) and the real BEACN capture are still to do.
- 2026-11-02, resumed from `420ee54`. I read the checkpoint diff against the plan: every Code Map item is in. Review patches went into `TrackMixer.cs` and the new `TrackMixCaptureTests.cs` (see the Review Triage Log). The first full test run had three failures:
  - `TrackMixCaptureTests` was my own measurement bug: ffmpeg's `-ac 1` downmix scales the level, so the test now reads the left channel directly.
  - `EncodeExperimentTests.ThePacedEncoderEncodesEverySlotWithAFastPreset` (latency p50) and `EngineConnectionTests.ASecondAppConnectsToTheRunningEngineWithoutLaunchingOne` (a 30 s connect) are real-time tests that ran under load. Both passed on their one rerun.
- Matrix audit, one row at a time:
  - Three-Track layout and Shared device: `TrackLayoutCaptureTests.EachTrackIsOneTitledAacStreamInLayoutOrder`, `TrackMixCaptureTests`, `SessionAudioTests.ADeviceSharedByTracksIsCapturedOnce`.
  - Track with no devices: `TheDefaultLayoutHasATrackWithNoDevicesAndIsRefused`, `SessionAudioTests.ATrackWithNoDevicesIsRefused`.
  - Absent device: `AnAbsentDeviceIsRefused…`, `AnInputEndpointNamedAsAnOutputIsAbsent`.
  - Invalid layout: `AnInvalidStoredLayoutIsSettingInvalidValue`.
  - Unreadable settings: `AnUnreadableSettingsFileIsSettingsUnreadableAndLeftInPlace`, plus the `ReadDocument` tests.
  - Flags mixed and Non-test instance: `CommandLineTests.DebugCaptureRefusesMixedDeviceFlagsAndNonTestInstances` and two `EngineProcessTests`.
  - Uneven inputs, stall, and end: `TrackMixerTests`.
  - All of them ran and passed.

## Plan Change Log

## Review Triage Log

### 2026-11-02 — Review pass
- verdicts: 6 findings — high 0, medium 2, low 3, false 1, maybe-false 0
- findings:
  - `[medium]` `[patch]` `TrackMixer.Append` held the mixer lock across a whole multi-chunk write, so a long silence fill stalled the other device's thread through every chunk's AAC encode — fixed: the lock is now taken per chunk, so the longest stall is one 4096-frame chunk's encode.
  - `[medium]` `[patch]` Acceptance criterion 2 (a mixed Track holds both devices, and a shared device appears in each of its Tracks) had no end-to-end check of `CaptureSession`'s wiring — fixed: added `TrackMixCaptureTests`. A fake hub plays 440 Hz and 1 kHz tones; the decoded Out, Both and In streams match the expected RMS within 10%, and each device is subscribed once.
  - `[false]` `[reject]` "No acceptance criterion verified; the commit is an unverified checkpoint" — that described the state while paused. Verification has since run: build, tests, publish, and the real 30 s BEACN capture.
  - `[low]` `[patch]` `DroppedFrames` was read without the lock — fixed: it is backed by a field and read under `_lock`, like `EmittedFrames`.
  - `[low]` `[patch]` On an encoder throw, `Emit` left `_emitted` unchanged, so the same range could be emitted again — fixed: `_emitted` is advanced before the write. A direct reorder.
  - `[low]` `[reject]` The `SessionAudio` constructor doesn't validate Track indexes — every caller today builds it through `FromLayout` or `OnePerDevice`, which guard; the fix would add guards for callers that don't exist. Story 2.2 changes the shape for empty Tracks anyway.

## Design Notes

Why one timeline per device and a fan-out, not one hub subscription per (Track, device) pair: AD-20's single client per endpoint already holds at the hub, but cutting at the anchor once per device keeps every Track sharing that device sample-identical. TrackMixer positions come from frame counts since the anchor, which `AudioTimeline` derives from QPC, so summing by position is summing by QPC.

TrackMixer core (sketch):
```csharp
// under _lock: append to input i's pending at position _written[i]; then
long ready = min(_written); // all inputs have reached this frame
if (max(_written) - ready > MaxLagFrames) ready = max(_written) - MaxLagFrames; // laggard → silence
EmitSum(_emitted, ready); _emitted = ready; // drop consumed pending
```

## Verification

**GPU lock:** story 1.14 runs benchmarks in parallel. Before every build, test run or capture, check for `%USERPROFILE%\Documents\repos\ReplayBuffer\.bench-running`. While it exists, wait for it to go, checking every few minutes with a background wait; never build, test or capture while it is there.

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings, 0 errors
- `dotnet test --solution ReplayBuffer.slnx` -- expected: all pass
- `pwsh tools/publish-engine.ps1` -- expected: 0 IL/AOT warnings

**Manual checks (if no CLI):**
- A real 30 s `debug-capture --instance ReplayBuffer-Test-2-1 --track-layout` with the BEACN Game, Chat and Mic devices; ffprobe shows three titled AAC streams in order. Listening in VLC is the builder's check.

## Auto Run Result

**Status:** built, verified 2026-11-02.

**Summary:** `debug-capture --track-layout` reads the instance's Global `trackLayout` read-only and captures one AAC stream per Track, titled with the Track's name, in layout order; the first Track is the default stream. Each distinct device is subscribed once through AudioDeviceHub and cut at the anchor once. Its stream fans out to every Track holding it, and `TrackMixer` sums each Track's devices by frame position since the anchor (QPC-derived), passing a single device straight through. `debug-capture --instance ReplayBuffer-Test-*` lets a run use its own settings and output folders. `debug-list` prints each endpoint's flow, name, parent name and id. Without `--track-layout`, captures are unchanged.

**Files:**
- `src/ReplayBuffer.Media/TrackMixer.cs` — new: `TrackMixer` (QPC-aligned per-Track sum, 2 s lag bound, Flush pads) and `AudioFanOut`.
- `src/ReplayBuffer.Core/Sessions/SessionSpec.cs` — `SessionTrack`; `SessionAudio(Devices, Tracks)` with `FromLayout`, `OnePerDevice`, `None`.
- `src/ReplayBuffer.Engine/Sessions/CaptureSession.cs` — one encoder and mixer per Track; a timeline per device fanned out to its Tracks; stats stay per device.
- `src/ReplayBuffer.Engine/TracerCapture.cs` — layout audio: rejects a stored layout that fails validation, a Track with no devices, and an absent device.
- `src/ReplayBuffer.Engine/CommandLine.cs`, `Program.cs` — `--track-layout`, `--instance`, and the new debug-list line.
- `src/ReplayBuffer.Engine/Settings/SettingsStore.cs` — `ReadDocument`, read-only.
- `src/ReplayBuffer.Platform.Audio/AudioEndpoints.cs`, `NativeMethods.txt` — `AudioEndpoint.ParentName`.
- Tests:
  - New: `TrackMixerTests`, `SessionAudioTests`, `TrackLayoutCaptureTests`, `TrackMixCaptureTests`.
  - Extended: `CommandLineTests`, `SettingsStoreTests`, `EngineProcessTests`.
  - Adjusted for the new `SessionAudio` shape: `SessionOrchestratorTests`, `SessionTestKit`, `SessionCaptureTests`.

**Review:** one quick pass, 6 findings.
- Patched: 2 medium and 2 low (listed in the Review Triage Log).
- Deferred: none.
- Rejected: 1 false, because verification was pending, not missing. And 1 low, because constructor validation for `SessionAudio` would guard callers that don't exist.

**Follow-up review recommended:** false. The pass patched no high entry and two medium ones (the lock scope, and the end-to-end test). Both are now covered by tests that pass, so no unverified risk can be named.

**Verification:**
- `dotnet build ReplayBuffer.slnx -c Release`: 0 warnings, 0 errors.
- `dotnet test --solution ReplayBuffer.slnx`: 574 tests. `TrackMixCaptureTests` failed first on its own downmix measurement; fixed, it passes. Two real-time tests (`ThePacedEncoderEncodesEverySlotWithAFastPreset`, `ASecondAppConnectsToTheRunningEngineWithoutLaunchingOne`) failed under load and passed on their one rerun.
- `pwsh tools/publish-engine.ps1`: 0 IL/AOT warnings.
- Real 30 s `debug-capture --instance ReplayBuffer-Test-2-1 --track-layout`: Game (BEACN Mix Create), Chat (BEACN Mix Create) and Microphone (BEACN Mic) as Tracks Game, Chat and Mic. ffprobe shows h264 plus three AAC streams named Game, Chat, Mic in order, Game the default. Their durations are 29.948, 29.963 and 30.040 s against 30.017 s of video, all within 0.1 s. All three were silent (-91 dB): nothing was playing, and the mic was idle.
- Proxy: a 10 s capture with Tracks System, Game and Both (System + Game), while ffplay played a -30 dBFS 440 Hz tone on the default output, System (BEACN Mix Create). System read -54.1 dB mean, Game -91.0 dB (silent), and Both -54.1 dB, identical to System. Each Track carries only its own devices.

**Residual risks:**
- Each Track ends where its own devices' last block landed, so streams can differ by up to about 0.1 s at the end.
- A Track with no devices is refused rather than recorded silent (story 2.2).
- The sum has no limiter, so a mix can exceed full scale (story 2.2).
- VLC listening and sync are the builder's check.
