---
title: 'Supervision and single instance'
type: 'feature'
ticket: '8'
created: '2026-10-25'
status: done
baseline_revision: '0ad6c84f978fb44bfaaaf394bc641afcc0af7276'
route: 'full'
route_source: 'auto'
risk: 'medium'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/initiative-replay-buffer/architecture-replay-buffer/architecture-replay-buffer.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** A crashed or hung engine stays down, a crashed App never comes back, and nothing stops a second copy of either process from starting (R7, AD-16).

**Approach:** The engine emits `engine.heartbeat` every 2 s. The App's `EngineConnection` becomes a supervisor: after 3 missed heartbeats, or a command with no reply for 10 s, it kills the engine through the process handle it opened from the snapshot's `pid`, and it relaunches a crashed or killed engine with backoff. The App registers with `app.attach { pid, engineRestart? }`, and the engine relaunches that App with backoff if it exits while no `engine.shutdown` has been received. A named mutex per process per instance enforces one copy of each, and `WAIT_ABANDONED` counts as acquired.

## Boundaries & Constraints

**Always:**
- Contracts change additively (AD-22): `engine.heartbeat` (coalescing) → new optional snapshot field `heartbeatAt`; `app.attach` command; `app.attached { pid }` → new optional field `appPid`; codes `engine-restarted`, `app-restarted`, `instance-running` and `recording-interrupted` in `ErrorCodes`.
- The new engine raises restart notices as `warning.raised` (source `supervision`, severity warning, `overlay: false`, dismissable). It raises `engine-restarted` when `app.attach` carries a reason (`crashed` or `hung`), and `app-restarted` after it relaunches the App.
- Timings live once in Contracts: heartbeat every 2 s; hung after 3 missed heartbeats (6 s with no event) or a reply older than 10 s. Backoff for both sides: 0, 1, 2, 4, 8, 16, then 30 s, reset after 60 s with no restart.
- The App never kills a process it didn't verify: it kills only through the handle it opened at connect, and only when that handle's image is `ReplayBuffer.Engine.exe` in its own folder.
- The App starts supervising only after its connect, and stops before Quit sends `engine.shutdown`. The engine relaunches only an App that attached.
- Mutex names are `Local\ReplayBuffer.{Engine|App}.v1.<instance>`. Each process takes its mutex before anything else and holds it for its whole life. A second engine `serve` exits 1 with `error instance-running`. A second App exits 0.
- The App gains `--instance ReplayBuffer-Test-*`, with the engine's rule, now in `Contracts.Instance`. It passes the instance to the engine it launches, and the engine passes it back when it relaunches the App with `--tray`.
- The resume rule is recorded where epic-recordings will find it: after a restart only the Replay Buffer resumes, never a Recording, and `recording-interrupted` is raised. It goes in the doc comments on `ErrorCodes.RecordingInterrupted` and `RecordingMessages`. Nothing implements it yet.
- 0 warnings; the engine stays AOT-clean; no P/Invoke outside `Platform.*`.

**Never:** Hotkeys (1.9). The Run key, install or publish layout (1.13). Detecting a hung App. Touching spike 1.2's bench or measurement code.

**Decisions (user, 2026-10-25):**
- A second App launch exits silently (code 0). It doesn't signal the running App.
- The AD-3 server image-path check is in scope. `Platform.Desktop` gets `GetNamedPipeServerProcessId`. The App's `VerifyServer` accepts only a server whose image is `ReplayBuffer.Engine.exe` in the App's own folder, and the connect fails with `engine-untrusted` otherwise. The snapshot's `pid` must also equal the server's pid.
- When the engine exits with code 0 without the App's Quit (an `engine.shutdown` from elsewhere, or Ctrl+C), the App quits too, so the pair ends together.
- The plan is kept whole at about 2 300 tokens.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Engine crash | engine killed | App sees the pipe close and the process exit non-zero → relaunch (backoff) → attach with `crashed` → `engine-restarted` raised | relaunch fails → status "not running", retries on backoff |
| Engine hang | no event for 6 s, or a reply pending for 10 s | App kills the verified handle → relaunch → attach with `hung` | — |
| Dropped connection, engine alive | writer-queue drop | reconnect, no relaunch; still not answering after 10 s → treated as hung | — |
| App crash | App killed, no `engine.shutdown` | engine relaunches `ReplayBuffer.App.exe --tray [--instance]` (backoff) → `app-restarted` raised; new App takes the abandoned mutex | start fails → logged, retried on backoff |
| Tray Quit | `engine.shutdown`, then App exits | neither restarts the other; no process left | — |
| Clean engine exit | engine exits 0, not from the App's Quit | App quits; the engine doesn't relaunch it | — |
| Untrusted server | pipe served by an image other than `ReplayBuffer.Engine.exe` beside the App | no connection; status shows `engine-untrusted`; the App never launches over it | retried on backoff |
| Second launch | engine or App, same instance, one running | exits; the running one is unaffected | — |
| Restart loop | repeated crashes | delays grow to 30 s | — |

</frozen-after-approval>

## Code Map

- `src/ReplayBuffer.App/Engine/EngineConnection.cs` -- the stable `IEngineSession`. `ConnectAsync`'s connect-or-launch is reused per attempt, with `_launched` reset each time. `SendAsync` is the place to time pending commands. `ShutdownAsync` must stop supervision first.
- `src/ReplayBuffer.App/ShellApp.cs`, `Program.cs` -- startup (`--tray`), `_ = _engine.ConnectAsync()` becomes the supervise loop, `QuitAsync`. The mutex is taken in `Main`, before `Run`.
- `src/ReplayBuffer.App/MainViewModel.cs` -- maps `EngineStatus` to text. Add a "restarting" status.
- `src/ReplayBuffer.Engine/EngineHost.cs` -- composition: register `app.attach`, start the heartbeat, `DebugHold`'s test-only pattern for `debug.hang`, `ShutdownRequested`.
- `src/ReplayBuffer.Engine/Program.cs`, `CommandLine.cs` -- `Serve` takes the mutex. Move the test-instance build and `TestInstancePrefix` into `Contracts.Instance` (keep `ServeCommand.TestInstancePrefix` pointing to it).
- `src/ReplayBuffer.Engine/Ipc/Warnings.cs` -- `Raise(code, severity, source, message, overlay)`.
- `src/ReplayBuffer.Contracts/Messages/Engine.cs` -- `StateSnapshot` (fields `required`; new ones optional), `EngineMessages`, `EngineJsonContext`.
- `src/ReplayBuffer.Contracts/Ipc/MessageTypes.cs` -- every `EventType` names one snapshot field. `MessagesTests` enforces this and needs a sample payload per event.
- `tests/ReplayBuffer.Engine.Tests/EngineServeTests.cs`, `IpcTests.cs` -- these assume `Seq == 0` and treat the first event as `settings.changed`. With the heartbeat on, filter by type, or start in-process hosts with the heartbeat off (an `EngineHost.Start` option). The second-engine test now expects `instance-running`.
- `tests/ReplayBuffer.App.Tests/` -- the bin holds `ReplayBuffer.App.exe` and `ReplayBuffer.Engine.exe` side by side, so the end-to-end test runs the real pair from there. `EngineConnectionTests.AnEngineThatDiesLeavesTheAppDisconnected…` no longer holds and is rewritten.

## Tasks & Acceptance

**Execution:**
- [x] `src/ReplayBuffer.Contracts/Supervision.cs` (new), `Instance.cs`, `ErrorCodes.cs`, `Messages/Engine.cs`, `Messages/Recording.cs` -- timing constants, `RestartBackoff(TimeProvider)`, `SingleInstance.TryAcquire(process, instance)` (`AbandonedMutexException` = acquired), `Instance.TestPrefix`/`ForTest`, the four codes, `engine.heartbeat`, `app.attach`, `app.attached`, the snapshot fields, and the resume-rule doc.
- [x] `src/ReplayBuffer.Engine/EngineHost.cs`, `Supervision/AppSupervisor.cs` (new), `Program.cs`, `CommandLine.cs` -- heartbeat timer; `debug.hang` on test instances (stops heartbeats, never replies); `app.attach` publishes `app.attached`, raises `engine-restarted` when it carries a reason, and watches the pid; on exit with no shutdown, backoff → start the App → `app-restarted`; the engine mutex in `Serve`.
- [x] `src/ReplayBuffer.App/Engine/EngineConnection.cs`, `Engine/EngineWatchdog.cs` (new), `ShellApp.cs`, `Program.cs`, `MainViewModel.cs` -- the supervise loop (connect → attach → watch → kill/relaunch per the matrix); a pure watchdog (last event and oldest pending against a clock); App mutex; `--instance`; status text.
- [x] `src/ReplayBuffer.Platform.Desktop/PipeServer.cs` (new), `NativeMethods.txt`; `tests/ReplayBuffer.Platform.Desktop.Tests/` -- `PipeServer.ProcessId(SafePipeHandle)` over `GetNamedPipeServerProcessId`, tested against an in-test `NamedPipeServerStream` (this process's pid). The App's `VerifyServer` uses it with `Process.MainModule.FileName`.
- [x] `tests/ReplayBuffer.Contracts.Tests/` -- backoff sequence and reset; mutex held, freed, and abandoned by a thread that exits holding it; samples for the new events.
- [x] `tests/ReplayBuffer.Engine.Tests/` -- heartbeat arrives about every 2 s and stops on `debug.hang`; `app.attach` with a reason raises `engine-restarted`; the App watcher relaunches with a fake starter, and doesn't after shutdown; second engine `instance-running`; fix the seq assumptions.
- [x] `tests/ReplayBuffer.App.Tests/EngineWatchdogTests.cs`, `SupervisionTests.cs` -- watchdog edges; end to end against `App.exe --tray --instance ReplayBuffer-Test-<guid>`: kill the engine, `debug.hang` it, kill the App; each is back (a new engine pid answers, or `appPid` changes) within 10 s; a second App and a second engine each exit while the pair runs; cleanup by tray-equivalent shutdown and a kill of recorded pids by name.

**Acceptance Criteria:**
- Given the pair running, when the engine is killed, hung, or the App is killed, then each is running and connected again within 10 s, and the matching restart warning is in the snapshot.
- Given the pair running, when the engine or the App is launched again for the same instance, then the new process exits and the running pair is unaffected.
- Given tray Quit, when it completes, then no `ReplayBuffer.App` or `ReplayBuffer.Engine` process is left and none is relaunched.
- Given the solution, when `dotnet build ReplayBuffer.slnx` and `dotnet test --solution ReplayBuffer.slnx` run, then 0 warnings and all tests pass.

## Implementation Notes

- **Engine mutex placement:** taken in `Program.cs` right after parsing, before logging starts, and only for `serve` (the code map said inside `Serve`). A second engine exits 1 with `error instance-running` before it opens a log file or the pipe.
- **Engine relaunch guard:** an App the engine *started* that exits with code 0 before attaching is not relaunched (another copy holds the App mutex); otherwise a manually started App plus an engine relaunch would loop forever. An attached App is relaunched on any exit without `engine.shutdown`, as planned. `engine.shutdown` stops the supervisor before its reply.
- **App Quit between connections:** `ShutdownAsync` stops supervising, then, if no connection is up but a known engine process is alive, tries one connect so `engine.shutdown` still reaches it.
- **Dropped connection to a live engine:** after the pipe closes the App waits up to 3 s for the engine process to exit; if it is alive it reconnects without launching, and kills it as hung after 10 s with no new connection.
- **Tests:** in-process `IpcTests` start hosts with `EngineHostOptions { Heartbeat = null }`; `AppSupervisorTests` (new) cover the engine side with a fake launcher. The end-to-end "tray Quit" case sends `engine.shutdown` from a test client (the App then quits on the clean engine exit), then checks both instance mutexes are free.
- `EngineHost.DisposeAsync` and `AppSupervisor.Dispose` are now idempotent (an IPC test disposes the host twice).
- **Dropped-connection row:** a test-instance-only `debug.drop { refuseNew }` ends every connection after its reply and, with `refuseNew`, closes the listener so the engine lives on but can't be reached (`IpcServer.DropAll`, `StopAcceptingAsync`). `SupervisionTests` cover both halves: without `refuseNew` the same engine is still running and attached 13 s later with no `engine-restarted`; with it, the App kills that engine after the 10 s `ReplyLimit` and the new engine reports `engine-restarted` with the "stopped answering" (hung) message.
- **Verified after the review patches:** `dotnet build ReplayBuffer.slnx` (clean rebuild) has 0 warnings; `dotnet test --solution ReplayBuffer.slnx` gives 295 total, 287 passed, 0 failed, 8 skipped (the FFmpeg and capture tests that need a machine setup), with no `ReplayBuffer.App` or `ReplayBuffer.Engine` left running.

## Plan Change Log

## Review Triage Log

Lens `quick`, iteration 0, one subagent against the full diff. Verdicts: high 0, medium 2, low 4, false 2, maybe-false 0.

| # | Finding | Verdict | Route | Evidence |
|---|---------|---------|-------|----------|
| 1 | `SuperviseAsync` awaits `app.attach` before `WatchAsync`. An engine that hangs after its snapshot but before replying is never detected. | medium | patch | `EngineClient.SendAsync` has no timeout, and `Check()` runs only inside `WatchAsync`. Fixed: the attach is sent without awaiting it, and the watch covers it. |
| 2 | `VerifyServer` reads `MainModule` by pid, not through the handle it later kills with. | false | reject | `SafeHandle` is opened first, and an open handle keeps the process object alive, so the pid can't be reused until it is closed. The pid lookup therefore names the same process. |
| 3 | A Quit during a relaunch, or while a live engine refuses connections, leaves that engine running. | medium | patch | `ShutdownAsync` makes one 1 s attempt, then returns `true` with `_launched` still alive. Fixed: if it's still not connected, it kills `_launched` and the last verified engine. |
| 4 | A clean exit (0) during a reconnect-to-live is treated as a crash and relaunched. | low | patch | In the `Failed` branch, `!IsAlive(live)` gives `Crashed` without reading `ExitCode`. That's contrary to the clean-exit decision. Fixed: the branch now goes to `EngineEnded`. |
| 5 | `AppSupervisor` relaunches a started App that exits non-zero before attaching. | false | reject | Such an App exists only because an attached App died, so the chain starts from an attach. Retrying a failed start on backoff is the matrix's "start fails → retried on backoff". |
| 6 | The tray-Quit AC isn't exercised end to end. The test sends `engine.shutdown` from a client. | low | reject | `EngineConnectionTests` covers `ShutdownAsync` with supervision, and `AppSupervisorTests.AfterShutdown…` covers "no relaunch after shutdown". Driving the real tray menu needs UI automation, which isn't worth it for a rarely met gap. The tray stays the builder's manual check. |
| 7 | `AppSupervisor.Attach` publishes `app.attached` and raises `engine-restarted` before the `_stopped` check. | low | patch | Confirmed at `Attach`, lines 54–69. The fix is to reorder the check. |
| 8 | `AppSupervisor.WatchAsync` catches only three exception types from `launcher.Start()`. Any other faults the unobserved loop, and supervision stops silently. | low | patch | Confirmed. The fix widens the catch to any exception other than a cancellation. |

## Design Notes

- `heartbeatAt` and `appPid` exist only because AD-3's Contracts tests require every event to change a snapshot field. They also give the Status area something true to show, and give the tests a way to see the relaunched App.
- Liveness counts any event, not only heartbeats; the heartbeat just guarantees traffic. Opening the process handle at connect makes "kill by PID" immune to PID reuse and gives the crash exit code.
- Hang test math: detection is at most 6.5 s (500 ms checks), and a relaunch plus connect takes about 1 s, inside 10 s. Each scenario gets its own App, so backoff starts at 0.

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx` -- expected: 0 warnings, 0 errors.
- `dotnet test --solution ReplayBuffer.slnx` -- expected: all pass, with no `ReplayBuffer.App` or `ReplayBuffer.Engine` process left.

**Manual checks:**
- Run the dev App; kill the engine in Task Manager (it comes back and the Status shows the warning); kill the App (it comes back in the tray); tray Quit leaves nothing running.
