---
title: 'Final conformance run'
type: 'feature'
ticket: '7'
created: '2026-11-04'
status: done
route: 'full'
---
# Story 8.7 plan: Final conformance run

**Ticket:** 8.7 (epic-hardening, R7; risk medium; HITL). Halved by the builder (2026-11-04): a 30-minute drift Recording, and `bench fps --overlay` at 3 balanced passes of about 2.5 minutes. The bounds are unchanged: drift under one frame, and FPS loss within NFR-1's target.

**Hard limits (coordinator brief):** never install over, launch or touch the installed App. Drive a locally published build (`tools/publish.ps1` → `publish/ReplayBuffer`) on a throwaway `ReplayBuffer-Test-*` instance with temp folders. Change no Windows, driver, NVIDIA or default-device setting. Read the builder's Track Layout and never write it.

## Design

| Piece | What it does |
|---|---|
| `LiveApp` (new) | The real App (`--tray --instance ReplayBuffer-Test-… --replay-buffer`), which starts the engine beside it. Bench drives it by injected Hotkeys (Save `ctrl+alt+shift+f16`, Record Toggle `ctrl+alt+shift+f17`) and reads its snapshot over IPC. The App comes from `--app-exe`, else the local publish, else the Release build. Feedback Sounds are off, because they play on the default communications endpoint, which may be a Track's device, where they would read as bursts. |
| `bench drift --app` (new, `ConformanceDrift`) | Bench's own marker window is the Target Game: Bench's exe goes in with `game.add`, with Recordings on. The Replay Buffer buffers it and the Overlay shows. The Library and Cleanup run on the run folder's `clips` (Cleanup gets a 500 GB, 30-day limit, so it runs but never deletes the Recording). Save is pressed every `--save-seconds` (300). One real Recording of `--minutes` (30) runs from a Record press. Tones burst into Output Devices A and B, half a second apart, on the marker's flash grid. The analysis: A/V drift per Track and toned device (1.14's `DriftMath.AnalyzeAv`; a Track mixing A and B is split by burst grid); Track-to-Track drift for every pair of series (the largest change of their per-minute median difference); and every Track's length against the video's (which bounds an Input-only Track such as the Mic). All against one frame. File time 0 is the Record press plus the engine's logged key offset ("… s after the press"). It holds `.bench-running` itself, like `bench soak`. Writes `conformance-drift.json`, `conformance-drift.md` and `conformance-drift-markers.json` (`--reanalyze`). |
| Track Layout | `--layout <settings.json>` reads the Global `trackLayout` of a settings file, read only. A and B are its first two Output Devices, or the ones `--device-a`/`--device-b` name. By default it is 2.9's layout: Everything = Game + Chat + Mic, Game = `Game (BEACN Mix Create)`, Chat = `Chat (BEACN Mix Create)`, Mic = `BEACN Mic`. The mixer's outputs and the BEACN Mic are separate USB devices, so their clocks are independent. |
| `bench fps --overlay --recording` (new) | `measure-path-a.ps1` gets an `app` condition: PresentMon only, no engine from the script. As each `app` run settles, Bench clears the load profile's Ignored flag, waits for the App's Replay Buffer to buffer the load, and presses Record. Each baseline settle presses Record again and sets the profile Ignored. So the capture runs are the App's real capture, with the Replay Buffer, a Recording, the Overlay dot and a message every 10 s. `--seconds` defaults to 150 with `--recording`. Latency, slot and GC figures come from debug-capture's stats, so they are empty in this mode. |
| `bench playback --codecs h264,hevc,av1 --recording-file <mp4>` (new) | One marked clip per codec. A codec the engine can't encode is recorded as not encoded. `--recording-file` adds the `drift --app` Recording, with every Track, to the scripted players (Chrome, Windows Media Player); the other players need a person. |

## Decisions (unattended defaults, awaiting review)

- Decision: neither real instance holds a Track Layout (the installed `settings.json` has only `startWithWindows`, and the Dev one has profiles and `appTheme`), so the default layout is 2.9's four Tracks over the BEACN Game, Chat and Mic devices. `--layout` takes the builder's own file once one exists.
- Decision: `drift --app` takes `.bench-running` itself. Don't create it by hand around it. `fps` and `playback` don't take it, so wrap them by hand.
- Decision: `fps --recording` measures the App's own capture of the load (an `app` condition), not the script's debug-capture plus a second capture. A second capture of the load would double the cost. Running a Recording needs the App's game session, and in this design that session is the capture.
- Decision: audio-to-audio from the engine's stats (1.14) isn't available, since the App's engine is `serve`, not `debug-capture --stats`. Track-to-Track drift between the toned devices' bursts, and the Track lengths, replace it.

## Verification

- 0-warning Release build; `ReplayBuffer.Bench.Tests` (the only test project the diff touches; the script change is covered by its `MeasureScriptSessionTests`); `tools/publish.ps1` clean.
- Smoke runs (phase 1): `drift --app --minutes 2`, `fps --overlay --recording --seconds 60` and `playback --auto`.
- The full run (phase 2, on the coordinator's go): into `measurements/bench-8.7`.

## Log

- 2026-11-04: phase 1 built. `LiveApp`, `ConformanceDrift`, `fps --recording` with the script's `app` condition, and `playback --codecs`/`--recording-file`. Build 0 warnings; Bench.Tests 150 passed, 10 explicit skipped; publish clean.
- 2026-11-04: smoke runs (phase 1), into the worktree's gitignored `measurements/smoke-8.7*`:
  - `drift --app --minutes 2 --save-seconds 45`: **pass**. A/V drift 0.51 ms and Track-to-Track 0.005 ms against 16.7 ms. 120/120 flashes and bursts matched on every series (Everything split into A and B, Game, Chat). All four Track lengths are within one AAC frame plus one frame of the video (−28 ms against 38 ms). 2 Saves made 2 Clips. The test instance was cleaned up.
  - `fps --overlay --recording --seconds 60 --passes 1`: the plumbing works (app condition, profile toggling, the logs copied to `fps/app-logs`). Fixed on the way: a settings conflict as the App starts (now retried), and a Record press that came before the new session could record (now repeated every 2 s). **The FPS figures are not valid:** the builder's own Visual Studio debug session of the Dev App (pid 59732, Debug build, started 16:24) detects the GPU load and captures it in every run, its own plus the control below. The baseline is 69 fps against a calibrated 79, Composed: Flip. The capture runs collapse to about 12 fps, and the 5.12 control (`fps --overlay`, unchanged code) collapses to 8.5 fps (88 % loss). The full FPS run needs that Dev App closed. Bench must not stop it.
  - `playback --auto --seconds 10 --recording-file <the drift Recording>`: H.264, HEVC and AV1 all encode (NVENC). Chrome plays all four files; Windows Media Player plays H.264, HEVC and the Recording, and refuses AV1 (likely no AV1 Video Extension: a "players that support them" Human Check). The Recording lists all four Tracks, named.
- 2026-11-04: **full run (phase 2), into `measurements/bench-8.7`**, at `7f46995`, with the builder's Visual Studio debug Dev App (pid 59732) open throughout. It never detected the marker window, so drift and playback are unaffected.
  - `drift --app` (30 min, 17:54–18:24): **pass.** A/V drift 0.831 ms (Game) and 0.818 ms (Chat), against 16.667 ms. The constant offset is about −19.9 ms and the slope about −0.003 ms/min. Track-to-Track drift is at most 0.882 ms (Game against Chat), and 0.01–0.02 ms between Everything and its own single-device Tracks. Markers matched: 1801/1801 flashes and 1800–1801/1800–1801 bursts on every series. All four Tracks (Everything, Game, Chat, Mic) are named, and each is −20.67 ms against the video, within the 38 ms tolerance. 5 of 5 Saves made Clips, and the Recording was stopped by the Record press.
  - `playback --auto` (30 s clips, plus the drift Recording): H.264, HEVC and AV1 all encode on NVENC. Chrome (headless) plays all four files. Windows Media Player plays H.264, HEVC and the Recording, and refuses AV1. The Recording has its 4 named Tracks.
  - **Done 2026-11-05; see the fps run entry below.** Was pending: the builder closes their VS Dev App, then about 18 min with the command below. `fps --overlay --recording`. The Dev App must be closed first: it captures the GPU load in every fps run. Before any attempt, check that pid 59732, and any other Dev-instance `ReplayBuffer.App`, is gone. Command (worktree root, about 18 min, lock created and deleted by hand): `bench/ReplayBuffer.Bench/bin/Release/net10.0-windows10.0.26100.0/ReplayBuffer.Bench.exe fps --overlay --recording --presentmon %USERPROFILE%/Documents/repos/ReplayBuffer/native/presentmon/PresentMon-2.6.0-x64.exe --out measurements/bench-8.7`.
- Human Checks for the builder: the FPS run in their GPU-bound game; the unscripted players (Discord, VLC, DaVinci Resolve) with every Track, and A/V sync by eye in all five; Windows Media Player and AV1 (install the AV1 Video Extension, or accept it as a player that doesn't support AV1); and whether NFR-1's provisional 3 % stands (capture cost about 7 % on this PC since 2026-11-03, `research/capture-cost-regression.md`).
- 2026-11-04: QA passed. Merged into develop (`1400c42`), with 8.7 left open for the pending fps run. Its Human Check Queue rows are in `automation-state.md`, and `feature/8.7-final-conformance` is kept for the fps results.
- Correction (bugfix `bench-videoencode-count`, 2026-11-04): `measure-path-a.ps1` summed the three identical `engtype_VideoEncode` counter instances each process gets on this GPU, so every `gpu_videoencode` figure was **3× over-counted**. This plan quotes no video-encode figure, but any `gpu_videoencode_meanPercent` in its `bench fps` summaries is 3× too high; the pending fps run uses the fixed script. FPS loss, 1 % lows, engine CPU and 3D use are unaffected (the sampler only reads counters, and 3D has one instance).
- 2026-11-05: **fps run (phase 2), into `measurements/bench-8.7/fps`**, 02:00–02:18. Develop (with the video-encode fix) merged in, Release build with 0 warnings, and the App taken from the worktree's Release build. `fps --overlay --recording`: 3 balanced passes of 150 s with 20 s settles (B A | A B | B A), the load calibrated to 80.09 fps at 2560×1440 on the RTX 5090, PresentMode Hardware Composed: Independent Flip in every run, and no overlay planes. `.bench-running` was held by hand. No `ReplayBuffer.App` other than the test instance ran: the VS Dev App (pid 59732) was gone, and Visual Studio was open with nothing debugging. NVIDIA FrameView's own `PresentMon_x64.exe` service process ran throughout, as in the 2026-11-04 runs. **Valid:** the test App's engine captured the load only in the app passes (02:03:57–02:09:39 for app-1 and app-2, and from 02:15:27 for app-3), with a Recording in each (the app-3 press was repeated once: "can't record yet"). Baselines 2 and 3 ran with no session, there were no warnings or errors in the engine log, and the load ran all six passes.

  | Run | Avg FPS | 1 % low | Loss against the in-run baseline mean (82.77) | Loss against its paired baseline | Loss against the calibration (80.09) |
  |---|---|---|---|---|---|
  | 01 baseline | 80.20 | 74.39 | | | |
  | 02 app | 77.73 | 70.81 | 6.09 % | 3.08 % (B1) | 2.95 % |
  | 03 app | 78.78 | 73.41 | 4.82 % | 3.94 % (B2) | 1.64 % |
  | 04 baseline | 82.01 | 76.61 | | | |
  | 05 baseline | 86.10 | 78.17 | | | |
  | 06 app | 80.70 | 74.74 | 2.50 % | 6.27 % (B3) | −0.76 % |
  | **Mean** | app 79.07, baseline 82.77 | app 72.99, baseline 76.39 | **4.47 %** | 4.43 % | 1.27 % |

  - **FPS loss is 4.47 % against NFR-1's provisional 3 %: a miss** (Bench's own verdict). The 1 % lows fall 4.46 % (4.81, 4.18 and 4.39 % per pass against the paired baseline).
  - The baseline drifted upward through the run, 80.2 → 82.0 → 86.1 fps (+7.4 %), as in v0.1.0's run on 2026-11-04 (83.2 → 86.9). That spreads the per-pass losses (2.5–6.1 % against the mean, 3.1–6.3 % paired). Against the 80.09 fps calibration alone, the loss is 1.3 %. The in-run mean is the method's figure.
  - **Against the capture-cost figures** (`research/capture-cost-regression.md`): 1.47 % (1.14, 2026-11-02), 6.59 % (develop, capture only, 2026-11-04), 6.96 % (v0.1.0) and 6.66 % (5.12 control). This run is the full App, with the Replay Buffer, a Recording, the Overlay dot and a message every 10 s, and it costs 4.47 %: about 2 points under the 2026-11-04 capture-only figures, but still well over 1.14's.
  - WGC looks healthy again. The engine's first session filled 20,491 slots with new frames and repeated 41 (0.2 %), against 12–15 % repeated slots and 52–54 WGC frames a second on 2026-11-04. The research doc's "WGC itself is worse" signature didn't show in this run, and the remaining cost isn't explained here.
  - Data: `measurements/bench-8.7/report.md`, `fps/summary.{md,json,csv}`, `fps/app-logs/engine-20261105.log`.
  - **Finding for the builder:** NFR-1's 3 % is missed at 4.47 % on this PC. NFR-1 is unchanged here: whether 3 % stands, or is re-baselined, is the builder's decision (Human Check). Their game's FPS run is still a Human Check.
