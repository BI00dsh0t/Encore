---
title: 'Engine crash recovery and the restart warnings'
type: 'feature'
ticket: '2'
created: '2026-11-04'
status: 'in-progress'
baseline_revision: '4390a0e6adab46146917f7229c6a8af52b186a87'
route: 'full'
route_source: 'auto'
risk: 'high'
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
- [ ] `src/ReplayBuffer.Contracts/*` -- guard, constants, codes, `EngineHang` -- one rule for both supervisors.
- [ ] `src/ReplayBuffer.Engine/*` -- `--restarted`, early warning, dedupe, hang, App crash loop, `--replay-buffer` to the App.
- [ ] `src/ReplayBuffer.App/*` -- `--restarted` on relaunch, crash-loop status, tray error state and Restart, App log, `--replay-buffer` pass-through.
- [ ] `bench/ReplayBuffer.Bench/*` -- kill/hang options and recovery checks.
- [ ] Tests (default, fast): guard (3 in window allowed, 4th refused, allowed again after the window slides, `Reset`); `serve --restarted hung` parses, a bad value fails; an engine started with `Restarted = Crashed` has `engine-restarted` in the first snapshot and an attach with a reason adds no second one; `AppSupervisor` with fakes: 4th App exit within the window isn't relaunched and `app-crash-loop` is raised; `dev.simulate engine-hang` refused on the installed instance; the tray's Restart item is hidden by default; schedule fires a one-shot event once.
- [ ] `tests/ReplayBuffer.App.Tests/EngineConnectionTests.cs` (Heavy, explicit, real engine on a test instance): a supervising `EngineConnection`; kill its engine (the `State.Pid` it connected to) 4 times, each after it is back → 3 relaunches, then `Status == CrashLoop`, the log action got an `engine-crash-loop` line and no engine serves the pipe; `RestartAfterCrashLoop()` → `Connected`. `tests/ReplayBuffer.App.Tests/SupervisionTests.cs` (Heavy): `dev.simulate engine-hang` on the pair → a new engine within 20 s with "answering".
- [ ] Run `bench soak --minutes 6 --kill-engine 2 --kill-app 3 --hang-engine 4` holding `.bench-running` when free; record the result here.

**Acceptance Criteria:**
- Given the App supervising an engine, when the engine is killed, then a new engine serves within 10 s and its first snapshot carries one dismissable `engine-restarted`, and the status line shows it.
- Given `dev.simulate engine-hang` on a test instance, then the engine is relaunched within 20 s with an `engine-restarted` that says it stopped answering.
- Given 3 engine restarts within 10 minutes, when it crashes a fourth time, then it isn't relaunched, the tray shows the error state with Restart, and `engine-crash-loop` is in the App log; Restart brings it back.
- Given the engine relaunched the App 3 times within 10 minutes, when the App exits a fourth time, then it isn't relaunched and `app-crash-loop` is logged and raised.
- Given `bench soak` with `--kill-engine`, `--kill-app` and `--hang-engine` on the synthetic game, then after each the Replay Buffer is buffering again in time, a Save press with the App killed saves a passing Clip, the App returns with `app-restarted`, and the report records each recovery.
- Given a Recording running when the engine is killed, then its file plays to its last fragment and `recording-interrupted` is raised at the next start (6.2's `RecordingCrashTests` default test covers the engine start path this story reuses).

## Implementation Notes

## Plan Change Log

## Review Triage Log

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
