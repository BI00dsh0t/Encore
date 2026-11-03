---
title: 'Tracer: detect a borderless game and make it the Target Game'
type: 'feature'
ticket: '1'
created: '2026-11-03'
status: 'ready-for-dev'
baseline_revision: '898121f15adb57704d152e8788901cfe319b7417'
route: 'full'
route_source: 'auto'
risk: 'high'
review: ''
review_source: ''
lenses_ran: []
review_loop_iteration: 0
followup_review_recommended: false
context: []
warnings: ['oversized']
deferred: []
---

<intent-contract>

## Intent

**Problem:** The engine has no idea which game is running: nothing detects a game, there is no Target Game, and the capture session is only ever pointed at a window by hand (`debug-capture --window`).

**Approach:** The thinnest path through every layer. A `GameMonitor` in Platform.Windows polls the foreground window once a second; when it covers its monitor with no caption (borderless or fullscreen) and its process isn't the engine's or the attached App's, it canonicalizes the executable with the one `Canonicalize` and proposes it. The SessionOrchestrator commits `target.changed { from, to, reason: detected }`, which sets the new `targetGame` snapshot field, and, when the engine runs with `serve --debug-capture`, ends any game session and starts one on the Target's window (retargeting in place when the same process's window changes). The Main App's Status tab shows the Target Game.

## Boundaries & Constraints

**Always:**
- AD-12: a game process is opened only with `PROCESS_QUERY_LIMITED_INFORMATION`; no memory reads, module enumeration, hooks or WinEvents. Exit is detected by polling `GetExitCodeProcess` on that handle (no `SYNCHRONIZE`).
- AD-18: the Game Profile id comes only from `Canonicalize(path)` in Platform.Windows: `GetFinalPathNameByHandle(FILE_NAME_NORMALIZED | VOLUME_NAME_DOS)`, `\\?\` stripped (`\\?\UNC\` → `\\`), lowercase invariant.
- AD-19: GameMonitor only proposes (posts to the orchestrator); only the orchestrator commits the Target, publishes `target.changed` and starts, retargets or ends sessions.
- AD-22: Contracts change additively, in `Messages/Game.cs` plus one nullable `StateSnapshot` field that reads as null from an older engine.
- Detection within 5 s: poll interval 1 s.
- Test-instance engines don't detect games unless a test injects a window probe or `--debug-capture` is passed, so other engine tests never see a stray `target.changed` (Bench's GPU-load window runs in parallel sessions).

**Never:**
- The 10 s focus rule, Recording block, cold start, exit handover and drain (story 3.3), other detection sources, stickiness rules or the install-folder exclusion (3.2), Game Profiles or product names (3.4), `game.detected`/`game.exited` events, the Replay Buffer (epic 4).
- A session started on Target change without `--debug-capture` (that is epic 4's Replay Buffer).
- Edits outside this story's files: other sessions are working on audio (2.4), the OutputPathAllocator (3.5) and test trimming in parallel.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Borderless game | foreground window covers its monitor, no `WS_CAPTION`, pid not excluded | one proposal `(pid, hwnd, canonical path, exe name)`, reason detected; `target.changed` from null; snapshot `targetGame` set | — |
| Same game, new window | already-detected pid, foreground window handle changes | proposal again; orchestrator retargets the running session in place, no `target.changed` | retarget failure ends the session (existing orchestrator behaviour) |
| Second game | another pid detected | `target.changed` from the first to the second; debug capture ends and restarts on the new window | start failure → reported to the failure callback, Target still committed |
| Excluded | foreground fullscreen window of the engine's own pid or the attached App's pid | nothing proposed | — |
| Not a game | captioned or smaller-than-monitor foreground window, shell/desktop window | nothing proposed | — |
| Target exits | the Target's process exits | `target.changed { to: null, reason: exit }`; the pid is forgotten | — |
| Unreadable process | `OpenProcess` / image path / `Canonicalize` fails | skipped this poll, retried next poll | logged at Debug |

</intent-contract>

## Code Map

- `src/ReplayBuffer.Contracts/Messages/Game.cs` -- empty area file; add payloads, `GameJsonContext`, `target.changed` in `Events`. Model on `Messages/Audio.cs`.
- `src/ReplayBuffer.Contracts/Messages/Engine.cs` -- `StateSnapshot`: add `TargetGame? TargetGame { get; init; }` (JSON `targetGame`), null in `Initial`.
- `tests/ReplayBuffer.Contracts.Tests/MessagesTests.cs` -- `Samples` needs a `target.changed` payload or the coverage theory fails.
- `src/ReplayBuffer.Core/Sessions/SessionOrchestrator.cs` -- message-switch orchestrator; add `ProposeTarget` handling. Reuse `Start`, `Retarget`, `EndActive`. Constructor: append optional params only (2.4 also edits this file).
- `src/ReplayBuffer.Core/Sessions/SessionSpec.cs` -- `CaptureTarget(ProcessId, Window, ProfileId)`.
- `src/ReplayBuffer.Platform.Windows/WindowFinder.cs` -- pattern for CsWin32 calls, `IsCloaked`, `OpenProcess`+`QueryFullProcessImageName` with limited access. Don't change it.
- `src/ReplayBuffer.Platform.Windows/HotkeyThread.cs` -- pattern for a dedicated thread posting a callback.
- `src/ReplayBuffer.Platform.Windows/NativeMethods.txt` -- add the CsWin32 names used.
- `src/ReplayBuffer.Engine/EngineHost.cs` -- composition root; `EngineHostOptions` for test seams; `hub.State.AppPid` is the attached App's pid; dispose order (stop GameMonitor before `Sessions.Dispose()`).
- `src/ReplayBuffer.Engine/CommandLine.cs` -- `ServeCommand`, `ParseServe`, `Usage`; `src/ReplayBuffer.Engine/Program.cs` `Serve(...)` builds the host.
- `src/ReplayBuffer.App/MainViewModel.cs`, `MainWindow.xaml` -- Status tab text built from `state`.
- Tests: `tests/ReplayBuffer.Core.Tests/SessionOrchestratorTests.cs` (`FakeFactory`), `tests/ReplayBuffer.Engine.Tests/DeviceListServeTests.cs` (host + client pattern), `tests/ReplayBuffer.App.Tests/MainViewModelTests.cs` (`FakeEngineSession`), `tests/ReplayBuffer.Platform.Windows.Tests/`, `tests/ReplayBuffer.Engine.Tests/CommandLineTests.cs` if it exists.

## Tasks & Acceptance

**Execution:**
- [ ] `src/ReplayBuffer.Contracts/Messages/Game.cs` -- add `TargetGame(string ProfileId, string Name, int ProcessId)`; `TargetChangeReason { Focus, Exit, Ignored, Detected, ColdStart }` (camelCase converter, `ColdStart` named `cold-start` via `JsonStringEnumMemberName`); `TargetChanged(TargetGame? From, TargetGame? To, TargetChangeReason Reason)`; event `target.changed` on snapshot field `targetGame` applying `To`; `GameJsonContext` -- AD-18 shape.
- [ ] `src/ReplayBuffer.Contracts/Messages/Engine.cs` -- nullable `TargetGame` snapshot property -- the App reads the Target from the snapshot.
- [ ] `src/ReplayBuffer.Core/Sessions/` -- `TargetCandidate(int ProcessId, nint Window, string ProfileId, string Name)`; `ProposeTarget(TargetCandidate? To, TargetChangeReason Reason) : SessionMessage`; orchestrator handles it per the matrix, publishing through a new optional `Action<TargetChanged>? targetChanged` and, when a new optional `TargetCapture? targetCapture` (audio func + failure callback) is given, ending the game session and starting one on the Target (`CaptureTarget(pid, window, profileId)`) -- AD-19 commit path and the debug capture that follows the Target.
- [ ] `src/ReplayBuffer.Platform.Windows/GamePaths.cs` -- `public static string Canonicalize(string path)` -- AD-18's one canonicalizer.
- [ ] `src/ReplayBuffer.Platform.Windows/GameMonitor.cs` -- `IGameWindowProbe` seam (foreground window if it covers its monitor without caption and isn't the shell; open a process with limited access returning an `IGameProcess` with image path and `HasExited`); real `ForegroundGameWindowProbe`; `GameMonitor(probe, Func<int,bool> isExcluded, Action<TargetCandidate?, TargetChangeReason> propose, TimeSpan interval, ILogger)` with a background thread and an internal `Poll()` tests call -- detection per the matrix.
- [ ] `src/ReplayBuffer.Engine/` -- `EngineHostOptions`: `IGameWindowProbe? GameWindows`, `bool DebugCapture`, `TimeSpan GameScanInterval = 1 s`; detection on when a probe is injected, `DebugCapture` is set, or the instance isn't a test one; exclusion = `Environment.ProcessId` or `hub.State.AppPid`; wire `target.changed` to the hub; `serve [--instance <test>] [--debug-capture]` in `CommandLine`/`Program`; `--debug-capture` passes `TargetCapture` with `SessionAudio.None` and logs failures.
- [ ] `src/ReplayBuffer.App/MainViewModel.cs`, `MainWindow.xaml` -- a `TargetGame` line on the Status tab: `Target Game: <Name> (process <pid>)` or `Target Game: none`.
- [ ] Tests (lean, one or two per AC): Platform.Windows -- `Canonicalize` of a mixed-case path with `..` returns the lowercase final path with no `\\?\`; a real borderless popup covering the primary monitor is classified fullscreen and a captioned window isn't; `GameMonitor.Poll` with a fake probe proposes a new game once, ignores an excluded pid, proposes null/exit when the Target exits. Core -- a proposal publishes `target.changed` and, with `TargetCapture`, starts a session on the window; a second game ends it and starts another; same pid new window retargets. Engine -- a host with a fake probe: the client sees `target.changed` and its snapshot's `targetGame`, and the App pid is never proposed. Contracts -- the `target.changed` sample. App -- the Status line shows the Target Game.

**Acceptance Criteria:**
- Given the engine serving and the Main App attached, when a borderless window covering its monitor comes to the foreground in another process, then within 5 s the client receives `target.changed { from: null, to: { profileId: <lowercase canonical exe path>, name, processId }, reason: "detected" }` and the Main App's Status tab shows that game as the Target Game.
- Given `serve --debug-capture`, when a game becomes the Target, then a capture session is running on its window, and when another game becomes the Target the first session ends and a new one starts on the new window.
- Given the attached App's process (or the engine's own) owns a borderless window, when it is in the foreground, then nothing is proposed and `targetGame` stays unchanged.
- Given the Target's process exits, when the next poll runs, then `target.changed { to: null, reason: "exit" }` is published.

## Implementation Notes

## Plan Change Log

## Review Triage Log

## Design Notes

- Classification: the window rect (`GetWindowRect`) contains `MONITORINFO.rcMonitor` of `MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST)`, the style has no `WS_CAPTION`, it is visible, not cloaked, not minimized, and it's neither `GetShellWindow()` nor class `Progman`/`WorkerW`. Foreground-only keeps transparent overlays and background windows out; stickiness for alt-tabbed games is story 3.2.
- Tracer target rule: each newly detected pid becomes the Target at once (story 3.3 replaces this with the 10 s focus rule). A detected pid stays known (handle held) until it exits.
- `TargetCapture` lives only until epic 4's Replay Buffer starts sessions on `target.changed`; it uses `SessionAudio.None` and the store's video settings, writing to `LocalData\sessions`.

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings, 0 errors
- `dotnet test` on `ReplayBuffer.Contracts.Tests`, `ReplayBuffer.Core.Tests`, `ReplayBuffer.Platform.Windows.Tests`, `ReplayBuffer.Engine.Tests` (the new class only, plus `CommandLine` tests), `ReplayBuffer.App.Tests` -- expected: all pass
- `pwsh tools/publish-engine.ps1` -- expected: 0 IL/AOT warnings

**Manual checks (if no CLI):**
- Proxy for the HITL check: run the published engine `serve --instance ReplayBuffer-Test-gd31 --debug-capture`, launch `ReplayBuffer.Bench.GpuLoad` (borderless popup over the primary monitor), and confirm with an IPC client that `targetGame` names `replaybuffer.bench.gpuload.exe` within 5 s and a session file grows in the instance's `LocalData\sessions`; close the load and see `target.changed { to: null, reason: exit }`.
