---
title: 'Tracer: a soak harness and a 20-minute soak report'
type: 'feature'
ticket: '1'
created: '2026-11-04'
status: 'in-progress'
baseline_revision: '080f9147e7fe5f05913ebb6cec5cfaabd711a51c'
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

**Problem:** Epic 8 needs a soak harness that runs the real engine and App for a long time and proves no memory or handle growth and that every Clip is correct. Nothing in Bench does this, and a test instance's `serve` can't turn the Replay Buffer on (4.14's finding), so no separate engine process can make a Clip without touching the builder's real instances.

**Approach:** Add a dev-only `serve --replay-buffer` switch (test instances only) and a `bench soak` command. Bench starts the engine (`serve --instance ReplayBuffer-Test-… --replay-buffer`) and the App (`--tray --instance …`) as separate processes from the App's Release folder, starts the synthetic GPU load (the Target Game, also hand-added with `game.add`), injects the Save Hotkey on a schedule, samples both processes every 10 s, checks every Clip with ffprobe/ffmpeg, and writes a CSV, a per-Clip table and a pass/fail report to the run folder.

## Boundaries & Constraints

**Always:** Bench never references the engine or App assemblies (AD-2): only Contracts over the pipe. Throwaway `ReplayBuffer-Test-…` instance; its folders are deleted after the run; Output Folder and ring path inside the run folder. The engine switch is refused without a test `--instance`. Contracts unchanged (no new messages needed). The soak itself is a Bench command, never in the default test run. The soak takes the `.bench-running` lock file for its whole run and deletes it after; it refuses to start (exit 1) while the file exists. The lock is `--lock <path>`, by default `.bench-running` in the main checkout (the folder above `.claude\worktrees\<name>` when Bench runs from a worktree, else the repo root), created with `FileMode.CreateNew`.

**Never:** The builder's installed (`ReplayBuffer`) or Dev (`ReplayBuffer-Dev`) instance. Kill options, Recordings, Cleanup, ring-size and drift extras (stories 8.2 and 8.8). Real-time waits or long runs in default tests.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Flat series | private bytes flat after warm-up, handles flat | bound check passes | — |
| Memory growth | private bytes +3 MB/h after warm-up | fails "slope ≥ 2 MB/h" | verdict fail, run exits 1 |
| Handle growth | handles +60 by the end | fails "handles +60 > 50" | verdict fail |
| Clip missing a Track | layout has 2 Tracks, file has 1 | Clip row fails, names the missing Track | verdict fail |
| Clip cut short | video 3 s for a 30 s Buffer Window | Clip row fails on length | verdict fail |

</intent-contract>

## Code Map

- `src/ReplayBuffer.Engine/CommandLine.cs` -- `ServeCommand(Instance, DebugCapture)` (line ~90) and `ParseServe` (~380): add `ReplayBuffer` bool and `--replay-buffer`, refused unless `--instance` is given (check after the loop). Update the usage/fail text.
- `src/ReplayBuffer.Engine/Program.cs` -- `Serve(...)` builds `EngineHostOptions { DebugCapture = … }`: pass `ReplayBuffer = serve.ReplayBuffer ? true : null`. `EngineHost.Start` line ~210 already reads `hostOptions.ReplayBuffer ?? !instance.IsTest`, and Game Detection turns on when `replay` (~431).
- `tests/ReplayBuffer.Engine.Tests/CommandLineTests.cs` -- add one parse test (accepted with `--instance`, refused without).
- `bench/ReplayBuffer.Bench/BenchOptions.cs` -- add `soak` to `Allowed` and `Usage`: `--minutes 20`, `--mode memory|disk`, `--press-seconds 60`, `--buffer-seconds 30`, `--sample-seconds 10`, `--warmup-minutes` (default min(30, minutes/4)), `--key ctrl+alt+shift+f16`, `--target-fps 80`, `--load-size`, `--load-exe`, `--app-exe`, `--lock`.
- `bench/ReplayBuffer.Bench/Program.cs` -- route `"soak" => SoakRun.Run(context)`.
- Reuse: `AppOverlayHost.Resolve` (App Release exe with the engine beside it; the soak's engine is that sibling), `AppOverlayHost.Canonicalize`, its `Connect` pattern (`GetNamedPipeServerProcessId`), cleanup/delete pattern; `GpuLoadProcess.Resolve/Start` (borderless load, synthetic); `KeyCombo.Parse` and `KeyInjector`/`HotkeyServeTests.Input`-style `SendInput` in `KeyInjector.cs`; `HotkeyBenchmark.StartServe` pattern (wait for `listening <pipe>`; drain stdout to a log); `MediaAnalysis.Run` for ffprobe/ffmpeg; `FfmpegTools` via `context.ResolveFfmpeg()`; `BenchJson.Write`; `DriftMath.Percentile` if needed.
- Engine state via `EngineClient.State`: `Capture.Replay == ReplayState.Buffering`, `LastClip`, `LastClipFailure`, `TargetGame`, `Hotkeys.Bindings`. Settings via `SettingsMessages.Update` + `SettingsOp.SetGlobal` (`Keys.OutputFolder`, `BufferWindow`, `SaveCooldown`, `ReplayStorage`, `RingPath`, `Hotkeys`, see `ClipsEndToEndTests`). Track Layout titles: the Global `Keys.TrackLayout` value in the snapshot's settings, else `Keys.TrackLayout`'s default (one "Everything" Track).
- Hotkey combo: Ctrl+Alt+Shift+F16 — engine tests use F18–F21, F23, F24; Bench tests F17, F22.

## Tasks & Acceptance

**Execution:**
- [x] `src/ReplayBuffer.Engine/CommandLine.cs`, `Program.cs` -- `serve --replay-buffer` for test instances -- lets a separate engine process buffer and save on a throwaway instance.
- [x] `bench/ReplayBuffer.Bench/SoakSampler.cs` -- `ProcessCounters` record (time, pid, name, private bytes, working set, handles, GDI, USER) and `Sample(Process)` via `GetProcessMemoryInfo` (PROCESS_MEMORY_COUNTERS_EX.PrivateUsage), `GetProcessHandleCount`, `GetGuiResources(GR_GDIOBJECTS=0, GR_USEROBJECTS=1)`.
- [x] `bench/ReplayBuffer.Bench/SoakBounds.cs` -- pure `Evaluate(samples per process, warmup)`: warm-up value = first sample at or after warm-up; private bytes end within max(5 %, 20 MB) of it and least-squares slope over post-warm-up samples < 2 MB/h; handles, GDI, USER end within 50. Returns per-process, per-counter rows with value, limit and pass.
- [x] `bench/ReplayBuffer.Bench/SoakClipCheck.cs` -- `Check(tools, path, expectedTitles, bufferSeconds, fps)`: ffprobe streams; every expected Track present by `tags.name`, in order; video length in [window − GOP(1 s) − 1 frame, window + 1 frame]; each audio stream within 1 frame of the video; `ffmpeg -v error -i <clip> -map 0 -f null -` exits 0 with no error output (decodes to the end). Pure evaluation split from probing so tests feed a probe result.
- [x] `bench/ReplayBuffer.Bench/SoakRun.cs` -- the command: resolve App/engine/load/ffmpeg; create instance; start engine with `--replay-buffer`, connect, apply settings; start App `--tray --instance`; start load; `game.add` the load exe; wait for Buffering on the load's profile; wait one Buffer Window; then loop until the duration ends: every `sample-seconds` sample both processes (append CSV row), every `press-seconds` inject the Save combo and wait ≤ 30 s for `clip.saved`/`clip.failed`, then check the Clip. A process exiting stops the loop. Write `soak-samples.csv`, `soak-clips.csv`, `soak.json`, `soak.md`; exit 0 only when every Clip passes, ≥ 1 Clip was made, and all bounds pass. Cleanup on every path (shutdown engine, App quits, kill leftovers, delete instance folders). Uses an injectable clock/delay (`TimeProvider`) for the schedule.
- [x] `tests/ReplayBuffer.Bench.Tests/SoakTests.cs` -- default run, fast: sampler reads the test process's own counters (all > 0); bounds pass a flat series and fail 3 MB/h growth and +60 handles (fake timestamps); Clip check fails a probe missing a Track and one cut short; the schedule with a fake `TimeProvider` produces the expected sample and press counts for a short duration.
- [ ] Run the 20-minute Memory-mode soak holding `.bench-running`; commit `soak.md` and the CSVs to `measurements/` (or link in this plan).

**Acceptance Criteria:**
- Given a Release build, when `ReplayBuffer.Bench soak --minutes 20 --mode memory` runs on an unlocked console, then a run folder holds `soak-samples.csv` (one row per process per 10 s), `soak-clips.csv`, `soak.json` and `soak.md` with every Clip passing and every bound met, and Bench exits 0.
- Given the soak finished or failed, then no `ReplayBuffer-Test-*` folder from it, engine, App or load process remains.
- Given `serve --replay-buffer` without `--instance`, then the engine refuses it with `invalid-arguments` and exit 2.

## Implementation Notes

- Code: `4b112aa` on `feature/8.1-soak-harness`. The schedule lives in its own `SoakSchedule.cs` (clock and wait injected); `BenchLock` (the `.bench-running` lock) is in `SoakRun.cs`. Reused `HotkeyBenchmark.StartServe` (now internal, with extra arguments), `AppOverlayHost.Cleanup`/`DeleteQuietly`/`Canonicalize`, and a new `KeyInjector.PressOnce`.
- Interpretation: "end within" a bound is growth since warm-up; a counter that falls meets it.
- The soak sets only the Save Hotkey on its instance (the other defaults would take Alt+F8/F9/F10 from the builder's instance), Save Cooldown 0, and keeps the default video (1080p60, 40 Mbps).
- Passing Clips are deleted after their check, so a long run's disk use stays flat; a failing Clip stays in `clips/`.
- `soak` does not regenerate `report.md`; it writes its own `soak.md`.
- Pre-existing, not changed: a `serve` that fails to parse still opens the process's own instance's log folder (`ReplayBuffer-Dev` for a dev build) before it exits 2.

### 20-minute Memory soak, 2026-11-04 (`measurements/bench-20261104-011956/`, force-added)

- 121 samples per process, 19 presses, **19 of 19 Clips pass** (video 29.02–30.00 s for a 30 s window; audio within one frame; frames = length × 60). Cleanup left no process, instance folder or ring. Bench exited **1**: 8 of 10 bounds met.
- **Engine private-bytes slope 4.28 MB/h (≥ 2)**, but the engine is flat: 530.8 MB at warm-up, 530.3 MB at the end, ±3 MB noise. A least-squares slope over 15 minutes can't resolve 2 MB/h against that noise. The bound only means something over long runs (8.8). Its end-growth bound passes.
- **App private-bytes slope 59 MB/h (≥ 2)**: a real, steady climb, 82.3 → 94.2 MB over 15 min (+11.9 MB, inside the 20 MB end bound). It may be lazy GC or a per-Clip leak in the App (notices or Library). It needs a longer run, or a forced GC, to tell which. Handles, GDI and USER are flat for both processes.
- AC 1 is therefore **not met**. AC 2 (nothing left behind) and AC 3 (`serve --replay-buffer` without `--instance` → `invalid-arguments`, exit 2) are met.

## Plan Change Log

## Review Triage Log

## Design Notes

- Decision (unattended default): the epic's bounds use a 30-minute warm-up, longer than a 20-minute run. The warm-up defaults to min(30 min, duration / 4), so 5 minutes here; 8.8's 8-hour runs get the full 30.
- Decision (unattended default): the ticket's "within one frame of the Buffer Window" can't hold: a Clip starts at the first keyframe inside the window (AD-8, R2), so it is up to one GOP (≤ 1 s) short, as 4.14 measured (7.2–7.3 s of 8 s). The check is [window − 1 s − 1 frame, window + 1 frame], and each Track within one frame of the video.
- The brief asked for the soak against the synthetic GPU load; the epic's 8-hour runs drop the load. The tracer runs it at 80 fps target.
- A press that makes no Clip (`clip.failed`, or no `clip.saved` within 30 s) is a failed Clip row; an engine or App exit stops the run with a failing report and exit 1. Both are run-time behaviour, covered by the real soak, not default tests.
- Frames: each Clip row also records its frame count against length × fps as information (dropped frames), not a bound.

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings
- `dotnet test --project tests/ReplayBuffer.Bench.Tests -c Release --no-build` and `--project tests/ReplayBuffer.Engine.Tests` -- expected: pass
- `pwsh tools/publish-engine.ps1` -- expected: 0 IL warnings
- `bench/ReplayBuffer.Bench/bin/Release/net*/ReplayBuffer.Bench.exe soak --minutes 20 --mode memory` (holding `.bench-running`) -- expected: exit 0, passing report
