# Encore

Encore is a Windows instant replay and recording app. Its identity is a single counterclockwise replay arrow: a moment worth returning to.

## Mark

The approved direction is the counterclockwise replay arrow alone, with a compact arrowhead and a narrow gap. Keep the center empty. The ring is an exact 300-degree circular arc with center `(128,128)`, radius `82`, and constant thickness `28` on a `256 × 256` canvas. The open tail has a round cap with radius `14`; the arrowhead join remains flat and perpendicular to the shaft. Before rotation, it joins a vertical arrowhead base at `(128,46)`, pointing horizontally left with equal height above and below the shaft. The entire mark is rotated 45 degrees counterclockwise about `(128,128)`. It extends beyond the ring intentionally; judge circularity by the ring, not the arrowhead's bounding box.

Use `encore-mark.svg` as the scalable master. White and ink variants support monochrome use. Keep the proportions, open center, and built-in clear space. Avoid gradients, glow, stretching, extra play symbols, or horizontal bars. Minimum standalone mark size: 16 px.

## App icon

`encore.ico` contains 16, 20, 24, 32, 40, 48, 64, 128, and 256 px frames. `encore-app-icon.svg` and `encore-app-*.png` use the mint mark on a charcoal tile. `encore-mark-*.png` have transparent backgrounds. Raster masters extend to 1024 px.

These are prepared assets; this kit does not change the app's current executable, tray, or navigation icon.

## Wordmark and type

Write the name **Encore** in title case. Use Segoe UI Semibold for the wordmark and headings; Segoe UI Regular for body text. The lockup SVGs have outlined wordmarks, so they display without a font dependency. `encore-lockup-dark.svg` is for dark backgrounds; `encore-lockup-light.svg` is for light backgrounds.

## Color

| Token | Hex | Use |
|---|---|---|
| Mint | `#2DD4BF` | Primary mark and brand accent |
| Ink | `#0A0B0E` | Dark canvas |
| Surface | `#16191F` | Cards and raised surfaces |
| Mist | `#E6E8EB` | Light wordmark and primary dark-theme text |
| Muted | `#8B949E` | Secondary text |
| Record | `#FF4655` | Recording status |
| Caution | `#F5B83D` | Warnings |

The palette continues the app's existing Gaming dark theme. Brand mint is separate from recording red. Fluent controls may continue following the Windows accent.

## Voice

Calm, direct, and specific. Suggested brand line: **Keep the moment.** Product description: **Instant replay and recording for Windows.** Use concrete interface copy such as “Clip saved”, “Recording”, and “Ready to save clips”.

## Reproduce

Run `pwsh -STA -File branding/generate-assets.ps1` on Windows. The generator uses WPF for exact vector rasterization and creates SVG masters, PNG exports, the multi-resolution ICO, and the preview sheet. No external packages are required.

The repository keeps only the SVG masters, this README, and the generator. The PNG and ICO exports and the preview sheet are regenerated, so git ignores them; the icons the app builds with are committed separately in `src/Encore.App/Icons/`.

The AI-generated exploration is kept outside the repository. Production exports use the mathematically constructed vector master rather than the approximate generated ring. Image exploration used the built-in image generation tool; the final edit prompt is saved beside that reference.
