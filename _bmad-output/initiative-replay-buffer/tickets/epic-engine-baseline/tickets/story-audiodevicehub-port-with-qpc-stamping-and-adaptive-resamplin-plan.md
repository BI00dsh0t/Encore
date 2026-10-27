---
title: 'AudioDeviceHub port with QPC stamping and adaptive resampling'
type: 'feature'
ticket: '12'
created: '2026-10-27'
status: 'built'
baseline_revision: 'f3406896c4485cb0d2ae4afa35296c628dfafc62'
route: 'full'
route_source: 'auto'
risk: 'high'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
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
- [x] `src/ReplayBuffer.Core/Audio/AudioDeviceHub.cs` -- `AudioFormat` (48000, 2), `IAudioDeviceHub`, `IAudioPcmSubscriber.OnPcm(in AudioBlock)`, `AudioBlock` ref struct (StartQpc, Frames, Interleaved, IsSilent), `IAudioSubscription` (DeviceFault, SubscriberFault, Dispose). `AudioTimeline.SampleRate/Channels` alias `AudioFormat`.
- [x] `src/ReplayBuffer.Media/AdaptiveResampler.cs` -- polyphase windowed-sinc, fractional step, preallocated history and output; reset to silence.
- [x] `src/ReplayBuffer.Media/DeviceStream.cs` -- per-device: stamp resolution, gap/overlap/idle handling, PI steering of the resampler to elapsed QPC, emits stamped blocks to a sink; counters (gap fills, trims, idle fill, resolved stamps, step corrections, ratio ppm, steering error histogram after settle).
- [x] `src/ReplayBuffer.Platform.Audio/AudioDeviceHub.cs`, `WasapiCapture.cs` (internal client) -- endpoint → device map under a lock, open on first subscribe, close on last; copy-on-write subscriber array; dispatch under the device lock; faults per subscription; diagnostics per device (stamp stats + stream counters) on the concrete subscription.
- [x] `src/ReplayBuffer.Platform.Audio/TonePlayer.cs` -- WASAPI shared render of a quiet tone with 3 kHz bursts once a second, recording each burst's QPC from `IAudioClock::GetPosition`; reusable by story 1.14.
- [x] `src/ReplayBuffer.Engine/Sessions/CaptureSession.cs`, `EngineHost.cs`, `TracerCapture.cs`, `CaptureStatsReport.cs` -- rewire through the hub; report every device with a `resampler` block.
- [x] `tests/ReplayBuffer.Media.Tests/AdaptiveResamplerTests.cs`, `DeviceStreamTests.cs` -- every matrix row on synthetic devices; ratio 1 passes a sine unchanged (bar the filter delay); a 1 kHz sine through ±1000 ppm keeps its amplitude within 0.1 dB.
- [x] `tests/ReplayBuffer.Platform.Audio.Tests/AudioDeviceHubTests.cs` -- the hub rows (two subscribers, last unsubscribe reopen, throwing subscriber, bad endpoint) on real default devices.
- [x] `tests/ReplayBuffer.Platform.Audio.Tests/DeviceSyncVerifyTests.cs` -- the story verify: tone into the BEACN output while the hub captures it and the BEACN Mic, 10 min (or 60 s); per second, each stream's error between output position and elapsed QPC; burst onsets in the loopback stream against the player's QPC; asserts |error| and |out − mic| < 1024 samples and burst-offset spread < 1024 samples; writes a JSON report with raw drift, ratio ppm, error max/p99.
- [x] Update `SessionTestKit`, `SessionCaptureTests`, `CaptureEndTests` for the hub; epic Notes with the measured results.

**Acceptance Criteria:**
- Given the builder's PC, when the verify runs for 10 minutes, then both streams stay within one AAC frame of elapsed QPC and of each other, the loopback bursts keep a constant offset to the player within one AAC frame, and the mic's raw drift is reported against its corrected error.
- Given a capture session or `debug-capture`, when it runs, then its file has both audio tracks from the hub, with the existing session tests and ffprobe checks passing.
- Given the solution, when `dotnet build ReplayBuffer.slnx`, `dotnet test --solution ReplayBuffer.slnx` and `pwsh tools/publish-engine.ps1` run, then 0 warnings and all tests pass (hotkey latency flake noted, not counted).

## Implementation Notes

- **Gains.** The loop is critically damped at ω = 1 rad/s: Kp = 2/48000 per sample and Ki = 1/48000 per sample-second, with dt taken from the stamps. That is faster than the Design Notes' "halves in 1–2 s". Proportional control alone halves an error in 0.35 s. At the slower gains, the matrix's ±200 ppm row is still ≈ 8 samples off after 5 s, against its bound of 2. The higher gain moves the step by ≈ 3·10⁻⁴ per packet at 7 samples of jitter, which shifts the position by 0.14 samples. On ±0.15 ms uniform jitter the synthetic rows stay within 2 samples after the settle.
- **Resampler.** 32 taps (16 each side) with a Kaiser β 8.6 window, 256 phases with linear interpolation between them, and cutoff at Nyquist. Row 0 is a unit impulse, so a step of exactly 1 copies the input. The kernel is centred on the position, so in index terms there is no group delay: input I comes out where position = I. The only delay is in time, 16 frames of look-ahead. A block is silent only when the whole kernel window lies after the last sound pushed. That test is conservative: a few frames of zeros before a sound come out as samples.
- **Stream origin.** The origin is the first packet's stamp, or the first `FillTo` when an idle loopback starts with no packets. A bad stamp before anything has started has nothing to extrapolate from, so the packet is dropped and counted as `UnplacedPackets`.
- **Counters.** As in `AudioTimeline`, `GapFills` and `Trims` count corrections past 5 ms, exact placements included (a flagged gap, resume after fill). `StepCorrections` counts only the unflagged ones. An overlap or gap correction that drops a whole packet carries on into the next packet, which is placed the same way and not counted again, so one overlap counts once.
- **Hub.** Devices are keyed by flow + endpoint ID. Dispatch runs under the device's lock, so `Dispose` waits for a block in flight. Disposing a subscription from inside its own callback throws, since that would deadlock. A subscriber that throws is dropped from dispatch but still counts toward keeping the device open until its owner disposes it. `AudioDeviceHub.SubscribeDevice` returns the concrete subscription with `Diagnostics` (stamp stats and the stream). The session casts the port's subscription to it for the stats report.
- **Session faults.** An `encode-failed` subscriber fault fails the session. Any other subscriber fault, and any device fault, is logged as an `audio-device-failed` warning, as `WasapiCapture.Fault` was before.
- **Stats.** `CaptureStatsCollector` no longer holds stamp stats. Every device reports the hub's stamps and stream counters under `timeline` (`writtenFrames` stays the session timeline's), plus a new `resampler` block. The schema stays 1.
- **Verify error.** The verify's per-second "error between output position and elapsed QPC" is the stream's error at its latest packet: where that packet's content comes out minus where its stamp puts it. The raw lag of output behind now also includes packet latency and the 50 ms idle-fill lag, so it is not used. The burst offsets against the player's QPC are the end-to-end check.
- **Wall-clock lag check.** Added after review. The verify also reads each stream once a second against QPC alone, independent of the device stamps: lag = (now − stream origin) × 48 kHz − frames emitted. After the settle, it asserts that each lag's spread, and the spread of the out − mic lag, stays under 1024 samples. Results of the 60 s run:
  - Loopback lag from −903 samples, spread 420. It is negative because loopback stamps are render times, so its content is stamped ahead of when it is read.
  - Mic lag from +18 samples, spread 480.
  - Out − mic lag spread 633.
  - Each spread is about one 10 ms packet (480 samples) plus latency jitter, because the once-a-second sample lands at a random point in the packet cycle. There is no drift, but the margin to 1024 is only about 1.6×.
  - So the lag is now read at delivery instead: a `DeliveryAgeTap` around each subscriber records each block's age (QPC now minus the QPC of its last frame), and the verify takes the smallest age in each second. Results of the 60 s run: loopback from −937 samples, spread 2.3; mic from +13 samples, spread 9.9; out − mic spread 11.2.
- **Load flake.** In one full-suite run, `EncodeExperimentTests.ThePacedEncoderEncodesEverySlotWithAFastPreset` saw latency p50 of 18.4 ms. The FFmpeg tests then passed 29/29 twice on their own. The test is unchanged by this story and only measures the GPU, so the failure reads as contention with the parallel Engine GPU tests, like story 11's p99 note; it is noted for story 15.
- **Results.** Recorded in the epic's Notes (story 12 results). On the 10-minute run every bound holds by at least 20×.
- **Verification.** `dotnet build`: 0 warnings. `dotnet test --solution`: 537/538, the one failure the known hotkey latency flake (`AnInjectedPressArrivesPromptlyAndIsLogged`). `pwsh tools/publish-engine.ps1`: exit 0, 9.6 MB, no IL2xxx/IL3xxx. `CaptureEndTests` with `REPLAYBUFFER_ENGINE_DIR` set to the absolute `publish/engine` path: 5/5. A relative value fails all 5, because the tests resolve it from their bin folder.

## Plan Change Log

## Review Triage Log

**Step 3 matrix audit:** every matrix row has a test that ran and passed: `DeviceStreamTests` (drift ±54/±200 ppm, idle, flagged and unflagged gap, overlap, bad stamps), `AudioDeviceHubTests` (two subscribers, last unsubscribe and reopen, throwing subscriber, bad endpoint). 537/538 ran; the one failure is the hotkey latency flake.

**Pass 1 (quick, 2026-10-27):** high 0, medium 3, low 4, false 1, maybe-false 0. (Before review, CRLF endings the implementer wrote into LF files were restored.)

| # | Finding | Verdict | Route | Evidence / action |
|---|---------|---------|-------|-------------------|
| 1 | `AudioDeviceSubscription.Dispose` sets its flag before `Release` throws on the capture thread, so the subscription can never be released | low | patch | Real: `Interlocked.Exchange` runs before the thread check. Fix: check the thread before setting the flag. |
| 2 | Blocks after the anchor are dropped while `StartOutput` builds the encoders, then silence-filled | medium | defer | Real, but it isn't new: the old clients started at the same point. The audio is there in the hub, though, so buffering it is a later change (deferred-work.md). |
| 3 | `SteeringLocksADriftingDeviceToQpc` bounds the output count at ±3, but the matrix says 2 samples | low | patch | Test disagrees with the matrix. Fix: assert ±2, and fix the code if that fails. |
| 4 | The verify's errors are measured against each device's own stamps, so the mic has no independent check against elapsed QPC | medium | patch | Real: `LastErrorSamples` is content position minus the device's own stamp. Fix: also sample, once a second, each stream's emitted frames against `(now − origin)·48 kHz`; assert its spread after settle, and the spread of the out − mic difference, < 1024 samples, and report both. |
| 5 | The verify never asserts the mic subscription's `SubscriberFault` | low | patch | Real. Fix: assert it is null. |
| 6 | `DeviceFault` is the shared device's, so a later subscriber sees an earlier fault | low | reject | Needs two overlapping subscribers on a failed device, which epic 1 never has; the fix adds per-subscription fault tracking. Epic 2 should revisit it when meters subscribe. |
| 7 | An unflagged overlap longer than one packet is counted as several step corrections and trims | low | patch | Real: on a whole-packet trim (`DeviceStream.cs:188-191`), `_exactNext` keeps `false`, so the next packet counts again. Fix: one overlap counts once. |
| 8 | Nothing checks the new `resampler` block or that every device is reported | medium | patch | Real: `CaptureEndTests` is unchanged. Fix: assert that each audio entry has a `resampler` block with a step-correction count and ratio. |
| 9 | The epic file's line 89 is removed and re-added | false | reject | It was the file's last line with no final newline; appending the story's results had to add one. |

## Design Notes

**Steering.** For each packet with stamp S at input index I, the error is e = (output index at which input I comes out) − (S − origin)·48000/QPF, in samples. The step (input frames per output frame) is 1 + Kp·e + Ki·Σe, clamped to 1 ± 10⁻³. Choose Kp so a constant error halves in ~1–2 s and Ki for a critically damped loop; at +54 ppm the integral settles at step ≈ 1 − 54·10⁻⁶. Stamp jitter of ±7 samples moves the step by < 10⁻⁴ per packet, which shifts position by a fraction of a sample. The resampler's group delay is part of "output index at which input I comes out", so stamps stay true.

**Why the session keeps `AudioTimeline`.** The hub's stream starts when the device opens, before the anchor; the timeline cuts it at the anchor and, because hub blocks are contiguous and QPC-locked, writes everything after as-is.

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx` -- expected: 0 warnings, 0 errors
- `dotnet test --solution ReplayBuffer.slnx` -- expected: all pass (hotkey latency flake noted)
- `REPLAYBUFFER_LONG_TESTS=1 dotnet test --project tests/ReplayBuffer.Platform.Audio.Tests --filter-class *DeviceSyncVerifyTests` (exact filter syntax per MTP) -- expected: pass over 10 minutes
- `pwsh tools/publish-engine.ps1` -- expected: 0 IL2xxx/IL3xxx warnings
