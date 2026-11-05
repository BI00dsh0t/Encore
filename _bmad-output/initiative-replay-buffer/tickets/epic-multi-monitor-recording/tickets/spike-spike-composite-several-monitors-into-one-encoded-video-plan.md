---
title: 'Spike 11.1: composite several monitors into one encoded video'
type: 'chore'
ticket: '1'
created: '2026-11-04'
status: done
baseline_revision: '5f8652c'
route: 'spike'
risk: 'high'
---

# Spike 11.1: composite several monitors into one encoded video

## Question

Can the Monitor session capture several monitors, blit them into one canvas laid out as Windows arranges them, and encode that on one chain (AD-25)? What does it cost in a GPU-bound game against NFR-1 and against today's one-monitor Monitor Recording? What are the real per-codec frame limits? Do mixed refresh rates and Desktop Duplication composite cleanly? This spike gives the go or no-go for story 11.3's design.

## Answer: GO for 11.3's design, with three changes

- **It works.** Two monitors captured with WGC monitor items on one D3D11 device, blitted into one canvas, went through the existing pacer, scaler and one encoder chain. The result was one fragmented MP4 per run with both Tracks, laid out as Windows has the monitors, with a black area where the canvas has no monitor. HEVC, AV1 and H.264 files all decoded with 0 errors, and VLC played a composite file to the end with the screens in place. Desktop Duplication sources composite the same way.
- **AD-7's budget holds.** `nvidia-smi` saw 2 NVENC sessions during each composite run (the game chain and the Monitor chain), and 1 during the game-capture-only runs. The exception: during run 04 (HEVC, pass 1), at 22:27:32 to 22:27:56, something else on the PC opened 1 to 4 more sessions (see Other activity).
- **The blits are negligible:** 10.9 µs of GPU time per 2560x1440 copy and 22.0 µs per 3840x2160 copy. At about 135 source frames a second that is about 2 ms of GPU time a second.
- **NFR-1 is not met by any Monitor Recording beside a GPU-bound game, and that includes today's one-monitor Recording.** The Replay Buffer alone costs 2.85 % (2.27 and 3.43 % per pass, so its pass is within noise of the 3 % line). Adding today's one-monitor Monitor Recording takes it to 5.15 %, and the full-width two-monitor composite to 7.6 to 10 % (pass 2: HEVC 7.67 %, AV1 7.63 %). This is a question for the builder (below), not a fault of the composite design.
- **Changes for 11.3:**
  1. **Composite once per output slot, not once per source frame.** The spike pushes the whole canvas each time any source delivers a frame. That gave 160 to 946 hand-off queue drops and 1,100 to 2,150 non-increasing stamps per 50 s run, against 6 to 8 drops for one monitor, and a full-canvas scale pass for every source frame. Story 11.3's `MonitorCompositor` should keep each source's latest frame (or blit it on arrival) and hand one canvas to the pacer per slot from its own slot clock, as design-multi-monitor.md already says ("once per output frame").
  2. **The codec limit can't come from the encoder probe.** Story 1.13's probe opens each encoder at 1280x720 and reports no maximum size. Measured NVENC limits on this RTX 5090: **H.264 4096 px a side** (4096x4096 encodes; 4098 wide or tall fails); **HEVC and AV1 8192 px a side** (8192x8192 encodes; 8194 fails). Above the limit FFmpeg fails the open with "No capable devices found". Story 11.2's canvas rule takes the limits as constants per codec (NVENC's), and 11.3 fits the canvas before opening the encoder. AMF and QSV limits are unmeasured; use the same constants until a PC with them exists.
  3. **The pixel budget changes, per the builder's full-width decision.** AD-25 and the design fit the canvas to max(Output area, 3840x2160). On this PC the canvas is **6400x2160 (13.8 MP)**, 1.67 × that budget, so the rule would encode 4956x1672 and each screen would lose its native resolution. The builder chose full width with HEVC or AV1. **Decision line on the pixel budget:** HEVC and AV1 encode the full canvas, limited only by 8192 px a side (a wider canvas is fitted to 8192 with its aspect kept); H.264 fits the canvas to 4096 px a side; the 3840x2160 area budget is dropped. AD-25's Canvas rule needs this edit before 11.3. The coordinator schedules that AD-25 spine edit (an architecture update session, AD-22) before 11.3 starts; this spike doesn't touch the architecture file. Epic R2 and 11.2's ticket now state the new rule.
- **Physical sizes, not GDI's.** DISPLAY2 is **3840x2160 at 144 Hz**, not 2560x1440. A DPI-unaware reader (WinForms `Screen` in PowerShell, and the engine itself, which declares no DPI awareness) sees a virtualized 2560x1440. The canvas must use each display's current mode (`EnumDisplaySettings`: `dmPosition`, `dmPelsWidth`, `dmPelsHeight`), as `MonitorCanvas.Place` does, never `rcMonitor`. Story 11.2's `DisplayMonitor` `X`, `Y` and size must come from the same source.
- **Mixed refresh composites without stutter at 60 fps.** DISPLAY1 (240 Hz, the 80 fps load) and DISPLAY2 (144 Hz, a 64 Hz animated window) each delivered their own cadence, and the composite still had a fresh frame in 98.5 to 99 % of its slots (details below).
- **Cross-adapter: not testable here.** Both monitors are on the RTX 5090. The Meta Virtual Monitor adapter has no active output: it is absent from `EnumDisplayMonitors`, so `MonitorFinder.All` and `MonitorCanvas.Place` never see it, and nothing needs to exclude it.

## Method

- **Spike code** (kept as the base for 11.3):
  - `Platform.Capture/MonitorCanvas.cs`: the placements from each display's current mode, the bounding box, and `Fit` (the codec side limit and even sides; the area budget was removed after the decision).
  - `Platform.Capture/CompositeCapture.cs`: an `ICaptureSource` that owns one capture per monitor (WGC, else Desktop Duplication, or Desktop Duplication for all) on the one device. It copies each frame into its place in one BGRA canvas and hands the canvas on, so the session sees a single source. It also keeps per-source arrival statistics.
  - `D3D11Device.CopyFrameTo` (a copy into a canvas position) and `TimeCopies` (timestamp queries).
  - `CaptureSessionOptions.CompositeMonitors` / `CompositeDuplication`: a dev-only flag, honoured by the Monitor session's `StartMonitorCapture` (so a GPU-reset recovery rebuilds it) and by a file-mode `debug-capture` session.
  - `ReplayBuffer.Engine debug-capture --monitors all|<device names> [--monitor-api wgc|dd]`. With no `--size`, the canvas is fitted to the codec's largest side only (the new rule). The measured runs passed `--size 6400x2160` for HEVC and AV1, and H.264 fitted to 4096x1382, so the rule change doesn't alter any result.
- **Measurement:** `bench fps --conditions` (new, dev only) passes the conditions to `tools/measure-path-a.ps1`. A new `-mon<mode>` suffix starts a second engine with `debug-capture --monitors` beside the game capture, for the same span, and the GPU sampler sums both engines. The second engine stands in for the Monitor session, which has its own device in the real engine too (AD-25).
- **Run:** 2026-11-04 22:23 to 22:39, at the PC, under `.bench-running`. The synthetic load was calibrated to 80.99 fps at 2560x1440 borderless on DISPLAY1. A 900x600 WinForms window cycling its colour every 10 ms ran on DISPLAY2, so that monitor's source had motion. Seven conditions, two balanced passes (A…G, then G…A), 45 s of PresentMon each after 10 s settling. The game capture was 1920x1080, 60 fps, H.264, CBR 20 Mbps. The composites were CBR 40 Mbps; one monitor was 20 Mbps. `nvidia-smi` sampled NVENC sessions every 2 s.
- **Other activity:** no other session held the bench lock. The quirky-fermat worktree's default Engine and App test runs (10.2 or 10.4) ended before the run started, at 22:21 and 22:23. No Dev App was running. The orphan engine PID 66604 stayed untouched.
  - **But `nvenc.csv` shows another NVENC user.** There were 4 sessions at 22:23:45 to 22:23:49, before any engine of mine, and single sessions on and off during baseline-1 (22:23:53 to 22:24:19) at about 0 % encoder use. During run 04 (HEVC, pass 1), 6 sessions at 22:27:32 to 22:27:36 and 3 at 22:27:38 to 22:27:56, against my 2. NVIDIA's encoder use rose from about 30 % to 37 % for those few seconds.
  - Neither of my engines logged anything then. The pattern (several sessions at once, then one) looks like another engine's encoder probe and a short capture, most likely another session's test run started after my 22:23 process check. It couldn't be attributed afterwards.
  - Run 04 is therefore contaminated. Its 9.99 % is the high end of the HEVC spread, and the 8.83 % HEVC mean includes it.
- **Captures** went to temp files under the session scratchpad, and every MP4 and frame grab was deleted after measuring. The kept results (summary, stats JSON, engine logs, PresentMon CSVs, `nvenc.csv`) are in this worktree's git-ignored `measurements/bench-11.1/`.

## Results

### FPS cost (the load: baseline 79.31 fps mean)

| Condition (beside the game capture) | Encoded | avg FPS | 1 % low | **FPS loss**, mean (pass 1 / pass 2) | Engine 3D | NVENC use: game + monitor engine |
|---|---|---|---|---|---|---|
| baseline (no capture) | | 79.31 | 72.1 | 0 | | |
| game capture only (the Replay Buffer's chain) | H.264 1920x1080 | 77.05 | 64.2 | **2.85 %** (2.27 / 3.43) | 1.4 % | 6.5 % |
| + one-monitor Monitor Recording (today) | H.264 2560x1440 | 75.23 | 63.4 | **5.15 %** (4.97 / 5.32) | 2.9 % | 6.5 + 11.2 = 17.7 % |
| + two monitors, HEVC full width | HEVC 6400x2160 | 72.31 | 55.5 | **8.83 %** (9.99* / 7.67) | 3.7 % | 6.5 + 23.9 = 30.3 % |
| + two monitors, AV1 full width | AV1 6400x2160 | 72.33 | 53.8 | **8.80 %** (9.97 / 7.63) | 3.8 % | 6.5 + 19.2 = 25.7 % |
| + two monitors, H.264 fitted | H.264 4096x1382 | 71.96 | 62.1 | **9.27 %** (9.39 / 9.14) | 5.5 % | 6.5 + 17.1 = 23.6 % |
| + two monitors, Desktop Duplication, HEVC | HEVC 6400x2160 | 73.72 | 62.6 | **7.06 %** (7.41 / 6.70) | 3.5 % | 6.4 + 23.9 = 30.3 % |

\* Run 04 overlapped another process's NVENC sessions (see Other activity). The uncontaminated HEVC figure is pass 2's 7.67 %.

- **The spread is wide.** Pass 1 ran hotter for both full-width codecs: HEVC 9.99 vs 7.67 %, and AV1 9.97 vs 7.63 % with no stray sessions, so a first-pass effect is real too. The game capture alone varied from 2.27 to 3.43 %. Read the two-monitor composite as **7.6 to 10 %** in total, and about 2.5 to 4.8 points more than today's one-monitor Recording. HEVC and AV1 cost the same. The fitted H.264 cost no less. Desktop Duplication cost less, but it also delivered fewer frames (below). None of this changes the go verdict.
- **The NVENC column is corrected.** The sampler gets three identical `engtype_videoencode` instances per process (eng_6 to eng_8), and `measure-path-a.ps1` sums them, so the summary's video-encode column is 3× too high (it said 19.0, 51.9, 89.0, 75.6, 69.2 and 88.8 %). The figures above take one instance per process. `nvidia-smi`'s own `utilization.encoder` agrees: 28 to 33 % during the HEVC runs.
- **The encoder has plenty of headroom** (about 30 % at 6400x2160, 60 fps, preset p5), and engine 3D stays ≤ 5.5 %. So neither the encode nor the blits explain the extra 2.5 to 4.8 points. It must come from somewhere this run didn't measure: DWM's capture of the second, 4K 144 Hz monitor, the full-canvas copy and scale traffic, or memory bandwidth. 11.3 can split it with `--stages` on the composite.

### Per-source pacing (output 60 fps, slot 16.7 ms)

| Run | DISPLAY1 (240 Hz, the load) | DISPLAY2 (144 Hz, the animator) | Composite: duplicates / missed slots | Queue drops, non-increasing stamps |
|---|---|---|---|---|
| HEVC (2 passes) | 72.9 to 73.2 fps; p50 12.75, p99 29.25 ms | 61.4 to 66.4 fps; p50 12.75 to 16.75, p99 33.5 ms; 52 to 55 late | 2.2 / 1.0 %, 1.1 / 1.4 % | 898 and 2,011; 386 and 1,878 |
| AV1 | 73.5 to 74.6 fps; p99 29.25 ms | 61.6 to 67.8 fps; p99 33.5 ms | 1.3 / 1.2 %, 0.9 / 1.5 % | 440 and 2,151; 160 and 1,958 |
| H.264 fitted | 70.5 to 70.6 fps; p99 29.25 ms | 60.3 to 60.5 fps; p99 33.5 ms | 5.6 / 1.1 %, 7.3 / 1.1 % | 875 to 946 and 1,568 |
| Desktop Duplication | 53.1 to 54.4 fps; p99 40.5 to 41.25 ms | 47.0 to 47.4 fps; p99 42 to 43.5 ms; 202 to 220 late | 6.9 / 12.5 %, 3.4 / 11.8 % | 492 and 1,147; 244 and 1,117 |
| one monitor (today) | 75.4 to 76.0 fps; p99 29.25 ms | | 0.6 / 7.2 %, 0.4 / 5.3 % | 6 to 8 and 0 |

- "Late" is an arrival more than two slots after the previous one. DISPLAY2's late arrivals (about 2 %) follow the WinForms timer's jitter.
- The composite's own pacer had a fresh frame in 98.5 to 99 % of slots under WGC: no stutter from mixing 240 Hz and 144 Hz.
- Desktop Duplication composites correctly but at a lower and burstier rate: 12 % missed slots, against 1 to 1.5 % for WGC. That is acceptable for a fallback, and the same as 6.10's finding.
- The queue drops and non-increasing stamps are the per-source-push artefact that change 1 removes.

### Codec limits (NVENC, RTX 5090, `ffmpeg -f lavfi testsrc2`)

| Codec | Encodes | Fails |
|---|---|---|
| H.264 (`h264_nvenc`) | 4096x2160, 4096x4096 | 4098x1382, 5120x1440, 2560x4098 |
| HEVC (`hevc_nvenc`) | 6400x2160, 8192x2160, 8192x8192 | 8194x2160, 10240x1440 |
| AV1 (`av1_nvenc`) | 6400x2160, 8192x2160, 8192x8192 | 8194x2160, 10240x1440 |

### Files

- Each composite file: one video stream at the canvas size (HEVC and AV1 6400x2160, H.264 4096x1382), 2,997 to 3,015 frames for 50 s, two AAC Tracks. `ffmpeg -f null` decoded each with 0 errors. About 240 MB per 50 s at 40 Mbps.
- A frame at 20 s showed DISPLAY1 (the load) top-left at 2560x1440, DISPLAY2 to its right at full 3840x2160 height, and the canvas area below DISPLAY1 black: Windows' arrangement.
- **VLC check (QA round 1, 22:57):**
  - The capture: a fresh 10 s two-monitor HEVC 6400x2160 file (597 frames), under `.bench-running`.
  - VLC 3 (`C:\Program Files (x86)\VideoLAN\VLC\vlc.exe`, which the first report missed by looking only under Program Files) played it read-only and headless (`-I dummy --vout vdummy --play-and-exit`). It exited 0 after 20.1 s, having played to the end.
  - VLC's scene filter wrote frames 1 and 301. Frame 301 matched ffmpeg's grab at 5 s: DISPLAY1 top-left (2560x1440), DISPLAY2 to its right at full height, and black below DISPLAY1.
  - Every file was deleted afterwards.

## For the builder

- **Decision awaiting review (NFR-1):** the epic's Done-when says a multi-monitor Monitor Recording beside a game stays within NFR-1. On the synthetic load, no Monitor Recording beside the Replay Buffer meets 3 %: today's one-monitor Recording costs 5.15 % in total and the two-monitor full-width composite 8.8 %. Unattended default: NFR-1 stays the game-capture target (2.85 %, pass). A Monitor Recording beside a game is reported as a known extra cost (one monitor about +2.3 points, two at full width about +5 to +7 points). 11.3 re-measures after change 1 and splits the cost by stage. The builder decides whether that's acceptable or whether 11.3 needs a cap while a game runs. The cause isn't established, so no particular cap is recommended yet.
- **Human Check (optional):** the VLC check ran headless, above. The builder can watch one with a real window. Make a file (about 10 s), open it in VLC, then delete it:

  `src\ReplayBuffer.Engine\bin\Release\net10.0-windows10.0.26100.0\ReplayBuffer.Engine.exe debug-capture --monitors all --codec hevc --size 6400x2160 --seconds 10 --output %TEMP%\mm.mp4`
- **Architecture:** AD-25's Canvas rule is replaced by the pixel-budget decision line above. The coordinator schedules that AD-25 spine edit (an architecture update session, AD-22) before 11.3 starts; this spike doesn't touch the architecture file. The design's "the probe reports the real limit" is wrong: the limits are per-codec constants.
- **Follow-up (not fixed here):** `measure-path-a.ps1`'s `Measure-GpuSamples` sums every instance of an engine type. Video-encode has three identical instances per process on this GPU, so every earlier `gpu_videoencode` figure (1.2, 1.14, 8.7 and the rest) is 3× too high. 3D has one instance, so it's unaffected. The fix is to take the maximum over a process's instances of one type, then sum over processes. **Fixed (bugfix `bench-videoencode-count`, 2026-11-04):** `Measure-GpuSamples` (now in `tools/measure-gpu.ps1`) takes that per-process maximum. This plan's NVENC column was already corrected by hand; its raw `summary` files keep the old 3× figures.

## Verification

- `dotnet build ReplayBuffer.slnx -c Release`: 0 warnings.
- `tests/ReplayBuffer.Platform.Capture.Tests` (default suite): 6 passed, 1 explicit skipped. The new `MonitorCanvasTests` canvas-rule test passed (after QA round 1: the codec side limit only).
- Heavy `ABlitOfEachMonitorIntoTheCanvasCostsMicroseconds` passed under `.bench-running`, giving the blit times above.
- `tests/ReplayBuffer.Engine.Tests` (default suite): 182 passed, 43 explicit skipped. It runs because the engine's session and command line changed.
- `tests/ReplayBuffer.Bench.Tests` (default suite): 150 passed, 10 explicit skipped. It runs because `bench fps` gained `--conditions`.
- The other projects were skipped, because no change touches them: Contracts, Core, Media, App, Platform.Audio, Platform.Desktop, Platform.FFmpeg and Platform.Windows.
- `pwsh tools/publish-engine.ps1`: clean, 11.9 MB.

## Log

- 2026-11-04 22:05: branch `feature/11.1-multi-monitor-spike` from develop `5f8652c`.
- 22:17: smoke capture (5 s, HEVC): found DISPLAY2 is 3840x2160 at 144 Hz, and the canvas 6400x2160.
- 22:23 to 22:39: `bench fps`, 7 conditions × 2 passes. 22:41: codec limits. Captures deleted.
- 22:50 to 23:05: QA round 1 (4 findings):
  - The VLC check ran; VLC was installed after all.
  - The NVENC figures were 3× over-counted, and the conclusion is reworded.
  - The stray NVENC sessions are documented, with the per-pass spread.
  - Stale text after the decision is fixed: epic R2, 11.2's ticket, the PC line and the open question in the Notes, the code comments, and the no-`--size` fit.
