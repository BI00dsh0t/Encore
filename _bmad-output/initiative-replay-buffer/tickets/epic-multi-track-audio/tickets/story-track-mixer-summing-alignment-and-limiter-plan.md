---
title: 'Track mixer: summing, alignment and limiter'
type: 'feature'
ticket: '2'
created: '2026-11-02'
status: 'in-progress'
baseline_revision: '6a5ff147352517ac195d34935bfdb5ba7959867b'
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

**Problem:** 2.1's `TrackMixer` sums a Track's devices by QPC-derived position but has no limiter, so a mix can exceed full scale (R3), and a Track with no devices is refused instead of recording silence (R2). Tracks also end wherever their own devices' last block landed.

**Approach:** Add a stereo-linked `PeakLimiter` in `ReplayBuffer.Media` (instant attack, smooth release, no look-ahead, no gain control) that is the last stage of every `TrackMixer`, so audio that never exceeds full scale passes through bit-identically. `TrackMixer` accepts zero inputs (a silent Track) and gains `PadTo(frame)`, which emits up to that frame with any input that hasn't reached it counted as silence. The capture session drives silent Tracks from a QPC clock while it runs, and at the end pads every Track to one common end frame.

## Boundaries & Constraints

**Always:** A Track whose audio stays within [-1, 1] is written bit-identically (single device: the device's samples; several: their float sum). Output never exceeds full scale (|x| ≤ 1.0f) for any input, including NaN or ±Inf (written as 0). The limiter applies one gain to both channels. No allocation per block once constructed. Device threads never block beyond one 4096-frame chunk's encode (2.1's bound). A device in several Tracks stays sample-aligned in each. Without `--track-layout` a capture has the same streams as before.

**Never:** No gain control, normalisation, compressor below full scale or look-ahead delay. No mute (2.4), dropout and rebind (2.5), meters (2.6), Contracts or settings changes, App UI. `SessionAudio`'s shape stays as 2.1 left it.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Single device in range | one input, samples in [-1, 1] incl. exactly ±1 | output bit-identical to input | — |
| Two full-scale sources | two inputs, both ±1.0 square or sine at full scale | every output sample within [-1, 1]; the peak frame reads ±1.0 | — |
| Over in one channel | left 1.5, right 0.5 | both channels scaled by the same gain (1/1.5) | — |
| Release | an over, then in-range audio | gain recovers toward 1 over ~50 ms and snaps to exactly 1; later audio bit-identical again | — |
| Non-finite | NaN or ±Inf sample | written as 0; neighbours unaffected by it | — |
| Shared device | device D in Track A alone and Track B with E | A = D bit-identical; B[k] = D[k] + E[k] for each k | — |
| Track with no devices | layout Track with `deviceKeys: []` | an AAC stream of silence, as long as the other Tracks | — |
| Only silent Tracks | the default layout (Everything, no devices) | capture succeeds: video plus one silent AAC stream "Everything" | — |
| End | Tracks whose devices stopped at different frames | every Track padded with silence to one common end frame (the longest device, or the last video frame if later) | — |
| PadTo then late input | `PadTo(f)` then an input writes frames below f | those frames are dropped and counted in `DroppedFrames` | — |

</intent-contract>

## Code Map

- `src/ReplayBuffer.Media/TrackMixer.cs` -- 2.1's mixer (read its class doc first). Changes:
  - Allow `inputs >= 0`.
  - Wrap `output` in a `PeakLimiter`; every emission goes through it, including the single-input path.
  - The single-input path stays ring-free but now runs under `_lock` and tracks `_written[0]` and `_emitted`, so `PadTo` and late-drop work for it too.
  - Add `public void PadTo(long frame)`: under the lock, emit up to `frame` (inputs behind count as silence; their frames below `_emitted` are dropped on arrival, as today). For zero inputs it writes silence straight to the limiter's `WriteSilence`.
  - Add `public long LongestInputFrames` (max of `_written`, 0 for no inputs).
  - `Flush()` becomes `PadTo(LongestInputFrames)`.
  - Keep the per-chunk lock (2.1 review) and "counted before the write" ordering. Update the class doc (drop "No gain, limiter…").
- `src/ReplayBuffer.Media/PeakLimiter.cs` (new) -- `public sealed class PeakLimiter(IAudioSampleSink output) : IAudioSampleSink`.
  - Stereo frames, `AudioTimeline.Channels`.
  - Fast path: while gain is exactly 1, scan the block. If every sample satisfies `Math.Abs(x) <= 1f` (NaN fails it), forward the span untouched.
  - Otherwise process per frame into a preallocated 4096-frame scratch buffer, written chunk by chunk:
    - `peak = max(|l|, |r|)` with non-finite samples set to 0 first.
    - `if peak * gain > 1: gain = 1 / peak`.
    - Output `clamp(x * gain, -1, 1)`.
    - Then release: `gain = 1 - (1 - gain) * ReleaseCoefficient`; snap to 1 when `gain >= 1 - 1e-6`.
  - `ReleaseCoefficient = exp(-1 / (0.05 * 48000))`.
  - `WriteSilence(frames)` forwards and advances the release by `frames` (`1 - (1 - g) * coef^frames`, snapped).
  - Public `const double ReleaseSeconds = 0.05`. Expose `public long LimitedFrames` (frames written with gain < 1), for tests and logs.
- `src/ReplayBuffer.Core/Sessions/SessionSpec.cs` -- `FromLayout`: a Track with no devices becomes `SessionTrack(name, [])`, not an error. Fix the doc comment and the exception doc.
- `src/ReplayBuffer.Engine/Sessions/CaptureSession.cs` --
  - `StartOutput` creates `TrackMixer(tracks[i].DeviceIndexes.Count, encoder)`, which handles 0.
  - When any Track has no devices, start a `System.Threading.Timer` every 100 ms that calls `PadTo(FramesAt(Stopwatch.GetTimestamp()))` on each zero-input mixer. Add a lock or flag so a callback never runs after it is stopped.
  - `FramesAt(qpc) = (qpc - _anchorQpc) * 48000 / _qpcFrequency`, using `Math.BigMul` or `Int128` to avoid overflow.
  - In `Finish` (devices already unsubscribed): stop that timer and wait for it (`Timer.Dispose(WaitHandle)`). Compute `end = max(max over mixers of LongestInputFrames, FramesAt(_lastFrameQpc))`, call `PadTo(end)` on every mixer, then flush the encoders.
  - `DisposeAll` also stops the timer before the encoders are freed (the Abandon and fail paths).
  - Log the frames each Track's limiter acted on beside "Audio written".
  - Update the class doc.
- `src/ReplayBuffer.Engine/TracerCapture.cs` -- `LayoutAudio` doc: no longer refuses a Track with no devices (the code already defers to `FromLayout`).
- Tests:
  - `tests/ReplayBuffer.Media.Tests/TrackMixerTests.cs`: the existing tests, plus new ones.
  - `tests/ReplayBuffer.Media.Tests/PeakLimiterTests.cs` (new).
  - `tests/ReplayBuffer.Core.Tests/SessionAudioTests.cs`: `ATrackWithNoDevicesIsRefused` becomes "is a silent Track".
  - `tests/ReplayBuffer.Engine.Tests/TrackLayoutCaptureTests.cs`: `TheDefaultLayoutHasATrackWithNoDevicesAndIsRefused` becomes a capture of the default layout.
  - `tests/ReplayBuffer.Engine.Tests/TrackMixCaptureTests.cs`: its fake hub and RMS check are the pattern for the in-process session tests.

## Tasks & Acceptance

**Execution:**
- [x] `src/ReplayBuffer.Media/PeakLimiter.cs` -- the limiter -- R3, AD-13 "limiter that acts only above full scale"
- [x] `tests/ReplayBuffer.Media.Tests/PeakLimiterTests.cs`:
  - in-range audio bit-identical (incl. ±1);
  - overs never exceed 1;
  - linked gain;
  - release recovers and snaps to 1;
  - non-finite samples written as 0;
  - silence advances the release;
  - no allocation per block once warmed (`GC.GetAllocatedBytesForCurrentThread`).
- [x] `src/ReplayBuffer.Media/TrackMixer.cs` -- zero inputs, `PadTo`, `LongestInputFrames`, limiter stage, single input under the lock
- [x] `tests/ReplayBuffer.Media.Tests/TrackMixerTests.cs`, the ticket's verify list:
  - single-device bit-identical through the limiter;
  - two full-scale sources never exceed full scale;
  - a device fanned out to two mixers is in both, sample-aligned;
  - a zero-input mixer `PadTo(n)` writes n silent frames;
  - `PadTo` then late input is dropped;
  - `Flush` still pads to the longest input.
- [x] `src/ReplayBuffer.Core/Sessions/SessionSpec.cs` + `tests/ReplayBuffer.Core.Tests/SessionAudioTests.cs` -- empty Track allowed; mixed empty and non-empty Tracks keep their indexes
- [x] `src/ReplayBuffer.Engine/Sessions/CaptureSession.cs` -- silence clock, common end padding, limiter log
- [x] `tests/ReplayBuffer.Engine.Tests/TrackMixCaptureTests.cs`:
  - Add a Track `Silent` with no devices to the in-process session. It decodes to silence (below −90 dB) and lasts as long as the video to within 0.1 s, like every other Track.
  - Add a case with two full-scale tones in one Track: the decoded peak stays ≤ 1.0 + AAC tolerance (0.05), and the raw mixer output is checked in the unit tests.
- [x] `tests/ReplayBuffer.Engine.Tests/TrackLayoutCaptureTests.cs` -- the default layout with no settings.json captures: exit 0, h264 plus one AAC stream titled `Everything`, silent, as long as the video to within 0.1 s
- [x] `src/ReplayBuffer.Engine/TracerCapture.cs` -- doc only

**Acceptance Criteria:**
- Given the instance has no settings.json (the default layout: Everything, no devices), when `debug-capture --track-layout --seconds 3` runs, then it exits 0 and ffprobe shows h264 plus one AAC stream titled Everything, silent, within 0.1 s of the video's duration.
- Given a Track layout with Tracks Out={out}, Both={out, in} and Silent={}, when a capture runs, then every AAC stream lasts as long as the video to within 0.1 s, and Silent is silent.
- Given two devices both at full scale in one Track, when it is captured, then no decoded sample of that Track exceeds full scale beyond AAC coding error.

## Implementation Notes

- The common end frame in `CaptureSession.Finish` also takes the silent (zero-input) mixers' `EmittedFrames`: the silence clock runs until `Finish` stops it, so it may already have taken a silent Track a little past the last video frame or the longest device, and written audio can't be taken back. Including it keeps "one common end frame" true; the plan's formula is otherwise unchanged.
- A silence-clock tick that throws (encoder or muxer failure) stops the clock and fails the session with `encode-failed`, instead of crashing the timer thread.
- The single-input path takes the lock once per block rather than per 4096-frame chunk: it has one writer, and `PadTo` on it runs only in `Finish`, after the devices are unsubscribed.
- The existing `TrackMixerTests` stream values were rescaled into [-1, 1] (they reached ~49 before), so the limiter leaves them bit-identical and the sums stay exact.

## Plan Change Log

## Review Triage Log

## Design Notes

Why instant attack without look-ahead: look-ahead delays the Track against the video and the other Tracks, which breaks alignment and single-device bit-identity. Instant attack still guarantees |out| ≤ 1. Its only cost is mild distortion on the very sample of an over, which is rare because inputs are captured as recorded.

Why the limiter runs on a single-device Track too: a loopback float stream can exceed 1.0. Since the limiter leaves in-range audio untouched, the single-device level (R3) is unchanged.

Why the silent-Track clock is a timer and not the capture thread: `OnFrame` runs under `_gate` on the latency-sensitive capture path (1.14). A silent Track only needs to track elapsed QPC, and AAC-encoding silence 10 times a second off-thread is negligible.

## Verification

**GPU lock:** before every build, test or capture, check that `%USERPROFILE%\Documents\repos\ReplayBuffer\.bench-running` doesn't exist; wait while it does.

**Environment:** a fresh worktree needs `native/ffmpeg` (copy it from the main checkout with its `.sha256`, or run `tools/fetch-ffmpeg.ps1`), or the capture tests skip.

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings, 0 errors
- `dotnet test --solution ReplayBuffer.slnx` -- expected: all pass. Known real-time flakes get one rerun.
- `pwsh tools/publish-engine.ps1` -- expected: 0 IL/AOT warnings
