---
id: 9
type: epic
title: "Modern UI"
parent: initiative-replay-buffer
covers: [R37]
sources: []
companions: []
after: []
refined: true
risk: medium
---

# Modern UI

## Description

This epic gives the Main App a finished look, in two themes the builder picks between in Settings. Fluent is .NET 10 WPF's built-in Windows 11 theme: it follows the Windows light or dark mode and the accent colour, with Mica where Windows offers it. Gaming dark is a custom near-black theme with a teal accent, square-ish corners and uppercase navigation. A `bmad-ux` design pass settles both looks first; then each UI area is restyled to it: the shell and navigation, the Library, the settings pages, and the tray menu, dialogs and Overlay messages. Switching applies live and is saved. Nothing the app does changes.

## Outcome

The builder opens the Main App in either look, switches between them without a restart, and finds no window, menu, dialog or control left in the old default style (R37).

## Requirements

- R1 (parent R37; architecture AD-4, AD-23): A theme setting in Settings offers Fluent and Gaming dark, with Fluent the default. Choosing one restyles every open window at once, with no restart, and the choice is the one in use after the App or the PC restarts.
- R2 (parent R37; architecture AD-23): Fluent follows the Windows light or dark mode and the accent colour, including a change made while the App is open, and shows Mica on the Main App window where Windows supports it (a solid background elsewhere).
- R3 (parent R37; architecture AD-23): Gaming dark uses its fixed palette (background #0A0B0E, surfaces #16191F and #1E222A, stroke #262B34, text #E6E8EB, muted text #8B949E, teal accent #2DD4BF, record dot #FF4655), 4 px corners, and uppercase navigation items and button labels, whatever the Windows mode.
- R4 (parent R37; architecture AD-23): In both themes every control the App uses is styled, in its normal, hover, pressed, selected, focused and disabled states: buttons, toggle buttons, text boxes, combo boxes, check boxes, radio buttons, sliders, list items, menus and context menus, tooltips, scroll bars and progress or level meters. Keyboard focus is always visible, and text meets WCAG AA contrast on its background.
- R5 (parent R37; architecture AD-15, AD-23): Every surface follows the chosen theme: the shell (title bar, navigation and status line), the Library, every settings page, the tray menu, the App's confirmation and error dialogs, and the Overlay's message cards. The Overlay's capture dot, saving icon and red X keep their meaning and colours in both themes, and every new window keeps `WDA_EXCLUDEFROMCAPTURE`.
- R6 (parent R37): The restyle changes no behaviour: every page keeps its settings, actions and messages, and every existing App test still passes.

## Constraints

- (architecture AD-23) Colours, brushes, corner radii and type come only from theme resources referenced with `DynamicResource`; no XAML or code in the App hard-codes a colour except the Overlay's status glyphs.
- (architecture AD-4, AD-22) The theme is a Global-only key in the closed catalog, added with a default, applied immediately; the engine stores it and the App applies it on `settings.changed`.
- (architecture AD-1, AD-3) The engine and Contracts' messages don't change beyond the new key; the epic is App-side.
- (CLAUDE.md) .NET 10 WPF's `ThemeMode` is marked experimental (WPF0001); the build stays at 0 warnings, so the suppression is scoped and justified where it's used.

## Done when

- Settings offers Fluent and Gaming dark, Fluent by default; switching restyles every open window live, and the choice survives an App restart (R1).
- Fluent follows a Windows light/dark or accent change made while the App runs, with Mica on the Main App window; Gaming dark shows its palette, corners and uppercase navigation in either Windows mode (R2, R3).
- A control gallery shows every styled control in every state in both themes, focus always visible and text at AA contrast (R4).
- The shell, the Library, every settings page, the tray menu, every dialog and the Overlay's messages follow the theme, and the Overlay's glyphs keep their colours (R5).
- Every existing App test passes, and every page and action still works (R6). Verified integrated on the builder's PC.

## Boundaries

- Out: new features, pages or settings other than the theme; changing a page's behaviour, wording or engine commands; a light variant of Gaming dark; per-game themes; custom accent colours beyond Windows' own; animations beyond the controls' built-in transitions.
- Out: the Overlay's status glyphs (dot, saving icon, mic-muted icon, red X), which keep their fixed colours for recognition over any game (epic-overlay-feedback).
- Touch point: .NET 10 WPF's Fluent theme (`ThemeMode`) and the Windows accent and app-mode settings, consumed only.
- Touch point: H.NotifyIcon's tray context menu, restyled through the App's resources.

## References

- parent — _bmad-output/initiative-replay-buffer/initiative-replay-buffer.md, Requirements R37
- architecture — _bmad-output/initiative-replay-buffer/architecture-replay-buffer/architecture-replay-buffer.md, AD-1, AD-3, AD-4, AD-15, AD-22, AD-23
- research — _bmad-output/initiative-replay-buffer/research/ui-look-mockups.html, ui-fluent-dark-render.png, ui-gaming-dark-render.png
- research — _bmad-output/initiative-replay-buffer/research/ui-look-render/ (a working WPF prototype of both themes; gaming-theme.xml has control templates for Button, ToggleButton, TextBox, ComboBox, CheckBox, RadioButton, Slider and ListBoxItem layered over Fluent's resource keys)

## Notes

- Waits on epic-recordings and epic-library-cleanup because: every page the restyle touches must be finished first, so the restyle is done once.
- Decision: 2026-11-04, the builder's decisions: a new epic, with a `bmad-ux` design pass first and then stories per UI area. Both looks from the mockups become selectable themes in Settings: Fluent (.NET 10 WPF's built-in `ThemeMode`, following the Windows light/dark setting and accent colour, Mica where available) and Gaming dark (a custom resource dictionary: #0A0B0E background, #16191F and #1E222A surfaces, #262B34 stroke, #E6E8EB text, #8B949E muted, #2DD4BF teal accent, #FF4655 record dot, 4 px corners, uppercase navigation and buttons). Switching applies live without a restart and is saved in settings; the default is Fluent.
- Decision: 2026-11-04, the timing: after epics 6 and 7, and before epic 8's final conformance run (8.7); 8.8, 8.9 and 8.11 follow 8.7, so they wait too. 8.7's `after` names 9.9.
- Decision: 2026-11-04, the theme is a Global-only settings key (`appTheme`, `fluent` | `gamingDark`, default `fluent`, applied immediately) in the engine's settings store, like `startWithWindows` and `overlayDot`, so it follows AD-4's single writer and survives restarts with the rest of the settings. Architecture AD-23 records the theming rule. Unattended default, awaiting review.
- Decision: 2026-11-04, the Overlay follows the theme for its message cards (surface, text, corners and type), but its capture dot, saving icon, mic-muted icon and red X keep their fixed colours in both themes: they are read over any game, and their meaning comes from the colour (initiative R6, R13). Fluent's Mica never applies to the Overlay, which stays transparent. Unattended default, awaiting review.
- Decision: 2026-11-04, the App's `MessageBox` prompts (delete, Cleanup and other confirmations) can't be themed, so story 7 replaces them with one themed dialog window, keeping each prompt's wording and default button. Unattended default, awaiting review.
- Decision: 2026-11-04, the tracer is the design pass (story 1), because every later story builds to its DESIGN.md and EXPERIENCE.md; the least certain part is the theme infrastructure (story 2: Fluent's experimental `ThemeMode` with a dictionary layered on it, live switching, and the controls the prototype doesn't cover: menus, tooltips, scroll bars, focus visuals and disabled states), so it comes next and carries a control gallery the later stories check against.
- Decision: 2026-11-04, the sequencing: the shell (3) comes before the Library (4) and the settings pages (5, 6), which sit inside it; the settings pages are split in two, the simpler pages first with the shared settings layout, then Track Layout, Game Profiles and Library and Cleanup. The tray menu, dialogs and Overlay (7) need only the infrastructure and can run beside 3 to 6. A Refactor sweep (8) and a closing end-to-end check on the builder's PC (9, hitl) close the epic, as in epics 6 and 7.
- Decision: 2026-11-04, the builder approved 9.1's DESIGN.md and EXPERIENCE.md and their renders. The Main App's default window size grows to 960×600 (the minimum stays 480×320), and the empty Library shows "No Clips or Recordings yet.". Under Gaming dark, the navigation's Settings section label is caption-sized and muted, so it doesn't read as a nav item.
- Decision: 2026-11-04, lean tests: the theme's logic is tested (the setting's round trip, live switching, the Gaming dictionary defining every theme key the App uses, every page loading in both themes with no missing resource); how it looks is the builder's check, against the control gallery and each story's hitl step. No screenshot comparison tests.
- Assumption: the design pass may rename or regroup the navigation (the mockups show Library, Recordings, Game profiles, Hotkeys, Audio tracks, Settings) as long as every existing page stays reachable; story 3 follows its EXPERIENCE.md.
