---
title: 'Refactor sweep'
type: 'refactor'
ticket: '15'
created: '2026-11-02'
status: 'in-progress'
baseline_revision: '633130beaa27511d96b215883947dbb09c08c166'
route: 'full'
route_source: 'auto'
risk: 'low'
review: ''
review_source: ''
lenses_ran: []
review_loop_iteration: 0
followup_review_recommended: false
context: []
warnings: []
deferred: []
---

<intent-contract>

## Intent

**Problem:** Epic 1 left cleanup behind. Three real-time timing tests fail now and then when `dotnet test --solution` runs the test assemblies in parallel, so every story has had to rerun them by hand (1.14's accepted-flake list). A killed `tools/measure-path-a.ps1` leaves its `ReplayBuffer-PathA-<pid>` ETW session running, and later PresentMon runs then lose events and write no CSV (1.14's QA round 1). Four items in `_bmad-output/deferred-work.md` have no recorded outcome.

**Approach:**
- Each of the three timing tests repeats its measurement up to 3 times and passes on the first attempt that meets every bound, logging each failed attempt. A real regression still fails all three.
- `measure-path-a.ps1` stops leftover `ReplayBuffer-PathA-<pid>` sessions whose process is gone before it starts, and stops its own session on every exit path.
- Each deferred item is closed or recorded with its owner.

## Boundaries & Constraints

**Always:**
- The bounds the three tests assert stay exactly as they are; only the number of attempts changes.
- A skipped test (FFmpeg not fetched, no GPU) is still a skip, never retried into a failure.
- Each attempt writes its own output files, so a failed attempt's file can't satisfy a later attempt's checks.
- The script only stops sessions named `ReplayBuffer-PathA-<pid>`, and only those whose pid has no running process, or its own.

**Never:**
- Code under `src/` changes. Epic 1's product code has no deferred cleanup that belongs here, and 2.1 is merging audio and track changes into `develop` at the same time.
- Loosen a bound, mark a test skipped, or exclude it from the suite.
- Retry tests other than the three named ones.
- Change Windows settings.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Contended first attempt | attempt 1 misses a timing bound, attempt 2 meets every bound | test passes; attempt 1's failure is in the test output | none |
| Real regression | every attempt misses a bound | test fails with the last attempt's assertion message; earlier attempts are logged | none |
| Not runnable | FFmpeg not fetched | test is skipped, once | the skip propagates, no retry |
| Stale ETW session | `ReplayBuffer-PathA-<pid>` running, no process with that pid | the script stops it with `logman stop <name> -ets` and says so, then runs as before | a failed stop is a warning, not a failure |
| Live run's session | `ReplayBuffer-PathA-<pid>` whose pid is running | left alone, with a warning | none |
| Run killed outright | the script is killed mid-run, so its own session survives | the next run's sweep stops it (the stale row) | as above |

</intent-contract>

## Code Map

- `tests/ReplayBuffer.Platform.FFmpeg.Tests/EncodeExperimentTests.cs` -- `ThePacedEncoderEncodesEverySlotWithAFastPreset` (line ~95): builds the encoder stack, paces 1 s of slots, asserts counts, latency p50 < 1 frame, uniform PTS via `Ffprobe(... thread.mp4)` (the probe runs in `_directory`). The output path is hard-coded `thread.mp4`; make it per attempt.
- `tests/ReplayBuffer.Engine.Tests/CaptureEndTests.cs` -- `AStatsRunWritesAReportThatMatchesTheFile` (line ~136): runs the engine 12 s with `--stats` against `OpenWindow(closeAfterMs: 0)`; asserts 0 queue drops, 0 skipped slots, p99 ≤ 250 ms and stream counts. The window can be shared between attempts; `stats.mp4`/`stats.json` become per attempt.
- `tests/ReplayBuffer.Engine.Tests/OutputResolutionTests.cs` -- `AStatsRunOnAFastWindowMeetsTheLatencyGcAndAllocationBounds` (line ~198): 20 s `debug-capture` with `--stats`; asserts p99 < 1 frame, 0 GCs, < 2048 B/s, ≥ 95 % fresh. Same pattern: share the window, per-attempt files. The engine process goes in `_processes` for cleanup.
- `tests/ReplayBuffer.Platform.FFmpeg.Tests/EncoderOracle.cs` and `tests/ReplayBuffer.Engine.Tests/ReplayBuffer.Engine.Tests.csproj` -- the existing pattern for a helper shared by both test projects: the file lives in the FFmpeg tests (namespace `ReplayBuffer.Platform.FFmpeg.Tests`, `public static class`) and the Engine tests link it with `<Compile Include=... Link=...>`.
- xunit v3 4.0.2: assertion failures implement `Xunit.Sdk.IAssertionException`; the test output is `TestContext.Current.TestOutputHelper`. Confirm that a skip (`Assert.SkipUnless`) is not caught as a failure.
- `tools/measure-path-a.ps1` -- PresentMon starts at line ~365 with `--session_name "ReplayBuffer-PathA-$PID"` and `--stop_existing_session` (which only clears a session of the same name, so a dead run's differently-named session survives). `Invoke-Run`'s `catch` (line ~398) kills the engine and PresentMon; the timeout path (line ~370) kills PresentMon. The setup checks (elevation line ~155, then PresentMon, engine, game, to line ~215) end with `Fail` (exit 1). The run loop's `finally` (line ~451) resets the execution state.
- `_bmad-output/deferred-work.md` -- four entries from epic 1, with no outcome recorded.

## Tasks & Acceptance

**Execution:**
- [ ] `tests/ReplayBuffer.Platform.FFmpeg.Tests/RealTimeAttempts.cs` -- new public static helper: runs an attempt delegate (given the attempt number) up to 3 times; it catches only assertion failures, logs each failed attempt's message to the test output, and rethrows the last failure -- one place for the retry rule, shared like `EncoderOracle`.
- [ ] `tests/ReplayBuffer.Engine.Tests/ReplayBuffer.Engine.Tests.csproj` -- link `RealTimeAttempts.cs` beside `EncoderOracle.cs`.
- [ ] `tests/ReplayBuffer.Platform.FFmpeg.Tests/EncodeExperimentTests.cs` -- wrap the paced-encoder test's measurement and asserts in the helper, with a per-attempt file name; update the comment on the p50 bound.
- [ ] `tests/ReplayBuffer.Engine.Tests/CaptureEndTests.cs` -- the stats-report test: open the window once, then the engine run and asserts per attempt with per-attempt files.
- [ ] `tests/ReplayBuffer.Engine.Tests/OutputResolutionTests.cs` -- the fast-window stats test: same pattern.
- [ ] `tools/measure-path-a.ps1` -- before every setup check (elevation, PresentMon, engine, game), stop leftover `ReplayBuffer-PathA-<pid>` sessions (from `logman query -ets`) whose pid has no process; after a PresentMon kill or a failed run, and in the run loop's `finally`, stop this run's own session if it is running (best effort, errors ignored). Mention the sweep in the help text.
- [ ] `tests/ReplayBuffer.Platform.FFmpeg.Tests/RealTimeAttemptsTests.cs` -- new: the helper's matrix rows without a GPU -- passes on a later attempt and logs the earlier failure; fails with the last message after 3; a skip thrown by the attempt is a skip after 1 call.
- [ ] `tests/ReplayBuffer.Bench.Tests/MeasureScriptSessionTests.cs` -- new: the stale and live rows against the real script. Start sessions with `logman start <name> -p {<random guid>} -ets` (skip the test when that is refused), one named for a pid with no process and one for the test host's own pid; run `pwsh -NoProfile -File tools/measure-path-a.ps1 -Game NoSuchGame -PresentMon <missing path>`; assert exit 1, the dead one is stopped and named in the output, the live one still runs. Always stop both sessions in cleanup. Join `BenchProcessCollection` if the folder's other process tests do.
- [ ] `_bmad-output/deferred-work.md` -- record each epic 1 item's outcome (see Design Notes).

**Acceptance Criteria:**
- Given the full suite runs with `dotnet test --solution ReplayBuffer.slnx`, when one of the three timing tests misses a bound on an attempt under contention, then it passes on a later attempt, and the output shows the attempt that failed and why.
- Given a test body whose bound is broken on every attempt (checked once by temporarily breaking a bound locally, not committed), when the test runs, then it fails after 3 attempts with the bound's message.
- Given the sweep, when `pwsh tools/publish-engine.ps1` runs, then it succeeds with 0 IL2xxx/IL3xxx warnings, and a 2-minute `bench drift --minutes 2` completes.

## Implementation Notes

## Plan Change Log

## Review Triage Log

## Design Notes

**Why retry, not serialize:** the contention comes from GPU tests in other assemblies (FFmpeg, Capture, Engine, Bench) that `dotnet test --solution` runs as parallel processes; a collection only serializes within one assembly, and a cross-process lock taken by these three tests wouldn't stop the others loading the GPU. The bounds are about the code, not the neighbours: one clean attempt in three is the evidence the stories asked for when they reran alone.

**The deferred items (ticket verify: closed or recorded):**
- The publish folder carrying `ffmpeg.exe`, `ffplay.exe` and the PDBs: closed by story 1.13. `tools/publish.ps1` composes `publish/ReplayBuffer/` with only the two exes and the FFmpeg DLLs and fails on any other exe or a PDB; `publish/engine/` keeps the CLIs for the tests.
- The serve path's `OutputNaming.Allocate` instead of AD-21's `OutputPathAllocator`: recorded, owned by story 3.5 (`OutputPathAllocator`), the first story that writes files from serve.
- The encoder probe feeding BGRA where QSV/AMF may want NV12: recorded, still open. It needs an Intel or AMD GPU, which the builder's PC lacks; epic 8's hardening is the place for it.
- The ≈ 50 ms of silence at each track's start: recorded, owned by epic 4's exact save cut (4.2). It changes `SessionAudioTrack`, which 2.1 is reworking now.

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings, 0 errors.
- `dotnet test --solution ReplayBuffer.slnx` -- expected: all pass.
- `pwsh tools/publish-engine.ps1` -- expected: 0 IL2xxx/IL3xxx warnings.
- `dotnet run --project bench/ReplayBuffer.Bench -c Release -- drift --minutes 2 --out <scratch folder>` -- expected: completes with `drift.json` and `report.md`.

**Manual checks (if no CLI):**
- `logman start ReplayBuffer-PathA-999999 -p Microsoft-Windows-Kernel-Process -ets`, then `pwsh tools/measure-path-a.ps1 -Game NoSuchGame`: the output names the stopped session, and `logman query -ets` no longer lists it.
