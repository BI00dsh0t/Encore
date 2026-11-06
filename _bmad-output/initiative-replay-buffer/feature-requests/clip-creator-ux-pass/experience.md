# Interaction and acceptance: clip creator UX pass

## Composition

The accepted `mockup.html` is the visual reference. Use the product's existing destinations, theme tokens and glyph conventions; the driving scene, Game/Mic names, Dashboard/Recordings placeholders and design-tweak sliders are illustrative.

The shell has a title-bar sidebar toggle, a primary navigation region and a separate bottom Settings button. The creator heading identifies the source, offers Back and places the split export action at the right. A large aspect-preserving player occupies the main area. Playback controls and Preview selection sit immediately below it, followed by a compact waveform/ruler, small pan/zoom tools, In/Out fields and selection duration. Show progress, cancellation and messages when relevant. Do not reserve permanent space for a long idle export form or repeated instructional paragraphs.

Reference sizing is a 960×900 client area at 100% scale with two tracks, in each sidebar mode. Target an approximately 400 px or taller picture and roughly 24 px waveform lanes plus a shared ruler, matching the reference proportions. These are layout targets rather than fixed heights: preserve aspect ratio, readable controls and available space at other sizes/scales. Many tracks use a bounded scrollable lane region with shared time mapping, rather than increasing the whole editor indefinitely. A track reached by keyboard must scroll into view. Smaller windows may scroll the editor; controls must not overlap, clip or disappear.

Start/end remain numeric to milliseconds, with accessible 1 ms nudges and draggable handles. Zoom buttons show plus/minus; pan buttons show left/right arrows. Tooltips distinguish timeline zoom from boundary nudges. The effective range remains visible in compact form; precision information must not be available only on hover. Do not restore the former tall waveform merely to satisfy its old minimum-height test.

## Export behavior

1. New creators start with Export video, preserving the existing MP4 default. The arrow opens a small menu with Export as video and Export as audio, showing the current choice. Choose one to update the main action to Export video or Export audio. Keep the source, requested selection and included tracks; refresh effective boundaries for that format and invalidate stale rendered preview.
2. The main action opens a modal options popup for that type. It must not render, open a save chooser or start export merely because the popup opened. Keep the underlying editor visible for context.
3. Show a name field, applicable format summary, named included tracks, separate/mix choice when multiple tracks are selected, and a compact selected-range/duration summary. All/None conveniences may be compact or contextual; every named track must remain selectable. With one track, avoid presenting an irrelevant mixing choice. Explain overlapping audio when mixing could double count it.
4. Video summarizes MP4 / H.264 / AAC and the existing source-resolution behavior. Multiple separate tracks mean selectable named streams in one MP4; a mix means one stream. No tracks means silent video. Audio summarizes MP3 / stereo / 48 kHz / 192 kbps; separate tracks mean named MP3 files in one directory and a mix means one MP3. Selecting no audio disables audio saving with a plain explanation. Do not show irrelevant video-only controls for MP3 or introduce unsupported codec/resolution settings.
5. Closing the popup starts no work and leaves the source/range intact. Under the recorded assumption, current track/mix choices remain available to Preview selection outside the popup; no Apply action is required. The next popup reflects those choices. Preview/export use the same effective configuration, retaining MP3 sample boundaries independently of MP4 picture alignment.
6. Save video… or Save audio… invokes the appropriate existing file chooser, or folder chooser for separate multiple MP3 files. Cancelling it starts no work. Confirming a valid destination starts one operation with current progress/cancel behavior. Repeated presses cannot launch concurrent work. Keep the source and existing output safe on collision, cancellation or failure.

The mockup demonstrates the interaction; it does not model real encoding, chooser validation, source disappearance or every disabled/busy state. Existing lifecycle rules determine those states in the product. Opening Settings is genuine navigation and follows the existing creator cancellation/disposal rules; toggling sidebar width is not navigation.

## Sidebar and Settings

- The toggle is always reachable by keyboard and pointer and exposes expanded/collapsed state. Expanded navigation shows icons and labels; collapsed navigation shows the same icons with accessible names and tooltips. Use one selected-page indication in either state. The mockup's approximately 184 px/58 px widths are reference proportions, not fixed sizing requirements.
- Toggling expands/contracts the layout and gives released space to the editor. Preserve current page, source, requested range, chosen format/audio, viewport and pending work. Reflow can request appropriately sized waveform detail; it must not reset the selection or re-open the media source.
- Settings is one dedicated bottom button in a separate layout region from the primary destinations. It remains anchored while primary navigation/content scrolls; it is not duplicated in the main list. Expanded: gear and Settings label. Collapsed: gear and Settings tooltip. Selecting it shows the existing Settings pages and active indication with existing settings/return behavior.
- Preserve actual destinations and their existing order; the mockup does not authorize inventing Dashboard or Recordings pages or moving Settings sections into top-level destinations. On narrow windows the expanded menu may overlay content rather than squeezing the editor; it must be dismissible and must not strand keyboard focus.

## Acceptance cases

| Case | Given / When | Observable result |
|---|---|---|
| Workspace (R1, R9) | Open a finished two-track item at the reference size in either look and sidebar mode. | Video is larger than the former 300 px player; the compact waveform, time fields, preview and export fit without scrolling through export settings. A many-track item remains bounded, with every lane reachable and no overlap. |
| Precision (R2, R9) | Set a millisecond range, nudge either endpoint, zoom/pan, then resize/toggle the sidebar. | Text and range agree to 1 ms; selection survives and finer source bins appear on zoom. Effective sample/frame boundaries remain displayed. |
| Split action (R3) | Choose each type using only the arrow, then activate the main action. | Correct type/check and matching options appear; neither action starts encoding or the chooser. Dismissal restores trigger focus. |
| Type-specific options (R4) | Select one, several or no named tracks in video/audio options. | Applicable separate/mix controls and file/folder meanings are clear; silent video works and no-track MP3 cannot save; no unselected track is silently included. |
| Preview parity (R5) | Change type/track/mix, close options, preview, then reopen Export. | Stale preview is invalidated; current configuration survives dismissal; preview and export requests use matching effective range and audio choices. |
| Save/cancel (R5, R8) | Save, cancel the chooser, save again, then cancel active work or simulate a failure. | Cancelling the chooser starts nothing; confirmed output starts only once; progress/cancel and actionable result remain reachable; source/existing output stay intact. |
| Sidebar (R6) | Toggle while idle, editing, previewing or performing an export. | Icons/labels/tooltips and selected page remain clear; editor reflows without source reload, state loss, duplicate work or width-toggle cancellation. |
| Settings (R7) | Use the bottom Settings button in expanded and collapsed modes. | Same existing Settings experience opens, with current lifecycle behavior; button is outside the primary list, remains bottom-anchored and has the correct label/tooltip. |
| Access and lifecycle (R8–R9) | Use keyboard only, Escape, focus traversal, a narrow/high-scale window, many tracks, disappearance/navigation and both looks. | Controls and effective range are reachable/legible; overlays dismiss and restore focus; source safety and owned-operation cancellation follow the baseline. |

These are future verification criteria, not claims of completed runtime tests. Reuse/adjust the existing lean `ClipCreatorTests` regressions, including the progress binding, millisecond display and both-look layout cases. Add only focused coverage for new interactions. The currently allowed non-Explicit clip-creator classes do not authorize unrelated shell/Settings tests, Heavy media, benchmarks, publish or HITL. Record unrun checks in the backlog; clear shared-shell test execution with the builder before running it.
