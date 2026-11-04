---
title: 'Theme infrastructure, live switching and a control gallery'
type: 'feature'
ticket: '2'
created: '2026-11-04'
status: done
baseline_revision: '1fa7ed1'
risk: 'high'
context: ['{project-root}/CLAUDE.md', 'ux-modern-ui/DESIGN.md', 'ux-modern-ui/EXPERIENCE.md']
---

<intent-contract>

## Intent

**Problem:** The App has no theme: every window uses WPF's classic look. The area stories (3 to 7) need one token set, two themes on it, live switching and a gallery to check the controls against (AD-23, R1 to R4).

**Approach:** A Global-only `appTheme` key (`fluent` | `gamingDark`, default `fluent`, Immediate) in the AD-4 catalog, added under AD-22. An App `ThemeService` applies it: Fluent is `ThemeMode = System`; Gaming dark is `ThemeMode = Dark` with `GamingDarkTheme.xaml` merged after Fluent's dictionary. Both resolve every DESIGN.md token key. The service follows the engine's `settings.changed` and swaps the merged dictionary in place on the UI thread. It sets each window's frame through DWM: Mica behind the Main App under Fluent, and Gaming's caption colours.

## Boundaries & Constraints

**Always:** Colours, corners and type come only from token keys through `DynamicResource`. The theme is applied before the first window shows. Switching recreates no window. WPF0001 is suppressed only on the lines that use `ThemeMode`, with the reason. The Overlay (a transparent window) never gets Mica or a caption colour.

**Never:** No page layout changes beyond the theme picker, which stories 3 to 7 own. No engine behaviour beyond storing the key. No `MessageBox` changes (story 7). No screenshot-comparison tests.

</intent-contract>

## What was built

- **Contracts:** `AppTheme` enum (camelCase JSON), `Keys.AppTheme` (Global, Immediate, default Fluent) in `Keys.All`, `EffectiveSettings.AppTheme`, and the source-generated serializer entry. Core's resolver fills it.
- **Platform.Desktop:** `WindowFrame` (CsWin32): Mica on or off (`DWMWA_SYSTEMBACKDROP_TYPE` plus the extended frame), and the caption and text colours.
- **App `Theme/`:**
  - `SharedTheme.xaml`: the `Rb*` spacing constants, `RbCardStyle`, `RbInfoBarDangerStyle` and `RbInfoBarCautionStyle`, `RbStarToggleStyle`, `RbLevelMeterStyle` (success fill, caution at 90 % or more, text-disabled when disabled).
  - `FluentTheme.xaml`: Fluent's side of the App keys (`RbAccentSubtleBrush`, `RbRecordBrush`, `RbOverlayCardBrush` on Fluent colour keys, and the small and nav-item radii), plus `RbSectionHeaderStyle`, `RbPageTitleStyle` and `RbBrandStyle`.
  - `GamingDarkTheme.xaml`: every token, Fluent's `WindowBackground` and `WindowForeground`, the focus visuals (`DefaultControlFocusVisualStyle`, `DefaultCollectionFocusVisualStyle`), the type styles, and templates in every state for Button, AccentButtonStyle, ToggleButton, TextBox, ComboBox and its items, CheckBox, RadioButton, Slider, ListBox, ListView and their items, ContextMenu, MenuItem, Separator (including `MenuItem.SeparatorStyleKey`), ToolTip, ScrollViewer and ScrollBar, TabControl and TabItem, and ProgressBar.
  - `UppercaseConverter` for Gaming's button, toggle, tab and title templates, and `LevelPeakConverter`.
  - `ThemeService`: `ForApplication`, `ForWindow` (tests), `Apply`, `Follow(IEngineSession, …)`, `Decorate(Window)`, `ReadLast` and `WriteLast`, and `TokenKeys`.
  - `ControlGallery`: a `ShellWindow` with every control in every static state.
- **Shell:** `ShellApp` applies the last theme (`%LOCALAPPDATA%\<instance>\app-theme.json`) before creating any window, then follows `settings.changed`. `ShellWindow` asks the service to set its frame as it gets a handle. `MainWindow.UsesMica` is true. The tray menu has a **Control gallery** item on dev and test instances only.
- **General page:** a Theme combo box ("Fluent (follows Windows)", "Gaming dark") with the help text "Applies at once to every window.". It sends `settings.update` at once, like Start with Windows.

## Decisions

- **WPF's own backdrop is off.** .NET 10's Fluent theme puts Mica (`DWMSBT_MAINWINDOW`) behind every non-transparent window, in both `System` and `Dark`. DESIGN.md wants Mica on the Main App only and never under Gaming dark. So `ThemeService` sets the `Switch.System.Windows.Appearance.DisableFluentThemeWindowBackdrop` AppContext switch, which gives solid window backgrounds, and sets Mica itself on the Main App through DWM. With the switch on, Fluent's Window style paints `WindowBackground`, so Gaming redefines that key as `bg-base`.
- **The theme applied at start is the last one applied,** cached by the App in its local data folder. The engine's snapshot arrives after the first window is created (unless the App starts in the tray), so without the cache a Gaming user would see a Fluent flash. The engine's settings stay the record: each `settings.changed` applies the committed key and updates the cache.
- **Type styles are ContentControl styles** (`RbSectionHeaderStyle`, `RbPageTitleStyle`, `RbBrandStyle`), because WPF has no text-transform and Gaming uppercases them in a template (DESIGN.md Typography).
- **The DWM caption colour** is set here with Mica, as EXPERIENCE.md's Build Map gives it to story 2's ThemeService. Story 3 still owns the title bar's final look.
- **Fluent's disabled text (QA 9.2, F1).** Fluent's built-in TextBox, ListBoxItem, ListViewItem and TabItem keep full-contrast text when disabled. So under Fluent the service layers a style over each of Fluent's own styles (`BasedOn` the style in WPF's Fluent dictionary) that sets `TextFillColorDisabledBrush` while disabled. Everything else stays Fluent's.
- **Pressed list items:** WPF's `ListBoxItem` has no pressed state, so Gaming shows hover and selected only.
- **Tests scope the theme to one window** (`ThemeService.ForWindow`), since a process has one `Application` and other tests' views must not inherit it. A window-scoped `ThemeMode` skips a window whose `Resources` were set before it, so the gallery keeps no resources of its own. The App uses `Application.ThemeMode`, which has no such limit.

## Known limits, left to the area stories

- The views with their own implicit styles (Hotkeys, Replay Buffer, Library and Cleanup, and Track Layout's `Row` buttons) replace the theme's style for those controls, so those controls keep WPF's classic look until stories 5 and 6 restyle the pages.
- The window size (960×600) and the nav pane are story 3's.

## Verification

- Release build of the solution: 0 warnings.
- Default tests: Contracts 123/123, Core 152/152, App 135 passed and 18 skipped (the opt-in Heavy tests). New tests:
  - `KeysTests.AppThemeIsGlobalImmediateFluentByDefaultAndWritesGamingDarkCamelCase`
  - `EngineConnectionTests.TheThemePickerSendsAppThemeAndTheEngineReadsItBackAfterARestart` (Guid test instance)
  - `ThemeTests`: Mica behind the Main App under Fluent only, never behind the gallery (QA F2; skipped where Windows has no backdrops); Fluent dims a disabled text box and list item (QA F1); every token key in both themes; a `settings.changed` swapping the dictionary in the same window; every `DynamicResource` key in the App's XAML resolving in both themes; the gallery, Main App, every settings page and the Library loading in both themes with no binding error; the last theme read back; the gallery tray item on dev instances only.
- PNGs for the builder: `gallery-fluent.png` and `gallery-gaming-dark.png` beside the App test binary.
- The NativeAOT engine publish (Contracts and Core changed): passed with 0 IL warnings.
- Smoke run of the real App on a test instance, seeded with Gaming dark: it starts in Gaming dark, switches live to Fluent on the engine's snapshot, and writes `fluent` back to the cache.

## Log

- 2026-11-04: built and verified on `feature/9.2-theme-infrastructure` (`ad57d54`).
- 2026-11-04: QA round 1 had 2 findings, both fixed. F1: Fluent's disabled TextBox and list items now dim. F2: the set-only `WindowFrame.CaptionColor` is deleted, and `HasMica` backs the new frame test. Also from QA's notes: turning Mica off now restores the window's composition background.
- 2026-11-04: QA round 2 passed at `ecc4508`. Folded in its non-blocking note: Fluent's disabled TabItem now dims too.
