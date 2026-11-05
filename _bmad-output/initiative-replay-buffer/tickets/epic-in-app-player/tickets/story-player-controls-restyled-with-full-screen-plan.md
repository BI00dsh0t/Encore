---
title: 'Player controls restyled, with full screen'
type: 'feature'
ticket: '9'
created: '2026-11-05'
status: done
baseline_revision: 'a639f57'
route: 'quick'
risk: 'medium'
---

# Story 14.9: Player controls restyled, with full screen

Built on develop `a639f57` (14.7 merged), at the builder's word on the mockup: "Yes, build it." The spec is the mockup's two boards, `project/Main.dc.html` (gaming dark) and `project/Fluent.dc.html` (Fluent dark), at https://claude.ai/artifact/4CCgWPDzuYpDAxFJWsZdxz. App only; AD-27. Under the builder's current rule, it builds with 0 warnings and its tests are written but not run.

## Changes

- **`PlayerView.xaml`**, the layout from the mockup:
  - The video on black. Under it, one bar on the Overlay's card brush: a thin, full-width seek bar above a single 48 px row.
  - **Left:** Play/Pause (a glyph, named "Play" or "Pause" for screen readers and the tooltip). Then the speaker: a click mutes, and its 72 px volume slider slides out while the pointer or keyboard focus is in the speaker's group. Then the time, elapsed / total. When the file can't play, 14.6's "Get the … Video Extension" and "Open in default player" follow.
  - **Right:** the gear ("Player settings") and full screen ("Full screen" / "Exit full screen"). No logo button.
  - The Audio track ComboBox (14.4) and the overflow are gone. The gear's menu replaces them.
- **`LibraryTheme.xaml`**, tokens only, in both looks:
  - **The bar:** `RbOverlayCardBrush`, 16 / 10 / 16 / 6 px padding.
  - **The seek bar:** a 4 px line in a 16 px hit area. The played part is `AccentFillColorDefaultBrush`, the rest `ControlStrongStrokeColorDisabledBrush`, and a 12 px round thumb in `TextFillColorPrimaryBrush` (white in the dark looks). The volume slider is the same, its played part in the text colour, as the mockup draws it.
  - **Icon buttons:** 40 px, `ControlCornerRadius`, transparent, then `SubtleFillColorSecondaryBrush` on hover and `…Tertiary` pressed. The gear holds the subtle fill while its menu is open. Glyphs are Segoe Fluent Icons: Play `E768`, Pause `E769`, Volume `E767`, Mute `E74F`, Settings `E713`, Full screen `E740`, Back to window `E73F`.
  - **The time:** 14 px, text-primary, tabular figures.
  - **Full screen:** the backdrop is square, and the bar sits over the video's bottom edge.
- **The gear menu** (`PlayerView.OpenSettingsMenu`), the themed ContextMenu built from the page:
  - "Audio track", as a caption-size text-secondary heading that can't be focused or clicked;
  - the Tracks in file order, the playing one ticked (`IsChecked`), and an undecodable one disabled (14.6);
  - a `Separator`, then "Open in default player".
  - It opens by click, Enter or Space on the gear (the ToggleButton's own keys). Esc, a click outside and a choice close it (the ContextMenu's own), and the arrow keys move through its focusable items. A choice sets `PlayerPage.SelectedTrack`, so 14.4's Track rules hold. Leaving the page closes it.
- **Full screen:**
  - **`PlayerPage`:** `IsFullScreen`, `FullScreenCommand`, `FullScreenGlyph` and `FullScreenName`. `Dispose` leaves full screen.
  - **`PlayerView`:** F toggles it (the page's keys, after a focused control's own).
  - **`MainViewModel`:** `IsPlayerFullScreen` follows the Library's player page and its `IsFullScreen`, and `LeavePlayerFullScreen()`.
  - **`MainWindow`:**
    - **Entering:** the window goes borderless and maximized (Normal first, so it covers the taskbar). The nav pane, status line, page header and page padding collapse, and the content layer turns square and black.
    - **Leaving:** each value goes back as it was.
    - **Esc** in full screen only leaves it; the page stays open.
    - **Closing the page,** by Back, a settings page or Quit, leaves it with the page's disposal.
- **Unchanged from 14.2 to 14.7:** the keys (Space, Left/Right 5 s, Up/Down 5 %, Esc), the volume and mute rules and the kept volume, the unknown length, the failures, and the Tracks.
- `PlayerPage`'s glyph strings are `\uXXXX` escapes; 14.5's `MuteGlyph` had raw private-use characters.

## Tests (written, not run: the test backlog)

- **`PlayerViewThemeTests.ThePlayerPageShowsInBothLooksAndTheHiddenListIgnoresItsKeys`**, rewritten for 14.9, per look:
  - **At rest:** Play named "Play"; the seek bar, gear, full-screen button and `0:04 / 0:10` showing; the volume slider hidden.
  - **The volume slider:** it shows on the group's mouse enter, hides on leave, shows when Mute takes focus, and hides when focus moves to Play.
  - **The page's keys, as in 14.5:** Right, Down, Space.
  - **The gear menu:** the heading "Audio track" (not focusable); Game, Mic, Discord, Desktop with Game ticked; one separator; "Open in default player" last; each item focusable. Discord invoked through its automation peer gives `SelectedTrack` 2 and closes the menu, with the gear unchecked.
  - **Full screen:** F enters it (`WindowStyle.None`, nav collapsed); Esc leaves it with the page still open; the button enters it again; closing the page leaves it.
  - The codec and failure parts are unchanged.
- **The PNGs that test renders,** beside the App test binary: `player-fluent.png`, `player-gaming.png` (resting), `player-menu-fluent.png`, `player-menu-gaming.png` (the open menu), and `player-fullscreen-fluent.png`, `player-fullscreen-gaming.png`. **Not rendered yet:** they come only from that test, so they wait for the test backlog.
- The other player tests (`PlayerTransportTests`, `PlayerTrackTests`, `PlayerFailureTests`, `LibraryPlayerTests`) drive the page, not the controls, and still apply unchanged.
- **Builder's check:** compare the bar and the menu with the mockup.

## Verification

- `dotnet build ReplayBuffer.slnx -c Release`: 0 warnings, 0 errors.
- No tests run, per the builder's rule. The test backlog's 14.9 row should name `PlayerViewThemeTests` (the one test changed) and the six PNGs.

## Log

- 2026-11-05: branch `feature/14.9-player-restyle` from develop `a639f57`; built, tests deferred.
