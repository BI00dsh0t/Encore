---
title: 'Refactor sweep (epic 12)'
type: 'refactor'
ticket: '4'
created: '2026-11-05'
status: done
baseline_revision: 'c7f7013'
route: 'quick'
risk: 'low'
---

# Story 12.4: refactor sweep

Cleanup only, with no behaviour change. It's scoped from 12.1 to 12.3's plans and QA notes, the epic Notes and 12.6's follow-ups. Tests are deferred by the builder ("don't run any tests until later"): nothing was run, and the build is 0 warnings.

## Scope and outcome

| # | Item (source) | Outcome |
|---|---|---|
| 1 | The fixed-size path beside the source-size one in the chain build (ticket) | **Changed.** `CaptureSession` built its five chains through five copies of the `EncoderChain` constructor, four wrapped in `SizedToSource` and the first not. They now come from one `NewChain(spec, encoder, codec, path, device)` that always applies `SizedToSource`. That's a no-op for the first chain at `Open`, since no source size is known yet; it's sized at the anchor as before. The fixed size stays: it's `matchSource`'s fallback (AD-26) and the size with it off. |
| 2 | The session's class doc said the snapshot alone sets the output size | **Changed** (doc): it now says how `matchSource` sizes a game's chain and moves it on a held size change (AD-26). |
| 3 | QA's note on 12.2: the Heavy "Record during a move build" test depends on timing and can pass without exercising the cancel | **Changed** (test hooks, no product behaviour). |
|   | | **Test hooks:** `CaptureSession.MovePending` and `MovesCancelled` are internal probes. `CaptureSessionOptions.MoveArmDelay` holds a built move's arm (zero in production; only a test sets it). |
|   | | **The test:** `ARecordPressWhileAMoveBuildsCancelsItAndTheRecordingKeepsItsChain` now runs with a 3 s arm delay, presses Record only once the move is pending, and asserts exactly one cancel. So it can't pass without the cancel. |
| 4 | 12.6 follow-up: R41 missing from the spine's binds and map | **Closed elsewhere** (the R41 map row is in develop with AD-26). |
| 5 | 12.3 follow-ups | None were listed. |
| 6 | 12.2 note: a failed rebuild on the tear-down-first path fails the session | **Recorded**, unchanged: the same rule as `SwitchChain` today. |
| 7 | `debug-capture --match-source`, `measure-path-a.ps1 -ms`, `SetBuildAhead` | **Kept**: the night Heavy and cost runs (backlog §2, #3 to #5) use them. No later ticket (12.5, 8.9 to 8.11) names any internal removed here; the ticket tree was grepped. |

## Tests (none run; the builder deferred them)

- **New or changed:** only the Heavy `ARecordPressWhileAMoveBuildsCancelsItAndTheRecordingKeepsItsChain` (item 3), which is already in the backlog's §2 #4 command, and still Heavy.
- **The verify's Heavy part** (a fixed-size and a source-size Clip still record playable files):
  - the source-size Clip is 12.2's `ARealWindowResizedTo2560By1440MovesTheReplayBufferAndTheNextClipHasIt` and 12.1's capture (backlog §2, #3 and #4);
  - the fixed-size Clip is any existing Replay Clip Heavy test, for example `dotnet test --project tests/ReplayBuffer.Engine.Tests -c Release --no-build --explicit only --filter-class "*ReplayClipCaptureTests"`.
- **The default suites** (`Engine` above all, for `NewChain`) run in the backlog's §1 on develop once this merges.

## Verification

- `dotnet build ReplayBuffer.slnx -c Release`: 0 warnings.
- No test runs, by the builder's instruction. The backlog row is on this branch.
