---
title: 'Engine crash recovery and the restart warnings'
type: 'feature'
ticket: '2'
created: '2026-11-04'
status: 'built'
baseline_revision: '4390a0e6adab46146917f7229c6a8af52b186a87'
route: 'full'
route_source: 'auto'
risk: 'high'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
followup_review_recommended: true
context: ['{project-root}/CLAUDE.md']
warnings: ['oversized']
deferred: []
---

<intent-contract>

## Intent

**Problem:** Story 1.8's mutual supervision relaunches a dead engine or App forever, the engine learns it was restarted only from `app.attach` (after its first snapshot), a crash loop has no stop or tray state, a hang can be simulated only by the test-only `debug.hang`, and a test instance's relaunched engine or App loses `--replay-buffer`, so 8.1's soak can't kill either process and keep saving.

**Approach:** The App relaunches with `serve … --restarted crashed|hung`, and the engine raises `engine-restarted` before its pipe opens. A shared crash-loop guard (3 restarts within 10 min) stops the App relaunching the engine (`engine-crash-loop`, a tray error state with a Restart item) and the engine relaunching the App (`app-crash-loop`). `dev.simulate engine-hang` freezes the engine. `--replay-buffer` passes through both relaunches on test instances. `bench soak` gains `--kill-engine`, `--kill-app` and `--hang-engine`, which check the recovery.

## Boundaries & Constraints

**Always:** Contracts change additively only (AD-22): new `ErrorCodes` (`EngineCrashLoop`, `AppCrashLoop`, added to `All`), `Supervision` constants and a guard class beside `RestartBackoff`, a new `SimulatedEvent.EngineHang` at the end of the enum. `AppAttach.EngineRestart` stays and still works, but a second `engine-restarted` is never raised while one is active. `dev.simulate` stays refused by the installed instance (`not-dev-instance`). Kills in Bench and tests target only processes of their own throwaway `ReplayBuffer-Test-…` instance: the engine by its pipe's server pid (image = the expected engine exe), the App by the snapshot's `AppPid` (image = the expected App exe); never by name. Real-process kill tests that need capture or long waits are `[Fact(Explicit = true)]` + `[Trait("Category","Heavy")]`. Bench soak runs take the `.bench-running` lock (8.1's `BenchLock`).

**Never:** Resuming a Recording after a restart (only the Replay Buffer resumes; 6.2's `recording-interrupted` path stays as is). Touching the installed or Dev instance's processes. Changing the backoff delays. New Recording or Cleanup soak options (8.8).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Crash, first | engine killed | App relaunches `serve --restarted crashed`; snapshot has one `engine-restarted` ("unexpectedly") | — |
| Hang | `dev.simulate engine-hang` | heartbeat stops, hub frozen; App kills it after ≤ 6 s silence and relaunches with `--restarted hung` ("stopped answering") | — |
| Crash loop | 4th engine crash within 10 min of the first 3 restarts | not relaunched; status `CrashLoop`; tray error icon and visible Restart item; `engine-crash-loop` line in the App log | Restart clears the guard and relaunches |
| Crash after 10 min | 3 restarts, then a crash 11 min after the first | relaunched (the window slides) | — |
| App crash loop | 4th App exit within 10 min | engine doesn't relaunch; logs and raises `app-crash-loop` (error, not overlay) | a new App attaching clears the guard |
| Installed instance | `dev.simulate engine-hang` on `ReplayBuffer` | refused `not-dev-instance`, nothing freezes | — |
| `--restarted` bad value | `serve --restarted maybe` | `invalid-arguments`, exit 2 | — |
| `--replay-buffer` on App | non-test instance | refused, exit 2 (as `--instance` malformed) | — |

</intent-contract>

## Code Map

- `src/ReplayBuffer.Contracts/Supervision.cs` -- add `CrashLoopRestarts = 3`, `CrashLoopWindow = 10 min`, and `CrashLoopGuard(TimeProvider)`: `bool TryRestart()` (records a restart and returns true while fewer than 3 fall in the window before now; false = crash loop, records nothing), `Reset()`. Not thread-safe, like `RestartBackoff`.
- `src/ReplayBuffer.Contracts/ErrorCodes.cs` -- `EngineCrashLoop = "engine-crash-loop"`, `AppCrashLoop = "app-crash-loop"` beside `EngineRestarted`/`AppRestarted` (line ~84) and in `All` (~278).
- `src/ReplayBuffer.Contracts/Messages/Dev.cs` -- `SimulatedEvent.EngineHang` (last).
- `src/ReplayBuffer.Engine/CommandLine.cs` -- `ServeCommand` gains `EngineRestartReason? Restarted`; `ParseServe` (~379) takes `--restarted crashed|hung` once, any instance; Usage text.
- `src/ReplayBuffer.Engine/Program.cs` -- `Serve` (~110) passes `Restarted` into `EngineHostOptions`.
- `src/ReplayBuffer.Engine/EngineHost.cs` -- `EngineHostOptions.Restarted`; in `Start`, raise `engine-restarted` with the warnings block before `server.Start` (~603). Pass `--replay-buffer` to the App launcher when `instance.IsTest && replay` (`AppLauncher.Beside`, ~291). `dev.simulate engine-hang`: give `DevSimulator` a `hang` action = cancel `_stopHeartbeat` + `hub.Freeze()`. Existing test-only `debug.hang` stays.
- `src/ReplayBuffer.Engine/Ipc/EventHub.cs` -- `Freeze()`: a background thread enters `_lock` and never leaves (dev only: every publish, snapshot and attach then blocks, like a real hang).
- `src/ReplayBuffer.Engine/Sessions/DevSimulator.cs` -- new `Action hang` parameter; `EngineHang` case after the installed check.
- `src/ReplayBuffer.Engine/Supervision/AppSupervisor.cs` -- a shared `RaiseEngineRestarted(EventHub, Warnings, reason)` that skips when an `engine-restarted` warning is active, used by `Attach` and `EngineHost.Start`. A `CrashLoopGuard`: in `WatchAsync`, before each relaunch, `TryRestart()`; false → log error with `app-crash-loop`, raise warning `AppCrashLoop` (Error, overlay false), stop watching. A later `Attach` of a different pid after a crash-loop stop calls `Reset()`. `AppLauncher` gets extra arguments (`--replay-buffer`).
- `src/ReplayBuffer.App/Engine/EngineConnection.cs` -- `EngineStatus.CrashLoop`; `TryLaunch(restart)` appends `--restarted crashed|hung`; every relaunch after a crash or hang in `SuperviseAsync` (Unanswered, Failed-with-crashed-live, hung watch, non-zero exit) goes through one helper that asks the guard first; refused → `_problem` names `engine-crash-loop`, status `CrashLoop`, `options.Log?.Invoke(...)`, then waits for `RestartAfterCrashLoop()` (public; resets guard and backoff, relaunches with no restart reason). `EngineConnectionOptions.Log` (Action<string>?).
- `src/ReplayBuffer.App/ShellApp.cs` -- parse `--replay-buffer` (test instance only, else `Program` returns 2) into `EngineArguments`; `Log` writes a timestamped line to `<instance.LocalDataFolder>\logs\app.log`; tray: `CreateTrayMenu` gains an optional `restart` action and a hidden "Restart" item (name it `RestartItem` or tag it) shown only in `CrashLoop`; `SyncTray` also tracks the crash-loop state.
- `src/ReplayBuffer.App/Program.cs` -- `--replay-buffer` validity (with `TryParseInstance`).
- `src/ReplayBuffer.App/TrayCapture.cs` -- an error icon ("!" on amber/dark red) and tooltip "The engine keeps crashing; Restart from this menu" for `CrashLoop`.
- `src/ReplayBuffer.App/Status/StatusLine.cs` -- `ConnectionText` for `CrashLoop`.
- `bench/ReplayBuffer.Bench/SoakSchedule.cs` -- optional one-shot events `(TimeSpan At, Action Run)` fired once when due.
- `bench/ReplayBuffer.Bench/SoakRun.cs` -- options `--kill-engine <min>`, `--kill-app <min>`, `--hang-engine <min>` (minutes into the loop). Start the App with `--replay-buffer`. Recovery check per event, recorded as `SoakRecovery(Kind, AtSeconds, Seconds, Pass, Detail)` in `soak.json`/`soak.md`: engine kill/hang → reconnect the client (new `OnEvent` wiring), Buffering on the load's profile and an active `engine-restarted` within 10 s (kill) / 20 s (hang); App kill → one extra Save press while the App is gone must save a passing Clip, then a new `AppPid` and `app-restarted` within 10 s. Sampling and `Stop()` follow the new processes; bounds use each process's first pid only (8.8 kills late). Verdict also needs every recovery to pass and rows = presses + extra presses. `BenchOptions.cs`: `Allowed`/`Usage`.
- Reuse: `AppOverlayHost.Canonicalize`, `HotkeyBenchmark.StartServe`, `KeyInjector.PressOnce`, `EngineClient.ConnectAsync`, `PipeServer.ProcessId`-style server pid lookup (Bench has `AppOverlayHost.Connect`/`GetNamedPipeServerProcessId`).
- Tests to extend: `tests/ReplayBuffer.Contracts.Tests/SupervisionTests.cs` (has `ManualTime`), `tests/ReplayBuffer.Engine.Tests/AppSupervisorTests.cs` (`FakeLauncher`/`FakeProcess`), `CommandLineTests.cs`, a DevSimulator test file if one exists (grep `DevSimulator`), `tests/ReplayBuffer.App.Tests/SupervisionTests.cs` (real pair; `AKilledAppIsRelaunchedByTheEngine` is a known flake), `TrayTests.cs`, `tests/ReplayBuffer.Bench.Tests/SoakTests.cs`.

## Tasks & Acceptance

**Execution:**
- [x] `src/ReplayBuffer.Contracts/*` -- guard, constants, codes, `EngineHang` -- one rule for both supervisors.
- [x] `src/ReplayBuffer.Engine/*` -- `--restarted`, early warning, dedupe, hang, App crash loop, `--replay-buffer` to the App.
- [x] `src/ReplayBuffer.App/*` -- `--restarted` on relaunch, crash-loop status, tray error state and Restart, App log, `--replay-buffer` pass-through.
- [x] `bench/ReplayBuffer.Bench/*` -- kill/hang options and recovery checks.
- [x] Tests (default, fast): guard (3 in window allowed, 4th refused, allowed again after the window slides, `Reset`); `serve --restarted hung` parses, a bad value fails; an engine started with `Restarted = Crashed` has `engine-restarted` in the first snapshot and an attach with a reason adds no second one; `AppSupervisor` with fakes: 4th App exit within the window isn't relaunched and `app-crash-loop` is raised; `dev.simulate engine-hang` refused on the installed instance; the tray's Restart item is hidden by default; schedule fires a one-shot event once.
- [x] `tests/ReplayBuffer.App.Tests/EngineConnectionTests.cs` (Heavy, explicit, real engine on a test instance): a supervising `EngineConnection`; kill its engine (the `State.Pid` it connected to) 4 times, each after it is back → 3 relaunches, then `Status == CrashLoop`, the log action got an `engine-crash-loop` line and no engine serves the pipe; `RestartAfterCrashLoop()` → `Connected`. `tests/ReplayBuffer.App.Tests/SupervisionTests.cs` (Heavy): `dev.simulate engine-hang` on the pair → a new engine within 20 s with "answering".
- [x] Run `bench soak --minutes 6 --kill-engine 2 --kill-app 3 --hang-engine 4` holding `.bench-running` when free; record the result here.

**Acceptance Criteria:**
- Given the App supervising an engine, when the engine is killed, then a new engine serves within 10 s and its first snapshot carries one dismissable `engine-restarted`, and the status line shows it.
- Given `dev.simulate engine-hang` on a test instance, then the engine is relaunched within 20 s with an `engine-restarted` that says it stopped answering.
- Given 3 engine restarts within 10 minutes, when it crashes a fourth time, then it isn't relaunched, the tray shows the error state with Restart, and `engine-crash-loop` is in the App log; Restart brings it back.
- Given the engine relaunched the App 3 times within 10 minutes, when the App exits a fourth time, then it isn't relaunched and `app-crash-loop` is logged and raised.
- Given `bench soak` with `--kill-engine`, `--kill-app` and `--hang-engine` on the synthetic game, then after each the Replay Buffer is buffering again in time, a Save press with the App killed saves a passing Clip, the App returns with `app-restarted`, and the report records each recovery.
- Given a Recording running when the engine is killed, then its file plays to its last fragment and `recording-interrupted` is raised at the next start (6.2's `RecordingCrashTests` default test covers the engine start path this story reuses).

## Implementation Notes

- Contracts: `Supervision.CrashLoopRestarts`/`CrashLoopWindow` and `CrashLoopGuard` (a queue of restart times; one that is 10 min old has left the window), `ErrorCodes.EngineCrashLoop`/`AppCrashLoop` (in `All`), `SimulatedEvent.EngineHang` last (JSON `engineHang`).
- Engine: `serve --restarted crashed|hung` (`ServeCommand.Restarted`, `EngineHostOptions.Restarted`) raises `engine-restarted` before `server.Start`. `AppSupervisor.RaiseEngineRestarted` is the one raise, skipped while an `engine-restarted` is active, under a static lock. The App's crash-loop guard counts each exit once, however many failed starts its relaunch takes; refused → error log `app-crash-loop: …`, warning `AppCrashLoop` (Error, no overlay), watch ends; a later attach of another pid resets it. `AppLauncher` passes `--replay-buffer` after `--instance` when the test instance's engine buffers. `dev.simulate engineHang` cancels the heartbeat and calls `EventHub.Freeze()` (a background thread holds the hub lock forever; the reply still goes out, as replies skip the hub).
- App: `EngineStatus.CrashLoop`; every relaunch after a crash or hang goes through `RestartAsync`, which asks the guard, then waits out the backoff; refused → `_problem` names `engine-crash-loop`, `Log` gets a line, status `CrashLoop`, then waits for `RestartAfterCrashLoop()` (resets guard and backoff, relaunches with no reason). `TryLaunch` appends `--restarted crashed|hung`. The App still sends the reason on `app.attach`; the engine's dedupe drops it. `ShellApp`: `--replay-buffer` (test instance only, else `Program` exits 2) goes into `EngineArguments`; `logs\app.log` lines; the tray's hidden `RestartItem` (first in the menu) and the "!" on amber icon with tooltip "The engine keeps crashing; Restart from this menu" while `CrashLoop`. The status line shows the problem text in `CrashLoop`.
- Bench: `SoakSchedule` takes one-shot `(At, Run)` events, read when `Run` starts. `SoakRun` options `--kill-engine`/`--kill-app`/`--hang-engine` (minutes, 0..`--minutes`); the App starts with `--replay-buffer`; every client feeds one event channel and records the pipe's server pid; kills check the pid and the image first. Recoveries go to `soak.json` (`recoveries`, `extraPresses`) and a `soak.md` table; the verdict needs every recovery to pass and rows = presses + extra presses. Bounds use each process's first pid.
- Every press but the App-gone one waits until a whole Buffer Window has passed since the last save or engine restart (`bufferFull`); `RecoverApp` waits for it before the kill, then presses at once. Without it, a press delayed by a recovery made a short Clip with the next one (runs 1 and 2 below).

## Plan Change Log

- 2026-11-04, unattended default: the soak's Clip check allowed each Track one video frame (16.7 ms at 60 fps) off the video, but the audio ends on a whole AAC packet (1024 samples, 21.3 ms at 48 kHz), so a Clip of some frame counts always fails it: 1747 frames (29.117 s) against 1364 packets (29.099 s) is 18 ms. 8.1's 19-Clip runs never hit such a count (max 15 ms); three 8.2 runs did. `SoakClipCheck` now allows max(one frame, one AAC packet), with a test. Not an engine change.

## Review Triage Log

### 2026-11-04 — Review pass
- verdicts: 8 findings — high 1, medium 1, low 6, false 0, maybe-false 0
- findings:
  - `[high]` `[patch]` A relaunched engine that crashes before serving skipped the crash-loop guard (relaunched forever) — `ConnectOrLaunchAsync` records `_launchFailure` (Crashed/Hung) and `SuperviseAsync` routes it through `RestartAsync`; Heavy `AnEngineThatCrashesBeforeServingEndsInACrashLoop` added (passes; failed before).
  - `[low]` `[patch]` A soak event at exactly `--minutes` never fired but was required — `At` refuses a value ≥ `--minutes`.
  - `[medium]` `[patch]` `RecoverApp` passed a press made after the new App attached — `pass` requires `goneAtPress`.
  - `[low]` `[reject]` The App-back 10 s limit includes the Save press (≤ 30 s wait) — saves take ~1 s; measuring the return during the press needs extra event plumbing.
  - `[low]` `[patch]` The soak's hang check accepted any `engine-restarted` message — requires "answering" for hang, "unexpectedly" for kill.
  - `[low]` `[patch]` `RecoverEngine` leaked replaced clients and adopted before the image check — disposes them; adopts only after `HasImage`.
  - `[low]` `[patch]` `app-crash-loop` stayed active after a new App attached — its id is kept and withdrawn on reset; test extended.
  - `[low]` `[reject]` `TheFourthAppExitWithinTheWindowIsACrashLoopAndIsNotRelaunched` waits ~3 s of real backoff — short, and injecting time would change the shared fixture.
- Also patched before review (matrix audit): `ShellApp.TryParseArguments` and `ShowCrashLoop` with tests for the App's `--replay-buffer` refusal and the tray Restart item.

## Auto Run Result

- **Summary:** engine crash recovery (AD-16): `serve --restarted crashed|hung` raises `engine-restarted` before the pipe serves (deduped with `app.attach`); a shared `CrashLoopGuard` (3 restarts / 10 min) stops the App relaunching the engine (`EngineStatus.CrashLoop`, tray "!" with Restart, `logspp.log` line `engine-crash-loop`) and the engine relaunching the App (`app-crash-loop`); `dev.simulate engineHang` freezes the engine; `--replay-buffer` passes through relaunches on test instances; `bench soak --kill-engine/--kill-app/--hang-engine` checks each recovery.
- **Contracts (additive, AD-22):** `ErrorCodes.EngineCrashLoop`, `AppCrashLoop`; `Supervision.CrashLoopRestarts`, `CrashLoopWindow`, `CrashLoopGuard`; `SimulatedEvent.EngineHang` (last).
- **Review:** quick lens, 8 findings: 6 patched (1 high, 1 medium, 4 low), 2 low rejected (above), 0 deferred.
- **Follow-up review recommended:** true — a high was patched (startup-crash path into the guard); the risk is `SuperviseAsync`'s Failed branch counting a non-crash exit (e.g. an engine refused by the pipe mutex) as a crash.
- **Verification:** Release build 0 warnings; Contracts 120, Engine 174 (28 skipped), App 117 (14 skipped), Bench 145 (10 skipped) pass; `publish-engine.ps1` 0 IL warnings; Heavy supervision tests pass (agent runs); soak `--minutes 6 --kill-engine 2 --kill-app 3 --hang-engine 4` passed before the review patches (`measurements/bench-20261104-045326`); a rerun after the patches was started but not finished at commit time.
- **Residual risks:** tray error state never seen on a real desktop; `EventHub.Freeze()` can only be ended by a kill; the App's private-bytes climb (8.1) is unchanged.

## Design Notes

- Decision (unattended default): "3 restarts within 10 minutes" = the guard allows a relaunch while fewer than 3 relaunches happened in the last 10 minutes; the 4th crash in that span is refused. A hang counts as a crash.
- Decision (unattended default): Restart from the tray clears the guard and the backoff and starts the engine with no `--restarted` (the user asked for it). For the App, a manually started App attaching clears the engine's guard.
- Decision (unattended default): the App has no logger; `engine-crash-loop` goes to `logs\app.log` in the instance's local data folder, one line, and into the status line's problem text.
- Decision (unattended default): `--hang-engine` is added to the soak beside the ticket's two kill options, since the hang AC needs a driver.
- The ticket's "on start the engine restores mic mute, registers the Hotkeys, runs Game Detection, raises `recording-interrupted`, reconciles the Library" already happens in `EngineHost.Start` (stories 2.4, 1.8, 3.x, 6.2, 7.2); the soak's recovery checks prove Buffering resumes on the running Target Game.

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings
- `dotnet test --project tests/ReplayBuffer.Contracts.Tests -c Release --no-build`, same for `Engine.Tests`, `App.Tests`, `Bench.Tests` -- expected: pass
- `pwsh tools/publish-engine.ps1` -- expected: 0 IL warnings
- the Heavy supervision tests (`--explicit only`, App.Tests filtered to the new ones) and the soak above -- expected: pass

**Results (2026-11-04):**
- Release build: 0 warnings. `publish-engine.ps1`: published, 0 IL warnings.
- Default suites: Contracts 120/120; Engine 174 passed, 28 skipped; App 116 passed, 13 skipped; Bench 145 passed, 11 skipped (one earlier Bench run had a single failure that didn't recur in 4 reruns, unidentified, outside this story's tests).
- Heavy (holding `.bench-running`): `AFourthCrashWithinTenMinutesIsACrashLoopUntilRestart`, `ASimulatedEngineHangIsKilledAndRelaunchedAsStoppedAnswering` and the existing `AHungEngineIsKilledAndRelaunched` pass (37 s).
- Soak `--minutes 6 --kill-engine 2 --kill-app 3 --hang-engine 4`, memory mode: **pass**, `measurements/bench-20261104-045326`: 6 of 6 Clips (5 presses + 1 extra), 10 of 10 bounds, 3 of 3 recoveries: kill-engine back buffering with `engine-restarted` ("unexpectedly") in 2.1 s; kill-app: the press with the App gone saved a passing Clip, App back with `app-restarted` in 0.7 s; hang-engine back with "stopped answering" in 8.7 s. Earlier runs, all with 3 of 3 recoveries passing: runs 1–2 failed on short Clips from presses bunched after a recovery (fixed by `bufferFull`), runs 3–5 on the 18 ms AAC-packet check (fixed above).
