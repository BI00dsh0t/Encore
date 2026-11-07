# Draft preview evidence — 2026-11-07

Offline HTML preview only. No product builds/tests, media processing, real Dev App, settings or Library access. An owned headless Chrome process rendered the artifact and was closed afterward.

Inspected Gaming dark, Fluent dark and Fluent light at 1280×960, Gaming expanded/rail at 960×900, and the 480×640 scrolling layout. The two audio lanes measure 40px. All desktop theme variants have no page overflow or creator scrolling and allocate about 410px to the picture. At 960×900 expanded navigation, the picture is about 370px, without creator scrolling. At 480×640 the creator scrolls vertically; both complete field/nudge groups and effective readout are reachable without horizontal overflow. These HTML measurements do not prove WPF/DPI behavior.

Mock interaction spot checks passed: seek preserves trim, nudges change one millisecond, invalid/crossed input retains selection, focused trim handles respond to arrows, double-click selects inward-rounded viewport bounds, pointer trim preserves the playhead and updates effective text after release, audio Export options open without output, and picture audio settings open/dismiss. A mock pointer-capture/double-click target issue found during preview was corrected and the gesture rechecked. No browser script errors were reported.

The mock's peaks, repeated SVG thumbnails, playback and 60fps alignment are illustrative. Thumbnail extraction, media fidelity, state/lifecycle, busy/cancel/error/success feedback, many-lane scrolling, native dialogs, runtime contrast/high DPI and real pointer behavior remain unverified. The accepted product behavior is described in EXPERIENCE.md, including those contract-only states.

## Rendered views

- [Gaming editor close-up](.working/gaming-editor.png)
- [Gaming desktop](.working/gaming-desktop.png)
- [Fluent dark desktop](.working/fluent-desktop.png)
- [Fluent light desktop](.working/light-desktop.png)
- [960×900 expanded](.working/gaming-reference-960.png)
- [960×900 rail](.working/gaming-rail-960.png)
- [Narrow precision controls after scrolling](.working/gaming-narrow-controls.png)
- [Illustrative audio options](.working/audio-options.png)

All files remain draft working artifacts pending design review. No historical accepted spec or mockup was overwritten; no commit/push was made.
