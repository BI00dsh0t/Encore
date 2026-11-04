---
title: 'Clips during a Recording on one encoder chain'
type: 'feature'
ticket: '5'
created: '2026-11-03'
status: done
baseline_revision: '0795f8f20fab3c554a8340f121fea64791092c83'
route: 'full'
route_source: 'auto'
risk: 'medium'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
followup_review_recommended: false
context:
  - '{project-root}/CLAUDE.md'
warnings: []
deferred: []
---

<intent-contract>

## Intent

**Problem:** A Recording shares the game session's encoder chain (6.1), and the engine already refuses to arm 4.9's chain switch while a Recording is attached, but the orchestrator still asks the session to *prepare* another chain on every settings change and every Reset during a Recording. That idle second chain breaks the two-chain budget a Monitor Recording needs (AD-7), nothing logs the deferral, and nothing proves that Clip saves (their forced keyframes, Resets and ring generations) leave a running Recording unbroken (FR-10).

**Approach:** In `SessionOrchestrator`, while a Recording runs on the active game session, a ChainKey-changing settings change is deferred: no chain is prepared, a chain already prepared is dropped (also at the Recording's start), and the deferral is logged once through `Recordings.Info`. When the Recording's writer posts back and its session is still active, the settings are resolved again, so the chain is prepared and the first save after the stop switches to it (4.9's switch at Reset). Prove the whole story with Core tests and one Heavy engine test.

## Boundaries & Constraints

**Always:** The orchestrator alone decides (AD-19). A Recording started with a change pending shares the session's current chain and settings (AD-5 snapshot as 6.1 already logs). The Save Cooldown, failed saves and two concurrent writers stay Clip-only: no Recording code reacts to them. Heavy engine tests are `[Fact(Explicit = true)]` + `[Trait("Category", "Heavy")]` and hold the GPU lock file `%USERPROFILE%\Documents\repos\ReplayBuffer\.bench-running` only in the session, not in test code.

**Never:** No Contracts change. No change to `CaptureSession`'s switch/arm logic, `RecordingWriter` or `EncoderChain` unless the Heavy test exposes a real glitch. No real-time waits in default-run tests.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Change mid-Recording | Recording runs; `SettingsUpdated` with a ChainKey change | no `PrepareChain(spec)`; one Info line saying it waits for the Recording | none |
| Resets mid-Recording | saves while the change is deferred | no `PrepareChain` call; no second Info line | none |
| Stop | Record press stops it; writer posts back; session still active | `PrepareChain(next)` once; the next save switches (its `SwitchesTo`) | none |
| Pending at start | a chain prepared (`Pending`), then Record press | `PrepareChain(null)` before/at the start; Recording starts on the current chain; deferral logged | none |
| Session ended | Recording ended by its session ending | nothing prepared (no active session) | none |

</intent-contract>

## Code Map

- `src/ReplayBuffer.Core/Sessions/SessionOrchestrator.cs` -- `ApplySettings(Active)` (called from `SettingsUpdated` and `OnClipWritten` after each Reset): add the Recording guard. `StartRecording`: after `write.Start` succeeds, drop a pending chain (`active.Pending` → `PrepareChain(null)`). `OnRecordingWritten`: after `Stopped`, when `_active?.Id == recording.SessionId`, call `ApplySettings(_active)` (before committing a held Target). `Active`: add a `Deferred` flag so the deferral logs once per Recording. `RecordingRun.SessionId` identifies the session. Update the class summary (the 4.9 sentence).
- `src/ReplayBuffer.Engine/Sessions/CaptureSession.cs` -- read-only: `TryArm` already refuses while `chain.Tap.HasWriter`; `Record` returns null while a switch is armed; `PrepareChain(null)` drops a prepared/wanted chain.
- `tests/ReplayBuffer.Core.Tests/SessionOrchestratorTests.cs` -- reuse `ASettingsChangeNeedingAnotherChainIsPreparedDroppedOnARevertAndTakenAtTheSwitchingSave`'s setup (`FakeFactory { Save = ... }`, `FakeSession.Prepares`, `FakeWrite`, `SwitchesTo`), `NewRecordingOrchestrator`/`FakeRecording`, `SetRecordingsEnabled`, `Press`, `WaitUntil`. `FakeFactory` can take both `Save` and `Record`.
- `tests/ReplayBuffer.Engine.Tests/RecordingCaptureTests.cs` -- the WGC + `ToneHub` rig (`StartRecordingAsync`, `Rig`, `LibraryCalls`, `AssertFinished`); it has no `ReplayClips` yet. `ChainSwitchCaptureTests.cs` shows a profile video override (1280×720) and checking a Clip's size. `SessionTestKit.Probe` / `AssertDecodesCleanly` for ffprobe.

## Tasks & Acceptance

**Execution:**
- [x] `src/ReplayBuffer.Core/Sessions/SessionOrchestrator.cs` -- defer ChainKey changes while a Recording runs on the active session, drop a pending chain at a Recording's start, resolve again when it stops; log the deferral once via `Recordings.Info` -- AD-7 two-chain budget, FR-10.
- [x] `tests/ReplayBuffer.Core.Tests/SessionOrchestratorTests.cs` -- one test for the matrix: a change mid-Recording prepares nothing and logs once, saves during it prepare nothing, the stop prepares it and the next save switches; one test for a change pending at the Recording's start (dropped, Recording starts, prepared again after the stop).
- [x] `tests/ReplayBuffer.Engine.Tests/RecordingCaptureTests.cs` -- give the rig optional `ReplayClips` (saves land in the events channel) and one Heavy test: start a Recording with a profile resolution change already pending (640×480 → 1280×720 committed + `SettingsUpdated` before the press), record 2 minutes with ten saves at seeded random times (respecting the Save Cooldown set to 0 and 2 writers), stop; then two saves. Assert: ten Clips during it, each decoding cleanly at 640×480 with all Tracks; the Recording is 640×480, its video packet PTS step is constant (no gap/duplicate/drop) and each audio Track's packet PTS is contiguous (no discontinuity); the second Clip after the stop is 1280×720.

**Acceptance Criteria:**
- Given a 2-minute Recording on the synthetic game window, when ten saves land at random times, then ten Clips decode cleanly and the Recording's frame stamps and audio show no gap, duplicate, drop or discontinuity.
- Given a Recording runs, when the profile's resolution changes, then the Replay Buffer keeps one chain until the Recording stops, and the Clip after the switching save uses the new resolution.
- Given a resolution change is pending, when a Recording starts, then it records at the current resolution on the shared chain.

## Implementation Notes

- `ApplySettings` gains a Recording branch: a chain change while a Recording runs on the active session drops a prepared chain and logs once per Recording (`Active.Deferred`). `StartRecording` calls it after `write.Start` succeeds; `OnRecordingWritten` calls it again when the Recording's session is still active and no held Target switch is about to end it.
- Core tests: `AChainChangeDuringARecordingWaitsForItsStopAndTheNextSaveSwitches` and `AChainPendingAtARecordingsStartIsDroppedAndPreparedAgainAfterItsStop`, sharing a small `ChainRecordingRig`.
- Engine: `StartRecordingAsync(clips, pending)` takes optional `ReplayClips` and a pending document; Heavy `ClipsDuringARecordingLeaveItUnbrokenAndAChainChangeWaitsForItsStop` (2 min, ten saves in 12 s seeded slots, then two saves after the stop). The rig's Save Cooldown is 0.
- Review fixes: `StartRecording` drops a pending chain directly, so a document that no longer resolves can't keep it alive; the stop doesn't prepare a chain when a held Target switch will end the session.

## Plan Change Log

## Review Triage Log

### 2026-11-03 — Review pass
- verdicts: 6 findings — high 0, medium 1, low 4, false 0, maybe-false 1
- findings:
  - `[medium]` `[patch]` A pending chain isn't dropped at the Recording's start when `ApplySettings` returns early (a document whose Track Layout no longer resolves), so an idle second chain lives through the Recording — fixed: `StartRecording` clears `Pending` and calls `PrepareChain(null)` itself before `ApplySettings`.
  - `[low]` `[patch]` The stop prepares a chain for a session a held Target switch ends right after — fixed: `OnRecordingWritten` skips the re-resolve while `_pendingTarget` is set.
  - `[low]` `[reject]` Two Info lines (6.1's mismatch line and the deferral) at a press with a change pending — cosmetic; 6.1's line is still the only one without Replay Clips, so removing it loses information.
  - `[low]` `[reject]` The Core tests' `SwitchesTo` assertions mirror the fake's `Prepares` — the `Prepares` sequence carries the orchestrator's behaviour, and 4.9's test covers `TakeSwitch`; lean tests.
  - `[low]` `[reject]` The Heavy test doesn't observe the engine dropping the prepared chain — the Core tests prove the drop call, and `CaptureSession.PrepareChain(null)` is 4.9's tested path; lean tests.
  - `[maybe-false]` `[reject]` The audio contiguity loop reads `duration` on every packet and could throw if ffprobe omitted it — the test passed twice on real output, so ffprobe emits it here; low if true (a test crash, not a false pass).

## Design Notes

Preparing again at the stop (not at the first Reset after it) is what makes the change land *at* that first Reset: 4.9 switches at the save press whose write arms a prepared chain. A post-back handled during `Dispose` (`commitPending: false`) must not prepare anything either; only the active session's post-back does.

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings
- `dotnet test --project tests/ReplayBuffer.Core.Tests -c Release --no-build` -- expected: all pass
- `dotnet test --project tests/ReplayBuffer.Engine.Tests -c Release --no-build` -- expected: all pass
- `dotnet test --project tests/ReplayBuffer.Engine.Tests -c Release --no-build --explicit only --filter-method "*ClipsDuringARecording*"` (under the GPU lock) -- expected: pass
- `pwsh tools/publish-engine.ps1` -- expected: no IL or AOT warnings

## Auto Run Result

- **Summary:** While a Recording runs on the game session, the Replay Buffer keeps one encoder chain (AD-7): a ChainKey-changing settings change is not prepared, a chain prepared before the Record press is dropped at its start, and the deferral is logged once through `Recordings.Info`. When the Recording's writer posts back with its session still running (and no held Target switch), the settings are resolved again, so the first save after the stop switches to the new chain. A Heavy test proves Clips during a 2-minute Recording leave it unbroken (constant video PTS step, contiguous audio on every Track) and the chain change lands after the stop.
- **Files:** `src/ReplayBuffer.Core/Sessions/SessionOrchestrator.cs` (deferral, drop at start, re-resolve at stop); `tests/ReplayBuffer.Core.Tests/SessionOrchestratorTests.cs` (two tests); `tests/ReplayBuffer.Engine.Tests/RecordingCaptureTests.cs` (rig with Replay Clips and a pending document; one Heavy test). No Contracts change.
- **Review:** 6 findings: 2 patched (1 medium, 1 low), 4 rejected (3 low, 1 maybe-false; reasons in the triage log). None deferred.
- **Follow-up review recommended:** false. One medium and no high were patched.
- **Verification:** Release build 0 warnings; Core.Tests 111 passed; Engine.Tests default 167 passed, 24 explicit skipped; Heavy `ClipsDuringARecordingLeaveItUnbrokenAndAChainChangeWaitsForItsStop` passed (2m 13s, under the GPU lock, after the review fixes); `tools/publish-engine.ps1` published with no IL or AOT warnings.
- **Residual risks:** the Heavy test waits a fixed 2 s after the stop for the new chain's build before the switching save, so a slow GPU could miss the switch. A Monitor Recording beside a game Recording isn't exercised until story 9.
