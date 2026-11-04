---
title: 'Refactor sweep'
type: 'refactor'
ticket: '8'
created: '2026-11-04'
status: done
baseline_revision: 'b51b93ebb284e6b616dd27632735098bbcdcd3cf'
route: 'quick'
risk: 'low'
context: ['{project-root}/CLAUDE.md', 'ux-modern-ui/DESIGN.md']
deferred: ['stale shell renders', 'Library tab wording']
---

# 9.8 Refactor sweep

## Intent

Epic 9 (9.1–9.7) deferred no review finding (every plan has `deferred: []`). The sweep looks at the theme code itself: `ThemeService`, the token dictionaries (`SharedTheme`, `FluentTheme`, `GamingDarkTheme`), the area dictionaries (`ShellFluent`/`ShellGamingDark`, `SettingsLayout`, `LibraryTheme`), the views, the tray, `PromptDialog` and the Overlay, for duplicate styles and brushes, unused or missing tokens, hard-coded colours, old plain-WPF remnants and stale docs. No behaviour, look, wording or Contracts change.

## Findings and changes

- **Duplicate helper.** `SettingsLayout.Place` and `LibraryTheme.Place` were the same "add once, before the last entry" method. Now one private `ThemeService.AddOnce<T>`.
- **Unused member.** `ThemeService.Shared` had no caller. Deleted (no ticket names it).
- **Duplicate brushes.** Gaming's `WindowBackground` and `WindowForeground` repeated `#0A0B0E` and `#E6E8EB`. Now `StaticResource` aliases of `SolidBackgroundFillColorBaseBrush` and `TextFillColorPrimaryBrush`, the same brush instances.
- **Hard-coded colour.** The nav list's `Background="Transparent"` now uses `SubtleFillColorTransparentBrush`.
- **Converter placement.** `EmptyToCollapsedConverter` moved from `SettingsLayout.cs` to `ThemeConverters.cs`, beside the other converters.
- **Stale names and docs.** `LibrarySettingsPage.ConfirmWithMessageBox` is `ConfirmWithPrompt` (it calls the themed `Prompts`). `LibraryPage`'s doc says "page", not "tab". `TrackLayoutPage.IsOpen`'s doc no longer names the Settings tab.

## Checked and kept

- **Tokens used but missing:** none. The keys the App references and doesn't define are Fluent's own (`BodyTextBlockFontSize`, `CaptionTextBlockFontSize`, `TitleTextBlockFontSize`, `SubtleFillColorSecondary`, `SystemFillColorCritical`, `SolidBackgroundFillColorQuarternary`).
- **Defined, not referenced by the App:** `RbSpacingXS`–`RbSpacingXXL` and `RbNavWidth` are DESIGN.md's Layout & Spacing tokens, kept as the spec defines them. `MainWindow` sizes the nav column in code (`NavWidth`, narrow mode). `WindowBackground`, `WindowForeground` and `DefaultCollectionFocusVisualStyle` override Fluent's keys, which Fluent's styles reference.
- **TabControl and TabItem styles** and the gallery's tab sample stay: DESIGN.md "Tab control" keeps TabItem styled for the gallery.
- **`IsSettingsTabSelected`** keeps its name: EXPERIENCE.md names it.
- **`RbShellUppercase`** stays beside Gaming's `RbUppercase`. The shell dictionary is built on its own before it's merged, so a `StaticResource` to the theme's converter wouldn't resolve.
- **Fixed colours:** the Overlay's glyph brushes (exempt under AD-23) and the tray icon's generated bitmap colours (`TrayCapture`), which are the state glyph in the notification area, not a themed surface.
- **`MessageBox`:** only `MessageBoxImage` remains, as `Prompts`' icon argument.
- **The views' per-view `BooleanToVisibilityConverter` "Visible"** stays. Moving it to a shared dictionary would make each view's `StaticResource` depend on Application resources at load, which the tests' window-scoped themes don't provide.

## Verification

- `dotnet build ReplayBuffer.slnx -c Release`: 0 warnings, 0 errors.
- `ReplayBuffer.App.Tests` (default): 170 passed, 0 failed. Contracts and Core aren't touched, so their suites were skipped. Engine code isn't touched, so the NativeAOT publish was skipped.
- Renders: every PNG the App tests write was compared with `ux-modern-ui/renders/`. Gallery, dialog, Library, Overlay card, tray menu and all ten settings-page renders are byte-identical in both themes. The shell renders differ from the committed ones, but develop's tip (`b51b93e`, built in a scratch worktree) renders the same shell as the sweep: 104 px differ, all in the status clock's seconds. The committed shell renders predate 9.5's General restyle (recorded in `deferred-work.md`).

## Log

- 2026-11-04: Swept, verified, and recorded the two open items in `deferred-work.md`.
