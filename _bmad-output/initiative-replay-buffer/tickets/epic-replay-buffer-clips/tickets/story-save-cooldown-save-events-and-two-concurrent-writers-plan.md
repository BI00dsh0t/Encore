---
title: 'Save Cooldown, save events and two concurrent writers'
type: 'feature'
ticket: '4'
created: '2026-11-03'
status: done
baseline_revision: 'f7814bac1b9ea60bb84ca616e260316352559dc0'
route: 'full'
route_source: 'auto'
risk: 'medium'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
followup_review_recommended: false
context: ['{project-root}/CLAUDE.md']
warnings: ['oversized']
deferred: []
---

<intent-contract>

## Intent

**Problem:** Every `save` press is acted on. Nothing debounces presses (R5's Save Cooldown), nothing tells the Overlay a save has started (`clip.saving`), and nothing limits how many Clips write at once. A press made while an earlier Clip is still writing also cuts back over that Clip (4.2's deferred finding), because the session's Reset moves only once a write succeeds.

**Approach:** The orchestrator gates each `save` press. A press within `saveCooldown` seconds (Global, Immediate, read from the current document at each press) of the last *accepted* press's Hotkey QPC is ignored: it gets no Clip and no event, only a log line. Any other press is accepted. It starts a new cooldown, publishes `CaptureState.CooldownUntil`, and emits `clip.saving` at once, then `clip.saved` or `clip.failed`. At most two writes run at once, counted across sessions. A press accepted while two are writing fails with `clip.failed { reason: save-busy }`: the session isn't asked for anything and there is no Reset. Writers already run at background I/O priority (`ClipWriter.EnterBackgroundMode`) and post back to the orchestrator; keep both. In the session, a write whose end key is known claims the span up to it, so a concurrent write cuts from that point and doesn't overlap the first. If the first write fails, its claim is rolled back when no later write has moved past it.

## Boundaries & Constraints

**Always:**
- AD-19: only the orchestrator gates presses, counts writers and publishes `clip.saving`, `clip.saved`, `clip.failed` and `capture.stateChanged`. Writers post back to its channel.
- AD-3/AD-22: Contracts change additively. Add `ClipSaving(DateTimeOffset At)` as `clip.saving`, with the snapshot field `lastClipSaving`, and `ErrorCodes.SaveBusy = "save-busy"`.
- The cooldown is measured in Hotkey QPC (Stopwatch ticks) from the accepted press. `saveCooldown` 0 disables it. It is global across sessions.
- The ring keeps capturing through the cooldown and while writers are busy; no Reset happens on an ignored or busy press.
- 4.2's 1 s `clip-too-short` refusal and 4.3's drain behave as before (a press past the cooldown is accepted first, then refused if it is too short).
- Lean tests (CLAUDE.md "Tests"): QPCs are synthetic in Core tests; no real-time waits beyond short ones.

**Never:**
- `clip.failed` for writer exceptions, retry semantics or `Library.BeginWrite/EndWrite` (4.5). Ring generations (4.6). Disk mode (4.7). App or Overlay UI (epic 5).
- Clearing `CooldownUntil` with a timer: it's an absolute time, which clients compare with now.
- Asking the session for a keyframe or a write on an ignored or busy press.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Press in the cooldown | `saveCooldown` 5; presses 1 s and 4 s after an accepted press | No `Save` call, no event, no state change | Logged at Information |
| Press after the cooldown | A press 5.1 s after the accepted one | `clip.saving`, then `clip.saved`; a new cooldown starts | — |
| Two writers busy | `saveCooldown` 0; two writes blocked; a third press | `clip.saving`, then `clip.failed { save-busy }`; `Save` isn't called; no Reset | Logged at Information |
| After the writers finish | Both blocked writes complete; another press | Saved. The Clip runs from the last successful Reset, so it holds the busy press's content too | — |
| Concurrent cut | A second write starts while the first still writes | The second Clip starts at the first's end key, not back over it | First fails: its claim is rolled back if nothing later moved past it |
| Saving latency | Any accepted press | `clip.saving` within 250 ms of the post (synchronously on the orchestrator thread) | — |

</intent-contract>

## Code Map

- `src/ReplayBuffer.Core/Sessions/SessionOrchestrator.cs` -- `SaveClip(qpc)` (lines ~639–685) picks the active game session or the drain, applies the 1 s `ResetQpc` refusal, calls `Session.Save`, allocates the path and starts `write.Write`. `OnClipWritten` handles the post-back (an error goes to `ReportSaveFailure`; success Resets and calls `Saved`). `ReportSaveFailure` sends `ClipTooShort` to `NotSaved` and others to `Failed`. `SetState`/`_state` publish `CaptureState` (which has `CooldownUntil`). Add the orchestrator-thread fields `_cooldownFrom` (long?) and `_writing` (int). `_writing` goes up after `write.Write` returns and down in `OnClipWritten`, whatever the session.
- `src/ReplayBuffer.Core/Sessions/TargetGames.cs` -- `ReplayClips(..., NotSaved = null, ExitDrain = null)`. Add a last optional `Action<ClipSaving>? Saving = null`, so existing callers still compile. `save-busy` goes to `NotSaved`.
- `src/ReplayBuffer.Core/Settings/SettingsResolver.cs` -- `Resolve(document, null).SaveCooldown` (int seconds) is the Global value.
- `src/ReplayBuffer.Contracts/Messages/Capture.cs` -- `ClipFailed`/`CaptureMessages.Failed` is the pattern. Add `ClipSaving`, `CaptureMessages.Saving` (to `Events` and `[JsonSerializable]`).
- `src/ReplayBuffer.Contracts/Messages/Engine.cs` -- add `StateSnapshot.LastClipSaving` beside `LastClipFailure`.
- `src/ReplayBuffer.Contracts/ErrorCodes.cs:137,180` -- add `SaveBusy` beside `ClipTooShort`, including in its list.
- `src/ReplayBuffer.Engine/EngineHost.cs:248` -- composes `ReplayClips`. `Saving` publishes `CaptureMessages.Saving` (logged at Debug).
- `src/ReplayBuffer.Engine/Sessions/CaptureSession.cs` -- `ClipWrite.Run` (lines ~1048–1105) cuts `[max(pressUs − window, _resetUs), toUs)`. `ResetTo` is the forward-only CAS pattern. Add `_claimedUs` (forward only). After `WaitForKey`, a write records the previous claim and claims `toUs`. `fromUs` also takes `max(..., claim before this write)`. On failure, `CompareExchange(_claimedUs, previous, toUs)`. Read-only: `Save`, `WaitForKey`, the ring's leasing.
- `src/ReplayBuffer.Platform.FFmpeg/ClipWriter.cs:150` -- already enters `THREAD_MODE_BACKGROUND_BEGIN`. Unchanged.
- Tests:
  - `tests/ReplayBuffer.Core.Tests/SessionOrchestratorTests.cs`: `FakeFactory { Save = … }`, `FakeWrite` (completes at once; add an optional gate), `NextAsync`, `Revision(n)` and `_document`. Existing multi-press tests (`APressUnderASecondAfterAReset…`, the drain test) need `saveCooldown` 0 in their document.
  - `tests/ReplayBuffer.Contracts.Tests/MessagesTests.cs:22`: the samples map needs `clip.saving`.
  - `tests/ReplayBuffer.Platform.FFmpeg.Tests/ReplayCutTests.cs:107–116`: writes two cuts one after the other.
  - `tests/ReplayBuffer.Engine.Tests/ReplayClipCaptureTests.cs:112,204`: these press twice within 5 s, so set `saveCooldown` 0 in their Global settings.

## Tasks & Acceptance

**Execution:**
- [x] `src/ReplayBuffer.Contracts/{ErrorCodes,Messages/Capture,Messages/Engine}.cs` -- `SaveBusy`, `ClipSaving(DateTimeOffset At)`, `clip.saving` → `lastClipSaving` -- the events for epic 5, additive (AD-22)
- [x] `src/ReplayBuffer.Core/Sessions/{TargetGames,SessionOrchestrator}.cs` -- `ReplayClips.Saving`. In `SaveClip`, after finding the session:
  - The cooldown check: ignore the press and report it through a new last optional `Action<TimeSpan>? Ignored = null` on `ReplayClips` (the time since the accepted press), which EngineHost logs. Core has no `ILogger`.
  - Accepting the press: set `_cooldownFrom`, publish `CooldownUntil` (now plus what's left of the cooldown), and call `Saving(new ClipSaving(now))`.
  - With `_writing ≥ 2`: `NotSaved(new ClipFailed(SaveBusy))`. Otherwise the existing too-short check and `Save` follow.
  - Count writes up and down.
  - R5 cooldown, R6 two writers and events, under AD-19.
- [x] `src/ReplayBuffer.Engine/EngineHost.cs` -- wire `Saving` (publish `clip.saving`) and `Ignored` (log at Information) -- events reach the App
- [x] `src/ReplayBuffer.Engine/Sessions/{ClipClaim,CaptureSession}.cs` -- the claim as a small internal class `ClipClaim` (forward-only `Claim(toUs)` returning the previous claim, `Start` the current one, `Rollback(previous, toUs)` by CAS), used by `ClipWrite.Run` -- concurrent Clips don't overlap (4.2's deferred item)
- [x] `tests/ReplayBuffer.Core.Tests/SessionOrchestratorTests.cs`:
  - (1) Cooldown: default `saveCooldown` 5. Press at T, saved. Presses at T+1 s and T+4 s make no `Save` call and no saving, saved or failed event. A press at T+5.1 s saves. Each `clip.saving` arrives within 250 ms of its post, and `Ignored` is called twice.
  - (2) Two writers: `saveCooldown` 0, gated `FakeWrite`s. Two presses start two writes with distinct paths. A third gets `clip.saving`, then `save-busy`, and `Save` is called only twice. Open both gates: two saved, each Reset once. A next press saves.
  - Set `saveCooldown` 0 in the existing multi-press tests.
- [x] `tests/ReplayBuffer.Contracts.Tests/MessagesTests.cs` -- the `clip.saving` sample
- [x] `tests/ReplayBuffer.Platform.FFmpeg.Tests/ReplayCutTests.cs` -- write the two Clips on two threads at once (cut both first, then `ClipWriter.Write` in parallel); the existing frame-count and duration asserts prove the two writers don't corrupt each other
- [x] `tests/ReplayBuffer.Engine.Tests/ReplayClipCaptureTests.cs` -- `saveCooldown` 0 in both tests' Global settings
- [x] `tests/ReplayBuffer.Engine.Tests/ClipClaimTests.cs` -- one test: write A claims its end, a concurrent write B starts there and claims further; A failing then rolls nothing back; a lone failed claim is rolled back

**Acceptance Criteria:**
- Given an accepted save at T with `saveCooldown` 5, when `save` is pressed at T+1 s and T+4 s, then no Clip is made and no `clip.saving`, `clip.saved` or `clip.failed` is emitted; when pressed at T+5.1 s, then one Clip is saved.
- Given two Clip writes blocked in flight, when `save` is pressed again past the cooldown, then `clip.saving` and then `clip.failed { reason: "save-busy" }` are emitted, the session is not asked to save, and nothing is Reset; when the writes finish and `save` is pressed, then a Clip is saved.
- Given any accepted press, when it is posted, then `clip.saving` arrives within 250 ms.
- Given two cuts of one ring written by two writers at once, when both finish, then each Clip has exactly its own frames (301 and 300) and Tracks within one AAC frame of its video.
- Given the default run, when the 0-warning Release build, the Contracts, Core, Platform.FFmpeg and Engine default suites and the NativeAOT publish run, then all pass.

## Implementation Notes

- An accepted press whose `Session.Save` returns null (the ring isn't anchored yet) now gets `clip.failed { clip-too-short }`, so every accepted press shows saving and then an outcome. Before, it was silent.
- With `saveCooldown` 0, `CooldownUntil` is published as null.
- `ReplayClips.MaxWriters` (2) holds the writer limit.
- After review: `ClipClaim` keeps the in-flight claim ends and the highest succeeded end under a lock (`Claim`, `Succeeded`, `Failed`), instead of one CAS value with a rollback, so failures in any order leave the start at the last succeeded end. The 250 ms check is structural: `clip.saving` fires on the orchestrator thread before the session's `Save` is called (no wall-clock bound in the default run, per CLAUDE.md "Tests").

## Plan Change Log

## Review Triage Log

### 2026-11-03 — Review pass
- verdicts: 3 findings — high 0, medium 1, low 2, false 0, maybe-false 0
- findings:
  - `[medium]` `[patch]` `ClipClaim.Rollback`: when two concurrent writes both fail (disk full hits both), A then B, B restored A's failed end, so A's span was left out of every later Clip; the other order ended at MinValue. Fixed: `ClipClaim` tracks the in-flight ends and the highest succeeded end; a failure only drops its own claim. `ClipClaimTests` covers both failure orders.
  - `[low]` `[patch]` The cooldown test asserted a wall-clock 250 ms bound in the default run, which CLAUDE.md "Tests" forbids. Fixed: the fake's `Save` records how many `clip.saving` events had fired, and the test asserts each fired before its `Save` (synchronous, on the orchestrator thread).
  - `[low]` `[patch]` The busy test didn't check that a `save-busy` press Resets nothing. Fixed: before the gates open, it asserts `Save` ran twice and both writes' Resets are `[0, 0]`.

## Design Notes

**What "accepted" means.** A press past the cooldown is accepted whatever follows: it starts the next cooldown, publishes `CooldownUntil`, and gets `clip.saving`, then `clip.saved` or `clip.failed` (`save-busy`, `clip-too-short`). That keeps one rule for the Overlay: every accepted press shows saving, then an outcome. A press with no game session or drain is ignored before any of this. (4.5 adds `clip.failed` for writer exceptions; until then a writer failure after `clip.saving` reaches `Failed` and is only logged.)

**CooldownUntil.** `DateTimeOffset.UtcNow + (cooldownFrom + cooldown·freq − Stopwatch.GetTimestamp()) / freq`, published with `SetState`. It is only the hint the architecture's `capture.stateChanged` names; gating uses QPC.

**The claim.** `ClipClaim` is ring time, not a Reset. The Reset (`_resetUs`, from the orchestrator) still moves only on success, so a busy or failed save leaves the next Clip starting at the last Reset. A concurrent write starts at `max(window start, _resetUs, claim)`, where the claim is the highest end of an earlier write still in flight or succeeded; a failed write drops its own claim. Saves are seconds apart, so an earlier write's key is known before a later write cuts.

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings, 0 errors
- `dotnet test --project tests/ReplayBuffer.Contracts.Tests -c Release --no-build` -- expected: all pass
- `dotnet test --project tests/ReplayBuffer.Core.Tests -c Release --no-build` -- expected: all pass
- `dotnet test --project tests/ReplayBuffer.Platform.FFmpeg.Tests -c Release --no-build` -- expected: all pass
- `dotnet test --project tests/ReplayBuffer.Engine.Tests -c Release --no-build` -- expected: all pass
- `pwsh tools/publish-engine.ps1` -- expected: 0 IL/AOT warnings

## Auto Run Result

- **Summary:** R5's Save Cooldown and R6's save events and two writers.
  - The orchestrator ignores a `save` press within the Global `saveCooldown` (5 s by default) of the last accepted press's Hotkey QPC: no Clip and no event, only an Information log line.
  - An accepted press starts the next cooldown, publishes `capture.stateChanged { cooldownUntil }`, and emits `clip.saving` at once, then `clip.saved` or `clip.failed`.
  - At most two Clip writes run at once, across sessions. A press accepted while two are writing fails as `clip.failed { save-busy }`: the session isn't asked for anything and nothing is Reset.
  - In the session, a write claims the ring up to its end key once that key is known, so a concurrent write cuts from there and doesn't overlap the first (4.2's deferred item). A failed write drops its claim.
  - Writers already run at background I/O priority and post completions to the orchestrator.
- **Files:**
  - Contracts: `ErrorCodes.cs` (`save-busy`); `Messages/Capture.cs` (`ClipSaving`, `clip.saving`); `Messages/Engine.cs` (`lastClipSaving`).
  - Core: `Sessions/TargetGames.cs` (`ReplayClips.Saving`, `Ignored`, `MaxWriters`); `Sessions/SessionOrchestrator.cs` (the cooldown, `CooldownUntil`, `clip.saving`, the writer count, `save-busy`).
  - Engine: `EngineHost.cs` (publishes `clip.saving`, logs ignored presses); new `Sessions/ClipClaim.cs`; `Sessions/CaptureSession.cs` (the claim in `ClipWrite.Run`).
  - Tests: `SessionOrchestratorTests` (cooldown, two writers and busy; existing multi-press tests set `saveCooldown` 0); `MessagesTests` (`clip.saving` sample); `ReplayCutTests` (the two Clips written on two threads at once); `ReplayClipCaptureTests` (`saveCooldown` 0); new `ClipClaimTests`.
- **Review:** quick lens, 3 findings. 3 patched (1 medium, 2 low). None deferred or rejected.
- **Follow-up review recommended:** false. One medium was patched, and no high.
- **Verification (after the patches):** `dotnet build ReplayBuffer.slnx -c Release` 0 warnings, 0 errors. Contracts 99/99, Core 69/69, Platform.FFmpeg 31 passed with 1 Explicit skipped, Engine 151 passed with 18 Explicit skipped. `tools/publish-engine.ps1` published `ReplayBuffer.Engine.exe` (10.5 MB) with no warnings. Heavy not run: the change gates presses and cut boundaries, and leaves capture, encode, mux and audio as they were.
- **Residual risks:**
  - A writer exception after `clip.saving` is only logged until 4.5 adds `clip.failed` for it, so the Overlay gets no outcome for that press yet.
  - An accepted press with nothing anchored now reports `clip-too-short` (before, it was silent).
  - No test runs two real `CaptureSession` writes at once. The claim is covered by `ClipClaimTests`, and two concurrent writers by `ReplayCutTests`.
  - `VideoEncoder.TryGetForcedKey` still keeps only the latest key (4.2). With `saveCooldown` 0 and presses under a frame apart, a write could read a later key.

