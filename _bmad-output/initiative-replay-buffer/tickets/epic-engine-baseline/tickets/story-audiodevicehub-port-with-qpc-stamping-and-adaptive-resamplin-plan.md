---
title: 'AudioDeviceHub port with QPC stamping and adaptive resampling'
type: 'feature'
ticket: '12'
created: '2026-10-27'
status: 'ready-for-dev'
baseline_revision: 'f3406896c4485cb0d2ae4afa35296c628dfafc62'
route: 'full'
route_source: 'auto'
risk: 'high'
review: ''
review_source: ''
lenses_ran: []
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/initiative-replay-buffer/architecture-replay-buffer/architecture-replay-buffer.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Each session opens its own WASAPI clients and writes device frames straight into AAC through `AudioTimeline`, which only steps by silence or trims once a device has drifted 5 ms. The BEACN Mic runs +54 ppm against QPC (spike 1.2), ≈ 32 ms over 10 minutes, so its Track walks away from the video and the other device. Epic 2's Track Layouts and meters need one shared, QPC-locked PCM source per device (AD-20, AD-13, R4, R6).

**Approach:** A Core port `IAudioDeviceHub` and its WASAPI implementation in `Platform.Audio`: one client per endpoint (loopback for Output Devices, capture for Input Devices), opened by the first subscriber and closed by the last, stamped from `pu64QPCPosition`, gaps and idle time filled with silence, then one adaptive resampler per device (in `Media`) steered so its output sample count tracks elapsed QPC. Blocks of 48 kHz stereo float, each stamped with its first frame's QPC, fan out to subscribers. `CaptureSession` subscribes instead of opening devices.

## Boundaries & Constraints

**Always:**
- Port in `ReplayBuffer.Core.Audio`: `Subscribe(endpointId, flow, subscriber)` returns a disposable subscription; after `Dispose` returns, that subscriber gets no further callback. Callbacks run on the device's MMCSS thread, synchronously, and must not block.
- Stream contract: device frame k is at `origin + k/48000 s` in QPC; each block carries its first frame's QPC. Blocks are contiguous; silence blocks carry no samples.
- Steering: PI control on the error between where input content lands in the output and its stamp; ratio clamped to ±1000 ppm. Error past 5 ms (a gap without a discontinuity flag, a stall) is corrected by a step: silence or trim. First packet, discontinuity, and resume after idle fill are placed exactly.
- A subscriber that throws is cut off and its exception kept on its subscription; others keep receiving. A device failure holds the stream silent, as today, and is kept as the subscription's device fault. Session behaviour is unchanged: an `encode-failed` subscriber fault fails the session, a device fault is a warning.
- Nothing managed allocated per packet or block (AD-7); buffers sized at open. NativeAOT publish 0 IL2xxx/IL3xxx, no new suppressions. P/Invoke only in `Platform.*`; resampler and steering pure in `Media`.
- Decision (2026-10-27, auto-approved by the user): "one audio frame" is one AAC frame, 1024 samples (21.3 ms), per the PRD's use with "the nearest AAC frame boundary". The verify asserts that bound and also reports the measured error, expected to be a few samples.
- Decision (2026-10-27, auto-approved by the user): the verify test runs 10 minutes when `REPLAYBUFFER_LONG_TESTS=1`, and 60 s in the normal suite.
- Decision (2026-10-27, auto-approved by the user): the stats report lists every audio device (not only the first per flow) and gains a `resampler` block per device, additively; `timeline` counts come from the hub's device stream. Schema stays 1.
- The verify's devices are matched by name ("BEACN Mix Create" Game output, "BEACN Mic" input), overridable by `REPLAYBUFFER_TEST_OUTPUT` / `REPLAYBUFFER_TEST_INPUT`; a missing device fails the test with the list of endpoints. The tone is quiet (−30 dBFS).
- Results recorded in the epic's Notes.

**Never:** Device-change notifications or rebinding (story 2.3). Track Layouts, mixers, mute, meters, `device-dropout` warnings (epic 2). Contracts or IPC changes. `SessionSpec` changes. The 60-minute benchmark (story 1.14). Replacing WASAPI's own 48 kHz float conversion.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Clock drift | device ±54 and ±200 ppm, stamp jitter 0.15 ms | output count within 2 samples of elapsed QPC after 5 s settle | No error expected |
| Idle loopback | no packets for 2 s, then packets | silence tracks QPC; resume placed exactly | No error expected |
| Gap, flagged | discontinuity after 30 ms missing | 30 ms silence, steering keeps its rate | No error expected |
| Gap, unflagged | stamp 20 ms after prediction | one step correction (silence) | counted |
| Overlap | stamp 10 ms before prediction | trim 10 ms | counted |
| Bad stamp | zero, error flag, or backwards | extrapolated from the last packet | counted |
| Two subscribers | session + second subscriber on one device | one client; both get identical blocks | No error expected |
| Last unsubscribe | final subscription disposed | thread stopped, client released; next subscribe reopens | No error expected |
| Subscriber throws | one of two subscribers throws | it is cut off with its fault kept; the other continues | fault on subscription |
| Device open fails | bad endpoint ID | `Subscribe` throws `audio-device-failed` | session start fails as today |

</frozen-after-approval>

## Code Map

- `src/ReplayBuffer.Platform.Audio/WasapiCapture.cs` -- client setup (AUTOCONVERTPCM to 48 kHz float stereo, event-driven, 100 ms buffer), MMCSS thread, `Drain` (`GetBuffer` flags + qpc → `QpcClock.From100ns`), idle fill (`FillTo(now − 50 ms)` on wait timeouts), fault handling. Becomes the hub's internal per-endpoint client, feeding a `DeviceStream` instead of an `AudioTimeline`.
- `src/ReplayBuffer.Platform.Audio/AudioEndpoints.cs` -- `OpenDevice`, `EnsureCom`, `Describe` (friendly name for logs), `List/Default/Match` (verify device lookup). `NativeMethods.txt` -- add `IAudioRenderClient`, `IAudioClock` for the tone player.
- `src/ReplayBuffer.Media/AudioTimeline.cs` -- keep: the session subscriber's anchor aligner (drops data before the anchor; hub blocks are contiguous, so it never corrects after that). Its stamp resolution and gap/trim rules are the model for the device stream's. `AudioPacketFlags` lives here.
- `src/ReplayBuffer.Media/AudioStampStats.cs` -- raw stamp health and raw drift; the hub keeps one per device (it is allocation-free).
- `src/ReplayBuffer.Media/QpcClock.cs` -- `From100ns`, `ToUnits` (Int128, rounded).
- `src/ReplayBuffer.Engine/Sessions/CaptureSession.cs` -- `Open` opens devices (`OpenDevice`, line 672), `StartOutput` starts them into timelines at the anchor, `End` stops audio after `_paced.Complete()` and before `Finish`, `ReportAudioFaults`, `StampsFor`, `WriteStats`, `DisposeAll` order. Rewire: subscribe in `Open`, subscriber drops blocks until `StartOutput` publishes its timeline (volatile), dispose subscriptions where `audio.Stop()` is today.
- `src/ReplayBuffer.Engine/Sessions/CaptureSession.cs` `CaptureSessionFactory` -- gains the hub. `src/ReplayBuffer.Engine/EngineHost.cs:132` and `src/ReplayBuffer.Engine/TracerCapture.cs:89` -- create one hub per engine / debug run and dispose it last.
- `src/ReplayBuffer.Engine/CaptureStatsReport.cs` -- `AudioReport`, `TimelineReport`, `Report(...)`; `tests/ReplayBuffer.Engine.Tests/CaptureEndTests.cs:180` asserts roles `["output","input"]` and `samplesEncoded == timeline.writtenFrames` (writtenFrames becomes the session timeline's, still equal).
- `tests/ReplayBuffer.Engine.Tests/SessionTestKit.cs:89`, `SessionCaptureTests.cs:150` -- build `SessionAudio` from default endpoints; `CaptureSessionFactory` calls need the hub.
- `src/ReplayBuffer.Platform.FFmpeg/AacEncoder.cs` -- `IAudioSampleSink`, takes its own lock; unchanged.
- Builder's PC: Game (BEACN Mix Create) −0.8 ppm, Microphone (BEACN Mic) +54 ppm, jitter p99 0.11–0.15 ms. Known flake: the hotkey latency test.

## Tasks & Acceptance

**Execution:**
- [ ] `src/ReplayBuffer.Core/Audio/AudioDeviceHub.cs` -- `AudioFormat` (48000, 2), `IAudioDeviceHub`, `IAudioPcmSubscriber.OnPcm(in AudioBlock)`, `AudioBlock` ref struct (StartQpc, Frames, Interleaved, IsSilent), `IAudioSubscription` (DeviceFault, SubscriberFault, Dispose). `AudioTimeline.SampleRate/Channels` alias `AudioFormat`.
- [ ] `src/ReplayBuffer.Media/AdaptiveResampler.cs` -- polyphase windowed-sinc, fractional step, preallocated history and output; reset to silence.
- [ ] `src/ReplayBuffer.Media/DeviceStream.cs` -- per-device: stamp resolution, gap/overlap/idle handling, PI steering of the resampler to elapsed QPC, emits stamped blocks to a sink; counters (gap fills, trims, idle fill, resolved stamps, step corrections, ratio ppm, steering error histogram after settle).
- [ ] `src/ReplayBuffer.Platform.Audio/AudioDeviceHub.cs`, `WasapiCapture.cs` (internal client) -- endpoint → device map under a lock, open on first subscribe, close on last; copy-on-write subscriber array; dispatch under the device lock; faults per subscription; diagnostics per device (stamp stats + stream counters) on the concrete subscription.
- [ ] `src/ReplayBuffer.Platform.Audio/TonePlayer.cs` -- WASAPI shared render of a quiet tone with 3 kHz bursts once a second, recording each burst's QPC from `IAudioClock::GetPosition`; reusable by story 1.14.
- [ ] `src/ReplayBuffer.Engine/Sessions/CaptureSession.cs`, `EngineHost.cs`, `TracerCapture.cs`, `CaptureStatsReport.cs` -- rewire through the hub; report every device with a `resampler` block.
- [ ] `tests/ReplayBuffer.Media.Tests/AdaptiveResamplerTests.cs`, `DeviceStreamTests.cs` -- every matrix row on synthetic devices; ratio 1 passes a sine unchanged (bar the filter delay); a 1 kHz sine through ±1000 ppm keeps its amplitude within 0.1 dB.
- [ ] `tests/ReplayBuffer.Platform.Audio.Tests/AudioDeviceHubTests.cs` -- the hub rows (two subscribers, last unsubscribe reopen, throwing subscriber, bad endpoint) on real default devices.
- [ ] `tests/ReplayBuffer.Platform.Audio.Tests/DeviceSyncVerifyTests.cs` -- the story verify: tone into the BEACN output while the hub captures it and the BEACN Mic, 10 min (or 60 s); per second, each stream's error between output position and elapsed QPC; burst onsets in the loopback stream against the player's QPC; asserts |error| and |out − mic| < 1024 samples and burst-offset spread < 1024 samples; writes a JSON report with raw drift, ratio ppm, error max/p99.
- [ ] Update `SessionTestKit`, `SessionCaptureTests`, `CaptureEndTests` for the hub; epic Notes with the measured results.

**Acceptance Criteria:**
- Given the builder's PC, when the verify runs for 10 minutes, then both streams stay within one AAC frame of elapsed QPC and of each other, the loopback bursts keep a constant offset to the player within one AAC frame, and the mic's raw drift is reported against its corrected error.
- Given a capture session or `debug-capture`, when it runs, then its file has both audio tracks from the hub, with the existing session tests and ffprobe checks passing.
- Given the solution, when `dotnet build ReplayBuffer.slnx`, `dotnet test --solution ReplayBuffer.slnx` and `pwsh tools/publish-engine.ps1` run, then 0 warnings and all tests pass (hotkey latency flake noted, not counted).

## Implementation Notes

## Plan Change Log

## Review Triage Log

## Design Notes

**Steering.** For each packet with stamp S at input index I, the error is e = (output index at which input I comes out) − (S − origin)·48000/QPF, in samples. The step (input frames per output frame) is 1 + Kp·e + Ki·Σe, clamped to 1 ± 10⁻³. Choose Kp so a constant error halves in ~1–2 s and Ki for a critically damped loop; at +54 ppm the integral settles at step ≈ 1 − 54·10⁻⁶. Stamp jitter of ±7 samples moves the step by < 10⁻⁴ per packet, which shifts position by a fraction of a sample. The resampler's group delay is part of "output index at which input I comes out", so stamps stay true.

**Why the session keeps `AudioTimeline`.** The hub's stream starts when the device opens, before the anchor; the timeline cuts it at the anchor and, because hub blocks are contiguous and QPC-locked, writes everything after as-is.

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx` -- expected: 0 warnings, 0 errors
- `dotnet test --solution ReplayBuffer.slnx` -- expected: all pass (hotkey latency flake noted)
- `REPLAYBUFFER_LONG_TESTS=1 dotnet test --project tests/ReplayBuffer.Platform.Audio.Tests --filter-class *DeviceSyncVerifyTests` (exact filter syntax per MTP) -- expected: pass over 10 minutes
- `pwsh tools/publish-engine.ps1` -- expected: 0 IL2xxx/IL3xxx warnings
