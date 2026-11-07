---
name: ReplayBuffer clip editor refresh
status: approved
updated: 2026-11-07
sources:
  - DESIGN.md
  - ../feature-requests/clip-creator-ux-pass/experience.md
  - ../coordinator-handoff.md
---

# Clip editor refresh — proposed experience contract

## Foundation

Windows desktop / WPF, extending ReplayBuffer's established Fluent and Gaming UI. [DESIGN.md](DESIGN.md) owns appearance and theme mapping. This draft adds to the accepted creator contract and incorporates implemented follow-ups through double-click visible-range selection. The builder approved the mockup and implementation on 2026-11-07. Existing eligibility, source safety, lifecycle, cancellation, preview invalidation, worker isolation, output atomicity and export format rules remain binding.

The user requested a concrete visual mockup first. Additions originally labeled **proposed** below were included in the builder's 2026-11-07 approval. No general Settings or Library redesign is included.

## Information Architecture

| Surface | Function | Draft coverage |
|---|---|---|
| Creator heading | Source identity, Back and split Export | Mocked; existing behavior |
| Picture | Responsive current source / rendered selection preview, volume/audio menu and fullscreen | Mocked; image is illustrative |
| Timeline card | Transport, shared ruler, named audio lanes, proposed video thumbnails, independent seek and trim | Main mockup, interactive |
| Precision section | Requested In/Out and nudges, selected duration, effective media boundaries | Main mockup, interactive; alignment simulated |
| Export menu / options | Existing video/audio choice, naming, tracks, separate/mix, Save | Mocked; simulated destination step only |
| Navigation / bottom Settings | Existing shell routes with expanded/rail layout | Context-only; toggle works; navigation intentionally does not leave prototype |
| Keyboard shortcuts | Proposed discoverable helper dialog for existing key mappings | Mocked; no new keyboard mappings |
| Errors / progress / results | Existing operation feedback, cancellation and output location | Contract only; no simulated encoding or fake success |
| Many tracks / busy / preparation failure | Existing bounded lane navigation and operation states | Contract only; two-track mock does not claim these are exercised |

## Voice and Tone

Use concise task labels: In, Out, selected, Preview selection, Export video/audio, Visible, and Effective video frames / Effective audio samples (48 kHz). Help says “Double-click a waveform to select the visible range.” Display source time for the shared ruler and elapsed counter in every playback mode. Keep initial waveform preparation quiet; show actionable failures locally. Top window status remains error-only and otherwise collapsed.

## Component Patterns

| Interaction | Required behavior / status |
|---|---|
| Source transport | **Proposed:** move source Play/Pause to the card center, add −10s/+10s source-time seek buttons clamped to source bounds. These change the independent playhead only. Keep existing playback controls for volume, audio-track audition and fullscreen accessible on picture chrome; move the complete audio menu with its trigger rather than silently replacing it with mute only. The mock shows mute, a volume slider revealed on hover/focus, an audio-track menu and fullscreen; audio playback remains illustrative. |
| Seek | Existing behavior: ruler thumb drag, waveform click/drag, P then Left/Right by 1ms seek independently of trim. **Proposed extension:** clicking/dragging a thumbnail seeks with the same mapping. Seeking never drags the selection. Line/pointed thumb use `{components.playhead.color}`. |
| Playback mode | One active playback surface. Source playback loops over the current requested In/Out range; Preview selection retains the current rendered preview workflow and matched export configuration. Display source-mapped elapsed and ruler position even while rendered preview plays. Proposed skip controls use source coordinates; leaving effective preview boundaries returns to source using existing invalidation rules. Preview selection remains an action; both source playback and rendered selection playback loop by default (builder amendment, 2026-11-07). |
| Trim | Existing In/Out controls and validity rules apply. **Proposed presentation:** shared grips span audio and video; dragging them edits only the selected endpoint, not the source time. No draggable selection body / range move is introduced. Playhead can be outside the selection, as shown on first load. |
| Trim responsiveness | Existing behavior: drag updates requested fields/handles/duration locally; defer effective-range work until release or lost capture, coalescing unchanged ranges. Do not read/rebuild whole-source peaks for overlay motion. Preserve the last valid range if a drag crosses the other endpoint. |
| Numeric precision | HH:MM:SS.mmm, Enter / focus-loss commit, 1ms nudges. Invalid/equal/crossed/out-of-source input preserves the last valid range with a near-field explanation. Never silently swap endpoints or move the opposite one. Text-editing arrows remain native. Requested values stay distinct from aligned effective values. |
| Effective timing | Show true worker-resolved source-frame or 48kHz sample boundaries below requested fields. During resolution, retain requested values and show a compact pending state; stale preview/export remains unavailable. The mock uses illustrative 60fps/sample arithmetic solely to demonstrate the readout. It is not an algorithm recommendation. |
| Waveforms | Preserve cached full-source waveform preparation, bounded viewport detail reads, 40px per-track rows and whole-source per-track amplitude maxima. Zoom/pan/inclusion cannot renormalize a track. Silence stays flat. Do not restore routine loading text. |
| Double-click | Preserve implemented behavior: double-click waveform body or trim handle selects the visible source-time bounds rounded inward to milliseconds. Do not alter viewport, cached peaks or export settings. Labels/ruler keep their existing click behavior; a filmstrip double-click adds no new range-selection shortcut. |
| Zoom | Existing +/− tools and wheel zoom preserve requested range and source playhead. Match current zoom centering on playhead clamped to viewport, and source-edge clamping. Thumbnails, waveforms, ruler and overlays use exactly the same transform. |
| Pan | Existing arrows pan half the current viewport; Shift+wheel pans 20%. Panning never clamps offscreen selection to the viewport. Numeric In/Out remain visible when grips leave view. Plot clipping omits offscreen handles/playhead instead of inventing edge positions. |
| Thumbnails | **Proposed new media presentation:** low-density full-source thumbnail cache, loaded asynchronously and owned by creator lifecycle. Cached images are sampled by current source viewport. Do not decode repeatedly during drag/seek. Thumbnail generation must be isolated from capture, cancellation-aware and separately sized/bounded during implementation planning. |
| Missing thumbnails | **Proposed:** audio editing, seek, preview and export remain usable if thumbnails are pending/unavailable. Keep the filmstrip's height and quiet neutral placeholders to avoid layout jumps. Show a local actionable failure only if it prevents an action. Do not claim media is ready before initial source preparation is complete. |
| Export | Retain default video, separate type arrow, type-specific modal options, name/tracks/mix retention after dismissal, no encoding/chooser on opening, Save then native destination choice, no duplicate operations. Video permits silent output. Audio requires tracks; separate MP3 uses the existing new-directory output contract, including one selected track. Preview/export use the same effective range and audio configuration. |
| Shell | Width toggle preserves source, viewport, range, settings and work. Bottom Settings performs existing navigation/lifecycle. No idle status header. Existing errors, active operation cancellation and success output location remain available when relevant. |

## State Patterns

- **Ready:** source time and full cached waveform visible; selection, independent playhead, In/Out, effective boundaries and format-specific actions agree.
- **Playing:** one Play/Pause state, continuous source-mapped cursor, fixed requested trim. Source playback returns to In at Out and follows live trim changes. A rendered preview loops from its local end to zero using effective media boundaries. Paused seeking stays independent, and scrubbing temporarily suspends automatic wrapping. Editing a playing rendered selection invalidates it and continues playback on the source using the updated range. Pause, hide and disposal stop playback without automatic restart.
- **Dragging trim:** local overlay/time/duration changes; defer effective work until release/lost capture. Preview cannot present an outdated selection as current.
- **Resolving effective range:** editing preserves requested time; pending caption appears locally. Preview/export gating follows the existing model.
- **Rendered preview stale:** return to source as existing code requires after valid selection or format/audio changes. Do not leave a “selection preview” badge on stale content.
- **Export / render busy:** retain existing edit restrictions consistently across numeric fields, nudges and handles. Keep progress, cancellation and actionable errors visible; preserve source/output safety. Do not disable cancellation with the editor.
- **Many audio tracks:** a bounded five-lane viewport with vertical scrollbar and Up/Down/Page keys; reached tracks scroll into view. Shared ruler, thumbnails, selection and playhead remain aligned over the currently shown rows. No per-lane independent trim or mute/inclusion controls are introduced by gutter dots.
- **Thumbnail pending/failure:** preserve timeline geometry and waveform use. No animated shimmer or persistent “loading waveform” message.

## Interaction Primitives

Use pointer capture for seek and boundary drags; release or lost capture clears drag state and commits the final valid requested range. A single click seeks; only the established waveform/handle double-click selects the viewport. Keep existing keyboard primitives: P selects playhead, Home/End choose In/Out, Left/Right adjust the focused role by 1ms, +/− zoom, Shift+wheel pan, Up/Down/Page keys scroll lanes.

Do not add global Space/arrow interception inside time/name inputs. Buttons retain native activation. Menus and dialogs close with Escape and restore trigger focus; dialogs trap focus. Thumbnail seeking is an extension of the existing timeline focus role, not a new tab stop per thumbnail. Tab reaches the playhead, In and Out grips as individually named roles. Cursor/trim never depends on thumbnails being available.

## Accessibility Floor

Color communicates with shape and text: `{components.playhead.color}` has a pointed ruler thumb and continuous line, selection has two grips and requested time labels, focus uses the inherited focus pair. Keep contrast-correct text, full accessible names/tooltips for icons, 24px-wide expanded grip targets, at least 32px-high icon targets and keyboard reachability at high scale. Final product contrast/high-DPI/native pointer verification remains pending; offline renders are not sufficient evidence for it.

Ruler text and waveform labels are never focus indicators. Prefer polite announcements of committed range changes, validation and operation status; avoid announcing each rendered cursor/drag frame. Volume/audio-menu state remains named. Respect reduced motion; there is no decorative animation.

## Key Flows

### Sam finds and trims a moment

1. Sam opens Create clip from a finished Library item. The source picture dominates; elapsed/source total and the independent cursor identify position.
2. Sam seeks via waveform or the proposed thumbnails. The selected 26–45s range remains fixed while the cursor moves outside it.
3. Sam zooms around the cursor, pans, and drags Out. In/Out and duration update locally without peak rebuilding; release resolves effective media boundaries.
4. Sam types a final millisecond adjustment or uses the integrated nudge. **Climax:** the requested values and visible boundaries agree, and aligned export times remain readable below them.
5. If Sam wants the entire visible interval instead, double-clicking a waveform selects inward-rounded viewport bounds without changing the viewport.

### Sam auditions and exports

1. Sam chooses Preview selection. The existing render workflow uses current format, effective range and included tracks; the mode badge changes only when that preview is current.
2. Sam switches export type through the arrow. This invalidates stale preview but starts no chooser or encoding.
3. Sam opens options, confirms named tracks and separate/mix output, and selects Save.
4. **Climax:** the native destination choice precedes one export with visible progress/cancellation; cancel starts no output. The design prototype demonstrates this point with text only.

## Inspiration & Anti-patterns

The supplied reference contributes centered transport, elapsed/total at left, shared ruler above quiet waves and video thumbnails, a continuous red seek line, and a rounded accent selection with integrated grips. Preserve distinct named audio lanes rather than collapsing them into a decorative waveform. Do not copy incidental reference text or hardcode purple. Do not replace requested millisecond entry with thumbnail snapping, add auto-snapping, multi-cut editing or selection-body dragging.

## Responsive & Platform

Keep the existing picture-first finite layout, minimum picture and editor scrolling at small heights. Two-track reference checks include 960×900 with expanded and 58px rail navigation, plus 1280×960; smaller 640×820 and 480×640 views reflow whole time/nudge groups, place Preview selection on a second transport line, and preserve reachable controls. Never hide Settings to solve overflow. At narrow widths use the existing collapsible shell behavior; the mock simplifies this to a rail.

The new 48px filmstrip consumes picture space. The builder approved this tradeoff and the additions above. Thumbnail resource/cancellation decisions are recorded in ../plan-clip-editor-refresh.md and the AD-28 filmstrip amendment; existing codec and export semantics are unchanged.

## Review scope and evidence

Review first: overall reference match, accent adaptation, red cursor role, combined multi-track/filmstrip selection, centered transport with 10s skips, and picture-height tradeoff. The HTML prototype exercises pointer seek/trim, exact field/nudge updates, viewport zoom/pan, double-click selection, theme/rail switches and simulated export options. All media frames/peaks/alignment/playback are illustrative; the repeated driving scene is not a real thumbnail extraction pipeline. Progress/cancel, more than two tracks, actual media/export fidelity and runtime states remain contract-only.

No builds, product tests, Dev App launches, real Library/settings access, push or implementation occurred in this design session.

Implementation evidence and visual reconciliation: [implementation-notes.md](implementation-notes.md).

Playback-loop amendment (2026-11-07): the builder requested “the playback should loop through the current boundary for the clip sliders”. This supersedes the original non-looping design. Play outside the selection starts at In; Play within it resumes until Out, then wraps. Ordinary Library playback remains non-looping. Native loop timing/short-range audio continuity remains deferred under existing real-media/HITL restrictions.
