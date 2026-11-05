# Renders of the ux-pass-2 design

Story 10.1's renders of epic 10's new screens, each in both themes (`-fluent` and `-gaming`).

| File | Shows |
|---|---|
| `library-*` | Library multi-select: three rows selected (one hidden by the date filter), the focused row, the command bar, the context menu on the selection, a being-saved row, and the search and sort controls. Under Gaming dark the command bar overflows, so Copy to folder… and Show in Explorer move into **More ⋯** (see the DESIGN.md addendum). |
| `library-search-*` | Nothing selected: the summary bar, a search ("clutch"), Sort by Largest first, Group by None, and a Recording's kind badge |
| `welcome-*` | The empty Library's welcome card, with the status reading "Waiting for a game" |
| `audio-tracks-*` | Audio Tracks: the save bar, the nav's unsaved dot, three Track cards with per-source meters, a not-connected source, the double-counting line, a silent track, the presets button, and the sound picker open with meters and the Windows defaults |
| `games-*` | The Games list: captions, the Capturing now and Not captured pills, the unsaved dot, Ignored games last, and a focused row |
| `game-page-*` | A game's page: the breadcrumb with Back, Capture, Clips using the main setting, Recordings, Video quality changed for the game with Reset, Audio tracks using the main setting, and Files |
| `clips-*` | The Clips page (was Replay Buffer): Clip length, Memory or Disk, the cache folder picker, and Video quality |
| `hotkeys-*` | Hotkeys grouped as Clips, Recordings, Monitor Recording (with the screen picker) and Microphone, each with its sentence |
| `storage-*` | Storage and Cleanup: the Clips folder picker and Cleanup |
| `surfaces-*` | The tray menu with its status item, the bulk delete and reset confirmations, and the presets menu with its confirmation |

## How they're made

They come from the scratch WPF prototype in `source/`. It isn't the App.

- **Real theme styles:** it loads the App's real theme dictionaries through `ThemeService` (by reflection on `ReplayBuffer.App`), so every existing control, card, nav item and token renders as it ships.
- **New components:** the components epic 10 adds are composed from those tokens, following the DESIGN.md addendum.
- **Output:** it renders with `RenderTargetBitmap` at 2×, 960 px wide. Tall pages are rendered taller than the 600 px window, so the whole page shows.

EXPERIENCE.md and DESIGN.md win where a render differs.

## What they don't show

- **Mica.** An off-screen bitmap can't capture it.
- **Fluent's accent colour.** It follows Windows, and it's grey on the PC that rendered these. So under Fluent the selected Library rows, the Save changes button and the checked boxes read grey.
  - With a coloured accent they take that colour.
  - With a grey accent, the selected rows are only faintly different from the others. Story 10.2 checks this on the builder's PC. The addendum's fallback is to draw the selected fill as `SubtleFillColorSecondaryBrush` plus the accent bar when the accent is low-contrast.
- **Program icons.** The Games icons are lettered stand-ins; the App reads each program's own icon.
- **Hover and focus, except where drawn statically:** the hovered menu item, picker row and tray item, and the focused Library and Games rows.

## Rebuilding them

1. Copy `source/Program.cs.txt` and `source/Render10.csproj.txt` to a scratch folder as `Program.cs` and `Render10.csproj`.
2. Point the `ProjectReference` at `src/ReplayBuffer.App/ReplayBuffer.App.csproj`.
3. Run `dotnet build -c Release`.
4. Run `Render10 <fluent|gaming> <scene> <out.png>`, with a scene from the table's file names.
