---
title: 'Path A measurement: FPS cost, BGRA encoder input and QPC stamps'
type: 'feature'
ticket: '2'
created: '2026-10-25'
status: 'built'
baseline_revision: 'a15cb5716f8a18ebc2d034c29ad383e592f02ba0'
route: 'full'
route_source: 'auto'
risk: 'high'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
context:
  - '{project-root}/PLAN.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Stories 6–12 will build on Path A (CsWin32 + WGC + FFmpeg NVENC, BGRA straight into the encoder, X1), but nothing has measured its cost against PLAN.md §6.3, whether `u64QPCPosition` is trustworthy on the BEACN endpoints (§7.4), or what `SetGPUThreadPriority` does (§7.2).

**Approach:** Instrument the tracer's `debug-capture` so a run writes a stats file (frames, latency, CPU, GC, allocations, per-device audio-stamp health) and can set the GPU thread priority; add a script that runs the game baseline and each capture condition under PresentMon and samples the engine's GPU engine use. The builder runs it with a real game; the measured figures decide Path A vs Path C.

## Boundaries & Constraints

**Always:**
- Instrumentation adds no per-frame or per-packet managed allocation (fixed buckets and counters), and changes no capture, encode or mux behaviour when its options are off.
- `--gpu-priority` uses only `IDXGIDevice::SetGPUThreadPriority` on the engine's one device (AD-7); the engine stays unelevated.
- Capture→encoded latency is `QPC when the packet leaves avcodec_receive_packet − the frame's SystemRelativeTime QPC` (AD-6 stamps).
- Every new error code kebab-case and in `Contracts.ErrorCodes`.
- The builder launches the game and PresentMon; the agent never runs a game or downloads tools.

**Decisions (builder, 2026-10-25):**
- Load: Cyberpunk 2077, borderless at **2560×1440** (the builder's monitor) instead of §6.3's 1080p; RT Overdrive, Frame Generation off, standing still in one busy spot. The Decision states the resolution change against R5's 1080p wording.
- FPS tool: PresentMon CLI, downloaded by the builder. The builder joins "Performance Log Users" so PresentMon and the unelevated engine run from one normal terminal; the script refuses to run elevated.
- Runs: 5 min per condition, conditions interleaved twice, then one 30 min run of the chosen condition for drops and GC.
- Path C rule: switch only if FPS loss ≥ 3 % or drops ≥ 0.1 % with no tracer shortcut to blame; latency, CPU and allocation misses traced to shortcuts become story 6/11 work, recorded as such.
- The agent drafts the epic Decision text in Implementation Notes; Sofia (`bmad-agent-po`) writes it into the epic's Notes.

**Never:**
- No `ReplayBuffer.Bench` harness (story "FPS and drift benchmark" owns it), no pacer, scaler, resampler or orchestrator.
- No X2/X3 colour paths, no NVENC tuning beyond what the conditions name, no Path C code.
- No elevation, no `D3DKMTSetProcessSchedulingPriorityClass`.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Stats on | `debug-capture ... --stats run.json` | JSON written after the trailer: video, latency p50/p99/max, CPU, GC, alloc/s, one audio block per device | — |
| Warm-up | first 10 s after the anchor | excluded from alloc/s, GC and CPU rates; included in frame counts | — |
| Missed slot | consecutive frame stamps > 1.5/60 s apart | counted as `round(Δ×60) − 1` missed slots | — |
| Duplicate | PTS not above the last | counted as duplicate (existing drop counter) | — |
| Audio stamp checks | each packet | counts of `TIMESTAMP_ERROR`, zero, backwards, discontinuity; stamp jitter vs previous stamp + frames (p99, max); stamp age at GetBuffer; long-run ppm drift of stamps vs frame count; timeline gap/trim corrections | — |
| Priority | `--gpu-priority N`, −7 ≤ N ≤ 7 | set once on the device, result logged and in stats | outside range → `invalid-arguments`; HRESULT failure → logged, run continues, stats record it |

</frozen-after-approval>

## Code Map

- `src/ReplayBuffer.Engine/CommandLine.cs` -- `DebugCaptureCommand`: add `Stats` path and `GpuPriority`; usage text.
- `src/ReplayBuffer.Engine/TracerCapture.cs` -- `OnFrame` (frame stamps), `StartSession` (anchor), `Finish` (write stats); CPU via `Process.TotalProcessorTime`, `GC.GetTotalAllocatedBytes`, `GC.CollectionCount` snapshots at warm-up end and stop.
- `src/ReplayBuffer.Platform.FFmpeg/VideoEncoder.cs` -- `Drain`: packet PTS → capture QPC (anchor + PTS), latency into the stats; `DroppedFrames` already counts duplicates.
- `src/ReplayBuffer.Platform.Audio/WasapiCapture.cs` -- `Drain`: `GetBuffer` gives flags and `qpcPosition`; feed the stamp stats before `timeline.Push`.
- `src/ReplayBuffer.Media/AudioTimeline.cs` -- expose gap-fill / trim correction counters.
- `src/ReplayBuffer.Platform.Capture/D3D11Device.cs` -- add `SetGpuThreadPriority(int)` via the `IDXGIDevice` it already queries; `NativeMethods.txt` already lists `IDXGIDevice`.
- New, pure and unit-tested in `src/ReplayBuffer.Media`: `LatencyHistogram` (0.1 ms buckets to 250 ms, overflow bucket), `FrameCadence` (missed slots), `AudioStampStats`.
- New `tools/measure-path-a.ps1` -- per condition: PresentMon on the game process for N s; capture conditions also start the engine with `--stats` and sample `\GPU Engine(pid_<engine>*)\Utilization Percentage` by engine type with `Get-Counter`; writes a summary table (avg FPS, 1 % low, FPS loss vs baseline, plus the stats files).
- Don't touch: the muxer, AAC path, keyframe forcing, window-close watchdog.

## Tasks & Acceptance

**Execution:**
- [x] `src/ReplayBuffer.Media/LatencyHistogram.cs`, `FrameCadence.cs`, `AudioStampStats.cs` -- fixed-size, allocation-free recorders with percentile/summary readout.
- [x] `src/ReplayBuffer.Media/AudioTimeline.cs` -- gap and trim counters.
- [x] `src/ReplayBuffer.Platform.Capture/D3D11Device.cs` -- `SetGpuThreadPriority`, returns the HRESULT.
- [x] `src/ReplayBuffer.Platform.FFmpeg/VideoEncoder.cs`, `src/ReplayBuffer.Platform.Audio/WasapiCapture.cs` -- optional recorders, null when stats are off.
- [x] `src/ReplayBuffer.Engine/CommandLine.cs`, `TracerCapture.cs`, new `CaptureStatsReport.cs` -- options, wiring, JSON via a source-generated `JsonSerializerContext` (AOT-safe).
- [x] `tools/measure-path-a.ps1` -- conditions `baseline`, `capture-p0`, `capture-p7`, `capture-m7` (configurable), interleaved, repeatable; refuses to start if PresentMon or the game process is missing.
- [x] `tests/ReplayBuffer.Media.Tests`, `tests/ReplayBuffer.Engine.Tests` -- each matrix row; option parsing; a synthetic NVENC run with `--stats` yields a report whose latency count equals the encoded frame count.

**Acceptance Criteria:**
- Given FFmpeg fetched, when `dotnet build` and `dotnet test --solution` run, then 0 warnings and all tests pass.
- Given a 30 s capture with `--stats`, when it finishes, then the stats JSON is valid and its frame and audio counts match the file's (ffprobe).
- Given the builder runs the script with their game, when it finishes, then the summary has every §6.3 metric plus GPU engine use per type (or why not) for every condition.
- Given the results, then a dated Decision recording Path A kept or Path C adopted, the X1 result, the QPC-stamp finding and the GPU-priority choice, with figures, is in the epic's Notes.

## Implementation Notes

**Built (2026-10-25, agent):**
- `debug-capture --stats <file.json> [--gpu-priority -7..7]`. The stats file is written after the trailer from `CaptureStatsReport` (schema 1, source-generated `CaptureStatsJson`). It holds video counts (arrived, encoded, duplicates, missed slots and gaps against 60 fps), capture→encoded latency (p50/p99/max/mean, 0.1 ms buckets to 250 ms plus overflow), steady-state CPU (% of one core and of the machine), alloc/s and Gen0/1/2 from warm-up end (10 s after the anchor) to stop, whole-run CPU/alloc/GC, process start→first frame, the GPU-priority HRESULT and read-back, and per device: packets, TIMESTAMP_ERROR/zero/backwards/discontinuity counts, jitter (10 µs buckets), stamp age at GetBuffer (50 µs buckets), drift ppm over the longest unbroken segment, and the timeline's gap/trim/idle-fill/resolved-stamp counters.
- Recorders are allocation-free (tests assert 0 bytes on the recording thread) and are null when `--stats` is off, so capture, encode and mux behave as before. Warm-up and stop snapshots are taken on the main thread, not the frame thread.
- `--gpu-priority` calls `IDXGIDevice::SetGPUThreadPriority` once on the engine's one device and reads it back with `GetGPUThreadPriority`. On failure it logs `gpu-priority-failed` and the run continues. Out of range gives `invalid-arguments`. A stats file that can't be written fails the run with `stats-write-failed`, but the MP4 is already complete.
- `tools/measure-path-a.ps1`: `-Game Cyberpunk2077`, `-Conditions baseline,capture-p0,capture-p7,capture-m7`, `-Seconds 300`, `-Repeats 2` (balanced order: ABCD, then DCBA), `-FinalCondition`/`-FinalSeconds 1800` for the long run, and `-PresentMon` (default: the newest `native/presentmon/PresentMon-*.exe` under the repo root; in a worktree, pass it explicitly). It refuses to start when elevated, when PresentMon is missing, or when the game isn't running. Each capture run starts the engine 11 s before PresentMon, so PresentMon's window lines up with the engine's steady state. It samples `\GPU Engine(pid_<engine>_*)\Utilization Percentage` once a second and writes `summary.csv`, `summary.json` and `summary.md` into `measurements/path-a-<time>/` (gitignored).

**Verified by the agent:** build with 0 warnings; 131/131 tests pass with nothing skipped (NVENC on an RTX 5090). An end-to-end engine test runs 12 s with `--stats --gpu-priority 0`; the report's latency count equals its encoded frames, which equal ffprobe's video packets, and each audio stream's samples equal its timeline frames and match ffprobe's packets and duration_ts. The script was smoke-tested against Task Manager with a stub PresentMon (the agent did not run PresentMon or a game). GPU counters resolved by engine type (3d, copy, videoencode, ...).

**Seen before the game runs (for the Decision):**
- Capture→encoded latency is about 6 frame intervals. A 1 fps window measured about 6000 ms. A 53 fps WinForms test window measured p50 107 ms, p99 159 ms and max 192 ms, with 0 duplicates, 190 KB/s allocated and no GC in 2 s of steady state. NVENC holds several frames in flight (FFmpeg's default async depth for 8 surfaces). Drain-on-submit only collects a packet when a later frame is sent. In the game, expect p99 well above the < 1 frame target. This is a tracer shortcut together with an NVENC setting (`delay`/surfaces), not BGRA input. This story doesn't tune it (Never: no NVENC tuning). Story 6/11 (encode off the capture thread, receive on its own loop) owns it.
- BEACN `Voice Chat Mic` stamps on this machine: 0 errors/zero/backwards, jitter p99 about 0.1 ms, age about 10.1 ms, drift about -1.5 ppm over 25 s. The idle `System` loopback sent no packets and was idle-filled. A game-audio run is needed for loopback stamps.
- `SetGPUThreadPriority(7)` and `(-7)` both return S_OK unelevated, and read back the value that was set.

**Decision draft (fill the figures from summary.md; Sofia writes it into the epic Notes):**

> **Decision (YYYY-MM-DD): Path A kept | Path C adopted.** Load: Cyberpunk 2077, borderless 2560×1440 (the builder's monitor, not R5's 1080p), RT Overdrive, Frame Generation off, standing still; PresentMon 5 min per condition, interleaved twice, then 30 min of `<condition>`.
> - FPS loss vs baseline: p0 _% / p7 _% / m7 _% (avg FPS _ vs _; 1 % low _ vs _). Target < 3 %.
> - Dropped/duplicated: _ % duplicates; missed slots _ % against the game's _ fps present rate. Target < 0.1 %.
> - Capture→encoded p99: _ ms (max _). Target < 1 frame. Cause: tracer drain-on-submit plus NVENC frames in flight → story 6/11 | Path A itself.
> - Engine CPU: _ % of one core (_ % of machine). Allocations: _ B/s; GC over 30 min Gen0/1/2 = _/_/_ (the WGC projection allocates per frame, story 1.1 finding 6).
> - X1 (BGRA into NVENC): engine GPU use 3D _ %, Copy _ %, VideoEncode _ %, with no hidden CPU round-trip (engine CPU flat, 3D limited to the one CopySubresourceRegion).
> - QPC stamps (§7.4): output `<device>` err/zero/back/disc = _, jitter p99 _ ms, drift _ ppm; input `<device>` likewise. Verdict: u64QPCPosition reliable | not on BEACN.
> - GPU priority: chosen `<N>` because _ (FPS _ vs _, latency _ vs _). No elevation, no D3DKMTSetProcessSchedulingPriorityClass.
> - NativeAOT: binary _ MB, first frame _ ms after start. Interop: _ lines in Platform.*.
> - Path C rule: FPS loss ≥ 3 % or drops ≥ 0.1 % with no tracer shortcut to blame → _. Shortcut-caused misses go to story 6/11: _.

## Plan Change Log

- 2026-10-25: added the error codes `gpu-priority-failed` (warning) and `stats-write-failed`; the report also records process start→first frame (for the §6.3 NativeAOT startup row). The script gained `-PresentMon` with the native/presentmon default (coordinator request), a balanced run order, comma-list `-Conditions` (so `pwsh -File` works), and a gitignored `measurements/` output folder. Added an FFmpeg-level test: synthetic NVENC with a latency recorder, count = encoded frames.
- 2026-10-25 (review): packets drained at flush are left out of the latency figures and reported as `latency.flushed` (count + flushed = encoded frames). Timeline gap/trim counters only count steps past the 5 ms tolerance, not exact placement at start or idle resume. `--stats` is checked for writability before capture and written via `<path>.tmp` then a move. `video.nonIncreasingStamps` is reported and excluded from `captureFps` and the missed-slot denominator. A failed run in the script (game gone, engine exited early) now stops what's running and ends the session, but the summary still covers the finished runs. summary.md shows the engine binary size.

## Review Triage Log

Lens `quick`, iteration 0: 0 high, 4 medium, 2 low, 2 false, 0 maybe-false. Findings 1–2 were refuted when the patch was attempted; the rest were patched.

| # | Finding | Verdict | Route | Evidence |
|---|---------|---------|-------|----------|
| 1 | `D3D11Device.SetGpuThreadPriority` ignores the HRESULT of `SetGPUThreadPriority` | false | reject | The generated wrapper (`obj/.../Windows.Win32.NativeMethods.g.cs:11546`) is `public void SetGPUThreadPriority(int)` ending in `.ThrowOnFailure()`, so a failure throws and the existing `catch` records `e.HResult`; assigning the result fails with CS0029. |
| 2 | `GetGPUThreadPriority` read-back HRESULT ignored | false | reject | `GetGPUThreadPriority` is the same throwing `void` wrapper; `readBack` is set only when it didn't throw. |
| 3 | Flush packets put shutdown time into latency max/mean | medium | patch | `Finish()` runs `_video.Flush()` after capture dispose and both WASAPI joins; `Drain` records every flushed packet (up to 8 surfaces), so `maxMs` measures stop work. |
| 4 | Timeline gap/trim counters count exact (tolerance 0) placements | medium | patch | `AudioTimeline.Push` uses `tolerance = 0` for the first packet, after a fill and after a discontinuity, so start and idle-resume placement shows as a correction; the docs say "past the tolerance". |
| 5 | Script `exit 1` mid-session loses every finished run and can leave the engine running | medium | patch | `Invoke-Run` calls `Get-GameProcess`/`Fail` → `exit 1` before the summary section; the engine started with `-PassThru` is never killed. |
| 6 | `summary.md` lacks the engine binary size (§6.3 NativeAOT row) | low | patch | Only `summary.json` has `engine.sizeBytes`; the Decision draft is filled from `summary.md`. Direct one-line fix. |
| 7 | Stats path validated only after the run | medium | patch | `WriteStats` first touches `command.Stats` after `Finish()`; a bad path loses a 5–30 min run. `File.Create` + serialize can also leave a truncated file. |
| 8 | `FrameCadence.NonIncreasing` unreported and skews capture FPS / missed-slot % | low | patch | `VideoReport` uses `cadence.Frames`, which includes non-increasing stamps; the count never reaches the report. Direct fix. |

## Design Notes

- X1 "no hidden CPU round-trip" is shown by engine CPU staying flat with resolution and by `Copy`/`3D` engine use against `VideoEncode` in the GPU samples; the tracer's one `CopySubresourceRegion` per frame is the only 3D-queue work X1 adds.
- Drain-on-submit means a packet may leave NVENC only after the next frame is sent, which inflates latency. The stats report it as measured; the Decision says whether it's the tracer shortcut (story 6/11 moves encode off the capture thread) or Path A itself.
- The tracer allocates per frame inside the WGC projection (review finding 6 of story 1.1); alloc/s is reported and judged by its GC effect (Gen2, drops), per the Path C rule.
- Cyberpunk may render below 60 fps at 1440p with path tracing; missed slots are judged against the game's own present rate from PresentMon, not against 60.


## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx` -- expected: 0 warnings, 0 errors.
- `dotnet test --solution ReplayBuffer.slnx` -- expected: all pass, NVENC tests not skipped.

**Manual checks (if no CLI):**
- Builder: `pwsh tools/measure-path-a.ps1` with the game; summary and stats files reviewed together.
