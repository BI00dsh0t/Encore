---
title: 'Modern UI end to end on the builder''s PC'
type: 'chore'
ticket: '9'
created: '2026-11-04'
status: done
baseline_revision: 'bdc5375'
route: 'hitl'
risk: 'low'
context: ['{project-root}/CLAUDE.md']
---

# 9.9 Modern UI end to end on the builder's PC

The epic's closing check. The unattended part drives the real engine and the App's real windows through theme switches and a restart, on a throwaway test instance, with no capture. What only a person can check, or what needs a real game, is the ordered list below.

**Against what.** The ticket says "against the installed build". The coordinator's hard limit for this run: never install over, launch or touch the installed App at `%LOCALAPPDATA%\Programs\ReplayBuffer\`, and don't run `tools/install.ps1`. So the automated suite runs against this worktree's built binaries, on a Guid-named test instance in temp folders. Installing the new version is step 1 of the builder's checklist.

## What was added

- `tests/ReplayBuffer.App.Tests/ModernUiEndToEndTests.cs` (Heavy, Explicit, CPU only, not a GPU test). Following 7.11's `LibraryEndToEndTests`, it hosts the whole engine (`EngineHost.Start`) in-process on `ReplayBuffer-Test-theme-<guid>`, with roaming and local folders under a temp folder and `ReplayBuffer = false`. It uses the App's own `EngineConnection` and the real `MainViewModel` and `MainWindow`, the tray menu from `ShellApp.CreateTrayMenu`, the `ControlGallery`, the `OverlayWindow` and the `PromptDialog` behind the Library's delete. Each window gets a `ThemeService` that follows the engine's committed `appTheme`, as `ShellApp` does. The theme starts from `ThemeService.ReadLast` of the App's own theme file before any window shows, and the Main App's service writes the file back. The test checks, in order:
  - **opens in Fluent (R1):** a fresh instance's General page shows Fluent, and every surface resolves Fluent;
  - **switch with every page open (R1, R5):** for each of the 9 nav items (Library and the eight settings pages), the item is selected, the tray menu is open, and the theme flips through the General page's `AppTheme` setter, a real `settings.update`. It then waits until every surface's `ThemeService` has applied the engine's `settings.changed`, and checks each surface: the Main App, the open tray menu (with the Gaming flyout fill), the Control gallery, the Overlay and the page's own view. Each must show the new theme's `SolidBackgroundFillColorBaseBrush` and resolve every `DynamicResource` key in the App's XAML. The same window objects stay shown, with the same nav item and the same page view, still loaded;
  - **switch with the delete confirmation open (R5):** a structurally complete 24-byte MP4 (`ftyp` + empty `moov`) sits in a temp Output Folder, chosen through the Library and Cleanup page. Delete on its row raises the themed dialog. The theme switches while the dialog is open, and the dialog, title "Delete", restyles in place while still shown. Its No answer keeps the file;
  - **no restart:** the connection came up once and stayed `Connected` for the whole run; the App's theme file holds Gaming dark;
  - **restart keeps the theme (R1):** everything is closed and the engine shut down. A new engine and App start on the same instance. `ReadLast` gives Gaming dark before the engine answers, and the snapshot's `appTheme`, the General page and every surface are all Gaming dark;
  - **no binding errors** across the run, apart from Fluent's known container-alignment trace (as in `ThemeTests`) and the Library thumbnail trace in Findings, whose binding the test asserts is `Active`;
  - **no capture:** the engine log has no `Detected`, `Capturing` or `Recording started` line.
- `ShellApp.ThemeFileFor(Instance)`: the App's theme-file path, now an internal static so the test uses the App's own path. No behaviour change.

Before the first run, the code was checked: the test instance is Guid-named and `IsTest` is asserted. The data folders are temp. The Replay Buffer is off and Recordings are off by default. Nothing goes to the Recycle Bin (the delete is answered No). No `StartupRegistration` or Run key is used. Nothing is launched (no `AppLauncher` start, and no shell action besides the Library's shell thumbnail request on the temp file).

## Unattended results

Run on 2026-11-04 in this worktree, Release, no GPU work, against the built binaries (not the installed App).

| What | Tests | Result |
|------|-------|--------|
| Opens in each theme, switch with every page, the tray menu and a dialog open, restyle with no restart and no missing resource, theme kept after an App and engine restart | `ModernUiEndToEndTests` (Heavy, Explicit, new, CPU only) | pass, 3 runs in a row (about 11 s each), after fixes to the test only (see Findings) |
| App default suite | `ReplayBuffer.App.Tests` | 151 passed, 20 skipped (explicit), 0 failed |
| Build | `dotnet build ReplayBuffer.slnx -c Release` | 0 warnings, 0 errors |

Totals: 152 passed, 0 failed; 0 for the GPU runner (no new GPU test).

Not run, with the reason:
- **Against the installed build:** the coordinator's hard limit. The built binaries were used instead; step 1 below installs the new version, and the builder's whole session runs on it.
- **Windows light or dark mode and accent changes under Fluent:** this would mean changing Windows settings, which the standing decisions forbid. It's step 6 below.
- **The real tray popup (H.NotifyIcon's host) and the App-wide `ThemeService.ForApplication`:** a test process has one `Application` per process, shared with the other tests, so the test scopes a `ThemeService` to each window, as `ThemeTests` and `SurfacesThemeTests` do. QA 9.2 switched themes 40 times in the real App through UI Automation, and steps 2 to 5 below use the real tray.
- **An Overlay message over a game:** needs capture. The test checks the Overlay window's resources follow each switch, and `SurfacesThemeTests` checks its card and glyph colours. Step 3 below covers a real game.
- Other test projects: the diff adds one App test file and makes one App path accessor internal static. There's no engine change, so no NativeAOT publish.

## Builder's hand checks for epic 9 (in order)

One sitting on the **installed** App, with a game handy. Each step names the Human Check Queue rows it closes; each row has the detail. Do steps 2 to 5 once in **Fluent** and once in **Gaming dark** (Settings > General > Theme), then steps 6 and 7.

1. **Install the new version.** Quit ReplayBuffer from the tray, then run:
   - `pwsh tools/publish.ps1`
   - `pwsh tools/install.ps1`

   The App starts from `%LOCALAPPDATA%\Programs\ReplayBuffer`. Its title has no instance suffix.
2. **Walk every page (9.3, 9.4, 9.5, 9.6).** Open the Main App and visit every nav item with the mouse, with the arrow keys and with Ctrl+Tab. In Fluent, Mica shows behind the Main App only. Check each page's look against its row: the Library's cards, filters, star and right-click actions; the settings pages' section cards, labels and muted help text; Track Layout's live meters; Game Profiles' Use Global; and Library and Cleanup's dry-run confirmation (answer No). Tab through each page: the focus is always visible. On General, switch the theme with the window open: it restyles in place and General stays selected. Switch back.
3. **Save a Clip in a game (9.7, 9.3).** Play a detected game and press the Save Hotkey. The Overlay's message card is in the theme's card, and the dot and saving icon keep their usual colours. The status header reports the save. Unplug the mic: its warning shows with its time, severity and Dismiss.
4. **The tray menu (9.7).** Right-click the tray icon and look at the menu in the theme. Optional: with the menu open, change the theme from another window (the Dev App, or `settings.update`) and watch the menu restyle.
5. **A delete confirmation (9.7, 9.4).** Delete a scratch Clip from the Library. In the dialog, Yes is the accent default. Esc answers No, and nothing is deleted. Delete again and answer Yes; restore the Clip from the Recycle Bin if you want it.
6. **Under Fluent, the Windows mode and accent (9.2).** With Theme set to Fluent and the Main App open, switch Windows between light and dark mode (Settings > Personalization > Colors), then pick a new accent colour. The App follows each change without a restart. Then set Gaming dark: it keeps its palette, 4 px corners and uppercase navigation in either Windows mode.
7. **The Control gallery (9.2), on the Dev App.** The installed App has no Control gallery item, so run the Dev build (`src/ReplayBuffer.App/bin/Release/net10.0-windows10.0.26100.0/ReplayBuffer.App.exe`) and open the tray's **Control gallery**. In both themes, hover, press and Tab through it: hover, pressed and focus show on every control, and disabled ones are dimmed. Quit the Dev App afterwards.
8. **Pick the theme to keep.** Set it on General, Quit from the tray and start the App again (or restart the PC): it opens in that theme. Say which one you kept, so the epic Notes can record it.

The automated run covers each Done-when line's logic against the real engine and windows. The list above covers what it can't: the installed build, the real tray popup, a game with the Overlay, Windows' own mode and accent, Mica, hover and focus, and how each surface looks.

## Findings

- None of the epic-9 checks failed.
- **Finding (not fixed: transient, and it changes no behaviour):** as each Library row's container is prepared, its thumbnail `ImageBrush` (`LibraryView.xaml`, `Border.Background`) traces "Cannot find governing FrameworkElement … Path=Thumbnail" once per row. This happens at the row's first render, not on a theme switch. Once the row is in the window the binding is `Active` (the test asserts it), so nothing visible is lost. `LibraryThemeTests` doesn't see it, because its view isn't in a shown window. A later sweep could bind through the Border (`Tag`, or an `Image` with `UniformToFill` and a clip) to silence it.
- Test-only fixes on the way to the first pass:
  - The Library takes only a structurally complete MP4 (`Mp4File.IsComplete`), so a zero-filled file wasn't listed. It's now a 24-byte `ftyp` + `moov`.
  - Placed on an element inside the Main App, the tray menu closed when Gaming dark was applied with the Replay Buffer page open. In the App the menu's host is H.NotifyIcon's, outside the Main App, so the test now gives it its own host window, and the menu then stays open through every switch.
  - A modal dialog takes activation and so closes any open menu, as in the real App; the menu is reopened after the dialog.

## Log

- 2026-11-04: branch `feature/9.9-modern-ui-end-to-end` cut from `develop` at `bdc5375` (9.1 to 9.8 merged). Added `ModernUiEndToEndTests` and `ShellApp.ThemeFileFor`. The test passed 3 runs in a row with no capture, the App default suite passed (151), and the build had 0 warnings. Pointed the six epic-9 Human Check Queue rows (9.2 to 9.7) at the checklist.
