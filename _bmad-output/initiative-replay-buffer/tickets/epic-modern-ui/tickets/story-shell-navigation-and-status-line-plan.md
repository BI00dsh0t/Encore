# 9.3 Shell, navigation and status line — plan and log

**Problem:** The Main App is a TabControl (Library, Settings) with a Settings ListBox inside, and a plain status line above it. EXPERIENCE.md wants one flat nav pane, a themed title bar, a status header card with the record dot, and a 960×600 default window (R5, R6).

## Approach

- **View model:** `MainViewModel.NavItems` is Library, a non-selectable "Settings" label, then `Pages` in order. `SelectedNavItem` is derived from `IsSettingsTabSelected` and `SelectedPage` and sets them, so every existing path (the tray's included) selects its item. `CurrentTitle` and `CurrentView` feed the page header and the page. `NavigateBy` gives Ctrl+Tab and Ctrl+Shift+Tab over the nine pages, skipping the label and wrapping.
- **Status header:** `StatusLine.Dot` (Idle, Buffering, Recording) from the same snapshot as the text; the view is a card with the dot (`text-disabled`, `success`, `RbRecordBrush`), the state line, and the warnings (time `caption`/`text-secondary`, severity in `danger` or `caution`, Dismiss). The state text and warnings are unchanged.
- **Shell dictionaries:** `Theme/ShellFluent.xaml` and `Theme/ShellGamingDark.xaml` define the same keys (`RbNavItemStyle`, `RbStatusStateStyle`, `RbShellContentLayerStyle`, nav margins). `ShellTheme.Place` swaps the theme's one into the merged list; it is the single line added to `ThemeService.Apply`, so 9.7 can add its own beside it. Every colour is a token key.
- **Window:** 960×600 (minimum 480×320). The nav pane is 200 wide and 160 below 640 px, where items trim and show their title as a tooltip. Ctrl+Tab is on the bubbling `KeyDown`, so the Hotkeys page keeps it while capturing. The title bar is 9.2's (Mica under Fluent, DWM caption colour under Gaming dark); `MainWindow.UsesMica` stays.

## Known limits

- ListBoxItem has no pressed state; selection happens on mouse down, so the selected look serves as pressed.
- WPF has no way to hide the disabled section label from UI Automation; it is disabled and unfocusable, and arrow keys skip it.
- The Library view keeps its own "Library" heading and filters until 9.4 moves them into the page header; the settings views keep their headings until 9.5 and 9.6.
- The page error message keeps today's text, in `danger`; 9.5 turns it into the info bar.

## Verification

- `ShellTests`: every nav item opens its page and view; the label is never selected; the IsSettingsTabSelected path selects the page's item; Ctrl+Tab wraps; the shell loads in both themes and the nav opens every page (Gaming items uppercase). Saves `shell-fluent.png` and `shell-gaming-dark.png` beside the test binary.
- `StatusLineTests`: the dot's three states.
- `ThemeTests` (existing): every DynamicResource key in the XAML resolves in both themes; every page loads in both themes with no binding error.

## Log

- 2026-11-04: built; 0-warning Release build; App default tests 138 passed, 18 explicit skipped.
- HITL for the builder: walk every page in both themes, switch theme with the window open, and see the status header report a Clip saving and a warning.
