---
title: 'FPS and drift benchmark, and the final stack decision'
type: 'feature'
ticket: '14'
created: '2026-10-27'
status: 'in-review'
baseline_revision: '891d34796966c5550a61590f179f28ce9f497961'
route: 'full'
route_source: 'auto'
risk: 'medium'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
context:
  - '{project-root}/PLAN.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** R4, R5 and R11 need a repeatable benchmark: FPS loss at 1080p60 in a GPU-bound game, A/V and audio-to-audio drift over 60 minutes on two independent clocks, fMP4 playback in the NFR-4 players, and real-key Hotkey latency. Today `bench/ReplayBuffer.Bench` is a placeholder, and only spike 1.2's script exists.

**Approach:** Turn `ReplayBuffer.Bench` into a CLI with four measurements: `fps`, `drift`, `playback` and `hotkey`. Each writes its JSON into one run folder, and `report` merges them into `report.md` and `report.json`. Every subcommand regenerates the report. `fps` reuses `tools/measure-path-a.ps1`. `drift` measures the finished MP4 itself, from flashes and bursts. The builder runs it, and the results and the final §6/§7.4 decisions go into the epic's Notes.

## Boundaries & Constraints

**Always:**
- The engine is driven only through its existing `debug-capture` and `serve` commands. The default output is H.264 1920×1080 at 60 fps, CBR 20 Mb/s.
- Every target sits beside its figure in the report:
  - FPS loss < 3 %;
  - A/V and audio-to-audio drift under one video frame at the output fps (16.7 ms at 60 fps);
  - Hotkey press-to-stamp under 1 s, with p50 and p99 shown against the old 1 ms bar.
- The run is repeatable: everything Bench uses is a command-line option with a sensible default, so epics 5 and 8 can rerun it. That covers the game, the devices, the seconds, the passes and the output folder.
- Output goes to `measurements/bench-<time>/` (gitignored), or to the folder `--out` names. A later subcommand adds to that folder.
- Like the script, Bench refuses to run elevated. It never starts the game and never downloads anything.
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

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| FPS | `bench fps --game Cyberpunk2077` with the game running | the script runs baseline vs capture at `--size 1920x1080 --fps 60`, with 3 balanced passes of 5 min by default. `fps.json` holds the mean loss, the 1 % lows and the engine's stats per condition | game missing, elevated, or no PresentMon → the script's message, exit 1 |
| Drift | `bench drift --minutes 60` | Bench shows a marker window that flashes white each second, plays `TonePlayer` bursts into the output device, and runs `debug-capture` on the marker window with both devices. It then analyses the MP4 | engine exits early → its stderr in `drift.json`, exit 1 |
| A/V analysis | MP4 + the flash and burst QPC logs | per burst k: `error_k = (audio onset − flash frame PTS) in file − (burst QPC − flash QPC) at source`. Reports the per-minute median, drift = the largest change of the per-minute median from minute 1, and the regression slope | < 90 % of flashes or bursts matched → `matched` low, verdict `inconclusive` |
| Audio-to-audio | the drift run's stats JSON (`audio[]` with roles `output` and `input`) | per device: error max and p99, drift ppm, mean ratio and corrections. Bound = output errorMax + input errorMax, in samples and ms, against 800 samples. Each track's sample count is also checked against the video duration in the file | a device missing from the stats → verdict `inconclusive` |
| Playback | `bench playback` | writes a 30 s clip (`debug-capture` of the marker window, both tracks) and its path, then asks for each player (Discord, Chrome, VLC, Windows Media Player, DaVinci Resolve): plays? A/V in sync? which audio tracks? notes | "skip" recorded as `not-checked` |
| Hotkey | `bench hotkey --presses 20 --key ctrl+alt+f9` | starts `serve --instance ReplayBuffer-Test-bench-<guid>` with Save bound, plus a `WH_KEYBOARD_LL` hook in Bench that stamps QPC on key-down. Each `hotkey.pressed` gives `press.Qpc − hook QPC`. Reports p50/p99/max | the key is held by another process → `hotkey-taken` from the snapshot, exit 1; no press within 60 s → stop with what was gathered |
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
- [ ] `_bmad-output/.../epic-engine-baseline.md` Notes -- after the builder's runs, Sofia records the results and decisions: the §6 final stack, §7.2 kept, and the §7.4 facts.

**Acceptance Criteria:**
- Given FFmpeg is fetched, when `dotnet build` and `dotnet test --solution` run, then there are 0 warnings and all tests pass. `pwsh tools/publish-engine.ps1` also gives 0 warnings.
- Given a 2-minute agent smoke run of `bench drift --minutes 2`, which needs no game, when it finishes, then `drift.json` matches at least 90 % of flashes and loopback bursts, and `report.md` shows the A/V figure against 16.7 ms.
- Given the builder runs `fps`, `drift --minutes 60`, `playback` and `hotkey` into one folder, then `report.md` shows the FPS loss, the 60-minute drift, per-player playback and the Hotkey latency, each against its target.
- Given those results, then the epic's Notes hold a dated Decision on the final §6 stack, with the §7.4 facts and the figures.

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

## Checkpoint Log

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
