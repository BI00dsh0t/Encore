---
title: 'Select the visible waveform range on double-click'
type: 'feature'
ticket: ''
created: '2026-11-07'
status: 'in-progress'
baseline_revision: '63ebe1920bd2ebebbc3a54f129aa66eb5b89ba67'
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

**Problem:** Zooming or panning can leave the clip handles outside the visible waveform. The builder wants a double-click to select the time range currently in view.

**Approach:** Double-clicking inside any waveform track's plot sets In to the visible viewport's start and Out to its end, using source time. Both handles, selection shading, time fields and duration update together. Example: viewing 00:40–00:50, then double-clicking the waveform selects that ten-second range. This selects the whole visible span, rather than an arbitrary subrange around the pointer. The action is available at any zoom level; a full-source viewport selects the full source.

The waveform body, including visible trim handles, accepts the gesture. Ruler, track labels, scrollbar and surrounding controls retain their existing behavior. Single-click seeking and ordinary dragging remain. The first click may seek normally; the second changes the range without another seek or restoring the previous playhead.

Use millisecond endpoints inside visible/source bounds: round start inward/up and end inward/down, handling floating-point values already on millisecond boundaries. If no positive interval fits, retain the selection. Exporting, disposed, suspended or uninitialized editors ignore the action. Requested/effective media boundaries remain distinct.

Invalidate stale rendered preview and refresh effective boundaries normally. Preserve viewport, peak data, audio/export settings and recent drag-performance improvements. No decoding, new popup or toolbar control.

This run is planning only. Implementation will follow approval, without invoking the BMad implementation workflow, per the builder's established preference.

</frozen-after-approval>

## Code Map

- `src/ReplayBuffer.App/Library/ClipCreatorPage.cs`: add SelectVisibleRange using authoritative ViewportStart/End and existing SetRange synchronization, preview invalidation and debounce. Unchanged bounds remain a no-op; preserve drag coalescing and cached peaks.
- `src/ReplayBuffer.App/Library/WaveformView.cs`: handle WPF ClickCount == 2 in OnMouseLeftButtonDown before trim/seek branching, within plot bounds below the 28-pixel ruler. Clean up capture/scrub/trim state, select range and mark handled without starting a drag. Retain cached lane geometry.
- `src/ReplayBuffer.App/Library/ClipCreatorView.xaml`: add a concise waveform tooltip and extend existing automation help text.
- `tests/ReplayBuffer.App.Tests/ClipCreatorTests.cs`: reuse fake services and both-look owned windows. A small input-routing helper may support synthetic click count/coordinates without real pointer automation.

## Tasks & Acceptance

**Execution:**
- [x] `ClipCreatorPage.cs`: implement one state-guarded visible-range selection operation using existing SetRange; preserve last valid range for an empty inward-rounded interval.
- [x] `WaveformView.cs`, `ClipCreatorView.xaml`: wire the gesture/cleanup and discoverability hint; preserve ordinary input and rendering reuse.
- [x] `ClipCreatorTests.cs`: cover zoom/pan, inward rounding/source edges, unchanged peaks/options, final request, preview invalidation, disabled states and one preparation. Extend both-look coverage for pointer-region precedence and capture cleanup.
- [ ] After approval, implement directly on a git-flow feature branch, verify/review, update backlog/handoff, commit and merge no-ff. No builds/tests or app operations during planning.

**Acceptance Criteria:**
- Given a zoomed/panned viewport with handles outside it, when double-clicking any track's waveform body, then both handles and their fields select the visible span, duration agrees, and viewport/peak data remain unchanged.
- Given fractional-millisecond visible bounds near either source edge, when selecting the viewport, then both requested endpoints stay inside the viewport/source, are millisecond-aligned and form a positive range; if none fits, the previous range is preserved.
- Given the pointer on a waveform-body handle, when double-clicking, then visible-range selection wins and releasing/moving the pointer afterward cannot continue a leftover trim or scrub operation.
- Given single-click or drag interaction, or double-click on the ruler/labels/scrollbar, when interacting, then the existing seek/trim/scroll behavior remains and the viewport range is not selected by those excluded regions.
- Given an export, unavailable metadata, suspended or closed editor, when attempting the gesture, then source and selection stay unchanged.
- Given a rendered selection preview, when double-click changes the range, then stale preview is invalidated, final effective boundaries are refreshed, and no source waveform decode/preparation restarts.

## Implementation Notes

Low risk: App-only input/selection changes; source media and protocol untouched. Estimated change fits oneshot scope. Builder's planning-only instruction overrides automatic implementation; draft retains the actionable code map/tasks. No product code changed.

## Plan Change Log

- Builder explicitly requested implementation. Implemented directly without BMad execution, preserving approved intent and the captured baseline. Decimal time quantization avoids binary floating-point noise at millisecond boundaries.

## Review Triage Log

- Ordinary diff/input-lifecycle review found no remaining issue. Second-click routing precedes trim hit testing; ruler/labels/outside plot and single clicks fall through unchanged. Clearing interaction state before release avoids leftover pointer drags. Selection calls existing SetRange and retains viewport/peaks and cached geometry.

## Verification

- `dotnet build ReplayBuffer.slnx -c Release --no-restore -v quiet`: require 0 warnings/errors.
- `dotnet test --project tests/ReplayBuffer.App.Tests -c Release --no-build --filter-class "*ClipCreatorTests" --explicit off`: require a nonzero executed count and all selected tests passing, one project at a time after the successful build.
- Review double-click precedence, rounding, lifecycle/capture cleanup and drawing reuse; record actual results after implementation.
- Real-media/Heavy, pointer/HITL and high-DPI checks remain deferred. No unrelated suites, media tools, publishing, real Dev App/data operations or Windows quoting retry.

### Implementation verification (2026-11-07)

- Feature Release build: **0 warnings, 0 errors**.
- Scoped ClipCreatorTests command above: **17 executed/passed, 0 failed/skipped**, 16.765 s. New fake-service scenario covers fractional zoom/pan, source start/end/full span, synchronized duration, unchanged viewport/peaks/playhead/audio track request, stale preview invalidation, no repeated preparation, export/suspend/dispose/uninitialized guards and a sub-millisecond source with no positive quantized interval. Existing both-look input test covers double-click on handle, excluded regions/single click, scrub/capture cleanup and automatic effective-range resolution. No real pointer automation or actual media used.
