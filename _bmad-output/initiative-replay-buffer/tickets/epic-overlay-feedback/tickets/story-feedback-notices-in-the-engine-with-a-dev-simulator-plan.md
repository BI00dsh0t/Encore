---
title: 'Feedback notices in the engine, with a dev simulator'
type: 'feature'
ticket: '4'
created: '2026-11-03'
status: 'in-review'
baseline_revision: 'd6b57b6ef463e170bf92f9b0f96ea25e148f419d'
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

**Problem:** The engine publishes `clip.saved` and `clip.failed`, but the Overlay and status line may render only `capture.stateChanged` and `warning.raised` (AD-3, AD-15), so no outcome reaches the user as a message, and nothing names the Feedback Sound each event plays. Recording state and disk full can't be exercised before epic-recordings exists.

**Approach:** A Core `FeedbackRouter` turns outcomes (Clip saved, Clip not saved, Recording started/stopped from the orchestrator's `CaptureState`, disk full) into `warning.raised` notices through a Core port the engine's `Warnings` implements, with engine-side auto-dismiss timers. A dev-only `dev.simulate { event }` command, in its own Contracts area, injects a stub Recording state, a disk full and a save failure, and is refused with `not-dev-instance` in the installed instance.

## Boundaries & Constraints

**Always:**
- Notices are plain `Warning` records published by `Warnings` (`warning.raised` / `warning.dismissed`); no new event types.
- Mapping (all `overlay: true`, `dismissable: true`):
  - `clip.saved` → `clip-saved`, info, sound `clipSaved`, source `clips`; the engine dismisses it 10 s later. It also dismisses every active `clip-failed` notice.
  - `clip.failed { reason }` → `clip-failed`, error, sound `saveFailed`, source `clips`, message `Clip not saved: <short reason>` (`disk-full` "disk full", `clip-too-short` "clip too short", `save-busy` "busy", `access-denied` "access denied", anything else "save failed"). Stays until dismissed by the user or the next saved Clip.
  - `CaptureState.Recording` Off→Running → `recording-started` (info, `recordingStarted`, source `recordings`, auto-dismissed after 10 s), and dismisses any active `disk-full` notice; Running→Off → `recording-stopped` (info, `recordingStopped`, auto-dismissed after 10 s).
  - disk full → `disk-full`, error, `diskFull`, source `recordings`; until dismissed or the next Recording starts.
- New codes `clip-saved`, `clip-failed`, `recording-started`, `recording-stopped`, `not-dev-instance` in `Contracts.ErrorCodes` and its `All` list. `disk-full` exists already.
- `dev.simulate` lives in a new `Contracts/Messages/Dev.cs` (`DevMessages`, `DevJsonContext`), registered in `MessageRegistry.Commands`. Event names (camelCase enum): `recordingStarted`, `recordingStopped`, `diskFull`, `saveFailed`.
- The stub Recording state goes through the orchestrator (the only `CaptureState` producer, AD-19): the existing `RecordingRunning` message also sets `CaptureState.Recording`.
- Lean tests: one or two happy-path tests per AC, no real-time waits (a manual `TimeProvider` drives the 10 s timer).

**Never:** No sound playback (story 5.6), no Overlay or App changes (5.5, 5.7, 5.10), no Platform.Audio. No new `capture.stateChanged` fields. Contracts additions stay in `ErrorCodes.cs`, the new `Dev.cs` and the `MessageRegistry` lists. Don't change how saves, retries or cooldowns work.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Clip saved | `clip.saved` | one `clip-saved` notice; `warning.dismissed` for it after 10 s | none |
| Save failed, then saved | `clip.failed {disk-full}`, later `clip.saved` | `clip-failed` "Clip not saved: disk full"; dismissed when the save lands | user dismissal earlier is harmless (withdraw of an inactive id is a no-op) |
| Simulate recording | `dev.simulate {recordingStarted}` then `{recordingStopped}` | `capture.stateChanged.recording` running then off, with `recording-started` / `recording-stopped` notices | none |
| Simulate disk full / save failure | `dev.simulate {diskFull}` / `{saveFailed}` | `disk-full` notice / `clip.failed {save-failed}` and its `clip-failed` notice | none |
| Installed instance | `dev.simulate` in `ReplayBuffer` | error `not-dev-instance`, nothing changes | `ErrorCodeException` |

</intent-contract>

## Code Map

- `src/ReplayBuffer.Contracts/ErrorCodes.cs` -- registry; add the five codes with doc comments and to `All` (ContractsTests already check `All` matches the constants).
- `src/ReplayBuffer.Contracts/Ipc/MessageTypes.cs:72` -- `MessageRegistry`; add `.. DevMessages.Commands` (and `.. DevMessages.Events`, empty).
- `src/ReplayBuffer.Contracts/Messages/Feedback.cs` -- pattern for a message area: `CommandType<TArgs,TResult>(name, argsInfo, resultInfo)`, `CamelCaseEnumConverter<T>`, its own `JsonSerializerContext` with `Empty`. Read only.
- `src/ReplayBuffer.Contracts/Instance.cs` -- `Instance.InstalledName`; the daily instance. Dev and test instances allow `dev.simulate`.
- `src/ReplayBuffer.Engine/Ipc/Warnings.cs` -- `Raise(...)` / `Dismiss(...)`; add `Withdraw(id)`: publishes `warning.dismissed` if active, regardless of `Dismissable`, no throw; implement the Core port.
- `src/ReplayBuffer.Core/Sessions/SessionOrchestrator.cs:262` -- `RecordingRunning` case; also `SetState(_state with { Recording = Running|Off })`. `SetState` at ~808 publishes on change only.
- `src/ReplayBuffer.Core/Sessions/TargetGames.cs:28` -- `RecordingRunning` doc: now also the stub Recording state (dev.simulate, tests).
- `src/ReplayBuffer.Engine/EngineHost.cs:206-273, 316-353` -- composition: `warnings` created at 206; orchestrator `publish` lambda (227) and `ReplayClips` `Saved`/`NotSaved` lambdas (252, 258) feed the router; command registrations at 316; test-only debug commands at 328 (dev.simulate is registered for every instance, refusing in the installed one).
- `tests/ReplayBuffer.Engine.Tests/MicMuteServeTests.cs` -- golden serve test: `EngineHost.Start(TestInstance, NullLoggerFactory.Instance, hostOptions: new EngineHostOptions { Heartbeat = null, ProbeEncoders = false, AudioEndpoints = new FakeEndpointSource() })`, `EngineClient.ConnectAsync`, events channel, `client.State`.
- `tests/ReplayBuffer.Engine.Tests/WriterQueueTests.cs:113` -- a `ManualTime : TimeProvider` (timestamp only); the router test needs one that also supports `CreateTimer`.

## Tasks & Acceptance

**Execution:**
- [x] `src/ReplayBuffer.Contracts/ErrorCodes.cs` -- add `ClipSaved`, `ClipFailed`, `RecordingStarted`, `RecordingStopped`, `NotDevInstance` and list them in `All` -- registry rule.
- [x] `src/ReplayBuffer.Contracts/Messages/Dev.cs` (new) -- `SimulatedEvent` enum, `DevSimulate(SimulatedEvent Event)`, `DevMessages.Simulate` (`dev.simulate`, result `Empty`), `Commands`/`Events`, `DevJsonContext` -- own area to avoid conflicts with parallel stories.
- [x] `src/ReplayBuffer.Contracts/Ipc/MessageTypes.cs` -- register `DevMessages` lists.
- [x] `src/ReplayBuffer.Core/Feedback/FeedbackRouter.cs` (new) -- `FeedbackNotice` record, `IFeedbackNotices` port (`string Raise(FeedbackNotice)`, `void Withdraw(string id)`), `FeedbackRouter(IFeedbackNotices, TimeProvider)` with `ClipSaved(ClipSaved)`, `ClipNotSaved(ClipFailed)`, `StateChanged(CaptureState)`, `DiskFull()`, `IDisposable` (disposes timers), `ShortReason(string)`; one lock; timers via `TimeProvider.CreateTimer`.
- [x] `src/ReplayBuffer.Core/Sessions/SessionOrchestrator.cs`, `TargetGames.cs` -- `RecordingRunning` also publishes `CaptureState.Recording`.
- [x] `src/ReplayBuffer.Engine/Ipc/Warnings.cs` -- `Withdraw`, implement `IFeedbackNotices`.
- [x] `src/ReplayBuffer.Engine/Sessions/DevSimulator.cs` (new) -- `DevSimulator(Instance, Action<bool> recording, Action diskFull, Action<ClipFailed> saveFailed)` with `Empty Simulate(DevSimulate)`; throws `not-dev-instance` for `Instance.InstalledName`.
- [x] `src/ReplayBuffer.Engine/EngineHost.cs` -- create the router after `warnings`; feed it from the state publish and the `Saved`/`NotSaved` lambdas; register `dev.simulate` (recording → `sessions.Post(new RecordingRunning(..))`, disk full → `router.DiskFull()`, save failure → the same not-saved path as `NotSaved` with `save-failed`); dispose the router after `Sessions`; update the class summary.
- [x] `tests/ReplayBuffer.Core.Tests/FeedbackRouterTests.cs` (new) -- saved notice dismissed after 10 s by manual time; failed notice with reason, dismissed by the next save.
- [x] `tests/ReplayBuffer.Engine.Tests/DevSimulateServeTests.cs` (new) -- over a test instance's pipe, each event raises its notice or capture state; `DevSimulator` with the installed instance refuses with `not-dev-instance`.

**Acceptance Criteria:**
- Given a saved Clip, when the router is told, then exactly one `clip-saved` notice (info, overlay, sound `clipSaved`) is active, and 10 s later it is dismissed.
- Given a failed save with reason `disk-full`, when it is reported, then a `clip-failed` notice with message containing "disk full" and sound `saveFailed` is active until the next saved Clip, which dismisses it.
- Given a test instance, when the App sends `dev.simulate` `recordingStarted`, `recordingStopped`, `diskFull`, `saveFailed`, then the snapshot shows the Recording running then off with `recording-started`/`recording-stopped` notices, a `disk-full` notice, and `lastClipFailure` `save-failed` with a `clip-failed` notice.
- Given the installed instance, when `dev.simulate` is sent, then it fails with `not-dev-instance`.
- Given the Contracts tests, when they run, then every new code is in `ErrorCodes.All` and every registered event maps to a snapshot field.

## Implementation Notes

- Notice messages: "Clip saved", "Clip not saved: <short reason>", "Recording started", "Recording stopped", "The disk is full".
- `Warnings` implements `IFeedbackNotices.Raise` explicitly (overlay, dismissable, with the sound); `Withdraw` is public.
- The router calls `Warnings` while holding its own lock; `Warnings` never calls back, so the lock order is router then warnings.
- The engine's not-saved path is one `notSaved` lambda in `EngineHost`, used by `ReplayClips.NotSaved` and by `dev.simulate saveFailed`, so it works in a test instance with no Replay Buffer.

## Plan Change Log

## Review Triage Log

## Design Notes

- The router derives Recording notices from `CaptureState` transitions, so epic-recordings gets them by publishing state, with no extra call. It remembers the last `Recording` it saw.
- Recording started/stopped auto-dismiss after 10 s like `clip-saved` (both info); FR-6 gives no timing and the status line would otherwise fill with them.
- The simulated save failure reuses the engine's not-saved path (publish `clip.failed`, feed the router) rather than adding orchestrator plumbing: a dev stub, so a test instance with no Replay Buffer can still exercise it.
- `Warnings.Withdraw` is the engine's own dismissal: no `WarningNotDismissable` check, and silent when the user already dismissed it.

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings, 0 errors
- `dotnet test --project tests/ReplayBuffer.Contracts.Tests -c Release --no-build`, same for `ReplayBuffer.Core.Tests` and `ReplayBuffer.Engine.Tests` -- expected: all pass
- `pwsh tools/publish-engine.ps1` -- expected: 0 IL warnings
