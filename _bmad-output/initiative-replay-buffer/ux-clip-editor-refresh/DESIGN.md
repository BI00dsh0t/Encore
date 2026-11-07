---
name: ReplayBuffer clip editor refresh
description: Reference-led compact timeline card; proposed addendum to the accepted clip creator styling.
status: approved
updated: 2026-11-07
extends: ../tickets/epic-modern-ui/ux-modern-ui/DESIGN.md
sources:
  - ../feature-requests/clip-creator-ux-pass/clip-creator-ux-pass.md
  - ../feature-requests/clip-creator-ux-pass/experience.md
  - imports/timeline-reference.png
typography:
  timeline-time: { fontFamily: Segoe UI, fontSize: 12px, fontWeight: '400', note: tabular numerals }
  timeline-label: { fontFamily: Segoe UI, fontSize: 12px, fontWeight: '400' }
rounded:
  editor: 8px
  selection: 9px
  grip: 7px
spacing:
  editor-inset: 14px
  lane-label: 100px
components:
  editor-card:
    background: '{colors.surface-card}'
    border: '{colors.stroke-card}'
    radius: '{rounded.editor}'
  selection:
    color: '{colors.accent}'
    fill-opacity: 0.09
    border-width: 2px
    radius: '{rounded.selection}'
  playhead:
    color: '{colors.record}'
    line-width: 2px
    thumb-size: 10px
  waveform:
    color: '{colors.accent}'
    opacity: 0.50
    lane-height: 40px
  filmstrip:
    height: 48px
    thumbnail-height: 42px
    nominal-thumbnail-width: 75px
---

# Clip editor refresh — proposed visual contract

## Brand & Style

Carry the reference's compact timeline card into ReplayBuffer's established Fluent and Gaming looks. Preserve the quiet surfaces, scoped normal-case clip-editor controls, single accent and large picture from the accepted creator pass. The reference's purple is translated to the current accent rather than becoming a fixed app color.

Approved 2026-11-07: “perfect, let's begin implementation.” This addendum and its paired [EXPERIENCE.md](EXPERIENCE.md) govern the changed editor; spines win if a mockup differs. Navigation, export and media contracts otherwise inherit unchanged.

## Colors

All UI colors resolve through the existing semantic resources, using DynamicResource in a later WPF implementation. The offline prototype's Fluent dark/light palette and blue accent are examples, not a change to Windows accent behavior.

| Role | Existing resource | Gaming | Fluent |
|---|---|---|---|
| Window | SolidBackgroundFillColorBaseBrush | #0A0B0E | System base |
| Editor card | CardBackgroundFillColorDefaultBrush | #16191F | System card |
| Fields / transport play button | ControlFillColorDefaultBrush | #1E222A | System control |
| Dividers / card border | DividerStrokeColorDefaultBrush / CardStrokeColorDefaultBrush | #262B34 | System divider / card stroke |
| Text / secondary labels | TextFillColorPrimaryBrush / TextFillColorSecondaryBrush | #E6E8EB / #8B949E | System primary / secondary |
| Waveform, selection, grips | AccentFillColorDefaultBrush | #2DD4BF | Windows accent |
| Grip / Export foreground | TextOnAccentFillColorPrimaryBrush | #04201C | System on-accent |
| Playhead | RbRecordBrush | #FF4655 | System critical color, as already defined by the theme |
| Focus | FocusStrokeColorOuterBrush + FocusStrokeColorInnerBrush | Existing teal / base | Existing focus pair |

**Proposed semantic extension:** reuse `{colors.record}` for the red/pink playhead, matching the supplied reference. It indicates position inside this editor, not an error or recording state. A downward ruler thumb, continuous line, elapsed readout and accessible name communicate the role independently of color. Do not give it an error background or warning label. This use of the existing red token is a visible change from the current neutral playhead and was included in the builder's approval.

Unselected media receives a 55% base-surface veil. Selected waveforms remain visible through the 9% accent tint; grip contrast comes from the on-accent foreground. In Fluent light, outside content stays recognizable and labels use full secondary text opacity. The illustrative image's colors are media, not UI tokens.

## Typography

Segoe UI, compact normal-case labels and medium primary actions continue the accepted scoped clip controls in both looks. Use tabular numerals for elapsed, ruler, requested/effective times and duration. Label/readout text is 12px; ruler and effective captions are 11px. Keep the existing Gaming shell branding. Do not restore global uppercase or heavy Gaming button templates inside the creator.

## Layout & Spacing

One responsive column: creator heading and split Export; dominant aspect-preserving picture; 14px gap; editor card; small optional helper row. Idle shell status occupies no space. The prototype's design-review strip is outside the app and must never ship.

Card anatomy, top to bottom:

1. A 50px transport row: source elapsed / source total at left, centered −10s / Play-Pause / +10s, Preview selection at right.
2. A 28px shared ruler (26px in the original mock target; see implementation-notes.md) above all media rows, with a 100px fixed label gutter. Tick labels use readable intervals and suppress collisions; no independent time scales per row.
3. Named audio rows at **40px each**. Show up to five in a bounded scroll area; video filmstrip, ruler and shared selection/playhead stay stationary when audio lanes scroll. The two-track mock shows Game and Mic.
4. A **48px video filmstrip** using the same horizontal time mapping. It adds 48px of editor height relative to the current audio-only timeline. Its fixed height must not squeeze audio lanes.
5. A compact viewport / pan / zoom row, about 48px including divider, followed by In / Out / selected duration and the visible effective-boundary caption.

Measure controls first, then allocate remaining finite height to the picture; retain the existing 300px player minimum and aspect containment. Smaller windows scroll the editor. Aim for the accepted roughly 400px picture at 960×900 with two tracks where available; the filmstrip reduces available picture height, so final acceptance must explicitly consider that tradeoff. Do not claim an unchanged picture-height guarantee. Rail mode returns available width to the same layout.

## Elevation & Depth

The timeline is one flat card with a 1px quiet border; internal rules divide transport and precision sections. No independent elevated waveform tiles. Retain the accepted export menu/popup's quiet border and subtle shadow. Images may be darkened outside selection; labels and precision controls remain legible.

## Shapes

Proposed card corner 8px, selection 9px and 12px-wide grips with 7px corners echo the reference's rounded trim region. Existing field/button corners remain 6px. The visible grip is narrow, with a 24px-wide pointer target and at least 32px-high target. Where edges meet, one outline surrounds the media rows; no doubled frame at the audio/video seam. Selection is clipped to the plot, never over labels.

## Components

| Component | Required appearance and visual delta |
|---|---|
| Transport | Move the primary Play/Pause control from the picture's lower chrome into the card's true center; 36px circular quiet Play target with a 17px vector icon. Skip targets 34px with a small 10 annotation. Keep volume/audio-menu and fullscreen functions available on picture chrome. No second visible source scrubber. |
| Audio lanes | Exactly 40px high; compact named gutter; subdued accent waveform with whole-source scaling. Use proportional peaks, not independent rescaling on zoom. Current dense accent peaks become quieter in this proposal. |
| Video strip | About 75×42px nominal thumbnails with 1px separators, aspect-preserving sampling/crop, in a 48px row beneath audio. One point on every row refers to the same source time. Source frames replace the repeated illustrative SVG in the real product. |
| Selection | One 2px accent outline, 9% tint, rounded ends and integrated full-media-height grips spanning every visible audio row and the video strip. This replaces the current 1px rectangular selection and 10px/3px handles. At viewport clipping, omit the offscreen grip and avoid drawing a false boundary; numeric values remain authoritative. |
| Seek | Independent 2px `{colors.record}` line extending continuously from the ruler thumb through audio and thumbnails; 10px pointed thumb in a 24px target. Render above selection. Focus adds the existing focus pair without changing trim colors. An offscreen playhead has no clamped fake marker. |
| Viewport tools | Pan ← / →; separate − / magnification / + zoom group. Visible source-time window at left. Distinguish these tools from millisecond nudges with grouping and accessible names. At full-source view, pan and zoom-out show disabled states. |
| Precision | Keep the integrated In / time / − / + and Out / time / − / + groups, each with 1ms nudges and HH:MM:SS.mmm entry. Selected duration at the trailing edge; effective frames/samples visible below. Fields remain requested values. |
| Export / shell | Preserve accepted split action, type menu, options popup, track choices, mix/separate semantics, rail toggle and bottom Settings; no route/order changes. The current shell exposes Library as the primary destination; do not add the old mockup's illustrative destinations. |

Hover is a subtle surface change; selected grips keep their accent. Keyboard focus is a 2px outline with inner separation. Disabled media-editing affordances dim consistently without reducing time text to illegibility; cancellation remains reachable. Never depend on color alone for selection, seek or keyboard focus.

## Do's and Don'ts

- Keep one plot/time transform across audio, thumbnails, selection and playhead.
- Keep all 40px lanes and requested/effective precision behavior.
- Keep thumbnails quiet enough that waveforms and boundary grips remain easy to see.
- Use scoped vector icon templates so Gaming implicit text styles cannot replace icons.
- Preserve the review strip and demonstration notices only in the prototype, outside product flows.

Visual artifact: [.working/mockup.html](.working/mockup.html). Supplied inspiration: [timeline-reference.png](imports/timeline-reference.png). Screenshots are offline mockup evidence, not product runtime acceptance.

Implementation evidence and visual reconciliation: [implementation-notes.md](implementation-notes.md).
