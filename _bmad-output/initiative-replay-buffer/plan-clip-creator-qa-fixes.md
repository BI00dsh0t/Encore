---
title: 'Fix clip creator opening and millisecond display'
type: 'bugfix'
ticket: ''
created: '2026-11-05'
status: 'built'
baseline_revision: '33317aeb51fa18b22351ea70b4aa5209ad34eb48'
route: 'oneshot'
route_source: 'auto'
risk: 'low'
review: 'quick'
review_source: 'pinned'
lenses_ran: [quick]
review_loop_iteration: 0
context:
  - '{project-root}/CLAUDE.md'
  - '{project-root}/_bmad-output/initiative-replay-buffer/coordinator-handoff.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Create clip throws because the progress bar attempts to write the private-set Progress property. Focused QA also shows a 1 ms nudge can display the preceding millisecond due to truncated floating-point time conversion.

**Approach:** Make progress display one-way and round displayed times to the nearest millisecond without changing selection, effective media boundaries or worker behavior. Existing fake-service view/precision regressions must pass. Only clip-creator non-Explicit tests are cleared; all Heavy/media, old backlog, HITL and real Dev App work remain held. Fix on a git-flow bugfix branch, then merge without fast-forward after a zero-warning Release build and review; no push.

</frozen-after-approval>

## Implementation Notes

- Oneshot: two localized UI corrections with existing regression coverage. Baseline Release build passed with 0 warnings/errors. Initial App run selected 7 tests: 4 passed, 3 failed (two view binding exceptions, one millisecond display mismatch).
- Set the progress binding to OneWay; round only formatted display times to the nearest millisecond. Selection values, sample/frame boundaries, preview, workers and cancellation remain unchanged. Existing view/precision tests reproduce the faults and pass after the fix; no duplicate tests added.

## Plan Change Log

## Review Triage Log

- Independent quick inspection: no findings. Reviewer confirmed private-set Progress works with the one-way binding and display rounding does not alter media requests. Broader lenses skipped for these localized changes.

## Verification

- `dotnet build ReplayBuffer.slnx -c Release`: 0 warnings/errors.
- Only non-Explicit `ClipCreatorTests`, `ClipMediaWorkerTests`, `SharedMediaProcessTests`, one project at a time; check actual counts and preserve pending Heavy gaps in the QA report.
- Post-fix Release build: 0 warnings/errors. App: 7 passed, 0 failed/skipped. Engine: 1 passed, 2 Explicit skipped. Windows: 4 quoting rows passed, 1 Explicit skipped. Final distinct non-Explicit cases: 12 passed, 0 failed. Real-media/Heavy/publish/HITL acceptance remains pending.
