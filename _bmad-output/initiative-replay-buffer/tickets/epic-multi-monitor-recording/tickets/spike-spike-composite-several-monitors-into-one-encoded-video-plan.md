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

- **It works.** Two monitors captured with WGC monitor items on one D3D11 device, blitted into one canvas, went through the existing pacer, scaler and one encoder chain. The result was one fragmented MP4 per run with both Tracks, laid out as Windows has the monitors, with a black area where the canvas has no monitor. HEVC, AV1 and H.264 files all decoded with 0 errors. Desktop Duplication sources composite the same way.
- **AD-7's budget holds.** `nvidia-smi` saw 2 NVENC sessions during each composite run (the game chain and the Monitor chain), and 1 during the game-capture-only runs.
- **The blits are negligible:** 10.9 µs of GPU time per 2560x1440 copy and 22.0 µs per 3840x2160 copy. At about 135 source frames a second that is about 2 ms of GPU time a second.
- **NFR-1 is not met by any Monitor Recording beside a GPU-bound game, and that includes today's one-monitor Recording.** The Replay Buffer alone costs 2.85 % (pass). Adding today's one-monitor Monitor Recording takes it to 5.15 %, and the full-width two-monitor composite to 8.8 %. This is a question for the builder (below), not a fault of the composite design.
- **Changes for 11.3:**
  1. **Composite once per output slot, not once per source frame.** The spike pushes the whole canvas each time any source delivers a frame. That gave 160 to 946 hand-off queue drops and 1,100 to 2,150 non-increasing stamps per 50 s run, against 6 to 8 drops for one monitor, and a full-canvas scale pass for every source frame. Story 11.3's `MonitorCompositor` should keep each source's latest frame (or blit it on arrival) and hand one canvas to the pacer per slot from its own slot clock, as design-multi-monitor.md already says ("once per output frame").
  2. **The codec limit can't come from the encoder probe.** Story 1.13's probe opens each encoder at 1280x720 and reports no maximum size. Measured NVENC limits on this RTX 5090: **H.264 4096 px a side** (4096x4096 encodes; 4098 wide or tall fails); **HEVC and AV1 8192 px a side** (8192x8192 encodes; 8194 fails). Above the limit FFmpeg fails the open with "No capable devices found". Story 11.2's canvas rule takes the limits as constants per codec (NVENC's), and 11.3 fits the canvas before opening the encoder. AMF and QSV limits are unmeasured; use the same constants until a PC with them exists.
  3. **The pixel budget changes, per the builder's full-width decision.** AD-25 and the design fit the canvas to max(Output area, 3840x2160). On this PC the canvas is **6400x2160 (13.8 MP)**, 1.67 × that budget, so the rule would encode 4956x1672 and each screen would lose its native resolution. The builder chose full width with HEVC or AV1. **Decision line on the pixel budget:** HEVC and AV1 encode the full canvas, limited only by 8192 px a side (a wider canvas is fitted to 8192 with its aspect kept); H.264 fits the canvas to 4096 px a side; the 3840x2160 area budget is dropped. AD-25's Canvas rule needs this edit before 11.3 (spine update, AD-22's rule for architecture changes).
- **Physical sizes, not GDI's.** DISPLAY2 is **3840x2160 at 144 Hz**, not 2560x1440. A DPI-unaware reader (WinForms `Screen` in PowerShell, and the engine itself, which declares no DPI awareness) sees a virtualized 2560x1440. The canvas must use each display's current mode (`EnumDisplaySettings`: `dmPosition`, `dmPelsWidth`, `dmPelsHeight`), as `MonitorCanvas.Place` does, never `rcMonitor`. Story 11.2's `DisplayMonitor` `X`, `Y` and size must come from the same source.
- **Mixed refresh composites without stutter at 60 fps.** DISPLAY1 (240 Hz, the 80 fps load) and DISPLAY2 (144 Hz, a 64 Hz animated window) each delivered their own cadence, and the composite still had a fresh frame in 98.5 to 99 % of its slots (details below).
- **Cross-adapter: not testable here.** Both monitors are on the RTX 5090. The Meta Virtual Monitor adapter has no active output: it is absent from `EnumDisplayMonitors`, so `MonitorFinder.All` and `MonitorCanvas.Place` never see it, and nothing needs to exclude it.

## Method

- **Spike code** (kept as the base for 11.3):
  - `Platform.Capture/MonitorCanvas.cs`: the placements from each display's current mode, the bounding box, and `Fit` (codec side limit, area budget, even sides).
  - `Platform.Capture/CompositeCapture.cs`: an `ICaptureSource` that owns one capture per monitor (WGC, else Desktop Duplication, or Desktop Duplication for all) on the one device. It copies each frame into its place in one BGRA canvas and hands the canvas on, so the session sees a single source. It also keeps per-source arrival statistics.
  - `D3D11Device.CopyFrameTo` (a copy into a canvas position) and `TimeCopies` (timestamp queries).
  - `CaptureSessionOptions.CompositeMonitors` / `CompositeDuplication`: a dev-only flag, honoured by the Monitor session's `StartMonitorCapture` (so a GPU-reset recovery rebuilds it) and by a file-mode `debug-capture` session.
  - `ReplayBuffer.Engine debug-capture --monitors all|<device names> [--monitor-api wgc|dd]`. With no `--size`, the canvas is fitted by the rule.
- **Measurement:** `bench fps --conditions` (new, dev only) passes the conditions to `tools/measure-path-a.ps1`. A new `-mon<mode>` suffix starts a second engine with `debug-capture --monitors` beside the game capture, for the same span, and the GPU sampler sums both engines. The second engine stands in for the Monitor session, which has its own device in the real engine too (AD-25).
- **Run:** 2026-11-04 22:23 to 22:39, at the PC, under `.bench-running`. The synthetic load was calibrated to 80.99 fps at 2560x1440 borderless on DISPLAY1. A 900x600 WinForms window cycling its colour every 10 ms ran on DISPLAY2, so that monitor's source had motion. Seven conditions, two balanced passes (A…G, then G…A), 45 s of PresentMon each after 10 s settling. The game capture was 1920x1080, 60 fps, H.264, CBR 20 Mbps. The composites were CBR 40 Mbps; one monitor was 20 Mbps. `nvidia-smi` sampled NVENC sessions every 2 s.
- **Other activity:** no other session's Heavy or bench run. The quirky-fermat worktree's default Engine and App test runs (10.2 or 10.4) ended before the run started, at 22:21 and 22:23. No Dev App was running. The orphan engine PID 66604 stayed untouched.
- **Captures** went to temp files under the session scratchpad, and every MP4 and frame grab was deleted after measuring. The kept results (summary, stats JSON, engine logs, PresentMon CSVs, `nvenc.csv`) are in this worktree's git-ignored `measurements/bench-11.1/`.

## Results

### FPS cost (the load: baseline 79.31 fps mean)

| Condition (beside the game capture) | Encoded | avg FPS | 1 % low | **FPS loss** | Engine 3D | Engine video-encode |
|---|---|---|---|---|---|---|
| baseline (no capture) | | 79.31 | 72.1 | 0 | | |
| game capture only (the Replay Buffer's chain) | H.264 1920x1080 | 77.05 | 64.2 | **2.85 %** | 1.4 % | 19.0 % |
| + one-monitor Monitor Recording (today) | H.264 2560x1440 | 75.23 | 63.4 | **5.15 %** | 2.9 % | 51.9 % |
| + two monitors, HEVC full width | HEVC 6400x2160 | 72.31 | 55.5 | **8.83 %** | 3.7 % | 89.0 % |
| + two monitors, AV1 full width | AV1 6400x2160 | 72.33 | 53.8 | **8.80 %** | 3.8 % | 75.6 % |
| + two monitors, H.264 fitted | H.264 4096x1382 | 71.96 | 62.1 | **9.27 %** | 5.5 % | 69.2 % |
| + two monitors, Desktop Duplication, HEVC | HEVC 6400x2160 | 73.72 | 62.6 | **7.06 %** | 3.5 % | 88.8 % |

- The per-pass spread is about 1 to 2.3 points (HEVC 9.99 / 7.67 %, AV1 9.97 / 7.63 %, game capture 2.27 / 3.43 %). The first pass ran hotter.
- The two-monitor composite costs about **3.7 points more than today's one-monitor Recording**, and the two codecs cost the same. The fitted H.264 costs no less, because its downscale pass makes up for the smaller encode. Desktop Duplication cost less, but it also delivered fewer frames (below).
- Engine 3D use stays small (≤ 5.5 %). The extra cost tracks NVENC load (video-encode 89 % for 6400x2160 at 60 fps, preset p5) and the second monitor's capture. A third full-width 4K monitor would need the 8192 fit. A lower NVENC preset for large canvases is a lever 11.3 can measure.

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
- **VLC isn't installed on this PC,** so the ticket's VLC playback check is queued for the builder (below). The ffmpeg decode is its automated stand-in.

## For the builder

- **Decision awaiting review (NFR-1):** the epic's Done-when says a multi-monitor Monitor Recording beside a game stays within NFR-1. On the synthetic load, no Monitor Recording beside the Replay Buffer meets 3 %: today's one-monitor Recording costs 5.15 % in total and the two-monitor full-width composite 8.8 %. Unattended default: NFR-1 stays the game-capture target (2.85 %, pass). A Monitor Recording beside a game is reported as a known extra cost (one monitor about +2.3 points, two at full width about +6 points). 11.3 re-measures after change 1. The builder decides whether that's acceptable or whether 11.3 needs a cap, such as the 4K budget or a lower preset while a game runs.
- **Human Check:** play a two-monitor composited file in VLC and confirm both screens in their Windows positions. To make one (about 10 s; delete it afterwards):

  `src\ReplayBuffer.Engine\bin\Release\net10.0-windows10.0.26100.0\ReplayBuffer.Engine.exe debug-capture --monitors all --codec hevc --size 6400x2160 --seconds 10 --output %TEMP%\mm.mp4`
- **Architecture:** AD-25's Canvas rule is replaced by the pixel-budget decision line above. The design's "the probe reports the real limit" is wrong: the limits are per-codec constants.

## Verification

- `dotnet build ReplayBuffer.slnx -c Release`: 0 warnings.
- `tests/ReplayBuffer.Platform.Capture.Tests` (default suite): 6 passed, 1 explicit skipped. The new `MonitorCanvasTests` canvas-rule test passed.
- Heavy `ABlitOfEachMonitorIntoTheCanvasCostsMicroseconds` passed under `.bench-running`, giving the blit times above.
- `tests/ReplayBuffer.Engine.Tests` (default suite): 182 passed, 43 explicit skipped. It runs because the engine's session and command line changed.
- `tests/ReplayBuffer.Bench.Tests` (default suite): 150 passed, 10 explicit skipped. It runs because `bench fps` gained `--conditions`.
- The other projects were skipped, because no change touches them: Contracts, Core, Media, App, Platform.Audio, Platform.Desktop, Platform.FFmpeg and Platform.Windows.
- `pwsh tools/publish-engine.ps1`: clean, 11.9 MB.

## Log

- 2026-11-04 22:05: branch `feature/11.1-multi-monitor-spike` from develop `5f8652c`.
- 22:17: smoke capture (5 s, HEVC): found DISPLAY2 is 3840x2160 at 144 Hz, and the canvas 6400x2160.
- 22:23 to 22:39: `bench fps`, 7 conditions × 2 passes. 22:41: codec limits. Captures deleted.
