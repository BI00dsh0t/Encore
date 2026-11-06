---
title: 'Scale clip waveforms to each track amplitude'
type: 'feature'
ticket: ''
created: '2026-11-06'
status: 'in-progress'
baseline_revision: '1130e843cd19ec1fcb0c478e562da5f4bd45ebca'
route: 'oneshot'
route_source: 'auto'
risk: 'low'
review: ''
review_source: ''
lenses_ran: []
review_loop_iteration: 0
context: []
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** The clip waveform uses a fixed full-scale amplitude range, so quiet tracks appear nearly flat. The builder asked for the displayed maximum range to follow the actual tracks. This plan interprets that request as amplitude scaling of the existing time-domain waveform.

**Approach:** Scale each track's display peaks against the maximum peak across that entire source track. Its loudest peak reaches the existing waveform envelope height, with the current lane padding retained. Keep that scale stable through zoom, pan, trim and track inclusion changes. Silent tracks remain flat.

This changes visualization only: source audio, playback volume, preview/export audio, cache bytes and timing remain unchanged. A frequency spectrum, audio normalization and new scale controls are outside this change.

The builder explicitly requested BMad planning only. Stop after presenting this plan; implement later without invoking the BMad build workflow.

</frozen-after-approval>

## Code Map

- `src/ReplayBuffer.App/Library/ClipWaveformCache.cs`: constructor validates all level headers; ReadAsync aggregates bounded viewport peaks. Retain a validated maximum per stream from the final single-peak reduction level, and normalize only the newly allocated display arrays after aggregation.
- `src/ReplayBuffer.App/Library/WaveformView.cs`: currently multiplies peak by LaneHeight * 0.35. Keep this renderer and its padding, styles, handles and geometry unchanged; normalized display peaks already fit its expected 0..1 range.
- `src/ReplayBuffer.Engine/ClipMediaWorker.cs`: PrepareWaveformsAsync already makes 16:1 max-reduction levels until one peak remains. Reuse that existing whole-track maximum; no worker/protocol/cache-version or architecture change is needed.
- `src/Shared/ClipPeakFile.cs`: reuse ReadPeak to reject nonfinite, negative or greater-than-one maxima through the existing cache error path. No extra scan of complete peak files.
- `tests/ReplayBuffer.App.Tests/ClipCreatorTests.cs`: synthetic binary peak fixtures and FakeMedia support cache behavior checks without real media. Extend fixtures for different track maxima and silence; preserve existing test defaults.

## Tasks & Acceptance

**Execution:**
- [x] `src/ReplayBuffer.App/Library/ClipWaveformCache.cs`: capture each track's whole-source maximum once during cache validation. Use direct division by a positive maximum, rather than a reciprocal that could overflow for extremely small values. Keep an all-zero track at zero and clamp display results to 0..1. Preserve raw cache data, bounded IO, cancellation and cleanup behavior.
- [x] `tests/ReplayBuffer.App.Tests/ClipCreatorTests.cs`: add one focused synthetic-cache scenario covering two differently quiet tracks, proportional peaks and silence. Read full and zoomed viewports, including one that excludes the loudest point, to establish a stable whole-track denominator. Confirm cache bytes remain unchanged and preparation is not repeated. Extend the existing corrupt-cache case to cover an invalid summary peak if practical without a new redundant test.
- [ ] Verify the scoped change, inspect its diff, record results/deferred checks in `test-backlog.md`, commit on a git-flow feature branch and integrate into develop under the standing merge rules. Use ordinary implementation and review, without BMad execution.

**Acceptance Criteria:**
- Given tracks whose whole-source peaks are 0.2 and 0.8, when their waveforms are displayed, then each maximum occupies the same existing envelope height, and a half-maximum peak in either track has half that height.
- Given a viewport excluding the loudest point, when zooming or panning, then peak heights remain relative to the whole-track maximum; a local half-maximum remains half-height rather than expanding to full-height.
- Given a silent track, when displayed and zoomed, then it remains flat with finite zero values and no error.
- Given a source with prepared peaks, when display scaling is applied, then cached file bytes and playback/export requests stay unchanged, and editing does not trigger another waveform preparation.
- Given a corrupt summary peak, when opening the cache, then the existing actionable waveform error is shown and no invalid display data is published.

## Implementation Notes

Estimated implementation plus focused fixture/assertion changes: at most 100 lines; low risk because this affects display arrays only. BMad recommends oneshot for that scope. At the builder's explicit direction this artifact remains draft for review, retains the actionable code map/tasks, and stops before BMad's implementation step. No product code, builds, tests or real app operations run during planning.

## Plan Change Log

- Builder approved the plan; implementation proceeded directly without invoking BMad. Product changes are confined to the App's peak-cache display reader, with existing renderer, worker, contracts and source peak bytes retained.

## Review Triage Log

- Ordinary diff review: no remaining finding. Checked validated per-track whole-source maxima, division/zero handling, bounded allocations and reads, raw-file preservation, existing renderer consumers and cache cleanup/error propagation. No independent subagent review was invoked.

## Verification

- `dotnet build ReplayBuffer.slnx -c Release`: require 0 warnings/errors.
- `dotnet test --project tests/ReplayBuffer.App.Tests -c Release --no-build --filter-class "*ClipCreatorTests" --explicit off`: require a nonzero executed count and all scoped cases passing. Run only after the successful build, one project at a time.
- Inspect that the renderer still uses the existing styled lane envelope and that normalized data is consumed only by the waveform display. Existing synthetic both-look layout checks remain part of the scoped class.
- Review implementation for per-track rather than visible-window maxima, safe zero handling, raw cache preservation and absence of audio/protocol changes. Record actual commands/counts after implementation; none have run for this plan.
- Real media, Heavy/GPU tests, publishing, high DPI and real Dev App checks remain deferred under existing clearance rules. No unrelated suites or Windows quoting retry.

### Implementation results (2026-11-06)

- Release build after the final test correction: **0 warnings, 0 errors** (10.34 s).
- Scoped ClipCreatorTests command above: **15 executed, 15 passed, 0 failed/skipped**, 14.864 s. New synthetic scenario proves per-track full/half amplitude, silent track, stable zoom/pan/inclusion scaling, exactly one preparation and unchanged cache bytes. Existing corrupt-cache test now rejects a NaN summary peak.
- Initial scoped run: 14 passed, 1 failed because the new test assumed a full-source initial viewport, while the editor starts at 30 seconds. Corrected the test to expand to the whole source before seeking/zooming; product implementation unchanged. Final build and entire scoped class rerun passed.
- No real media or Dev App operations, unrelated suites, Heavy runs or publish checks performed.
