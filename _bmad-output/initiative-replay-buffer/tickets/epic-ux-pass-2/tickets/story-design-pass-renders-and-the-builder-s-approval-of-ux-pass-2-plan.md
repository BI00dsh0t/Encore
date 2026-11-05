---
title: "Design pass: renders and the builder's approval of ux-pass-2"
ticket: 1
status: in-review
---

# Plan: 10.1 design pass

**Intent:** put the ux-pass-2 spines to the builder, with renders of every new screen in both themes and a short decision sheet. Documents only: no App code, no build of the solution, no tests (the coordinator's brief; everything outside epic 10 is paused).

## Done

- [x] **Render prototype** (`ux-pass-2/renders/source/`).
  - A scratch WPF project, as in 9.1, but it applies the App's real theme dictionaries through `ThemeService` (by reflection on `ReplayBuffer.App`), so the existing controls render as they ship. The new components are composed from the tokens per the DESIGN.md addendum.
  - The source is committed as `.txt`.
- [x] **Renders:** 20 PNGs in `ux-pass-2/renders/`, 10 scenes × Fluent and Gaming dark, with a README.
  - The Library with a multi-selection, the command bar and the context menu.
  - The Library with nothing selected, search, sort and the kind badge.
  - The welcome card.
  - Audio Tracks: the cards, the save bar, the nav dot, the picker with meters, and the presets button.
  - The Games list and a game's page.
  - The Clips page with the cache folder picker.
  - Hotkeys, grouped.
  - Storage and Cleanup.
  - The tray menu, the confirmations, and the presets menu.
- [x] **Design fixes the renders found,** written into the DESIGN.md addendum and EXPERIENCE.md:
  - The command bar's "More ⋯" overflow is needed at the default width under Gaming dark.
  - The folder picker's buttons go on a row under the path.
  - A stronger selected fill is needed when the Fluent accent is grey.
- [x] **The decision sheet** (`ux-pass-2/DECISIONS-FOR-BUILDER.md`): the page names and order, removing the Recordings page, the glossary labels, Favourites protected from Delete, a tray Save Clip now, the presets, and the renders themselves. Each has a recommendation and its alternatives.

## Left for the coordinator

- The builder's answers become Decision lines in the epic's Notes. The "builder approved" line isn't written here.
- Any change the answers need goes into the spines before 10.2 and 10.4 start.

## Verification

- Every scene rendered in both themes, with no missing-resource gaps seen on inspection.
- Every new component in the DESIGN.md addendum appears in at least one render. The Library kind badge is in `library-search-*`, the breadcrumb in `game-page-*`, and the inheritance chips in `game-page-*`.
