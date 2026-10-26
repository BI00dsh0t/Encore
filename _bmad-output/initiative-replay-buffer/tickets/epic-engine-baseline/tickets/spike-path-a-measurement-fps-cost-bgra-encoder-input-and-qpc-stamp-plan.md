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

**Game runs (2026-10-25, builder):**
- Session 1 (`measurements/path-a-20261025-154104`, 8 × 5 min, GPU sampler on): baseline 74.98 fps / 1 % low 55.5; capture p0 60.7 / 22.5 (−19.0 %), p7 63.3 / 23.7 (−15.6 %), m7 61.2 / 23.4 (−18.4 %). Priority differences are inside the run-to-run spread (p0 57.9–63.5).
- Session 2 (`path-a-20261025-163320`, baseline vs capture-p0, GPU sampler off): 75.8 → 58.9 fps (−22.3 %), 1 % low 57.8 → 22.7. The sampler is ruled out.
- PresentMon shows the loss as single-frame hitches (≈ 720 frames > 30 ms per 5 min vs 1–2), at irregular intervals (not 1 s), with frame times quantised in ~15 ms steps. The game's GPU busy barely moves (10.8 → 11.6 ms) but its GPU latency p99 goes 9 → 38 ms and CPU busy 12.9 → 16.7 ms. Present mode is `Composed: Flip` with and without capture.
- Engine: 0 duplicates; missed slots 0.2–1.4 % (where the game drops below 60); latency p99 ≈ 115–125 ms; CPU ≈ 10 % of one core, 0.41 % of the machine; ≈ 207 KB/s allocated; GC 20/20/20 (all Gen2) per 5 min run; GPU 3D ≈ 1 %, VideoEncode ≈ 31 %, Copy 0.
- Audio stamps: 0 TIMESTAMP_ERROR/zero/backwards on both BEACN endpoints over 6 runs; jitter p99 0.11–0.15 ms; drift Game (BEACN Mix Create) −0.5 to −0.8 ppm, Microphone (BEACN Mic) +52.9 to +56.4 ppm (its own clock); one discontinuity per stream (the first packet).
- The game was not fully GPU-bound (CPU busy ≈ frame time, GPU busy ≈ 80 %).

- Session 3 (`path-a-20261025-175049`, 2 min each, forward order) and session 4 (`path-a-20261025-201203`, reverse order), GPU sampler off: no hitches in any run (≤ 1 frame > 30 ms). Loss vs each session's baseline (80.7 / 86.7 fps): WGC only 2.1 / 0.3 % (mean 1.2 %), WGC + copy 4.1 / 1.3 % (2.7 %), full 5.0 / 10.7 % (7.8 %). The NVIDIA performance overlay's FrameView PresentMon ran during sessions 1–2 and not 3–4; the hitches of sessions 1–2 are attributed to that combination, unconfirmed. Capture delivers ≈ 51 fps against the game's 77–87 (11 % empty 60 fps slots), already at the WGC stage: `MinUpdateInterval` 1/60 skips a game frame whenever the next one lands under 16.7 ms.
- Session 5, overnight (`path-a-20261025-225752`, GPU sampler off): 6 conditions × 3 balanced passes × 5 min, then 30 min of `capture-p0`. Mean FPS loss vs baseline 78.74 fps (1 % low 64.1): p0 (X1, p5, capture-thread encode) 0.85 %, x2 0.90 %, fast (p1 ll) 0.76 %, thread 0.85 %, x2 + fast + thread 1.79 %. Per-run spread −0.9 to +1.9 %, so the four single options are indistinguishable. 1 % lows 62.9–63.6. No hitches. Latency p99 ≈ 128 ms in every condition (none of the options moves it). queueDrops 15 in one `-thread` run, 0 elsewhere.
- The 30 min run: the game ran 77–78.5 fps with capture for its first 12 min, then jumped to ≈ 540 fps (it left gameplay, likely to a pause or menu), so its FPS is not used. Engine figures over the full 30 min: 0 duplicates; capture 55.6 fps; CPU 0.32 % of the machine (7.6 % of one core); 207 KB/s allocated; GC 120/120/120 (4 Gen2 a minute); both audio stamps unbroken for 1815 s (one drift segment), 0 bad stamps, drift −0.81 ppm (game) and +53.75 ppm (mic); mic timeline 19 gap fills (7677 frames, 160 ms in all) over 30 min.
- Sessions 3–4 (single 2 min runs, 5–11 % for full capture) are superseded by session 5's balanced passes: their spread came from run order and the game's own drift (baseline 80.7 vs 86.7 fps between them).

**Decision draft (Sofia writes it into the epic Notes):**

> **Decision (2026-10-26): Path A kept.** Measured by spike 2 with Cyberpunk 2077, borderless 2560×1440 (the builder's monitor, not R5's 1080p), RT Overdrive, Frame Generation off, PresentMon; final figures from 3 balanced passes of 5 min per condition plus a 30 min run.
> - **FPS loss vs no capture:** 0.85 % for the tracer's path (X1, NVENC p5), 1 % low 63.6 vs 64.1. Inside the provisional 3 % target. Every encode variant tried (X2, p1 + low-latency, a separate encoder thread) landed within 0.8–1.8 % and none beat it beyond the run-to-run spread. With the NVIDIA performance overlay's frame counter also running, capture caused 15–22 % loss as single-frame hitches (game GPU latency p99 9 → 38 ms); the overlay should be off when the benchmark runs, and the Overlay epic should re-check this.
> - **X1 (BGRA into NVENC):** kept. No hidden CPU round-trip (engine 0.3–0.4 % of the machine, 3D ≈ 1 %, VideoEncode ≈ 31 %, Copy 0). X2 (D3D11 VideoProcessor → NV12) costs the same and adds code; story 11 keeps X1 unless scaling needs the VideoProcessor anyway.
> - **QPC stamps (§7.4):** `u64QPCPosition` is reliable on Game (BEACN Mix Create) and Microphone (BEACN Mic): 0 timestamp-error, zero or backward stamps, stamps unbroken over a 30 min run, jitter p99 0.11–0.15 ms. Drift against QPC: game −0.8 ppm, mic +54 ppm (an independent clock).
> - **GPU priority:** `SetGPUThreadPriority` 0, +7 and −7 all apply unelevated, with no FPS difference beyond the spread. Chosen: leave it at 0. No elevation, no D3DKMTSetProcessSchedulingPriorityClass.
> - **Misses that are tracer shortcuts, for stories 6/11/12:** capture→encoded p99 ≈ 128 ms against < 1 frame, unchanged by preset, X2 or an encode thread, so it comes from NVENC's frames in flight (surfaces/delay, story 11); capture delivers ≈ 55 fps against a 78 fps game, 15 % of 60 fps slots empty, because `MinUpdateInterval` 1/60 skips frames (the pacer, story 6/12); ≈ 200 KB/s allocated and 4 Gen2 GCs a minute, all GCs Gen2, against Gen2 = 0 (story 11). 0 duplicate frames.
> - **NativeAOT and interop:** framework-dependent build (spike 3 does AOT); first frame 0.7–1.5 s after process start; 1715 lines of interop in Platform.* for the tracer path.

## Plan Change Log

- 2026-10-25: added the error codes `gpu-priority-failed` (warning) and `stats-write-failed`; the report also records process start→first frame (for the §6.3 NativeAOT startup row). The script gained `-PresentMon` with the native/presentmon default (coordinator request), a balanced run order, comma-list `-Conditions` (so `pwsh -File` works), and a gitignored `measurements/` output folder. Added an FFmpeg-level test: synthetic NVENC with a latency recorder, count = encoded frames.
- 2026-10-25 (review): packets drained at flush are left out of the latency figures and reported as `latency.flushed` (count + flushed = encoded frames). Timeline gap/trim counters only count steps past the 5 ms tolerance, not exact placement at start or idle resume. `--stats` is checked for writability before capture and written via `<path>.tmp` then a move. `video.nonIncreasingStamps` is reported and excluded from `captureFps` and the missed-slot denominator. A failed run in the script (game gone, engine exited early) now stops what's running and ends the session, but the summary still covers the finished runs. summary.md shows the engine binary size.

- 2026-10-25: FPS loss of 15–22 % needs blame before the Path C rule can be applied. Added `debug-capture --stages full|copy|wgc` and script conditions `capture-copy` / `capture-wgc` (WGC alone, WGC + copy, no encode) to split the cost by stage, plus `-NoGpuSampler` (sampler ruled out) and a keep-awake / lock check after an idle lock spoilt the first session. No NVENC tuning.
- 2026-10-25 (encode-stage experiments): the builder asked for these after the stage split, which put WGC at about 1 % and WGC + copy at about 3 %, against 5–11 % FPS loss for full capture. They are new debug-capture options; the defaults keep today's behaviour. `--nvenc-preset p1..p7` (default p5) and `--nvenc-tune hq|ll|ull` (default unset). `--color x1|x2`: x2 is a D3D11 VideoProcessor BGRA→NV12 conversion into an NV12 D3D11VA pool, tagged BT.709 limited range. The NV12 pool is one texture per frame, because D3D11 refuses an NV12 texture array as a render target (E_INVALIDARG), and output views are cached per texture. `--encode-thread`: OnFrame only draws, then hands the frame to one encoder thread through a fixed-size, allocation-free queue. A full queue drops the frame, counted as `video.queueDrops`. The thread is joined before Flush. The stats JSON gains an `encoder` block (stages, preset, tune, color, encodeThread). Script conditions take any mix of the suffixes `-p<N>`/`-m<N>`, `-copy`/`-wgc`, `-x2`, `-fast` (p1 + ll) and `-thread` after `capture`, and the summary gains the encoder columns.

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
