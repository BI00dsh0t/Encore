# Drag the selected clip interval

Builder authorized implementation on 2026-11-08: “implement the paln”, referring to `feature-requests/drag-clip-selection/drag-clip-selection.md`. Ordinary implementation on `feature/drag-clip-selection`; existing test/data/app restrictions remain binding.

## Implemented behavior

- Pointer down inside the selected waveform/video body records an anchored candidate move without seeking or starting range work. A stationary click seeks on release. Crossing Windows' horizontal/vertical drag threshold starts the existing local range-drag transaction and translates both endpoints by one common millisecond delta from the original interval.
- Clamp to source start/end as one interval, preserving duration; use the press viewport's source-time scale, including when zoomed/panned. Pointer motion outside the visible plot can move the range beyond viewport bounds without treating those edges as source limits. No automatic edge pan, snapping, media rearrangement or new keyboard shortcut.
- Expanded trim-grip targets retain priority and resize one edge. Ruler and outside-selection drags seek immediately. Waveform/handle double-click still selects inward-rounded visible bounds; a completed body move suppresses an accidental subsequent double-click.
- In/Out, shading and duration update locally. Release/lost capture resolves effective boundaries through the existing transaction; loss of capture cancels an uncommitted candidate click without seeking. Viewport/resize/keyboard changes finalize the gesture before changing its mapping, and non-editable state clears it. Pointer capture is released on unload/context changes.
- Keep source playhead independent. If moving invalidates a paused rendered preview, transfer the displayed source-time position to the source player rather than jumping back to its earlier position. Active playback continues with existing live-boundary loop behavior.
- Cached waveform geometry, peaks and persisted/frozen thumbnails are reused. Update hover cursors, tooltip, accessible help and shortcuts help; reconcile the approved UX contract and interactive mockup with the new body gesture.

## Verification

Release builds must remain zero-warning. Authorized non-Explicit ClipCreatorTests only; no other suites or real-media/Heavy/HITL checks. Two focused additions cover duration/source clamping/precision/local work/cache preservation and actual view gesture dispatch in Fluent/Gaming, including zoom/pan, click threshold, grip/ruler/outside seek roles, waveform double-click suppression, filmstrip movement, lost capture and paused rendered-preview position. The existing double-click regression caught a displayed-playhead change during preview invalidation; corrected it and reran the suite.

Ordinary review covers gesture precedence, source-time mapping, rounding/clamping, pointer lifecycle, deferred range work, cache identity and preview/loop behavior. Native OS pointer routing/high DPI, drag feel, real-media timing/continuity and hit-threshold HITL remain deferred. No Dev App restart, real Library/settings/cache access, publish, system changes or push.

Final feature verification: Release **0 warnings, 0 errors**; authorized non-Explicit `ClipCreatorTests` **27 passed, 0 failed/skipped**, including both new regressions in the final zoom/pan/cursor-state run. Scoped ordinary review found no unresolved directly introduced defect. Updated HTML prototype script passes syntax validation. No other suites or actual media/input runs were executed.
