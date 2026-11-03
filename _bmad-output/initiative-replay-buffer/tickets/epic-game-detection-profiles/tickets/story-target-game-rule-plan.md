---
title: 'Target Game rule'
type: 'feature'
ticket: '3'
created: '2026-11-03'
status: 'built'
baseline_revision: '5f54ad6afe33bca4d1bf017f2a4a080f6d33c1d1'
route: 'full'
route_source: 'auto'
risk: 'high'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
followup_review_recommended: false
context: []
warnings: ['oversized']
deferred: []
---

<intent-contract>

## Intent

**Problem:** After 3.2, GameMonitor proposes every newly detected game in front at once (the tracer rule) and a game already known never takes over by focus. Nothing blocks a switch while a Recording runs, the Target's exit always proposes no Target even when another game is running, and a cold start has no rule.

**Approach:** GameMonitor gets the R2 proposal rules: 10 consecutive seconds of focus, exit handover to the most recently focused other detected game, and a cold start choice. The SessionOrchestrator gets a stub Recording-running flag. While the flag is set it holds a switch away from a live Target as pending, and commits the pending switch the moment the flag clears.

## Boundaries & Constraints

**Always:**
- AD-19: GameMonitor only proposes; `target.changed` comes only from the orchestrator's commit. AD-18 handover and cold start as written there.
- "Focus" = the process of `ForegroundWindow()` (any shape, an owned dialog counts) as sampled each poll. The streak starts at the first poll that sees that process in front, even before it is detected, and any other foreground (another process, the shell, none) breaks it. A switch happens at the first poll where a detected game ≠ the monitor's desired Target has streak ≥ 10 s.
- Time comes from an injected `TimeProvider` (default `TimeProvider.System`). Tests never sleep for the 10 s.
- The block applies to every switch from a live Target to another game except an `Exit` handover (the Target is gone, so it isn't a switch). The pending proposal is replaced by each later blocked one. A proposal for the current Target's process clears it, and so does any commit.
- Contracts unchanged: `TargetChangeReason` already has `Focus`, `Exit`, `Detected`, `ColdStart`.
- Lean tests: one or two per AC, plus the cases that would pick the wrong Target.

**Never:**
- The 30 s drain itself (epic-replay-buffer-clips): this story only proposes `to: null, reason: exit` when no other game is detected.
- Ignored (3.4), real Recordings (epic-recordings): the flag is a stub message, set only by tests until then.
- Contracts or App changes. Edits outside this story's files: story 2.8 runs in parallel.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Focus 9 s / 10 s | Target A; detected B in front for 9 s, then 10 s | no proposal at 9 s; `B, focus` at 10 s, once | — |
| Broken streak | B in front 6 s, A 1 poll, B 6 s | no proposal | — |
| New game in front with a Target | B launched in front while A is the Target | `game.detected` only; B needs its 10 s (the tracer's at-once rule ends) | — |
| No Target | detected games, desired Target none (after the first poll) | foreground detected game, else most recently started; reason `detected` | — |
| Cold start | first poll finds A (background) and B (front) | `B, cold-start` at once; no foreground game → most recently started | — |
| Exit handover | Target A exits; B and C detected, C focused last | `C, exit` at once; never focused → most recently started | none left → `null, exit` |
| Recording blocks | flag set; `B, focus` proposed with Target A | nothing committed; flag cleared → `A→B, focus` at once | proposal for A meanwhile → pending dropped |
| Blocked Target exits | flag set, pending B, A exits | monitor reads committed A, proposes handover; `Exit` commits through the block | — |
| Pending game exits | flag set, pending B, B exits | monitor proposes committed A again (pending dropped); nothing published | — |

</intent-contract>

## Code Map

- `src/ReplayBuffer.Platform.Windows/GameMonitor.cs` -- `IGameProcess` (add `DateTime StartTime`, UTC), `ForegroundGameWindowProbe.LimitedProcess` (read it once with `GetProcessTimes` on the limited handle; add the name to `NativeMethods.txt`), `GameMonitor` (`Poll`, `ScanListed`, `Follow`, `ForgetExited`, `Propose`, `Seen`, `_proposed`). Constructor gains, after the existing parameters, `Func<int?>? target = null` (the committed Target's pid, read each poll; null → `_proposed`) and `TimeProvider? time = null`.
- `src/ReplayBuffer.Core/Sessions/TargetGames.cs` -- add `public sealed record RecordingRunning(bool Running) : SessionMessage`, the stub flag until epic-recordings.
- `src/ReplayBuffer.Core/Sessions/SessionOrchestrator.cs` -- `Handle` (new case), `CommitTarget` (block or pending), fields `_recordingRunning`, `_pendingTarget`.
- `src/ReplayBuffer.Engine/EngineHost.cs` (~lines 234–248) -- pass `() => hub.State.TargetGame?.ProcessId` and `hostOptions.Time` to `GameMonitor`.
- Tests: `tests/ReplayBuffer.Platform.Windows.Tests/GameMonitorTests.cs` (`FakeProbe`, `FakeProcess`; models for a manual `TimeProvider`: `tests/ReplayBuffer.Engine.Tests/WriterQueueTests.cs` `ManualTime`), `tests/ReplayBuffer.Core.Tests/SessionOrchestratorTests.cs` (`TheTargetFollowsProposalsAndTheDebugCaptureFollowsTheTarget` is the model), `tests/ReplayBuffer.Engine.Tests/GameDetectionServeTests.cs` (`FakeProcess` needs `StartTime`).

## Tasks & Acceptance

**Execution:**
- [x] `GameMonitor.cs`, `NativeMethods.txt` -- `IGameProcess.StartTime`. `Seen` gains `LastFocused` (timestamp, 0 = never) and `StartTime`. The monitor keeps `_focusPid`, `_focusSince` and `_firstPoll`. `Poll` order: (1) `ForgetExited`. For exited detected games, fire `exited`. With T = committed (`target()` or `_proposed`) and D = `_proposed`: if T exited, propose `Best(remaining)` or null, reason `Exit`. Else, if D exited and D ≠ T, propose T's candidate (or null), reason `Exit`. (2) Detection without proposals: the fullscreen rule and the listed scan as now. A known game in front updates its candidate's window and re-proposes only when it is D and the window moved. (3) The focus streak and `LastFocused` from `ForegroundWindow()`. (4) Decide: if D is null and games are detected, propose the foreground detected game, else `Best(all)`, with reason `ColdStart` on the first poll and `Detected` otherwise. Else, if a detected F ≠ D has streak ≥ 10 s, propose F, reason `Focus`. `Best` = highest `LastFocused`, then latest `StartTime`. -- R2's proposal rules.
- [x] `TargetGames.cs`, `SessionOrchestrator.cs` -- `RecordingRunning` message. `CommitTarget`: same process → clear pending, follow window (existing). Else, if the flag is set, a Target exists, `to` isn't null and the reason isn't `Exit` → store it as pending and return. Otherwise clear pending and commit (existing). `RecordingRunning(false)` with a pending one → `CommitTarget(pending)` at once. -- AD-19's block and re-evaluation.
- [x] `EngineHost.cs` -- wire `target` and `time` as above.
- [x] Tests (lean). Platform.Windows, with a manual clock and an empty first poll in the fixture so later polls aren't cold starts: 9 s of focus doesn't switch and 10 s does (once); a broken streak doesn't; a newly detected game in front with a Target isn't proposed. Exit handover picks the most recently focused other game, and the last game exiting proposes null. A cold start (fresh monitor) with two games picks the foreground one, else the most recently started. With `target` fed and D ≠ T (a blocked switch), the committed Target exiting hands over, and the pending game exiting re-proposes the committed Target. Rewrite the 3.2 tests that encoded the tracer's at-once takeover (`AnotherKnownGamesNewWindowDoesNotTakeOver`, `AKnownGameInFrontIsProposedAgainOnceTheTargetHasExited`, `ABackgroundListedGameDoesNotTakeOverTheTargetButOneInFrontDoes`); keep the rest. Core: the flag blocks `A→B` until cleared, then it commits at once; a proposal for A while blocked drops the pending switch; `Exit` commits through the block. Engine: `FakeProcess.StartTime`; wait for the first polls before adding games; one serve test where Target A exits and a background listed B gets `target.changed { A → B, exit }`.

**Acceptance Criteria:**
- Given two detected games with A the Target, when B is in front for 9 s, then nothing changes; when it reaches 10 s, then `target.changed { A → B, reason: focus }`.
- Given the stub Recording flag set, when B reaches 10 s of focus, then no `target.changed` is published; when the flag clears, then `A → B, focus` is published at once.
- Given A the Target and B detected, when A exits, then `target.changed { A → B, reason: exit }` comes at once (most recently focused other game); with no other game, `to: null, reason: exit`.
- Given two games already running at engine start with B in front, when the first poll runs, then B becomes the Target at once with reason `cold-start`.

## Implementation Notes

- Every proposal now carries its real reason (3.1/3.2 sent `detected` for all). `Follow`'s re-proposal of the desired Target keeps that proposal's reason (`_proposedReason`), so a held focus switch whose window moves still commits as `focus` (fixed by the session after the implementer's report).
- The 3.2 tests that encoded the tracer's at-once takeover were rewritten; the monitor fixture polls once empty so later polls aren't cold starts; the engine fixture waits for its first polls before a test adds games.
- `IGameProcess.StartTime` comes from `GetProcessTimes` on the limited handle (AD-12), `DateTime.MinValue` when unreadable.

## Plan Change Log

## Review Triage Log

### 2026-11-03 — Review pass
- verdicts: 3 findings — high 0, medium 1, low 1, false 1, maybe-false 0
- findings:
  - `[medium]` `[patch]` While a Recording holds a switch (desired B, committed A), a window move of the committed Target A wasn't proposed, so A's capture kept its old window. The cause was that `Follow` re-proposed only the desired Target. Fixed: a `Committed()` helper; `Follow` also re-proposes the committed Target on a window move (reason `detected`, which drops the held switch, since A is in front), and new test `WithASwitchHeldTheCommittedTargetsNewWindowIsProposed`. Latent until epic-recordings sets the flag.
  - `[low]` `[patch]` In the commit lag (desired A proposed, not yet committed, so committed is null), A exiting proposed null instead of handing over, giving A → null → C rather than A → C, exit. Fixed: that branch proposes `committed is null ? Best() : Detected(committed)`.
  - `[false]` `[reject]` "The 3.2 test `ABackgroundListedGameDoesNotTakeOverTheTargetButOneInFrontDoes` was dropped, losing the background-listed-game check": the engine test `TheTargetExitingHandsOverToABackgroundListedGame` adds the listed game after A is the Target and requires `game.exited` as the next event after its `game.detected`, so a stray takeover fails it. The in-front case is `ANewGameInFrontWithATargetIsOnlyDetected`.

## Design Notes

- The monitor tracks its desired Target (`_proposed`) and reads the committed one, so it never proposes twice in the commit lag and still sees a blocked Target's exit. Without `target` (unit tests), committed = desired.
- A pending switch whose game exits is cancelled by re-proposing the committed Target: the orchestrator sees the same process and drops it.
- `Exit` passes the block: the Target is gone, and its game Recording ends with it (epic-recordings).

## Verification

**Commands:**
- Before every build or test, check that `%USERPROFILE%\Documents\repos\ReplayBuffer\.bench-running` doesn't exist. Never run the full solution test suite. Commit nothing; the session commits.
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings, 0 errors
- `dotnet test --project tests/ReplayBuffer.Platform.Windows.Tests -c Release --no-build`, same for `ReplayBuffer.Core.Tests` and `ReplayBuffer.Engine.Tests` -- expected: all pass
- `pwsh tools/publish-engine.ps1` -- expected: 0 IL/AOT warnings

## Auto Run Result

- **Change:** GameMonitor proposes by R2's rules:
  - A detected game other than the desired Target is proposed after 10 consecutive seconds of focus (reason `focus`). The time comes from an injected `TimeProvider`.
  - A newly detected game no longer takes over an existing Target at once.
  - When the Target exits, the most recently focused other detected game, else the most recently started, takes over at once (reason `exit`). With none left, it proposes `null, exit`.
  - On the first poll, the foreground detected game, else the most recently started, is chosen (reason `cold-start`).

  The SessionOrchestrator takes a stub `RecordingRunning` flag. While it is set, a switch away from a live Target (anything except an exit handover) is held as pending, and it is committed the moment the flag clears. The monitor reads the committed Target from the hub, so a held Target's exit is still handed over.
- **Files:**
  - Platform.Windows: `GameMonitor.cs` (the rules, `IGameProcess.StartTime` via `GetProcessTimes`) and `NativeMethods.txt`.
  - Core: `TargetGames.cs` (`RecordingRunning`) and `SessionOrchestrator.cs` (block, pending, re-evaluation).
  - Engine: `EngineHost.cs` (committed Target and `Time` wiring).
  - Tests: `GameMonitorTests.cs`, `SessionOrchestratorTests.cs`, `GameDetectionServeTests.cs`.
- **Review:** quick lens, 3 findings: 2 patches applied (1 medium, 1 low), 1 false finding rejected (see the triage log), nothing deferred.
- **Follow-up review recommended:** false (one medium and one low patched, no high).
- **Verification:**
  - Release build: 0 warnings.
  - Platform.Windows 25/25, Core 58/58, Engine 144 passed (17 explicit skipped).
  - `publish-engine.ps1`: clean.
  - App, Media, Capture and Audio suites were not run, since the diff doesn't touch them.
- **Residual risks:**
  - The real `GetProcessTimes` read is untested (an unreadable start time sorts as oldest).
  - Nothing in production sets `RecordingRunning` until epic-recordings.
  - A window move of the committed Target drops a held switch.
  - Ignored switches (3.4) are blocked like focus switches until 3.4 decides otherwise.
