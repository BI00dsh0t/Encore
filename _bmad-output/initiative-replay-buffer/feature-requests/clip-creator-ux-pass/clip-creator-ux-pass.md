---
type: epic
title: "Streamlined clip creator and expandable navigation"
parent: initiative-replay-buffer
covers: []
sources:
  - _bmad-output/initiative-replay-buffer/feature-requests/clip-creator-ux-pass/request.md
  - _bmad-output/initiative-replay-buffer/plan-clip-creator.md
companions:
  - _bmad-output/initiative-replay-buffer/feature-requests/clip-creator-ux-pass/experience.md
  - _bmad-output/initiative-replay-buffer/feature-requests/clip-creator-ux-pass/mockup.html
after: []
risk: medium
---

# Streamlined clip creator and expandable navigation

## Description

Apply the builder's accepted mockup to the existing clip creator: prioritize the picture, streamline the waveform, replace wordy zoom/pan buttons with symbols, and move export configuration into a type-specific popup reached through a split button. Extend the app shell with expandable/icon-only navigation and a dedicated Settings button at its bottom. The source conversation is preserved in `request.md`; interaction and acceptance details are in `experience.md`.

## Outcome

The builder can inspect footage, precisely trim it and configure audio/video exports with the editing surface visible, while choosing between labeled navigation and a compact icon rail and reaching Settings separately.

## Requirements

- R1 (request, Editor request and Accepted mockup): The video is the dominant editing surface, with compact named waveform lanes beneath it. At the reference desktop size, the ordinary two-track editor shows the picture, playback, waveform, trim times, preview and export without scrolling through permanently exposed export settings; additional tracks remain reachable in a bounded waveform area.
- R2 (request, Editor request; baseline, Intent): Zoom uses **+ / −** and pan uses **← / →**, with accessible names and tooltips. Numeric `HH:MM:SS.mmm` entry, 1 ms boundary nudges, detailed source waveforms, playhead, selection and effective frame/sample boundaries remain available and correct when zooming, panning or resizing.
- R3 (request, Editor request): A split export button has a separate arrow offering **Export as video** and **Export as audio**. The main button identifies the chosen type and opens its relevant options; choosing a type or opening the options starts neither encoding nor a destination chooser.
- R4 (request, Accepted mockup; baseline, Defaults and I/O matrix): Video options identify MP4 H.264/AAC; audio options identify stereo MP3 at 48 kHz/192 kbps. Both expose naming and named included-track choices, with separate or mixed output where applicable. Video allows silent output; audio requires a track. Separate MP4 streams retain names/order and separate MP3 files publish together as one directory; no track is silently dropped.
- R5 (request, Accepted mockup; baseline, Intent and Defaults): **Preview selection** remains available and matches the current export type, effective range and included-track/mix choices. Save in the popup opens the applicable file/folder chooser before export; closing the options or cancelling the chooser starts no export and preserves the editor's source and range.
- R6 (request, Sidebar addition): The app-wide sidebar toggles between labels with icons and an icon-only rail. Both retain current-page indication and accessible navigation; collapsed icons have tooltips. Collapsing reallocates width to the editor. Toggling alone does not navigate, reload the source, lose selection or cancel an operation.
- R7 (request, Settings addition): **Settings** is one dedicated button anchored at the bottom of the sidebar, separate from the primary navigation items. Expanded mode shows its label and gear; collapsed mode shows the gear with a tooltip. Both open the existing Settings experience with its usual navigation/lifecycle behavior.
- R8 (baseline, Constraints and Lifecycle matrix; request, Accepted mockup): Progress, cancellation of active work, actionable errors and the success location remain visible when relevant. Source validation, disappearance/navigation handling and operation isolation remain intact; repeated UI actions cannot create duplicate exports.
- R9 (baseline, both-look acceptance; request, Accepted mockup and Sidebar addition): Fluent and Gaming dark, keyboard interaction and resized windows keep controls, named tracks, selection, ruler and focus legible and reachable. Menu/popup dismissal restores focus to its trigger; larger track counts do not force unbounded waveform growth or overlap the trim controls.
- R10 (request, Control styling correction and Accepted mockup): Creator controls, export menu/popup and sidebar/Settings entry match the approved mockup's shapes, density, typography, icon weight, borders, fills, spacing and joined control groups. Hover, focus and disabled states preserve that visual hierarchy and accessibility. Acceptance includes a visual comparison with the mockup when authorized; equivalent functionality with visibly different controls does not satisfy this requirement.

## Constraints

- (baseline, frozen Boundaries & Constraints and Defaults) Preserve finished-source eligibility, Library/player entry context, source and existing-destination safety, capture independence, exact MP3 sample timing versus MP4 frame alignment, preview invalidation, shared input, isolated workers, atomic output and cancellation. This changes presentation and navigation, not media semantics.
- (request, Control styling correction; baseline, both-look acceptance) Preserve semantic light/dark/accent theme tokens while matching the mockup's control appearance; revise or introduce styles/templates where existing controls differ. The reference metrics and states are in `experience.md`. Preserve the merged one-way Progress binding and nearest-millisecond display correction. Illustrative media is excluded; matching control styling is required.
- (request, Spec instruction; current coordinator handoff) This spec does not start implementation or authorize tests. Existing clearance is limited to the non-Explicit clip-creator classes, one project at a time after a zero-warning Release build; shell/Settings tests remain deferred unless separately cleared. Heavy/media checks still require night/go. Do not run the old backlog, HITL, publishes, benches or soaks.
- (baseline, frozen Boundaries & Constraints) Later code uses git flow from develop, Conventional Commits and a no-fast-forward merge; no push. Leave installed/running apps, real data/settings, system settings and unowned processes untouched.

## Done when

- The builder accepts the integrated editor against `mockup.html` in both looks, with a larger usable picture and streamlined waveform at the reference desktop size (R1–R2, R9).
- Visual comparison confirms the mockup's control treatment for the editor, joined Export button, menu, popup and expanded/collapsed sidebar including Settings; existing theme styles do not override that appearance (R10).
- The split button selects either export type, presents only applicable options and reaches the correct save chooser; preview and export share the selected range/audio configuration (R3–R5).
- Sidebar expansion/collapse works throughout the shell without changing the selected page or creator state; Settings is a separate bottom button in both modes and opens the existing settings content (R6–R7).
- Existing source, timing, preview and cancellation regressions stay intact; relevant progress/errors remain accessible (R2, R5, R8).
- The acceptance cases in `experience.md` have honest pass/deferred evidence under the builder's current clearance; the zero-warning build, code review and required authorized checks pass before integration (R1–R10).

## Boundaries

- In scope: clip-creator composition, compact waveform presentation, symbol buttons, export type/options interaction, app-wide sidebar expansion/collapse, separate Settings access and matching control styles across these surfaces.
- Out: changing Settings content, renaming/reordering navigation destinations, new codecs or quality presets, media-engine redesign, multi-cut editing, effects, uploads, unrelated player redesign, capture/cleanup behavior or migration of real user data.
- Touch point: Library/player source and return context — existing clip creator and in-app player.
- Touch point: shell navigation, selected page and Settings entry — existing App shell; use actual current destinations, not the mockup's illustrative labels.
- Touch point: media preview/export — existing isolated clip-media service and worker; preserve their contract and output behavior.
- Tracer: Library/player → compact creator → precise selection → optional selection preview → export type → options → destination chooser → export progress/result; sidebar toggle can change width without restarting this flow.

## References

- request — `request.md`, Editor request, Accepted mockup, Sidebar addition, Settings addition, Spec instruction and Control styling correction.
- baseline — `_bmad-output/initiative-replay-buffer/plan-clip-creator.md`, frozen Intent, Boundaries & Constraints, Defaults, I/O matrix and both-look acceptance; implementation-time test-hold wording is superseded by the current coordinator handoff.
- interaction design — `experience.md`, composition, export behavior, sidebar/Settings behavior and acceptance cases.
- visual design — `mockup.html`, accepted interactive mockup preserved from this chat; illustrative content and tweak controls excluded from implementation scope.
- verification authority — `_bmad-output/initiative-replay-buffer/coordinator-handoff.md`, Current scoped exception; `_bmad-output/initiative-replay-buffer/test-backlog.md`, clip-creator section.
- regression evidence — `_bmad-output/initiative-replay-buffer/qa-clip-creator.md`, progress-binding and millisecond-display failures/fixes and pending media checks.

## Notes

- Decision: 2026-11-05, builder accepted the editor mockup, added collapsible navigation and a separate Settings button, and requested this BMad spec. This request is unscheduled, unnumbered and unsliced; it does not change existing ticket order or completion history.
- Decision: 2026-11-05, builder flagged control styling that does not match the mockup. R10 makes visual control fidelity mandatory and supersedes the earlier instruction to retain existing control conventions where they differ from the reference. No child stories exist to re-slice.
- Assumption: the sidebar starts expanded, matching the revised mockup, and retains its choice across navigation in the current app session. Persistence across restarts is not required by this spec.
- Assumption: popup option edits remain the current creator configuration after dismissal so Preview selection can audition them before reopening Export. Dismissal does not encode; type/audio changes invalidate stale selection preview.
