---
title: 'Tracer: Global Track Layout to named Track streams in a capture'
type: 'feature'
ticket: '1'
created: '2026-11-01'
status: 'ready-for-dev'
baseline_revision: 'bc4d4a12bb672a1efd392281b3c7c254d959c9f7'
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
- [ ] `src/ReplayBuffer.Media/TrackMixer.cs` -- TrackMixer + AudioFanOut -- the QPC-aligned per-Track sum
- [ ] `tests/ReplayBuffer.Media.Tests/TrackMixerTests.cs` -- passthrough bit-identical; uneven chunking sums aligned; silence inputs; 2 s lag bound; Flush pads; fan-out -- the matrix's mixer rows
- [ ] `src/ReplayBuffer.Core/Sessions/SessionSpec.cs` -- SessionTrack, FromLayout, OnePerDevice -- the layout-to-session mapping
- [ ] `tests/ReplayBuffer.Core.Tests/SessionAudioTests.cs` -- order, shared-device dedupe, unreferenced device skipped, empty Track refused
- [ ] `src/ReplayBuffer.Engine/Sessions/CaptureSession.cs` -- per-Track encoders, mixers, fan-out; stats per device
- [ ] `src/ReplayBuffer.Platform.Audio/AudioEndpoints.cs`, `NativeMethods.txt` -- ParentName
- [ ] `src/ReplayBuffer.Engine/Settings/SettingsStore.cs` -- ReadDocument + tests in `SettingsStoreTests.cs`
- [ ] `src/ReplayBuffer.Engine/CommandLine.cs`, `Program.cs`, `TracerCapture.cs` -- `--track-layout`, `--instance`, debug-list fields + `CommandLineTests`
- [ ] `tests/ReplayBuffer.Engine.Tests/TrackLayoutCaptureTests.cs` -- runs the published engine as a process: `debug-capture --instance ReplayBuffer-Test-<guid> --track-layout --seconds 3` on a throwaway pwsh window. settings.json holds a layout from the default output and input endpoints, with Tracks `Game`={out}, `Both`={out,in}, `Mic`={in}. ffprobe shows 3 AAC streams with those titles in order and the first is default. Then the empty-Track and absent-device error cases. The test instance's folders are deleted afterwards.
- [ ] Update the existing tests for the SessionAudio shape; extend the debug-list process test.

**Acceptance Criteria:**
- Given the instance's settings.json holds a Global trackLayout with three Tracks of one present device each, when `debug-capture --track-layout --seconds 30` runs, then ffprobe on the file shows one video stream and three AAC streams whose `title` tags are the Track names in layout order, and each stream lasts as long as the video to within 0.1 s.
- Given a Track that mixes two devices, when the capture runs, then its stream holds both devices' audio and the device shared with another Track also appears in that Track's stream.
- Given `debug-list`, when it runs, then every present endpoint's line shows its flow, friendly name, parent name and endpoint id.
- Given no `--track-layout`, when `debug-capture` runs, then the file has the same streams as before (video plus the output and input device, named after the devices).

## Implementation Notes

## Plan Change Log

## Review Triage Log

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

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings, 0 errors
- `dotnet test --solution ReplayBuffer.slnx` -- expected: all pass
- `pwsh tools/publish-engine.ps1` -- expected: 0 IL/AOT warnings

**Manual checks (if no CLI):**
- A real 30 s `debug-capture --instance ReplayBuffer-Test-2-1 --track-layout` with the BEACN Game, Chat and Mic devices; ffprobe shows three titled AAC streams in order. Listening in VLC is the builder's check.
