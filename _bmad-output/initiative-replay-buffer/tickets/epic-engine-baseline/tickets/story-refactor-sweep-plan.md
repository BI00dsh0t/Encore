---
title: 'Refactor sweep'
type: 'refactor'
ticket: '15'
created: '2026-11-02'
status: 'built'
baseline_revision: '633130beaa27511d96b215883947dbb09c08c166'
route: 'full'
route_source: 'auto'
risk: 'low'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
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
- [x] `tests/ReplayBuffer.Platform.FFmpeg.Tests/RealTimeAttempts.cs` -- new public static helper: runs an attempt delegate (given the attempt number) up to 3 times; it catches only assertion failures, logs each failed attempt's message to the test output, and rethrows the last failure -- one place for the retry rule, shared like `EncoderOracle`.
- [x] `tests/ReplayBuffer.Engine.Tests/ReplayBuffer.Engine.Tests.csproj` -- link `RealTimeAttempts.cs` beside `EncoderOracle.cs`.
- [x] `tests/ReplayBuffer.Platform.FFmpeg.Tests/EncodeExperimentTests.cs` -- wrap the paced-encoder test's measurement and asserts in the helper, with a per-attempt file name; update the comment on the p50 bound.
- [x] `tests/ReplayBuffer.Engine.Tests/CaptureEndTests.cs` -- the stats-report test: open the window once, then the engine run and asserts per attempt with per-attempt files.
- [x] `tests/ReplayBuffer.Engine.Tests/OutputResolutionTests.cs` -- the fast-window stats test: same pattern.
- [x] `tools/measure-path-a.ps1` -- before every setup check (elevation, PresentMon, engine, game), stop leftover `ReplayBuffer-PathA-<pid>` sessions (from `logman query -ets`) whose pid has no process; after a PresentMon kill or a failed run, and in the run loop's `finally`, stop this run's own session if it is running (best effort, errors ignored). Mention the sweep in the help text.
- [x] `tests/ReplayBuffer.Platform.FFmpeg.Tests/RealTimeAttemptsTests.cs` -- new: the helper's matrix rows without a GPU -- passes on a later attempt and logs the earlier failure; fails with the last message after 3; a skip thrown by the attempt is a skip after 1 call.
- [x] `tests/ReplayBuffer.Bench.Tests/MeasureScriptSessionTests.cs` -- new: the stale and live rows against the real script. Start sessions with `logman start <name> -p {<random guid>} -ets` (skip the test when that is refused), one named for a pid with no process and one for the test host's own pid; run `pwsh -NoProfile -File tools/measure-path-a.ps1 -Game NoSuchGame -PresentMon <missing path>`; assert exit 1, the dead one is stopped and named in the output, the live one still runs. Always stop both sessions in cleanup. Join `BenchProcessCollection` if the folder's other process tests do.
- [x] `_bmad-output/deferred-work.md` -- record each epic 1 item's outcome (see Design Notes).

**Acceptance Criteria:**
- Given the full suite runs with `dotnet test --solution ReplayBuffer.slnx`, when one of the three timing tests misses a bound on an attempt under contention, then it passes on a later attempt, and the output shows the attempt that failed and why.
- Given a test body whose bound is broken on every attempt (checked once by temporarily breaking a bound locally, not committed), when the test runs, then it fails after 3 attempts with the bound's message.
- Given the sweep, when `pwsh tools/publish-engine.ps1` runs, then it succeeds with 0 IL2xxx/IL3xxx warnings, and a 2-minute `bench drift --minutes 2` completes.

## Implementation Notes

- xunit v3's `SkipException` implements `IAssertionException`, so `RealTimeAttempts` excludes it explicitly; a skip propagates after one call.
- `Assert.Throws<SkipException>` lets a skip through to the runner (the test reports skipped), so `RealTimeAttemptsTests` catches the skip by hand.
- An attempt whose engine does not stop in time kills it before failing, so it never runs beside the next attempt.
- The script's sweep runs before the first setup check (elevation); a session for a running pid other than the script's own is left with a warning, the script's own pid's session is treated as stale.
- Verified: Release and Debug builds 0 warnings; full suite 666 passed, 0 skipped (the paced-encoder test logged one contended attempt, p50 18.6 ms, and passed on attempt 2); a locally broken p50 bound failed after 3 logged attempts (reverted); `publish-engine.ps1` 0 IL2xxx/IL3xxx; `drift --minutes 2` passed with `drift.json` and `report.md`.

## Plan Change Log

## Review Triage Log

### 2026-11-02 — Review pass
- verdicts: 4 findings — high 0, medium 1, low 3, false 0, maybe-false 0
- findings:
  - `[medium]` `[patch]` Checkpoint commit `1b52395` is typed `test(engine)` but also changes `tools/measure-path-a.ps1` and `_bmad-output/deferred-work.md`, against CLAUDE.md's "`test` — tests only" — confirmed. Fix: the unpushed, un-QA'd checkpoint is split into `test(engine)` (the timing tests and their helper), `fix(tools)` (the sweep and its Bench test) and `docs(tickets)` (deferred-work outcomes and this plan).
  - `[low]` `[patch]` `MeasureScriptSessionTests.DeadPid`'s comment claims an odd pid never runs, but `OpenProcess` ignores a pid's low two bits — confirmed; the loop still copes and agrees with the script's lookup. Fix: comment corrected.
  - `[low]` `[patch]` `Get-PathASessions` hides a failed `logman query -ets`, so the sweep says nothing when it couldn't look — confirmed (no `$LASTEXITCODE` check). Fix: a failed query is a warning naming logman's output, and the sweep is skipped.
  - `[low]` `[patch]` A timed-out engine is killed but not waited for, so it can still be exiting when the next attempt starts — confirmed (`Process.Kill` doesn't wait). Fix: `WaitForExit()` after the kill in both tests.
- Patches applied in the orchestrating session (each a one- to five-line change), not by the implementation subagent.


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

## Auto Run Result

**2026-11-02, bmad-build-auto (full route, quick review).**

- **Summary:** The three real-time timing tests repeat their measurement up to 3 times and pass on the first attempt that meets every bound (`RealTimeAttempts`), logging each failed attempt; bounds unchanged, skips not retried. `tools/measure-path-a.ps1` stops leftover `ReplayBuffer-PathA-<pid>` ETW sessions whose process is gone before its setup checks, and its own session on every exit path. Every epic 1 item in `deferred-work.md` has an outcome. No `src/` change.
- **Files:**
  - `tests/ReplayBuffer.Platform.FFmpeg.Tests/RealTimeAttempts.cs` (new): the retry rule, linked into the Engine tests.
  - `tests/ReplayBuffer.Platform.FFmpeg.Tests/RealTimeAttemptsTests.cs` (new): the helper's matrix rows without a GPU.
  - `EncodeExperimentTests.cs`, `CaptureEndTests.cs`, `OutputResolutionTests.cs`: the three timing tests, per-attempt files.
  - `tests/ReplayBuffer.Bench.Tests/MeasureScriptSessionTests.cs` (new): the sweep against the real script and real ETW sessions.
  - `tools/measure-path-a.ps1`: the sweep and own-session stop.
  - `_bmad-output/deferred-work.md`: outcomes.
- **Review:** 4 findings, all patched (1 medium, 3 low), 0 deferred, 0 rejected. Follow-up review recommended: false (no high, one medium).
- **Verification** (after merging `develop` with 2.1, `2475b9e`):
  - `dotnet build ReplayBuffer.slnx -c Release`: 0 warnings, 0 errors.
  - `dotnet test --solution ReplayBuffer.slnx`: 700 of 701. The failure is `AudioStampStatsTests.RecordingDoesNotAllocate` (1,560 B allocated on the thread against 0), an epic 1 test this branch doesn't touch; it passed 3 of 3 reruns of Media.Tests alone. None of the three timing tests failed.
  - `pwsh tools/publish-engine.ps1`: exit 0, 0 IL2xxx/IL3xxx warnings, 9.6 MB.
  - `bench drift --minutes 2` (NativeAOT engine, default devices): completed; A/V drift 0.759 ms (pass), flashes and bursts 120/120, audio-to-audio 54.3 samples (pass). Logs in `measurements/verify-1.15/` (gitignored).
  - Implementer's run before the merge: suite 666/666, with the paced-encoder test passing on attempt 2 after a contended p50 of 18.6 ms; a locally broken bound failed after 3 logged attempts.
- **Residual risks:**
  - `RecordingDoesNotAllocate` is a newly seen one-off flake (likely tiered JIT allocating on the test thread in Debug). Not fixed here: the cause is unconfirmed. A candidate for epic 2's or 8's sweep if it recurs.
  - The sweep parses English `logman` output and needs the right to query ETW sessions; a failed query is now a warning. The own-session stop after a killed PresentMon is not exercised by a test.

### QA round 1 (2026-11-02)

- **[medium] Retry covered correctness assertions.** Fixed (`bfdc7e1`): `RealTimeAttempts.Run` retries only `RealTimeBoundException`, thrown by `RealTimeAttempts.Bound`/`InRange`. The three tests check their correctness assertions first (exit code, pacer fault, counts, PTS, audio) with plain `Assert`, failing on any attempt, and only the timing bounds (latency p50/p99, queue drops, skipped slots, the slot ranges, flushed, fresh share, steady GC and B/s, and a full queue or empty pool while posting) through the helpers. Two new rule tests: a missed range names the value and is retried; a correctness assertion is not.
- **[low] Concurrent ETW test runs.** Fixed (`5164e61`): the test holds `Global\ReplayBuffer-MeasureScriptSessionTests` from the first session started to the last assert, and the dead pid starts at a random multiple of 4.
- **Note: owners of open deferred items.** Done (`d9726bc`): story 4.2's and epic 8's refactor sweep's descriptions name their epic 1 items.
- **Verification:** Release build 0 warnings; suite 702/703 — the failure, App `ASupervisedEngineThatDiesIsRelaunchedAndTheNewEngineRaisesEngineRestarted` (untouched), passed on one rerun alone. Publish and drift not rerun: the round changes tests and tickets only.
