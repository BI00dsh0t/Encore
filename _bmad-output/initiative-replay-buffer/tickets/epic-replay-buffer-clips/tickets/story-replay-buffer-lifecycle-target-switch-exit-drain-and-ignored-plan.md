---
title: 'Replay Buffer lifecycle: Target switch, exit drain and Ignored'
type: 'feature'
ticket: '3'
created: '2026-11-03'
status: done
baseline_revision: '7bd8a7da0f6bfc778b6bbee96a62b3e05b8322e6'
route: 'full'
route_source: 'auto'
risk: 'medium'
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

**Problem:** When the Target Game exits or its window closes, the orchestrator ends the replay session at once and frees the ring, so a Save pressed a moment after a crash makes nothing (R1). Nothing distinguishes an exit (keep the content for 30 s) from Ignored (end at once), and `capture.stateChanged` never reports `draining`.

**Approach:** A replay game session that loses its Target by exit (`ProposeTarget(null, Exit)`) or by its window closing (`SessionClosed`) is *drained*, not ended. The session stops capture and finishes its encoders as `End` does, but keeps its ring. The orchestrator holds it as the draining session, publishes `Replay = Draining`, and still sends it `save` presses, which cut up to the end of what it holds. After `ExitDrain` (30 s by default) a timer posts back, and the orchestrator releases the ring and publishes `Off`. Starting any new session (a new Target, or a new window of the same process) and engine shutdown release the drain at once. A switch to another Target, or Ignored, ends the session with no drain (FR-38).

## Boundaries & Constraints

**Always:**
- AD-19: only the orchestrator decides drain, release and state. The drain timer only posts a message to its channel, tagged with the session id, so a stale expiry is ignored.
- AD-8: a save in flight at release keeps its own lease and pins: the Clip still completes. Releasing never blocks on a writer.
- Drain applies only with `ReplayClips` set (the Replay Buffer). `serve --debug-capture` (no `ReplayClips`) ends sessions exactly as before.
- The R5 Reset and the 1 s refusal still apply during a drain (the drained session keeps its `ResetQpc` and `_resetUs`).
- Minimizing, alt-tab and windowed mode already keep the session (WGC repeats frames; no proposal is made). Don't change that.
- Contracts: no change (`ReplayState.Draining` already exists).
- Lean tests per `CLAUDE.md` "Tests": no 30 s real-time waits; tests set a short `ExitDrain`.

**Never:**
- Save Cooldown, `save-busy` or concurrent-writer rules (4.4); `clip.failed` for writer failures (4.5); ring generations (4.6); Disk mode (4.7).
- Changing `GameMonitor`'s handover rules, or keeping a drained ring across a new session.
- Asking the stopped encoder for a keyframe in a drained save.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Exit, then Save | Buffering; Target exits (`ProposeTarget(null, Exit)`) or its window closes; `save` within `ExitDrain` | State `draining`; the Clip runs from the last Reset (or window start) to the end of the buffer; `clip.saved` | — |
| Drain expires | `ExitDrain` passes with no new Target | Ring released (`Release`), state `off`; a later `save` makes nothing | — |
| New Target during drain | `ProposeTarget(other game, Detected/Focus)` or a new window of the same process | Drain released at once, then the new session starts (`buffering`) | — |
| Target switch | Buffering on A; `ProposeTarget(B, Focus)` | A is ended (not drained), its content discarded; B starts with a fresh ring | — |
| Ignored | Buffering; `ProposeTarget(null or other, Ignored)` | Ended at once, no drain; state `off` (or `buffering` on the other game) | — |
| Exit with another game | `ProposeTarget(other, Exit)` | Treated as a switch: ended, no drain | — |
| Engine stops mid-drain | Orchestrator disposed | The drain is released | — |
| Save in flight at release | A drained save still writing when the drain expires | The Clip completes from its lease | — |

</intent-contract>

## Code Map

- `src/ReplayBuffer.Core/Sessions/SessionOrchestrator.cs` -- `CommitTarget` (ends `_active` when `_targetCapture` is set, then `StartOnTarget`), `FollowTargetWindow`, `Start(kind, target, audio)` (throws `SessionRunning` when `_active`), `EndActive`, `Handle` (`SessionClosed`/`SessionFailed` matched by `_active?.Id`), `SaveClip` (uses `_active`), `GameName` (reads `_target`, which is null after an exit), `OnClipWritten` (Resets only `_active`), `Run` (ends `_active` on shutdown), private `Active` class.
- `src/ReplayBuffer.Core/Sessions/CaptureSessionPorts.cs` -- `ICaptureSession` (`End`, `Save` default null). New members must be default interface members so test fakes and other sessions compile.
- `src/ReplayBuffer.Core/Sessions/TargetGames.cs` -- `ReplayClips(Paths, DefaultOutputFolder, Saved, Failed, NotSaved = null)`: add a last optional `TimeSpan? ExitDrain = null` (null → 30 s, a public const/static `DefaultExitDrain`).
- `src/ReplayBuffer.Engine/Sessions/CaptureSession.cs` -- `End(reason)` (lines ~290–357: stop capture, `_paced.Complete`, unsubscribe, `Finish`, `_outputFinished = true`, `DisposeAll`), `DisposeAll` (`_ring?.Dispose()` at the end), `Save(qpc)` (needs `_ring` and `_video`), `ClipWrite` (`Write` calls `video.RequestKeyframe`; `WaitForKey` returns the press once `_outputFinished`). `ReplayRingSink.Lease/Release/Dispose`: the ring is freed when the session's reference and every lease are gone.
- `src/ReplayBuffer.Engine/EngineHost.cs:248` -- composes `ReplayClips`; keeps the default drain.
- `src/ReplayBuffer.Platform.Windows/GameMonitor.cs:617,647` -- `HandOver` proposes `Best()` (another game or null) with reason `Exit`/`Ignored`. Read-only.
- Tests: `tests/ReplayBuffer.Core.Tests/SessionOrchestratorTests.cs` (`FakeFactory { Save = … }`, `FakeSession` with `SaveWith`, `Ended`, `FakeWrite`, `Sessions[i]`), `tests/ReplayBuffer.Engine.Tests/ReplayClipCaptureTests.cs` (in-process pattern: `ToneHub`, `SessionTestKit.OpenWindow` with `_processes`, `ProposeTarget`, `Anchored`, `published` states).

## Tasks & Acceptance

**Execution:**
- [x] `src/ReplayBuffer.Core/Sessions/CaptureSessionPorts.cs` -- add `SessionOutcome Drain(string reason) => End(reason);` (stops capture and finishes the output like `End`, but a session holding a Replay Buffer keeps it, and `Save` still cuts from it, until `Release`) and `void Release() { }` (frees a drained Replay Buffer; a write in flight keeps its lease) -- the port for R1's drain
- [x] `src/ReplayBuffer.Core/Sessions/TargetGames.cs` -- `ReplayClips.ExitDrain` (optional, default 30 s) -- R1's 30 s, short in tests
- [x] `src/ReplayBuffer.Core/Sessions/SessionOrchestrator.cs` -- a `_drain` (an `Active`, plus its timer). `DrainActive(reason)`: calls `Session.Drain`, completes `Ended` with the outcome, sets `_drain`, publishes `Draining`, starts a one-shot timer that posts `DrainExpired(id)`. `ReleaseDrain()`: disposes the timer, calls `Session.Release()` (exceptions swallowed), clears `_drain`, publishes `Off` unless a session is active. Rules: `ProposeTarget(null, Exit)` on an active replay game session, and `SessionClosed` for one, drain (when `_clips` is set; otherwise end as now); every other end (switch, Ignored, `End` command, failure) ends with no drain; `Start` releases any drain first; `DrainExpired` with the drain's id releases it; `Run`'s shutdown releases it. `SaveClip` uses the active game session, else the drain. `OnClipWritten` Resets the drain too when its id matches. `Active` remembers the Target's display name at `StartOnTarget`, and `GameName` uses it, so a drained Clip keeps the game's name -- R1, FR-38 under AD-19
- [x] `src/ReplayBuffer.Engine/Sessions/CaptureSession.cs` -- `Drain(reason)`: `End`'s teardown but `DisposeAll` keeps the ring (a `_keepRing`/`_drained` flag). `Save` on a drained session returns a write with no encoder (no keyframe request: `toUs` is the press, so the cut runs to the end of the buffer). `Release()` disposes the ring once (idempotent); `Save` after it returns null. Log the drain and the release at Information -- R1
- [x] `tests/ReplayBuffer.Core.Tests/SessionOrchestratorTests.cs` -- add the fakes' `Drain`/`Release` tracking (`FakeSession` counts them). Tests: (1) exit drain: a buffering session, `ProposeTarget(null, Exit)` → `Drain` called (not `End`), state `Draining`; a `save` press goes to the drained session and `Saved` fires; with `ExitDrain` 200 ms, after it `Release` is called, state `Off`, and a press calls no `Save`. (2) a new Target during a drain releases it before the new session starts; a switch A → B, and `ProposeTarget(B, Exit)`, end A with no drain; Ignored ends with no drain and state `Off`; disposing the orchestrator mid-drain releases it. (The matrix's "save in flight at release" is the lease rule `PacketRingTests` test 2 and the ring's `Lease` already cover; no new test.)
- [x] `tests/ReplayBuffer.Engine.Tests/ReplayClipCaptureTests.cs` -- one in-process test (default run): `ExitDrain` 3 s; propose the pwsh window as Target; ~3 s after the anchor kill its process and note the QPC; ~1 s later press `save` → a Clip whose video ends within 0.5 s of the kill (its last moments); state `Draining` was published; after the drain, state `Off`, and a press makes no Clip within 1.5 s

**Acceptance Criteria:**
- Given an in-process replay session on a test window, when its process is killed and `save` is pressed during the drain, then `clip.saved` names a Clip of the game's last moments and `capture.stateChanged` reported `draining`.
- Given the drain has expired, when `save` is pressed, then no Clip is made, the ring was released, and the state is `off`.
- Given a buffering session on game A, when Target B is committed, then A is ended with no drain and B's session starts with its own ring, so B's first Clip holds none of A's frames.
- Given a buffering session, when the Target is marked Ignored, then the session ends at once with no drain and the state is `off`.
- Given the default run, when the 0-warning Release build, the Core and Engine default suites and the NativeAOT publish run, then all pass.

## Implementation Notes

- `SessionOrchestrator.DrainActive` releases a drain that somehow still exists before taking the new one, and a `Drain` that throws publishes `Off` and releases at once (nothing to keep). `End` (the command) with only a drain is still refused with `SessionNotRunning`; the drain runs on.
- `CaptureSession.Save` on a drained session passes no encoder to `ClipWrite` (it checks `_outputFinished`), so `Write` asks for no keyframe and `WaitForKey` returns the press at once; `Release` disposes the ring once and makes later `Save`s return null.
- `Active.GameName` is set only by `StartOnTarget`; `GameName(active)` falls back to the old Target/profile rule for sessions started through `StartAsync`.
- The Core drain test uses an unlisted profile so the Clip's name proves the Target's name survives the exit.

## Plan Change Log

## Review Triage Log

### 2026-11-03 — Review pass
- verdicts: 4 findings — high 0, medium 2, low 2, false 0, maybe-false 0
- findings:
  - `[medium]` `[patch]` `DrainActive` held a session whose `Drain` returned a failure outcome (never anchored, or already failed) as the drain, published `Draining` for the full 30 s, and dropped its queued `SessionFailed`. Fixed: one check on `outcome.Failure` (throw or failure outcome) publishes `Off`, releases at once and keeps no drain. A Core assertion covers it (`DrainFailure` on the fake).
  - `[medium]` `[patch]` A drain started by `SessionClosed` survived a later `ProposeTarget(null, Ignored)`, so saves still cut Clips of a game just marked Ignored. Fixed: `CommitTarget` releases the drain on every committed change except an exit to no game. A Core assertion covers it (window closed, then Ignored: `Releases == 1`, state `Off`).
  - `[low]` `[reject]` The Core test infers "released before the new session starts" from the published state order, not a recorded call order. The code calls `ReleaseDrain()` before `_factory.Start`; under the lean-tests rule the extra ordering probe isn't worth it.
  - `[low]` `[reject]` AC3 ("B's first Clip holds none of A's frames") is checked only as A ended with no drain and B started. Each `CaptureSession` creates its own ring, so B can't hold A's frames; a two-game capture test is past the lean-tests rule.

## Design Notes

**Why `SessionClosed` drains too.** WGC reports the window closing before (or without) Game Detection seeing the process exit. Ending there would free the ring before the exit proposal arrives. A later `ProposeTarget(null, Exit)` finds no active session and leaves the drain running. A late `SessionClosed` from a session already drained doesn't match `_active` and is ignored.

**A new window of the same process.** If the Target's window closes and the process opens another, `FollowTargetWindow` starts a new session, which releases the drain. That matches pre-4.3 behaviour, where the window closing ended the session anyway.

**Drained save.** The encoders are flushed, so the ring holds everything it will. `WaitForKey` sees `_outputFinished` and returns the press. The cut is `[max(press − window, reset), press)`, which runs to the last packet. The Clip's start is still the first keyframe in it.

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings, 0 errors
- `dotnet test --project tests/ReplayBuffer.Core.Tests -c Release --no-build` -- expected: all pass
- `dotnet test --project tests/ReplayBuffer.Engine.Tests -c Release --no-build` -- expected: all pass
- `pwsh tools/publish-engine.ps1` -- expected: 0 IL/AOT warnings

## Auto Run Result

- **Summary:** R1's lifecycle. A replay game session whose Target exits (`ProposeTarget(null, Exit)`) or whose window closes is drained, not ended: capture stops and the encoders flush, but the ring is kept, `capture.stateChanged` reports `draining`, and `save` presses still cut Clips (to the end of the buffer, keeping the game's name) until `ReplayClips.ExitDrain` (30 s) passes. Then the ring is released and the state goes `off`. Any new session, any other committed Target change (a switch, Exit to another game, Ignored) and engine shutdown release the drain at once. A switch or Ignored ends the active session with no drain (FR-38). A session with nothing to keep (never anchored, or failed) is never drained. `serve --debug-capture` is unchanged.
- **Files:**
  - Core: `Sessions/CaptureSessionPorts.cs` (`ICaptureSession.Drain`/`Release`, default members); `Sessions/TargetGames.cs` (`ReplayClips.ExitDrain`, `DefaultExitDrain`); `Sessions/SessionOrchestrator.cs` (the drain, its timer and `DrainExpired`, the release rules, saves and Resets on the drain, the Target's name kept per session).
  - Engine: `Sessions/CaptureSession.cs` (`Drain` keeps the ring, `Release` frees it, a drained `Save` asks no keyframe).
  - Tests: `SessionOrchestratorTests` (two drain tests), `ReplayClipCaptureTests` (a killed window's process, then a save during the drain and none after it).
- **Review:** quick lens, 4 findings. Patched 2 medium (a failed session drained; Ignored not releasing a window-close drain). Rejected 2 low under the lean-tests rule (see the triage log). Nothing deferred.
- **Follow-up review recommended:** true by the rule (two mediums patched); the named risk is the ordering of a drain against a queued `SessionFailed`/`SessionClosed`, now covered by the failure-outcome check and two Core assertions. QA is the next pass.
- **Verification (after the patches):** `dotnet build ReplayBuffer.slnx -c Release` 0 warnings, 0 errors; Core 67/67; Engine 150 passed, 18 Explicit skipped, 0 failed; `tools/publish-engine.ps1` published `ReplayBuffer.Engine.exe` (10.5 MB) with no warnings. Heavy tests not run: the change keeps the ring alive past the session's end but doesn't alter capture, encode, mux or audio.
- **Residual risks:**
  - If the Target recreates its window while the process lives, the new session releases the drained buffer (as before 4.3, when a closed window ended the session).
  - The `End` command with only a drain held is refused with `SessionNotRunning`; the drain runs on until it expires.
  - No test waits the real 30 s; tests shorten `ExitDrain`.
- QA (2026-11-03, at `8446562`): PASS, 0 blocking. Two low notes, accepted without change:
  - A window close starts the drain, so the 30 s runs from the close, not the process exit. A process that outlives its window gets a drain that ends sooner after the exit. Nothing leaks.
  - Marking an exited game Ignored makes no proposal (no Target is left), so its drain runs to expiry, at most 30 s. Detection never starts a buffer for an Ignored game.
