---
type: epic
title: "Move the selected clip range by dragging it left or right"
parent: initiative-replay-buffer
covers: []
sources:
  - _bmad-output/initiative-replay-buffer/feature-requests/drag-clip-selection/request.md
companions:
  - _bmad-output/initiative-replay-buffer/plan-drag-clip-selection.md
  - _bmad-output/initiative-replay-buffer/ux-clip-editor-refresh/EXPERIENCE.md
  - _bmad-output/initiative-replay-buffer/plan-clip-selection-loop.md
after: []
risk: medium
---

# Move the selected clip range by dragging it left or right

Status: implemented and verified, 2026-11-08. Feature `768ab85`, no-ff develop merge `6ac2a6f974a3aa8496796f20cf231a86969be34a`. The feature request is recorded as an unnumbered epic, following the existing feature-request convention.

## Request and outcome

The builder wants to drag the selected clip area left and right. After choosing a clip's duration, they can reposition that whole interval on the source timeline without adjusting In and Out separately.

## Requirements

- R1: Dragging the body of the selected range horizontally moves **In and Out together by the same source-time delta**, preserving the requested clip duration. The shared selection across the waveform and video rows represents one interval, not separate per-track selections.
- R2: Clamp the whole interval at the source start/end without shortening it, crossing endpoints or changing the opposite edge independently. Use the current source-time transform at every zoom/pan level and the existing millisecond precision. Viewport boundaries must not be mistaken for source boundaries or reset the selection.
- R3: Keep the edge grips dedicated to resizing and the ruler/playhead dedicated to independent seeking. This introduces a deliberate change to the current gesture: dragging inside the selected body will move the range, where it currently seeks. Preserve stationary click-to-seek and waveform double-click-to-select-visible-range behavior through an unambiguous gesture policy; a range drag must not accidentally seek or invoke double-click selection.
- R4: Update requested In/Out fields, grips, shading and selected duration together during movement. Moving a range does not translate the playhead with it. Paused playhead position stays independent; during playback the existing selection loop follows the new boundaries and may wrap to the new In as usual.
- R5: Keep movement local and smooth while dragging, reuse existing waveform/thumbnail caches, and resolve effective media boundaries after release/lost capture through the existing range-update path. Invalidate stale rendered previews and preserve current preview/export alignment, track choices, naming, cancellation and busy-state restrictions.
- R6: Make the movable body discoverable with appropriate pointer feedback/help in Fluent and Gaming. Existing field/nudge and keyboard trim/seek controls remain available, and source-range validity remains consistent across input methods.

## Proposed gesture details

Implementation adopts the recorded defaults: use the normal pointer drag threshold to distinguish a selected-body drag from a stationary seek click; prioritize trim-grip hit targets over the body; keep ruler gestures and dragging outside the selection as seek interactions. Preserve the established double-click behavior on waveform bodies/grips. Stationary selected-body clicks seek on release; moving beyond the Windows threshold starts a range move. Completed moves suppress an accidental subsequent double-click. Zoom/pan/resize or keyboard navigation ends an active pointer gesture before changing its mapping.

Automatic panning at viewport edges is not specified by the builder. Implementation leaves edge auto-pan and snapping out of scope and adds no keyboard shortcut. Existing pan/zoom and numeric/keyboard precision controls remain available.

## Acceptance examples

| Given / action | Expected result |
|---|---|
| A 10.000–15.000s selection is dragged 3.000s right | In becomes 13.000s, Out 18.000s, duration stays 5.000s. |
| That 5.000s selection is dragged left past source start | It stops at 0.000–5.000s without shrinking. |
| A 5.000s selection on a 20.000s source is dragged beyond its end | It stops at 15.000–20.000s without shrinking. |
| The same interval is moved while zoomed/panned | Pixel movement maps to the current viewport's source time; range validity and duration are preserved. |
| Drag either edge grip, click/drag the ruler, or double-click a waveform | Existing trim, seek and visible-range selection roles remain distinguishable and work as specified. |
| Move the range while paused or looping | Paused seeking remains independent; active looping uses the moved range without moving the handles itself. |
| Release a move after a current rendered preview exists | Stale preview is invalidated; effective boundaries refresh without decoding waveforms/thumbnails again or exporting stale media. |

## Boundaries and existing contracts

In scope: horizontal translation of the existing single selection and the gesture distinction needed to support it. No media modification, multi-cut editing, per-track clips, timeline rearrangement, new snapping rules or export format changes.

The current UX contract explicitly excludes selection-body dragging. The builder's implementation authorization supersedes that exclusion. Preserve recent live playback looping, persistent video-thumbnail caching, 40px waveform lanes and deferred effective-range work.

The builder explicitly authorized implementation on 2026-11-08. Existing restrictions and backlog remain in force: clip-creator-only non-Explicit verification, no real Dev App/data work, no Heavy/media/HITL or publish, and no push. See [the implementation plan](../../plan-drag-clip-selection.md).
