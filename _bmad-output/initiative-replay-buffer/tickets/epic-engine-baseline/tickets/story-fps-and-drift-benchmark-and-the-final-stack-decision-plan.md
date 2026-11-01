---
title: 'FPS and drift benchmark, and the final stack decision'
type: 'feature'
ticket: '14'
created: '2026-10-27'
status: 'built'
baseline_revision: '891d34796966c5550a61590f179f28ce9f497961'
route: 'full'
route_source: 'auto'
risk: 'medium'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
followup_review_recommended: true
context:
  - '{project-root}/PLAN.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** R4, R5 and R11 need a repeatable benchmark: FPS loss at 1080p60 in a GPU-bound game, A/V and audio-to-audio drift over 60 minutes on two independent clocks, fMP4 playback in the NFR-4 players, and real-key Hotkey latency. Today `bench/ReplayBuffer.Bench` is a placeholder, and only spike 1.2's script exists.

**Approach:** Turn `ReplayBuffer.Bench` into a CLI with four measurements: `fps`, `drift`, `playback` and `hotkey`. Each writes its JSON into one run folder, and `report` merges them into `report.md` and `report.json`. Every subcommand regenerates the report. `fps` reuses `tools/measure-path-a.ps1`. `drift` measures the finished MP4 itself, from flashes and bursts. Since 2026-11-01 the runs need nobody: `fps` measures Bench's own synthetic GPU load (`bench/ReplayBuffer.Bench.GpuLoad`) unless `--game` names a real one, `hotkey --inject` presses the key with `SendInput`, and `playback` scripts the players it can. The results and the final §6/§7.4 decisions go into the epic's Notes; what only a person can check goes to the Human Check Queue.

## Boundaries & Constraints

**Always:**
- The engine is driven only through its existing `debug-capture` and `serve` commands. The default output is H.264 1920×1080 at 60 fps, CBR 20 Mb/s.
- Every target sits beside its figure in the report:
  - FPS loss < 3 %;
  - A/V and audio-to-audio drift under one video frame at the output fps (16.7 ms at 60 fps);
  - Hotkey press-to-stamp under 1 s, with p50 and p99 shown against the old 1 ms bar.
- The run is repeatable: everything Bench uses is a command-line option with a sensible default, so epics 5 and 8 can rerun it. That covers the game, the devices, the seconds, the passes and the output folder.
- Output goes to `measurements/bench-<time>/` (gitignored), or to the folder `--out` names. A later subcommand adds to that folder.
- Like the script, Bench refuses to run elevated. It never starts the game and never downloads anything. Starting its own synthetic GPU load, and the players already installed, is not starting the game.
- The synthetic load is a process of its own, `ReplayBuffer.Bench.GpuLoad.exe`, so the script finds it with `-Game ReplayBuffer.Bench.GpuLoad` and PresentMon traces it by name, unchanged. Bench always stops it, on every path, and it exits by itself when Bench does.
- Whatever a run couldn't check without a person is recorded as `not-checked`, never guessed.
- The NativeAOT publish of the engine stays at 0 warnings. Bench itself is JIT, as its exclusion in `Directory.Build.props` already sets.

**Never:**
- Change the capture, encode or mux behaviour of the engine.
- Add a Path C, X2 or X3 implementation.
- Use elevation, or `D3DKMTSetProcessSchedulingPriorityClass`.
- Test HEVC/AV1 playback (they're labelled as possibly not playing everywhere).
- Replace `measure-path-a.ps1`. It stays the FPS driver, and only gains pass-through options.

**Decisions (builder, 2026-10-27):**
- Audio-to-audio has no acoustic coupling. The mic doesn't hear the bursts, so audio-to-audio comes from the engine's stats over the 60-minute drift run, per device:
  - `resampler.errorMaxSamples` and `errorP99Samples` (content position against its own QPC stamps);
  - raw `stamps.drift.ppm` and `resampler.meanRatioPpm`;
  - gap fills, trims and step corrections.
- The bound is |game error| + |mic error|, against one video frame (800 samples at 60 fps). The Decision states that this trusts the device stamps, which spike 1.2 and story 1.12 checked.
- The FPS run keeps Cyberpunk 2077 borderless at 2560×1440 (RT Overdrive, Frame Generation off, the NVIDIA overlay off), with the capture scaled to 1920×1080 at 60 fps by the 1.11 scaler.
- The full plan is kept despite its size: it's one benchmark deliverable.

**Decisions (builder, 2026-11-01, renegotiated so the runs need nobody):**
- `bench fps` with no `--game` starts Bench's own synthetic GPU load and measures that; `--game <process>` keeps the real game as an option. R4's/R5's "GPU-bound game" counts as met by the synthetic load. The real-game run goes to the Human Check Queue as a confirmation, not a gate.
- The load is a borderless popup covering the primary monitor (`--size` overrides), like a borderless game: a D3D11 flip-model swap chain (FLIP_DISCARD, ALLOW_TEARING when supported, Present with sync interval 0) drawing a fullscreen triangle whose pixel shader runs a loop of N trig iterations animated by time, so every frame is new to WGC. During warm-up it calibrates N until its present rate settles near `--target-fps` (default 80, spike 1.2's ≈ 78 fps in Cyberpunk at 2560×1440), then freezes N for the run.
- `bench hotkey --inject` presses the key with `SendInput`, about once a second, for unattended runs. Real presses stay the default and go in the Human Check Queue.
- `bench playback --auto` scripts the players it can check without a person and records the rest (at least Discord and DaVinci Resolve) as `not-checked`, queued for the builder. A scripted check records what it actually saw (plays, tracks listed); A/V sync in a player stays a person's check unless the script measured it.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| FPS, synthetic | `bench fps` with no `--game` | Bench starts `ReplayBuffer.Bench.GpuLoad --target-fps 80 --parent <pid>`, waits for its one `ready` line (hwnd, calibrated fps, iterations, adapter), then runs the script with `-Game ReplayBuffer.Bench.GpuLoad` as below. `fps.json` and `report.md` record the load: kind, resolution, target and calibrated fps, iterations, adapter. The load is stopped afterwards | no `ready` within the timeout, or the load exits early → its stderr, exit 1, load stopped |
| FPS, real game | `bench fps --game Cyberpunk2077` with the game running | the script runs baseline vs capture at `--size 1920x1080 --fps 60`, with 3 balanced passes of 5 min by default. `fps.json` holds the mean loss, the 1 % lows and the engine's stats per condition | game missing, elevated, or no PresentMon → the script's message, exit 1 |
| Drift | `bench drift --minutes 60` | Bench shows a marker window that flashes white each second, plays `TonePlayer` bursts into the output device, and runs `debug-capture` on the marker window with both devices. It then analyses the MP4 | engine exits early → its stderr in `drift.json`, exit 1 |
| A/V analysis | MP4 + the flash and burst QPC logs | per burst k: `error_k = (audio onset − flash frame PTS) in file − (burst QPC − flash QPC) at source`. Reports the per-minute median, drift = the largest change of the per-minute median from minute 1, and the regression slope | < 90 % of flashes or bursts matched → `matched` low, verdict `inconclusive` |
| Audio-to-audio | the drift run's stats JSON (`audio[]` with roles `output` and `input`) | per device: error max and p99, drift ppm, mean ratio and corrections. Bound = output errorMax + input errorMax, in samples and ms, against 800 samples. Each track's sample count is also checked against the video duration in the file | a device missing from the stats → verdict `inconclusive` |
| Playback | `bench playback` | writes a 30 s clip (`debug-capture` of the marker window, both tracks) and its path, then asks for each player (Discord, Chrome, VLC, Windows Media Player, DaVinci Resolve): plays? A/V in sync? which audio tracks? notes | "skip" recorded as `not-checked` |
| Playback, scripted | `bench playback --auto` | the same clip; Chrome (headless, own profile) and Windows Media Player (its `WMPlayer.OCX` engine) each play it and report whether playback advanced, the size, and the audio tracks they list; a screen grab around the marker during the capture answers the capture-border question. Every other player is `not-checked` with "needs a person" | a player not installed, or a script that times out → `not-checked` with the reason |
| Hotkey | `bench hotkey --presses 20 --key ctrl+alt+f9` | starts `serve --instance ReplayBuffer-Test-bench-<guid>` with Save bound, plus a `WH_KEYBOARD_LL` hook in Bench that stamps QPC on key-down. Each `hotkey.pressed` gives `press.Qpc − hook QPC`. Reports p50/p99/max | the key is held by another process → `hotkey-taken` from the snapshot, exit 1; no press within 60 s → stop with what was gathered |
| Hotkey, injected | `bench hotkey --inject` | as above, with Bench pressing the combination by `SendInput` once a second; each timing is marked injected, and the report says the presses were injected | `SendInput` inserts nothing (UIPI) → named, exit 1 |
| Report | any subset of the JSON files | `report.md` has a section per measurement present, and "not run" for the rest | malformed JSON → named in the report and skipped |

</frozen-after-approval>

## Code Map

- `bench/ReplayBuffer.Bench/` -- the placeholder today (`Program.cs`, a bare csproj). Already in `ReplayBuffer.slnx` and excluded from `IsAotCompatible`.
- `tools/measure-path-a.ps1` -- the FPS driver. Its `debug-capture` args are built around lines 323–330. Add `-Size`/`-Fps`/`-Bitrate` pass-through and append them to every capture condition. Keep its outputs (`summary.json`, with `byCondition` and `runs`) as they are; Bench reads them.
- `src/ReplayBuffer.Engine/CommandLine.cs` -- `debug-capture` already takes `--hwnd --seconds --output --stats --size --fps --bitrate --output-device --input-device`. No engine change is expected.
- `src/ReplayBuffer.Engine/CaptureStatsReport.cs` -- the stats JSON schema (`video`, `latency`, `resources.steady`, `audio[]` with `role`, `stamps.drift.ppm` and `resampler.errorMaxSamples`). Bench reads it with its own small source-generated or `JsonDocument` reader, with no project reference to the Engine.
- `src/ReplayBuffer.Platform.Audio/TonePlayer.cs` -- `new TonePlayer(endpointId, dbfs)`, `Start/Stop`, `BurstCount`, `BurstQpc(i)` and `Fault`. Its bursts are 3 kHz and 50 ms, one a second.
- `src/ReplayBuffer.Platform.Audio/AudioEndpoints.cs` -- `List/Match/Default` resolve the devices by name, as the verify test does.
- `tests/ReplayBuffer.Platform.Audio.Tests/DeviceSyncVerifyTests.cs` -- `BurstDetector`, the 3 kHz quadrature onset detector. Move it into Bench as a PCM-span detector, sample-indexed instead of QPC-indexed, and leave the test's copy alone.
- `src/ReplayBuffer.Contracts/Ipc/EngineClient.cs`, `HotkeyMessages`, `Keys.Hotkeys`, `SettingsJsonContext` -- the hotkey run copies `tests/ReplayBuffer.Engine.Tests/HotkeyServeTests.cs`: `WriteHotkeys`, `StartServeAsync`, waiting for `listening <pipe>`, then `ConnectAsync`, and cleaning up the instance folders.
- `native/ffmpeg/ffmpeg.exe` and `ffprobe.exe` -- the analysis decodes with them. Video goes to `scale=64:36,format=gray` rawvideo (CFR, frame i → i/fps), and each audio track to mono f32le at 48 kHz, streamed. `ffprobe` gives each stream's `start_time`.
- `.gitignore` -- `measurements/` is already ignored.
- `src/ReplayBuffer.Platform.Capture/NativeMethods.txt`, `NativeMethods.json`, `D3D11Device.cs` -- the CsWin32 pattern the GpuLoad copies: `allowMarshaling: false`, raw COM pointers, released in `finally`.
- `tools/measure-path-a.ps1` lines 209–214 and 365 -- finds the target by `Get-Process -Name $Game` with a `MainWindowHandle`, and traces `--process_name <name>.exe`: a WinExe named `ReplayBuffer.Bench.GpuLoad` with a visible unowned popup satisfies both.

## Tasks & Acceptance

**Execution:**
- [x] `bench/ReplayBuffer.Bench/ReplayBuffer.Bench.csproj` -- `UseWindowsForms` for the marker window. References Platform.Audio, Media and Contracts. Copies the FFmpeg natives the way the Engine does.
- [x] `bench/ReplayBuffer.Bench/Program.cs`, `BenchOptions.cs` -- the subcommands, options and usage, the elevation refusal, and resolving the run folder, engine (newest Release build or `--engine`), ffmpeg, PresentMon and pwsh.
- [x] `bench/ReplayBuffer.Bench/FpsBenchmark.cs` -- runs `pwsh -File tools/measure-path-a.ps1` with `-Conditions baseline,capture -Size … -Fps …`, the passes, the seconds, the devices, and `-OutDir <run>/fps`. Then it maps `summary.json` into `fps.json`, including fresh slots, latency p99, CPU, alloc/s and GC.
- [x] `bench/ReplayBuffer.Bench/MarkerWindow.cs` -- a black 640×360 window, titled "ReplayBuffer Bench Marker", that flashes white for 3 frames at each whole second from its start and logs each flash's QPC after `DwmFlush`.
- [x] `bench/ReplayBuffer.Bench/DriftBenchmark.cs` -- marker + `TonePlayer` + `debug-capture --hwnd <marker> --seconds 60·N --stats`, then `MediaAnalysis` → `drift.json`.
- [x] `bench/ReplayBuffer.Bench/MediaAnalysis.cs`, `OnsetDetector.cs`, `FlashDetector.cs`, `DriftMath.cs` -- the ffmpeg/ffprobe decode, onsets and flash PTS, nearest matching within ±0.5 s, per-minute medians, drift and slope. These are pure, apart from the decode.
- [x] `bench/ReplayBuffer.Bench/PlaybackCheck.cs` -- the 30 s clip and the interactive checklist → `playback.json`. It also asks whether a capture border showed (§7.4 `IsBorderRequired`).
- [x] `bench/ReplayBuffer.Bench/HotkeyBenchmark.cs`, `KeyboardHook.cs` -- the matrix's Hotkey row → `hotkey.json`.
- [x] `bench/ReplayBuffer.Bench/Report.cs` -- merges the JSON files into `report.json` and `report.md`, each figure with its target and a pass/miss mark.
- [x] `tools/measure-path-a.ps1` -- the `-Size`, `-Fps` and `-Bitrate` pass-through.
- [x] `tests/ReplayBuffer.Bench.Tests/` (new, in the slnx) -- tests for `DriftMath`, the onset and flash detectors on synthetic buffers, and option parsing. Also an end-to-end analysis test: ffmpeg lavfi makes a 2-minute fMP4 with a flash each second and bursts that drift 5 ms against the flashes over the 2 minutes. The analysis must report 5 ± 1.5 ms A/V drift. A no-drift twin must report under 1 ms. The test is skipped without ffmpeg.
- [x] `bench/ReplayBuffer.Bench.GpuLoad/` (new WinExe, in the slnx; AOT-compatible, CsWin32 like Platform.Capture) -- the synthetic load: borderless popup, D3D11 flip-model swap chain, `D3DCompile`d fullscreen-triangle shader with a time-animated `[loop]` of N trig iterations, warm-up calibration of N to `--target-fps`, one `ready` line on stdout, `--parent <pid>` exit, `--size`, `--warmup`, and Escape or `WM_CLOSE` to exit.
- [x] `bench/ReplayBuffer.Bench/GpuLoadProcess.cs`, `FpsBenchmark.cs`, `BenchOptions.cs` -- `fps` without `--game` starts the load, waits for `ready` (`--load-timeout`), passes `-Game ReplayBuffer.Bench.GpuLoad`, and always stops it; `fps.json` and `report.md` carry the load's details. Options `--target-fps`, `--load-size`. Confirm `measure-path-a.ps1` needs no change.
- [x] `bench/ReplayBuffer.Bench/HotkeyBenchmark.cs`, `KeyInjector.cs` -- `--inject`: `SendInput` presses once a second; the report marks injected presses.
- [x] `bench/ReplayBuffer.Bench/PlaybackCheck.cs`, `PlayerScripts.cs` -- `--auto`: Chrome headless and WMP's OCX scripted checks, the capture-border screen grab, and `not-checked` for the rest.
- [x] `tests/ReplayBuffer.Bench.Tests/` -- option parsing for the new options, the ready-line parse, `fps.json` mapping with the load, the scripted players' result parse, and a GpuLoad start/ready/exit smoke test skipped without a hardware GPU.
- [ ] Runs into `measurements/bench-1.14` (after review, by the orchestrating session, not the implementer: about two hours of exclusive GPU and audio use) -- the three timing tests rerun, then `fps` (synthetic), `hotkey --inject`, `drift --minutes 60` and `playback --auto`.
- [ ] `_bmad-output/.../epic-engine-baseline.md` Notes (after the runs, by the orchestrating session) -- Sofia records the results and decisions: the §6 final stack, §7.2 kept, and the §7.4 facts.

**Acceptance Criteria:**
- Given FFmpeg is fetched, when `dotnet build` and `dotnet test --solution` run, then there are 0 warnings and all tests pass. `pwsh tools/publish-engine.ps1` also gives 0 warnings.
- Given a 2-minute agent smoke run of `bench drift --minutes 2`, which needs no game, when it finishes, then `drift.json` matches at least 90 % of flashes and loopback bursts, and `report.md` shows the A/V figure against 16.7 ms.
- Given the builder runs `fps`, `drift --minutes 60`, `playback` and `hotkey` into one folder, then `report.md` shows the FPS loss, the 60-minute drift, per-player playback and the Hotkey latency, each against its target.
- Given those results, then the epic's Notes hold a dated Decision on the final §6 stack, with the §7.4 facts and the figures.
- Given no game is running, when `bench fps --seconds 30 --passes 1 --settle 5` runs, then it writes `fps.json` with a loss figure and the synthetic load's details (kind, resolution, target and calibrated fps, iterations, adapter), and no load process is left running.
- Given `bench hotkey --inject` and `bench playback --auto` run with nobody at the PC, then `hotkey.json` holds injected timings and `playback.json` holds the scripted players' results, with every other player `not-checked`.

## Plan Change Log

- **2026-11-01, builder renegotiation (unattended runs).** The builder is away and asked for the initiative to finish unattended. A synthetic GPU load (`ReplayBuffer.Bench.GpuLoad`) replaces the game in `bench fps`, `hotkey` gains `--inject`, and `playback` gains `--auto`. The real-game FPS run, real presses and the players that need a person go to the Human Check Queue in `automation-state.md`. Frozen intent edited by the builder's decision: Approach, Boundaries, Decisions (2026-11-01) and the Matrix rows. Status back to `ready-for-dev` for the new tasks.

## Review Triage Log

**Lens `quick`, iteration 0:** 0 high, 6 medium, 4 low, 3 false (rejected). Findings 1–10 were sent to the implementer as patches, with no test runs until the builder resumes. Findings 11–13 were rejected.

| # | Finding | Verdict | Route | Evidence |
|---|---------|---------|-------|----------|
| 1 | `HotkeyBenchmark.StartServe` leaks the started `serve` when it throws, and Bench crashes on timeout or cancel exceptions | medium | patch | `process` is assigned only after `StartServe` returns, and `Program` catches only `BenchUsageException`, `BenchFailure` and `ErrorCodeException`. |
| 2 | Audio-to-audio passes with 0 settled packets or NaN | medium | patch | `Number()` maps non-numbers to 0; `MaxSettledErrorSamples` starts at 0. |
| 3 | Gen2 total 0 / pass with no GC data | low | patch | `Enumerable.Sum` over `long?` returns 0 when every value is null (`FpsBenchmark.cs:215`). Direct fix. |
| 4 | `--reanalyze` takes minutes and settings from the current command line | medium | patch | `MarkerLog` holds no capture settings; `Run` reads `options.Double("minutes", 60…)`. |
| 5 | A skipped sync answer is marked pass | medium | patch | `Report.cs:258`: `AvInSync != false` lets null through. |
| 6 | A wrong-shape JSON file crashes the report | medium | patch | `Load` catches only deserialization exceptions; null non-nullable members make `Markdown()` throw. This breaks the matrix's "malformed → named and skipped" row. |
| 7 | The track check uses timestamp spans, not sample counts | medium | patch | The matrix asks for sample counts; `BurstTrackSamples` is computed and never used. |
| 8 | `report.md` cells format in the current culture | low | patch | `Row($"…")` interpolations; the fix is one line (an invariant culture for the process). |
| 9 | §6.3's dropped-frames figures (`QueueDrops`, `SkippedSlots`) are missing from `report.md` | low | patch | Both are collected in `fps.json` and never rendered. Direct addition. |
| 10 | A test asserts `whoami`'s English stderr | low | patch | `CommandFailureTests` checks for "Invalid argument". |
| 11 | The epic-Notes AC is unmet | false | reject | By design it waits on the builder's runs (Verify, task 12). It isn't a code defect. |
| 12 | The body of commit `8bb0d33` isn't wrapped at 72 characters | low | reject | Fixing it means rewriting committed history for a cosmetic rule. Later commits follow the rule. |
| 13 | The plan's edits are uncommitted | false | reject | That's this review's own in-progress frontmatter edit; it's committed with the patches. |

### 2026-11-01 — Review pass
- verdicts: 9 findings (8 from lens `quick`, 1 from the orchestrator's own read of the diff) — high 0, medium 5, low 4, false 0, maybe-false 0
- findings:
  - `[medium]` `[patch]` A load that exits after `ready` only warns; Bench exits 0 and the load's stderr is lost — confirmed: `measure-path-a.ps1` records `$aborted` and exits 0, and the stderr builder is local to `Start`. Fix: `GpuLoadProcess` keeps its stderr; a load gone during the run is reported with its exit code and stderr, exit 1.
  - `[medium]` `[patch]` `hotkey --inject` with every injected press dropped (UIPI drops silently) ends with 0 timings, no error, exit 0 — confirmed: `Result` names a shortfall only when `ms.Count > 0`. Fix: inject mode with 0 timings names the reason and exits 1.
  - `[medium]` `[patch]` The WMP script records a 20 s stall with no OCX error as `media` (player refused the file → miss) — confirmed in the script. Fix: `media` only when the OCX reported an error; a timeout is `not-checked` with the reason.
  - `[low]` `[reject]` The "during" border grab isn't tied to the capture having started — real only for very short clips on a slow start: the default 30 s clip grabs 8 s in, and engine start to first frame is ≈ 0.3 s (AOT) to ≈ 1 s (JIT); tying it to the engine's capture start needs a new signal from the engine, more than a direct correction.
  - `[medium]` `[patch]` A second `ReplayBuffer.Bench.GpuLoad` already running is measured too (the script takes the first by name; PresentMon traces every process of that name) — reachable after a manual run without `--parent`. Fix: refuse to start, naming the running pid(s).
  - `[low]` `[patch]` `--load-warmup` ≥ ≈ 57 s can never meet the default 60 s `--load-timeout` — direct correction: the ready timeout is at least warm-up + 30 s.
  - `[medium]` `[patch]` Scripted headless Chrome on a fresh profile does background downloads, against "never downloads anything" — fix: `--disable-background-networking --disable-component-update --no-pings`.
  - `[low]` `[patch]` `EnumAdapters1` failures other than `DXGI_ERROR_NOT_FOUND` dereference a null adapter (access violation, no stderr) — direct correction: throw on any other failed HRESULT.
  - `[low]` `[patch]` (orchestrator) The load's popup isn't topmost, and `SetForegroundWindow` is refused for a process started from a background shell, so in an unattended run other windows can cover the "game" for the whole FPS run — fix: `WS_EX_TOPMOST`, keeping `WS_EX_APPWINDOW`.

## Auto Run Result

**2026-11-01, bmad-build-auto on the amendment (full route, quick review).**

- **Summary:** `bench fps` now runs without a game. It starts Bench's own synthetic GPU load, calibrated to `--target-fps` (default 80), measures it through the unchanged `measure-path-a.ps1`, records the load in `fps.json` and `report.md`, and always stops it. `bench hotkey --inject` presses the key with `SendInput`. `bench playback --auto` scripts headless Chrome and Windows Media Player (`WMPlayer.OCX`), answers the capture-border question from screen grabs, and records every other player as `not-checked`.
- **Files:**
  - `bench/ReplayBuffer.Bench.GpuLoad/` (new): the load. Borderless topmost popup, D3D11 FLIP_DISCARD swap chain, `D3DCompile`d shader, calibration, ready line, `--parent`.
  - `GpuLoadProcess.cs` (new): starts the load, waits for ready, keeps its stderr, and stops it.
  - `FpsBenchmark.cs`: the synthetic path. It refuses a second load and fails on a load that dies mid-run.
  - `KeyInjector.cs` (new), `HotkeyBenchmark.cs`: `--inject`, and a named failure when no press arrives.
  - `PlayerScripts.cs` (new), `BorderProbe.cs` (new), `PlaybackCheck.cs`, `DriftBenchmark.cs`: `--auto`.
  - `BenchOptions.cs`, `Report.cs`: the new options and report lines.
  - `ReplayBuffer.slnx`, the csproj files: the new project.
  - `tests/ReplayBuffer.Bench.Tests/UnattendedTests.cs` and `UnattendedProcessTests.cs` (new), `PlaybackChecklistTests.cs`: the tests.
- **Review:**
  - 9 findings: 8 patched (`1693162`), 0 deferred.
  - 1 rejected as low: the border grab isn't tied to capture start. The default clip grabs 8 s in; tying it needs a new engine signal.
  - Patched by verdict: 5 medium, 3 low.
- **Follow-up review recommended: true** (two or more medium patched). The specific unverified risk: three fixes have no tests. They are the load dying mid-run (exit 1 with its stderr), `--inject` with every press dropped by UIPI, and the refusal of a second load instance. The QA session on this branch serves as that follow-up.
- **Verification:**
  - Implementer: Release build 0 warnings; Bench tests 115/115; NativeAOT publish 0 warnings; smoke runs of `fps --seconds 30 --passes 1 --settle 5` (loss figure plus load details, no load left) and `playback --auto --seconds 15`.
  - Orchestrator: the three timing tests rerun alone in Release and passed (3/3). Full build, tests and publish after the patches are in `measurements/bench-1.14/verify-after-review.log`.
- **Residual risks:**
  - The capture-border answer comes from a GDI screen grab: if WGC's border isn't in the grab, "no" could be a false negative. The PNGs are kept for a person to check.
  - The synthetic load is a proxy for a game, not a game. The real-game run is queued for the builder.

## Checkpoint Log

**2026-11-01, runs paused by the builder.** The amendment is built and reviewed on `feature/1.14-benchmark` (tip `2144fd6`). Release build 0 warnings and NativeAOT publish 0 warnings (10.0 MB). The full suite passes 653/654. The one failure is `EncodeExperimentTests.ThePacedEncoderEncodesEverySlotWithAFastPreset` under whole-suite parallelism; the pre-amendment `7229d26` fails it the same way (p50 18.6 ms), and it passes alone. Runs so far, in `measurements/bench-1.14` (NativeAOT engine, Game (BEACN Mix Create) and BEACN Mic):

- **Timing tests rerun alone:** 3/3 pass.
- **`fps`** (synthetic load, 2560×1440 on the RTX 5090, calibrated 80.52 fps at 6120 iterations, 3 balanced passes of 5 min, capture 1920×1080 at 60 fps): FPS loss **5.88 %, a miss** against 3 %.
  - Average fps: baseline 80.40, capture 75.67.
  - 1 % low: 77.77 → 70.63 (9.2 % loss).
  - Capture→encoded p99 **39.0 ms, a miss** against 16.7 ms.
  - Fresh slots 89.6 %, duplicates 10.4 %, 3 queue drops, 0 skipped.
  - Engine CPU 5.5 % of one core; 15 B/s allocated; 0 Gen2 GCs.
- **`hotkey --inject`** (20 presses): p50 1.135 ms, p99 1.631 ms, max 1.631 ms. That passes the 1 s target but not the old 1 ms bar.
- **`drift --minutes 60`:** stopped at the builder's request about 11 minutes in. Its `drift.mp4` is partial and has no `drift.json`; rerun it from the start.

When resuming, in order:

1. `drift --minutes 60`.
2. `playback --auto`.
3. A short diagnostic of where the FPS loss comes from: the script's `baseline,capture-wgc,capture-copy,capture` against the same load.
4. Sofia's Decision lines in the epic Notes.
5. The final verification and commit, then "READY FOR QA". The coordinator gates the merge.

**2026-11-01, measurements deferred by the builder.** Code and review are done (`7b810ab`); the story stays `in-review` on `feature/1.14-benchmark` and isn't merged into `develop`. The builder chose to come back to the runs later. At this checkpoint the review fixes still hold: the Release build has 0 warnings and the Bench tests pass (71 of 71). When resuming, in order:

1. Rerun the three real-time timing tests with no game running: `CaptureEndTests.AStatsRunWritesAReportThatMatchesTheFile`, `OutputResolutionTests.AStatsRunOnAFastWindowMeetsTheLatencyGcAndAllocationBounds` and `EncodeExperimentTests.ThePacedEncoderEncodesEverySlotWithAFastPreset`. They failed only while Cyberpunk held the GPU, and none is in code this story changed.
2. `bench fps` into `measurements/bench-1.14`: baseline vs capture, 3 balanced passes of 5 minutes (about 35 minutes), 1920×1080 at 60 fps, NativeAOT engine, Game (BEACN Mix Create) and BEACN Mic. The last attempt never started: the auto-start watcher needs two samples in a row under 200 fps, and Cyberpunk read 400–515 fps for 30 minutes (a menu, or Frame Generation on). Before starting, the builder loads into gameplay at a busy spot with RT Overdrive on, Frame Generation off, the NVIDIA overlay off, and the game focused (spike 1.2 saw about 78 fps at 2560×1440). Print one PresentMon sample first to confirm it's GPU-bound.
3. `hotkey`: about 20 presses with the game focused, re-checking the real-press latency story 1.10 loosened in the automated test (1 ms → 1 s).
4. `drift --minutes 60`, without the game.
5. `playback` in Discord, Chrome, VLC, Windows Media Player and DaVinci Resolve; a skipped player is `not-checked`.
6. Sofia records the results against the 3 % FPS and one-frame drift targets, the final PLAN.md §6 stack and the §7.4 facts as dated Decision lines in the epic's Notes.

Then: full build with 0 warnings, the tests and the NativeAOT publish; commit with `Refs: 1.14`; on the builder's go-ahead, merge into `develop` from the main folder with `--no-ff` and `feat(bench): merge 1.14-benchmark`; `tickets.py mark epic-engine-baseline 1.14 done` committed on `develop`.

## Design Notes

- **What drift means here:** the 60-minute change in offset. Constant pipeline offsets (DWM composition, AAC priming, capture latency) cancel out, and are reported separately as the "absolute offset, minute 1". Video onsets are quantised to 1/60 s, so a single burst's error can be off by ±8 ms. Per-minute medians of about 60 bursts average that out, so a raw max−min spread isn't used as the verdict.
- **The source term** `(burst QPC − flash QPC)` takes the render device's drift against QPC out, so only the capture path is measured.

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings, 0 errors.
- `dotnet test --solution ReplayBuffer.slnx` -- expected: all pass.
- `pwsh tools/publish-engine.ps1` -- expected: 0 IL2xxx/IL3xxx warnings.
- `dotnet run --project bench/ReplayBuffer.Bench -c Release -- drift --minutes 2` -- expected: `drift.json` and `report.md`, with ≥ 90 % matched.

**Manual checks (if no CLI):**
- The builder runs `fps` (with the game), `drift --minutes 60`, `playback` (with the players) and `hotkey` (with the game focused) into one `--out` folder, then reviews `report.md`.
