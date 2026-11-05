# Capture's FPS cost: 1.47 % on 2026-11-02, about 7 % now

Investigation for the coordinator, 2026-11-04. No code was changed.

**Answer: the PC changed, not the code.** On today's PC, v0.1.0 (story 1.14's own code) costs **6.96 %**, and develop costs **6.59 %**. Both runs used 1.14's exact `bench fps` settings. The 0.4-point gap is inside run-to-run noise, so epics 2 to 6 added no measurable cost, and no bisect was needed. The cost is GPU-side and mostly in Windows. In a stage split, **WGC alone (frames taken and released, nothing copied or encoded) costs 4.3 % of the 6.4 %**; on 2026-11-02 no stage had a measurable cost. The load's own GPU time per frame grows by about 0.8 ms while capture runs, clocks don't drop, and Windows Graphics Capture delivers only about 52–54 frames a second, against nearly 60 on 2026-11-02. Several PC changes landed between the two measurements. The likeliest cause is the Windows cumulative update installed at the 20:00 reboot on 2026-11-03, the same reboot after which the MPO overlay planes were gone. Only the builder can confirm it (Human Check, §6).

## 1. The question

- Story 1.14 (`bench fps`, synthetic GPU load, PresentMon) measured capture's FPS cost at **1.47 %** on 2026-11-02. NFR-1's provisional bound is under 3 % at 1080p60 in a GPU-bound game.
- Story 5.12 (`feature/5.12-benchmark-overlay-on` at `f146e8a`) measured capture alone at **6.66 %** on 2026-11-04.
- The cause is either (a) a code regression in epics 2 to 6, or (b) a change to the PC.

5.12's figure came from 2 × 60 s runs, against 1.14's 3 × 5 min, so the first step was to repeat 1.14's run like for like on both builds.

## 2. Method

- **Builds.** v0.1.0 (`f3203f9`, 1.14's code) and develop (`8a966fc`), each in a scratch worktree with `native/ffmpeg` and `native/presentmon` copied in. Release build with 0 warnings, then `tools/publish-engine.ps1`, so the NativeAOT engine is used, as in 1.14. `tools/measure-path-a.ps1` is identical in both. The only changes to the GPU load since v0.1.0 are its fullscreen modes and a firmer foreground grab. The borderless path draws the same frames.
- **Run.** Each build's own `bench fps` with 1.14's defaults: synthetic load calibrated to 80 fps at 2560×1440 on the RTX 5090, capture 1920×1080 at 60 fps, CBR 20 Mb/s, **3 balanced passes of 300 s with 20 s settles** (B C | C B | B C). Each run takes about 33 minutes. Devices are `Game (BEACN Mix Create)` and `Voice Chat Mic (BEACN Mix Create)`, as in 5.12. Each build gets its own `--engine` and `--repo`.
- **Conditions.** The PC is as the builder left it: NVIDIA App and Overlay, Discord, Steam, NZXT CAM and the BEACN App are running, two monitors are attached, and no MPO planes are available. No Windows, driver or app setting was changed.
- **GPU lock.** Each run held `.bench-running` alone. Between runs it was released for at least 2 minutes, and the next run waited until no `dotnet`/`testhost`/`ilc` process ran from a worktree. A soak test took the lock between the two runs. An overlap log (CIM, every 30 s) caught no worktree build or test during either full run or the stage split. Two `dotnet publish` runs from other worktrees ended before the stage split started. One stage-split attempt at 03:22 measured nothing (the script pointed at the wrong GpuLoad binary) and was rerun at 03:26.
- **Read-only probes during the runs:** `nvidia-smi --query-gpu` every 2 s (P-state, graphics, memory and video clocks, power, temperature, encoder use, clock-event reasons), and the `GPU Engine` performance counters every ~8 s for dwm, the load and the engine. Both are light, and both conditions saw them equally.

## 3. Results

### 3.1 FPS cost per build (1.14's exact `bench fps`)

| Build | When | Baseline fps | Capture fps | FPS loss | Per capture pass | Duplicated slots | WGC frames/s (per pass) | Latency p99 |
|---|---|---|---|---|---|---|---|---|
| v0.1.0 smoke (30 s, 1 pass) | 01:50 | 78.94 | 73.71 | 6.63 % | 6.63 % | 17.4 % | 51.4 | 29.9 ms |
| **v0.1.0** | 01:52–02:25 | 84.65 | 78.76 | **6.96 %** | 8.19, 7.61, 5.08 % | 11.7 % | 53.4, 53.7, 54.5 | 27.5–28.4 ms |
| **develop** | 02:46–03:19 | 80.24 | 74.95 | **6.59 %** | 6.75, 6.53, 6.48 % | 14.8 % | 52.2, 52.5, 52.4 | 27.2–27.5 ms |
| *1.14 (2026-11-02, for reference)* | | 80.17 | 79.00 | *1.47 %* | | *3.5 %* | *≈ 60* | *29.3 ms* |
| *5.12 control (2 × 60 s)* | 01:16 | 80.82 | 75.44 | *6.66 %* | 6.62, 6.69 % | 14.4 % | | 29.3 ms |

- In the v0.1.0 run the load's baseline drifted from 83.2 to 86.9 fps across passes. The capture passes moved with it, so the per-pass losses spread (5.1–8.2 %). The develop run was steady (6.5–6.8 %).
- **Like for like, a gap remains:** about 7 % now against 1.47 %, at 1.14's own duration, phases and repeat count. The shorter 5.12 runs gave the same figure. Run length is not the explanation.
- **No code regression:** v0.1.0 and develop differ by 0.37 points, inside 1.14's run-to-run variance of about 3 %. Both builds also show the same duplicate rate, WGC rate, latency and GPU signature (§3.2). The coordinator's rule calls for a repeat run only when the two results are close to the noise threshold. These two agree, and both sit 5 points above 1.14, so the release-tag bisect (v0.2.0 to v0.4.0) wasn't run.

### 3.2 Where the cost goes (both builds alike)

| Figure | Baseline | Capture | Change |
|---|---|---|---|
| Load's GPU busy per frame (PresentMon `MsGPUBusy`), v0.1.0 | 11.37–11.69 ms | 12.14–12.41 ms | +0.75 ms |
| Load's GPU busy per frame, develop | 12.21 ms | 13.00–13.04 ms | +0.81 ms (+6.6 %) |
| Load's CPU busy per frame | 0.01 ms | 0.01 ms | none: the load is GPU-bound |
| PresentMode | Hardware Composed: Independent Flip | the same in every run | none |
| Graphics clock (nvidia-smi), develop run | 2816 MHz | 2835 MHz | none (P1 throughout; only the "reliability" voltage limit active) |
| Board power | 394 W | 379 W | −15 W |
| dwm.exe 3D engine use | 0.58 % | 1.19 % | +0.6 points |
| Engine 3D / video-encode use | – | 1.3 % / 18.9 % | |
| Load's 3D engine use | 94.4 % | 92.6 % | −1.8 points |

**Correction (bugfix `bench-videoencode-count`, 2026-11-04):** `measure-path-a.ps1` summed the three identical `engtype_VideoEncode` counter instances each process gets on this GPU, so every `gpu_videoencode` figure was **3× over-counted**. The engine's video-encode use above is about 6.3 %, not 18.9 %. FPS loss, 1 % lows, engine CPU and 3D use are unaffected (the sampler only reads counters, and 3D has one instance), so this research's conclusions stand.

**Reading:**
- The FPS loss is exactly the growth in the load's per-frame GPU time: 6.6 % more GPU time per frame, 6.6 % fewer frames. Nothing in our process holds the game's CPU, and the game's presentation path doesn't change.
- The GPU doesn't slow down (same clock, no power or thermal cap). It does less work per second while capture runs: power drops 15 W, and the load's share of the 3D engine drops only 1.8 points while its frames take 6.6 % longer. The extra time isn't other work running in its place. dwm and the engine together add only about 2 points of 3D work. It is idle time and switching overhead: the load's long full-screen draw (about 12 ms per frame) is interrupted for DWM's capture copy (once per WGC frame) and the engine's scale and convert (once per slot), each a small job from another context.
- **WGC itself is worse than on 2026-11-02.** It delivers 52–54 frames a second from a 75–79 fps window (1.14: nearly every 60 fps slot fresh, 3.5 % duplicates). The pacer then repeats 12–15 % of slots. That is DWM's behaviour. The same v0.1.0 binary got about 60 frames a second on 2026-11-02.
- So the cost sits in the Windows/driver capture path: DWM's per-frame capture work and how the GPU scheduler interleaves it with a game. The engine's own GPU work is the same in both builds and small.

### 3.3 Stage split (1.14's `fps-stages` diagnostic, rerun today)

Develop's engine. The same load (calibrated 79.8 fps, 5953 iterations), `measure-path-a.ps1 -Conditions baseline,capture-wgc,capture-copy,capture`, 2 passes of 120 s, 03:26–03:47:

| Condition | What runs | Avg fps | FPS loss (runs) | WGC frames/s | 1.14 (display on, 2026-11-02) |
|---|---|---|---|---|---|
| baseline | load only | 79.22 | 0 | – | 80.43 / 75.48 |
| capture-wgc | WGC frames taken and released: no copy, no encode | 75.80 | **4.32 %** (4.33, 4.30) | 52.1, 51.7 | 79.89: no measurable cost |
| capture-copy | WGC plus the copy into the encoder pool | 75.01 | 5.32 % (5.09, 5.54) | 51.4, 51.5 | 81.94: no measurable cost |
| capture | full: copy, scale and convert, NVENC, mux | 74.12 | 6.44 % (6.39, 6.49) | 51.6, 51.5 | 81.14: no measurable cost |

- **Two-thirds of today's cost (4.3 of 6.4 points) is WGC alone**, with the engine doing nothing but release each frame. That part is all Windows: DWM producing capture frames. Our copy adds about 1 point and the encode about 1 point.
- WGC delivers only about 52 frames a second even when the engine does no work. So the low frame rate and the high duplicate rate (§3.1) aren't our pacer falling behind. Windows supplies fewer frames.
- On 2026-11-02 the same three stages cost nothing measurable (within the about 3 % noise of 120 s runs). The stage that changed most is the one with none of our code in it.

## 4. What changed on the PC between the two measurements

Read from the event logs and file times. Nothing was changed.

| When | Change | Evidence |
|---|---|---|
| 2026-11-01 19:05 | KB5127936 starts installing and waits for a restart | `WindowsUpdateClient` event 43 |
| 2026-11-02, after 12:18 | **1.14's 1.47 % run**, on the build from before the update: the System log has no boot between 2026-10-31 and 2026-11-03 19:59 | 1.14 plan, Checkpoint Log; events 6005/6006 |
| 2026-11-02 | KB5132402 listed as installed (date only) | `Get-HotFix` |
| 2026-11-03 13:12 | Spike 5.2: the always-shown dot keeps independent flip at no cost (MPO apparently present) | research doc §1 |
| 2026-11-03 19:57 | Restart from the Start menu (the builder) | event 1074 |
| 2026-11-03 20:00 | **Windows Update restart** (`TrustedInstaller.exe`, event 1074), finishing the **2026-10 Preview cumulative update KB5127936, build 26300.9818** (installed 20:03, event 19) | System log, `WindowsUpdateClient` |
| 2026-11-03 20:01–20:02 | Boot; NVIDIA Overlay, Steam and Discord start | process start times |
| 2026-11-03 20:05 | BEACN App 1.4.2.19 and BEACN USB Device Driver 6.16.0 installed | `MsiInstaller` events |
| 2026-11-03 20:12 | Story 5.8: the borderless load falls to Composed: Flip with the dot shown | research doc §1 |
| 2026-11-03 21:50 | `dxdiag`: **MPO MaxPlanes 1, not supported**, on both outputs | research doc §3 |
| 2026-11-03 22:20 | **NVIDIA App, FrameView SDK and NvContainer rewritten** (an NVIDIA App update; binaries dated 2026-10-30, App 128.4.13.34, FrameView SDK 1.9.12728). NVIDIA App, Overlay and the FrameView service (`nvfvsdksvc_x64` → `PresentMon_x64`, ETW session `FrameViewService`) restart then | folder and process times |
| unchanged | NVIDIA display driver 32.0.16.2101 (621.01, dated 2026-10-18), hardware-accelerated GPU scheduling on | `Win32_VideoController`, research doc §3 |

## 5. Cause, as far as it can be told without changing the PC

1. **Ruled out: a code regression.** v0.1.0 and develop cost the same today (§3.1). Most of the cost appears with WGC alone, where none of the epic 2–6 pipeline runs (§3.3).
2. **Ruled out: run length or phases.** 1.14's own 3 × 5 min settings give about 7 % on both builds.
3. **Ruled out: GPU clocks, power limits or thermals.** The clocks are the same in both conditions (§3.2).
4. **Ruled out: the Overlay.** 5.12 showed it adds nothing, and these runs didn't start the App.
5. **Likeliest: the Windows cumulative update KB5127936 (26300.9818).** It is the only change on the composition path itself (DWM, DXGK and WGC ship in it), and the driver didn't change. Three symptoms appeared with that reboot: MPO planes gone, Composed: Flip under the dot (5.8), and now WGC delivering fewer frames at a higher GPU cost. One OS change explains all three.
6. **Possible contributors, in decreasing order:**
   - The NVIDIA App update at 22:20, with its FrameView service tracing presents (`PresentMon_x64`). It is a second consumer of the same DXGK events as the benchmark's PresentMon, and NVIDIA features in the App (RTX HDR, Video Super Resolution, Image Scaling) can hold the overlay planes.
   - The second monitor (3840×2160 at 144 Hz beside the 2560×1440 at 240 Hz primary). Mixed refresh rates are a known reason for NVIDIA to drop MPO. Whether it was attached on 2026-11-02 isn't recorded.
   - Discord and Steam overlays: they hook games, not the bench load, so they are unlikely.
   - BEACN App: audio only. Its Qt renderer uses about 1 % of the 3D engine in both conditions.

## 6. Human Check: confirm the PC factor (pairs with the overlay research's E1)

Under `.bench-running`, at the console, nothing else running GPU work. Each step is one `bench fps` with 1.14's defaults (about 33 min), from develop's Release build:

```
bench\ReplayBuffer.Bench\bin\Release\<tfm>\ReplayBuffer.Bench.exe fps --output-device "Game (BEACN Mix Create)" --input-device "Voice Chat Mic (BEACN Mix Create)"
```

Read `FPS loss`, `Duplicated slots` and each capture run's `video.captureFps` in its JSON. Healthy is about 1.5 % loss, under 5 % duplicates and about 60 WGC frames a second. Stop at the first step that brings it back:

1. As things are now: expect about 7 % (this doc).
2. Quit NVIDIA App and NVIDIA Overlay (and stop the FrameView SDK service if it is still tracing), Discord and Steam; rerun. Also record E1's `SupportsOverlays` (`dxdiag /t`, "MPO MaxPlanes").
3. Disconnect or disable the second monitor; rerun (E1 step 2's monitor case).
4. In the NVIDIA App, turn off RTX HDR, RTX Video Super Resolution and Image Scaling; rerun.
5. If it is still about 7 %: uninstall KB5127936 (Settings → Windows Update → Update history → Uninstall updates), reboot, and rerun. Reinstall afterwards if wanted. A return to about 1.5 % pins the OS update.

Each step also answers E1: note whether MPO came back at the same step as the FPS cost. If both return together, the overlay-plane loss and the capture cost are one cause.

## 7. Recommended fix

- **No code fix for the regression: there isn't one.** Don't revert or bisect epics 2 to 6.
- **Re-baseline NFR-1 on the PC as it is**, after the Human Check: either restore the PC (if step 2–4 brings it back, it's a setting the builder chooses), or accept that this Windows build costs about 7 % with WGC and decide whether NFR-1's 3 % still stands. That is the builder's call and belongs in Decisions Awaiting Review.
- **If the OS update is the cause and stays, a spike (size S, about half a day, Bench only first)** can test the two engine-side levers that cut DWM's per-frame capture work without changing the product's output:
  - **WGC `MinUpdateInterval` at one slot (16.7 ms) instead of half a slot (8.3 ms).** WGC alone is the biggest stage (§3.3). Fewer requested frames may mean less DWM work per second. Today it already delivers only about 52 a second, so the gain is uncertain; measure `capture-wgc` with each interval. Watch the latency p99 and the duplicate rate.
  - **`--gpu-priority`** (already in `debug-capture`, from spike 1.2): the engine's context priority against the game. Measure p0 against m7 again under today's Windows.
  - Each is a one-line engine change if it helps, plus a `bench fps` run to prove it.
- **Bench hygiene (size XS):** `bench fps` could record the Windows build (UBR), the driver, `SupportsOverlays`, the monitor count and the overlay apps running in `fps.json`. A change like this one would then show in the report instead of needing an investigation.

## 8. Run data

Kept in this session's scratchpad until it's cleared, not committed (`measurements/` is ignored): `runs/smoke-v010`, `runs/v010-1`, `runs/dev-1`, `runs/stages-dev`, `nvsmi.csv`, `gpu-samples.csv`, `overlap.log`. The scratch worktrees (`capcost-v010`, `capcost-dev`) were removed after the runs.
