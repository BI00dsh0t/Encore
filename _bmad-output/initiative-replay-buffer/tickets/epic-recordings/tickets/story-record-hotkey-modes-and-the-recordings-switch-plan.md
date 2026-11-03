---
title: 'Record Hotkey modes and the Recordings switch'
type: 'feature'
ticket: '3'
created: '2026-11-03'
status: 'built'
baseline_revision: '4e98914249fec57c4d05703c01d8e46b933ccbcc'
route: 'oneshot'
route_source: 'auto'
risk: 'low'
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

**Problem:** The SessionOrchestrator acts only on `recordToggle`: `recordStart` and `recordStop` do nothing, and it doesn't read `global.recordHotkeyMode`, so in separate mode no Recording can start (R1).

**Approach:** In `SessionOrchestrator`, route the three Record actions through one handler that reads `recordHotkeyMode` live from the current document at each press (`SettingsResolver.Resolve(document, null)`, the same as `HotkeyPlan.For`) and ignores, with an `Info` line, an action `HotkeyPlan.IsActive` says the mode doesn't use. Toggle keeps 6.1's start-or-stop. In separate mode `recordStart` starts only when no Recording runs and `recordStop` stops only a running one; otherwise each is ignored with an `Info` line. Starting keeps 6.1's gates (a game session, a profile, `recordingsEnabled` read live). Stopping isn't gated by the switch, so a Recording that runs when the switch goes off keeps running until a stop press, the session's end or 6.4's reasons. Tests in `tests/ReplayBuffer.Core.Tests/SessionOrchestratorTests.cs` use the existing `FakeFactory`/`FakeRecording` fake hotkey path (`HotkeyPressed` posts): one test per verify group: (1) the toggle mode and the mode switch mid-session, (2) the separate mode's ignored presses, (3) the switch: off ignores with no Recording event or state change, on starts nothing, off mid-Recording doesn't stop it, and a press with no game session does nothing. No Contracts change.

</intent-contract>

## Tasks & Acceptance

**Acceptance Criteria:**
- Given toggle mode, when `recordToggle` is pressed twice, then a Recording starts then stops; `recordStart`/`recordStop` presses do nothing.
- Given separate mode, when `recordStart` is pressed twice, then one Recording runs; `recordStop` with none running does nothing; `recordToggle` does nothing.
- Given the mode is changed mid-session, when the next press arrives, then the new mode's actions act and the old mode's don't.
- Given `recordingsEnabled` off, when any Record press arrives, then no Recording starts, no `recording.*` event and no state change follow; switching it on starts nothing; switching it off mid-Recording leaves the Recording running; a press with no game session does nothing.

## Implementation Notes

Oneshot: about 50 lines in one method plus orchestrator tests, all on code 6.1 built.

- `ToggleRecording` became `OnRecordPress(action, qpc)`, routed from `Handle` for all three Record actions. The mode filter reuses `HotkeyPlan.IsActive`, so the orchestrator and the Hotkey registration agree on which actions a mode uses.
- Decision (unattended default): the ticket says that with the switch off "every Record press is ignored", and also that a Recording switched off mid-run keeps running "until it is stopped". So the switch gates only the start. A stop press still stops a running Recording, or the user couldn't stop it before the game exits.
- Decision (unattended default): "no event" means no `recording.*` event and no `capture.stateChanged` change. The `hotkey.pressed` diagnostic echo still fires for every press, as it does for every other action.
- The verify groups became two tests: `TheRecordHotkeyModeIsReadAtEachPressAndOnlyItsActionsAct` (toggle, separate, mode change mid-session) and `TheRecordingsSwitchIsReadAtEachPressAndGatesOnlyTheStart` (no Target, off, on starts nothing, off mid-Recording). No Contracts change.

## Plan Change Log

## Review Triage Log

### 2026-11-03 — Review pass
- verdicts: 3 findings — high 0, medium 0, low 3, false 0, maybe-false 0
- findings:
  - `[low]` `[patch]` The switch test's helper dropped `CaptureState`, so "no state change" went unchecked — fixed: the helper returns the published states, and the off section asserts Recording stays Off.
  - `[low]` `[reject]` With the switch off, `recordStart` in separate mode is untested — rejected: it reaches the same `StartRecording` gate the toggle test covers, and the lean-tests rule asks for no duplicate cases.
  - `[low]` `[patch]` One press read the document twice (mode, then the switch), so a commit in between could mix revisions — fixed: `OnRecordPress` reads it once and passes it to `StartRecording`.

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings
- `dotnet test --project tests/ReplayBuffer.Core.Tests -c Release --no-build` -- expected: all pass

## Auto Run Result

- **Summary:** Record presses now follow `global.recordHotkeyMode`, read live at each press. In toggle mode `recordToggle` starts or stops. In separate mode `recordStart` starts only while no Recording runs and `recordStop` stops only a running one. Actions the mode doesn't use are ignored with an Info line. `recordingsEnabled` gates only the start, so switching it on starts nothing and switching it off mid-Recording doesn't stop the Recording.
- **Files:** `src/ReplayBuffer.Core/Sessions/SessionOrchestrator.cs` (`OnRecordPress`, with `StartRecording` taking the press's document); `tests/ReplayBuffer.Core.Tests/SessionOrchestratorTests.cs` (two tests and a helper).
- **Review:** 3 low findings: 2 patched, 1 rejected (a duplicate test path; lean tests). None deferred.
- **Follow-up review recommended:** false. No high or medium findings were patched.
- **Verification:** Release build 0 warnings, 0 errors. Core.Tests 101/101. `tools/publish-engine.ps1` published with no IL or AOT warnings. Only Core changed, so the other test projects were skipped. No Hotkey input injection is needed: the tests post `HotkeyPressed`, so there are no Heavy tests.
- **Residual risks:** a real key press in each mode on the builder's PC is untested. Hotkey re-registration on a mode change is 4.12's `HotkeyService`, unchanged here.
