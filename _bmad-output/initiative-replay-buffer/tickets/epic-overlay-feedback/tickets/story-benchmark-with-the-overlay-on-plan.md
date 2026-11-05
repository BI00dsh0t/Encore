---
title: 'Benchmark with the Overlay on'
type: 'feature'
ticket: '12'
created: '2026-11-04'
status: done
baseline_revision: '039b4da22ed5bd1893522737df13193f227e3801'
route: 'full'
route_source: 'auto'
risk: 'medium'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
followup_review_recommended: false
context: ['{project-root}/CLAUDE.md']
warnings: []
deferred: []
---

<intent-contract>

## Intent

**Problem:** Nothing measures the real App's Overlay together with capture against NFR-1 (3 % FPS loss at 1080p60). Nothing measures how the 5.15 `overlayDot` modes cost FPS in each presentation mode.

**Approach:** Two additions to Bench.
- `bench fps --overlay` runs the real App on a throwaway test instance beside `tools/measure-path-a.ps1`. The App's dot and a message every 10 s are on during the capture runs only. Each run reports its dominant PresentMode.
- `bench overlay --host app --overlay-dot <mode>` sets the instance's `overlayDot`. The report also gives the overlay-plane answer for the load's monitor.

## Boundaries & Constraints

**Always:**
- Only a throwaway test instance: `AppOverlayHost`'s `ReplayBuffer-Test-bench-overlay-<hex>`, never the installed App (AD-16). Bench starts the App as a process and never compiles against it (AD-2).
- With `--overlay`:
  - Overlay state follows the script's stdout. Bench redirects it and still echoes every line.
  - A line `[<NN>-<condition>-<pass>] settling` turns the Overlay off when the condition is `baseline`, and on otherwise.
  - On is `dev.simulate recordingStarted` (the solid dot; no simulator event gives the hollow one), plus `clipSaved` every 10 s while on.
- `--overlay-dot auto|always|onChange` sets Global `overlayDot` on the instance, in the same `settings.update` that sets the load's profile Ignored. Default `auto`. It's valid only with `--overlay` (fps) or `--host app` (overlay).
- With `--game <name>`, the profile to set Ignored is the running process's exe path (`Process.MainModule.FileName`).
- The overlay-plane answer comes from `ReplayBuffer.Platform.Desktop`'s `Monitors.OverlayPlanes(pid)`. Bench gains a project reference to Platform.Desktop; that isn't the App.
- `fps.json` gains an optional `Overlay` record: dot mode, overlay-plane answer, message period. Each `FpsRun` gains an optional `PresentMode`, read with `PresentMonCsv.Analyze` from `<tag>-presentmon.csv` beside the stats file. `report.md`'s FPS section shows both.
- Cleanup on every path: `AppOverlayHost.Dispose`, and the message timer stopped.
- Lean tests. GPU runs are `[Fact(Explicit = true)]`, `[Trait("Category", "Heavy")]`.

**Never:**
- No change to `measure-path-a.ps1`, the App or the engine.
- No new `dev.simulate` event.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Overlay follows runs | script lines `[01-baseline-1] settling`, `[02-capture-1] settling` | Off, then On | an unparseable line changes nothing |
| Bad option | `--overlay-dot always` without `--overlay`; `--overlay-dot foo` | usage error naming the option | exit 2 |
| PresentMode per run | `02-capture-1-presentmon.csv` present | `FpsRun.PresentMode` is its dominant mode | missing CSV: null |

</intent-contract>

## Code Map

- `bench/ReplayBuffer.Bench/AppOverlayHost.cs` -- `Start(exe, ignoredExe, messages)` gains `OverlayDotMode? overlayDot`, which adds `SettingsOp.SetGlobal(Keys.OverlayDot.Name, Keys.OverlayDot.ToJson(mode))` to the existing ops. Add `public void Message() => Simulate(SimulatedEvent.ClipSaved)`.
- `bench/ReplayBuffer.Bench/FpsBenchmark.cs` -- `Run`:
  - Read `--overlay` (flag) and `--overlay-dot`.
  - Start `AppOverlayHost` after the load is ready, with the load exe or the game's path.
  - Redirect the script's stdout and parse it with a new `internal static bool? OverlayFor(string line)` (true for on, false for off, null for no change).
  - A `System.Threading.Timer` sends `Message()` every 10 s while on.
  - `Map`: per run, `PresentMonCsv.Analyze(Path.Combine(folder, tag + "-presentmon.csv"))` when the file exists → `PresentMode`.
  - `FpsReport` gains `FpsOverlay? Overlay = null`; `FpsRun` gains `string? PresentMode = null` (positional, last).
- `bench/ReplayBuffer.Bench/OverlayBenchmark.cs` -- `--overlay-dot` (host app only) passed to `AppOverlayHost.Start`. `OverlayReport` gains `string? OverlayDot = null` and per-load `bool? OverlayPlanes`, which goes in the markdown header line per load. Reuse `PresentMonCsv` unchanged.
- `bench/ReplayBuffer.Bench/BenchOptions.cs` -- usage text and allowed options: `fps` gets `overlay`, `overlay-dot`, `app-exe`; `overlay` gets `overlay-dot`. Add `overlay` to `FlagNames`.
- `bench/ReplayBuffer.Bench/Report.cs:94` (`Fps`) -- one line for the overlay record when present, and a PresentMode column in the runs table.
- `bench/ReplayBuffer.Bench/ReplayBuffer.Bench.csproj` -- ProjectReference to `src/ReplayBuffer.Platform.Desktop`.
- `tests/ReplayBuffer.Bench.Tests/OverlayBenchmarkTests.cs`, `OptionsTests.cs`, `ReportTests.cs` -- follow their patterns. `Map` is exercised by an existing test with a summary.json fixture; find it with grep "Map(".

## Tasks & Acceptance

**Execution:**
- [x] `bench/ReplayBuffer.Bench/ReplayBuffer.Bench.csproj` -- reference Platform.Desktop -- the overlay-plane answer.
- [x] `bench/ReplayBuffer.Bench/AppOverlayHost.cs` -- `overlayDot`, `Message()` -- shared by both commands.
- [x] `bench/ReplayBuffer.Bench/FpsBenchmark.cs` -- `--overlay`, `OverlayFor`, the message timer, PresentMode per run, `FpsOverlay`.
- [x] `bench/ReplayBuffer.Bench/OverlayBenchmark.cs` -- `--overlay-dot`, the planes answer per load.
- [x] `bench/ReplayBuffer.Bench/BenchOptions.cs`, `Report.cs` -- options, usage, report lines.
- [x] `tests/ReplayBuffer.Bench.Tests/*` -- `OverlayFor` on baseline, capture and other lines; the two usage errors; `Map` fills `PresentMode` from a CSV fixture; one Heavy, Explicit test `FpsWithTheOverlayOnRunsTheLoadWithTheAppsOverlay` (`--overlay --seconds 10 --passes 1 --settle 3`, asserting exit 0 and `Overlay` recorded).

**Acceptance Criteria:**
- Given the synthetic load, when `bench fps --overlay` runs, then `fps.json` has an FPS loss for the capture runs with the App's Overlay on. It also has each run's PresentMode, the dot mode and the overlay-plane answer, and `report.md` shows them.
- Given `bench overlay --host app --overlay-dot <mode>` for each of auto, always and onChange over borderless, fullscreen and exclusive, then `overlay.md` gives the PresentMode and FPS cost per presentation mode, the dot mode, and `SupportsOverlays` for the load's monitor.
- Given this PC with no overlay planes, then auto behaves like onChange in borderless. The plan's Verification records the numbers.

## Verification

- `dotnet build ReplayBuffer.slnx -c Release` -- 0 warnings.
- `dotnet test --project tests/ReplayBuffer.Bench.Tests -c Release --no-build` -- passes.
- Under `.bench-running` in the main folder:
  - `bench overlay --host app --dot-only --overlay-dot <mode> --modes borderless,fullscreen,exclusive` for each mode.
  - `bench fps --overlay --seconds 60 --passes 2`.
  - Record the results here and in the epic Notes.

**Results (2026-11-04, physical console, synthetic load 2560x1440 at 80 fps on the RTX 5090, PresentMon 2.6.0):**

- Build 0 warnings; Bench.Tests 135 passed, 10 explicit skipped.
- `SupportsOverlays` on the load's monitor: **no**, in every run.
- `bench overlay --host app --dot-only --overlay-dot <mode>` (15 s per phase, 5 s settle). PresentMode with the dot hidden / shown, and the FPS cost:

  | Dot mode | borderless | fullscreen | exclusive |
  |---|---|---|---|
  | auto | iflip / iflip, 0.19 %, dot not on screen | iflip / iflip, 0.31 % | Legacy Flip / Legacy Flip, 0.72 % |
  | always | iflip / iflip, no measurable cost: +3.63 % and −3.03 % in two runs, each with one phase where the load lost the foreground | iflip / iflip, −0.15 %, dot on screen | Legacy Flip / Legacy Flip, 0.27 % |
  | onChange | iflip / iflip, −0.16 %, dot not on screen | iflip / iflip, −0.11 % | Legacy Flip / Legacy Flip, 0.19 % |

  (iflip = Hardware Composed: Independent Flip.) With no overlay planes, auto behaves like onChange in borderless: the dot is gone by the time the shown phase is measured, and it costs nothing. In always, borderless stays iflip, and its two runs (+3.63 %, then −3.03 % in the re-run below) are noise around zero. Each run had one phase with the load out of the foreground; the first run's was the shown phase.
- `bench fps --overlay --seconds 60 --passes 2` (dot auto, a message every 10 s in the capture runs): FPS loss **5.80 %** (miss against 3 %), capture runs 6.04 % and 5.55 %, PresentMode iflip in every run, latency p99 28.7 ms.
- Control without `--overlay`, same settings, straight after: FPS loss **6.66 %** (6.62 % and 6.69 %). The Overlay adds no measurable cost. The NFR-1 miss is capture's own cost on this PC today, against 1.14's 1.47 %. That needs its own investigation, outside this story, which changes no capture code. The 6.7 % and 1.47 % aren't like-for-like: 1.14 ran 3 balanced passes of 5 min, and 5.12 ran 2 passes of 60 s plus one of 30 s. 1.14 itself saw about 3 % run-to-run variance at 120 s. The overlay-against-control comparison holds, since both used the same settings straight after each other. A 5 min x 3 re-run wasn't done here, so it is the first step of the capture-cost investigation.

**Human Check:** the 5.15 row (overlay planes back, research E1) covers the re-measure with planes present. Its 5.12 part: run `bench fps --overlay --game <GPU-bound game>` with the game in front.

## Review Triage Log

### 2026-11-04 — Review pass
- verdicts: 4 findings — high 0, medium 1, low 3, false 0, maybe-false 0
- findings:
  - `[medium]` `[patch]` An engine failure in `appHost.On()`/`Off()` escaped `Run` and left the pwsh script, its debug-capture engine and PresentMon running — fixed: `OverlaySwitch` catches and reports failures, the loop keeps reading, Run returns 1, and a `finally` kills the pwsh process tree if it is still running.
  - `[low]` `[patch]` A queued timer callback could send a saved notice after `Off()`, into a baseline settle — fixed: one lock and an on flag in `OverlaySwitch`, and the message is sent only while on.
  - `[low]` `[patch]` The Heavy fps test read `fps.json` before asserting the exit code, and checked only the dot — fixed: exit first, then dot, `messageSeconds` and `overlayPlanes`.
  - `[low]` `[patch]` `OptionsTests.cs:91` had a `\f` escape in its sample line — fixed: verbatim string.

## Auto Run Result

- **Summary:** `bench fps --overlay` runs the real App on a throwaway test instance beside `measure-path-a.ps1`.
  - The App's solid dot and a saved message every 10 s are on in the capture runs and off in the baseline runs.
  - Each run reports its dominant PresentMode, and `fps.json` and `report.md` record the dot mode and the overlay-plane answer.
  - `bench overlay --host app --overlay-dot <mode>` sets the instance's `overlayDot`, and `overlay.md` gives the mode and `SupportsOverlays` per load.
- **Files:**
  - `AppOverlayHost.cs`: `overlayDot`, `Message()`, serialized sends.
  - `FpsBenchmark.cs`: `--overlay`, `OverlayFor`, `OverlaySwitch`, PresentMode per run, `FpsOverlay`.
  - `OverlayBenchmark.cs`: `--overlay-dot`, planes per load.
  - `BenchOptions.cs`, `Report.cs`: options, usage, report lines.
  - `ReplayBuffer.Bench.csproj`: references Platform.Desktop.
  - Tests in `OptionsTests`, `ReportTests`, `OverlayBenchmarkTests`, including one Heavy, Explicit test.
- **Review:** quick lens, 4 findings, all patched (1 medium, 3 low); none deferred or rejected.
- **Follow-up review recommended:** false. One medium was patched and none were high.
- **Verification:** Release build 0 warnings; Bench.Tests 134 passed, 11 skipped. The GPU runs above were made before the review patches. The patched `--overlay` path still needs a GPU run, which happens when the GPU lock frees.
- **Residual risks:**
  - The `always`/borderless figure is noise. It was +3.63 % in the first run, which had a lost-focus shown phase, and −3.03 % in the re-run below.
  - NFR-1 misses for capture itself (6.66 % without the Overlay, against 1.14's 1.47 %). That is outside this story, which changes no capture code, and needs its own ticket. The 6.7 % and 1.47 % aren't like-for-like: 1.14 ran 3 balanced passes of 5 min, and 5.12 ran 2 passes of 60 s plus one of 30 s. 1.14 itself saw about 3 % run-to-run variance at 120 s. The overlay-against-control comparison holds, since both used the same settings straight after each other. A 5 min x 3 re-run wasn't done here, so it is the first step of the capture-cost investigation.

### 2026-11-04 01:40–01:44 — Re-runs after review (under `.bench-running`)
- **`always`, borderless, re-run** (`measurements/bench-5.12/overlay-always-borderless-rerun`): Hardware Composed: Independent Flip in all three phases. Cost −3.03 % (hidden 88.4, shown 91.08 fps). The load again lost the foreground in one phase, this time the first hidden one.
  - Together with the first run's +3.63 %, the `always` borderless figure is run-to-run noise around zero, not a cost.
  - In both 5.12 runs the dot kept the load in independent flip, even in the re-run's shown phase with the load in front. That is **unexplained** against 5.8's Composed: Flip at 20:12 on 2026-11-03, with the same "no planes" answer; focus does not account for it. The 5.15/5.12 Human Check row carries the cause.
- **Patched `fps --overlay`, 30 s, 1 pass** (`measurements/bench-5.12/fps-overlay-patched`): exit 0. The `overlay` record is `{dot: auto, overlayPlanes: false, messageSeconds: 10}`. PresentMode was Hardware Composed: Independent Flip in both runs (baseline 79.65, capture 74.34 fps). FPS loss 6.67 % (miss), the same as capture alone. No on, off or message failure was reported.

### Where the evidence is
- The raw reports and PresentMon CSVs aren't in the repo (`measurements/` is gitignored). They are in the worktree `%USERPROFILE%\Documents\repos\ReplayBuffer\.claude\worktrees\adoring-morse-f6f531\measurements\bench-5.12\`, one folder per run: `overlay-auto`, `overlay-always`, `overlay-onChange`, `fps-overlay`, `fps-control`, `overlay-always-borderless-rerun` and `fps-overlay-patched`.
- GPU lock: the first runs held `.bench-running` from 2026-11-04T00:51:47 until they finished (the file named "5.12 benchmark-overlay-on (adoring-morse-f6f531)"). The re-runs held it from 01:40:54 to 01:44:06, and their log is in this session's scratchpad. The file was gone after each, and another session's soak held it in between.

### 2026-11-04 — QA round 1 (FAIL, 2 doc fixes)
- Fixed: the `always`/borderless conclusion (noise, not a 3.6 % cost) in the Results and the epic Decision.
- Fixed: the NFR-1 comparison caveat (run lengths and passes differ from 1.14; no 5 min x 3 re-run).
- Noted: the 5.8 contradiction is worded as unexplained; the evidence location and lock times are recorded above.

**Correction (bugfix `bench-videoencode-count`, 2026-11-04):** `measure-path-a.ps1` summed the three identical `engtype_VideoEncode` counter instances each process gets on this GPU, so every `gpu_videoencode` figure was **3× over-counted**. This plan quotes no video-encode figure, but the `gpu_videoencode_meanPercent` column in its `bench fps --overlay` summaries is 3× too high. FPS loss, 1 % lows, engine CPU and 3D use are unaffected (the sampler only reads counters, and 3D has one instance), so the overlay's NFR-1 result stands.
