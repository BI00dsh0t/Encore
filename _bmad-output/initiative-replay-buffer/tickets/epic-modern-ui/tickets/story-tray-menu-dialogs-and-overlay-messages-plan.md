---
title: 'Tray menu, dialogs and Overlay messages'
type: 'feature'
ticket: '7'
created: '2026-11-04'
status: done
baseline_revision: '9d8f588'
risk: 'medium'
context: ['{project-root}/CLAUDE.md', 'ux-modern-ui/DESIGN.md', 'ux-modern-ui/EXPERIENCE.md']
---

# Plan: 9.7 Tray menu, dialogs and Overlay messages

**Ticket:** 9.7 (epic-modern-ui), R5 (tray, dialogs, Overlay) and R6. HITL, risk medium.

**Approach:** Build on 9.2's `ThemeService` and token dictionaries; no new theme keys and no edit to the shared dictionaries.

- **Tray menu:** `CreateTrayMenu`'s `ContextMenu` already takes its implicit `ContextMenu`, `MenuItem` and `Separator` styles from `Application.Resources` (9.2's Gaming templates, Fluent's built-ins), so it restyles live with no code change. A test proves it.
- **Dialog:** `PromptDialog`, an owned modal `ShellWindow` (so `WDA_EXCLUDEFROMCAPTURE`, AD-15) laid out per DESIGN.md "Dialog": 440 wide, the caption in `SubtitleTextBlockStyle` with a Segoe Fluent Icons glyph (Question in `text-accent`, Warning in `caution`, Error in `danger`), the message in `BodyTextBlockStyle` on `bg-base`, and a `surface-card-secondary` button bar with Yes (`AccentButtonStyle`, `IsDefault`, focused first) and No. Esc and the close box answer No. With no shown owner it centres on the screen and shows in the taskbar. `Prompts.YesNo` keeps its signature and wording, so `LibraryPage` and `LibrarySettingsPage` don't change.
- **Overlay card:** the message `Border` takes `RbOverlayCardBrush`, `SurfaceStrokeColorFlyoutBrush` (1 px), `ControlCornerRadius` and `CaptionTextBlockStyle` by resource reference; the text is `SystemFillColorCriticalBrush` for an Error and `SystemFillColorSuccessBrush` otherwise. Padding is 7×2 inside the 1 px stroke (8×3 outside), keeping today's 22 DIP height and 260 DIP max width. The glyph brushes, transparency, click-through and no-activation are unchanged.

**Tests (lean):** `SurfacesThemeTests`: the tray menu's styles come from the theme and follow a switch; the dialog answers Yes, No, Esc and close as MessageBox did, with Yes the default and the window excluded from capture; the Library delete and Cleanup confirmation still get their answers through it; the Overlay card's brushes come from the theme while the glyph colours stay. PNGs of the tray menu, dialog and Overlay card in both themes go beside the App test binary.

**Never:** No engine, Contracts or page-layout changes. No Mica or theme on the Overlay window. No screenshot comparison tests.

## Log

- 2026-11-04: built and verified. 0-warning Release build; App default suite 139 passed, 18 explicit skipped. Human check: open the tray menu, delete a Clip through the new dialog, and see an Overlay message during a game, in both themes.
