---
title: 'Settings pages: General, Replay Buffer, Recordings, Hotkeys and Feedback Sounds'
type: 'feature'
ticket: '5'
created: '2026-11-04'
status: 'built'
baseline_revision: '995f9df6d87c55f1d93cff545e8071873f826d27'
route: 'full'
route_source: 'auto'
risk: 'low'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
followup_review_recommended: false
context: ['{project-root}/CLAUDE.md', '{project-root}/_bmad-output/initiative-replay-buffer/tickets/epic-modern-ui/ux-modern-ui/EXPERIENCE.md']
warnings: ['oversized']
deferred: []
---

<intent-contract>

## Intent

**Problem:** The five simpler settings views are plain stacks with hard-coded `Gray`/`DarkRed` text, and two of them declare local implicit `TextBox`/`ComboBox`/`Button` styles that replace Gaming dark's templates. EXPERIENCE.md wants a shared settings layout (section header + card, label column rows, help text, a danger info bar for the page error) used by every settings page, in both themes (epic R5, R6).

**Approach:** Add one theme-neutral dictionary, `Theme/SettingsLayout.xaml`, of reusable styles that take every colour, corner and type from token keys via `DynamicResource`, merged by one line in `ThemeService.Apply`. Restyle the five views onto it, keeping every binding, wording and behaviour; turn the shell's page message into the danger info bar. Story 9.6 reuses the same styles for its three pages.

## Boundaries & Constraints

**Always:** every colour, corner, type style from DESIGN.md token keys through `DynamicResource`; every existing binding, command, `x:Name`, event handler and text kept verbatim (EXPERIENCE.md Voice and Tone; the only new strings are "Appearance" and "Startup and Overlay"); views keep a root `ScrollViewer` (`SettingsPage.Embedded` strips it when Game Profiles embeds Replay Buffer); keyboard focus visible (never set `FocusVisualStyle` to null on controls); 0-warning build; existing App tests pass unchanged.

**Never:** touch Track Layout, Game Profiles, Library and Cleanup, Library, or their tests (9.4 and 9.6 own them); add a local implicit (key-less) control style in a view, which would replace the theme's template; uppercase strings in XAML; change view models, `SettingsSender` or engine messages; add a `StaticResource` reference from a view to an application-level resource (views are created before they join the tree).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Page loads | Each of the five pages selected in MainWindow, under Fluent and Gaming dark | View loads, shows its section header(s) in cards, no binding error, no missing resource | No error expected |
| Refused update | `General.StartWithWindows = true` and the engine throws `ErrorCodeException` | Page message shows as the danger info bar with today's text | Bar hidden while the message is null or empty, and on the Library page |
| Estimate over limit | `ReplayBuffer.EstimateWarning` non-empty | Caution info bar with the warning text | Collapsed when null |

</intent-contract>

## Code Map

- `src/ReplayBuffer.App/Theme/SharedTheme.xaml` -- read-only. Has `RbCardStyle` (Border), `RbInfoBarDangerStyle` / `RbInfoBarCautionStyle` (ContentControl), spacing doubles `RbSpacing*`, `RbCardPadding`. Reference them with `DynamicResource` from the new dictionary (a separately loaded dictionary can't `StaticResource` or `BasedOn` them).
- `src/ReplayBuffer.App/Theme/FluentTheme.xaml`, `GamingDarkTheme.xaml` -- read-only. Per-theme `RbSectionHeaderStyle` (ContentControl; Gaming uppercases + muted), `CaptionTextBlockStyle`, `BodyTextBlockStyle`. Gaming defines implicit styles for every control.
- `src/ReplayBuffer.App/Theme/ShellTheme.cs` -- pattern to copy: `ShellTheme.Place(merged, theme)` inserts at `Math.Max(0, merged.Count - 1)` so the theme dictionary stays last (`ThemeTests` asserts `merged[^1]` is the theme dictionary).
- `src/ReplayBuffer.App/Theme/ThemeService.cs` -- `Apply`: add exactly one line after `ShellTheme.Place(merged, theme);`, e.g. `SettingsLayout.Place(merged);`. 9.4 adds its own line beside it; touch nothing else here.
- `src/ReplayBuffer.App/MainWindow.xaml` -- only the `PageMessage` element (docked Bottom, hidden when `IsSettingsTabSelected` is false). Keep its name and dock; 9.4 edits the page header in the same file, so change no other line.
- `src/ReplayBuffer.App/Settings/{General,ReplayBuffer,Recordings}SettingsView.xaml`, `HotkeysView.xaml`, `FeedbackSoundsView.xaml` -- the five views. `HotkeysView.xaml.cs` handlers (`OnFocusWithinChanged`, `OnPreviewKeyDown`, `OnUnloaded`) stay wired on the root.
- `src/ReplayBuffer.App/Settings/*Page.cs` -- read-only; bound properties: General `StartWithWindows`, `OverlayDotChoices`/`OverlayDot`, `AppThemeChoices`/`AppTheme`; ReplayBuffer `IsProfile`, `IsGlobal`, `BufferWindowUsesGlobal`, `CanEditBufferWindow`, `BufferWindowText`, `IsMemory`, `IsDisk`, `RingPath`, `SaveCooldownText`, `EstimateText`, `EstimateWarning`, `VideoUsesGlobal`, `CanEditVideo`, `Codecs`/`SelectedCodec`, `Resolutions`/`SelectedResolution`, `FpsText`, `MaxFps`, `BitrateText`, `IsVbr`; Hotkeys `IsToggleMode`, `IsSeparateMode`, `Rows` (`IsShown`, `Label`, `BindingText`, `SetCommand`, `ClearCommand`, `StatusText`, `IsFailed`); Feedback `CapturedWarning`, `Devices`/`SelectedDevice`, `Sounds` (`Label`, `IsOn`, `PlayCommand`); Recordings `Monitors`/`SelectedMonitor`, `RefreshCommand`.
- `tests/ReplayBuffer.App.Tests/ShellTests.cs` -- pattern for an offscreen `MainWindow` + `ThemeService.ForWindow` + `Save(window, name)` PNG helper. `ThemeTests` already checks every `DynamicResource` key resolves in both themes and every page loads with no binding error.
- `tests/ReplayBuffer.App.Tests/MainViewModelTests.cs:116` -- how to make a refusal: `engine.Reply = (_, _) => throw new ErrorCodeException(ErrorCodes.SettingsWriteFailed, "Disk full.")`.
- Renders: `ux-modern-ui/renders/fluent-light-settings.png`, `gaming-settings.png` (General page: section header above a card; Theme row = label column + combo with help text under the combo; then the Start with Windows check box and "Show the Overlay's dot" label above its combo; danger bar below).

## Tasks & Acceptance

**Execution:**
- [x] `src/ReplayBuffer.App/Theme/SettingsLayout.xaml` + `SettingsLayout.cs` -- new `ResourceDictionary` class (like `ShellFluent`) with: `RbSettingsContentStyle` (StackPanel: `MaxWidth` 720, left-aligned, `Grid.IsSharedSizeScope` true); `RbSettingsSectionStyle` (HeaderedContentControl, not focusable: header in a ContentControl styled `{DynamicResource RbSectionHeaderStyle}`, 8 px gap, then a Border `{DynamicResource RbCardStyle}` holding the content; bottom margin 24 = `spacing.xl` between sections); `RbSettingsLabelStyle` (TextBlock: right margin 12, centred vertically, `text-primary`); `RbSettingsHelpTextStyle` (TextBlock: caption size, `text-secondary`, wrap, top margin 4); `RbSettingsStatusTextStyle` (TextBlock inline status: `text-secondary`, wrap) and `RbSettingsErrorTextStyle` (`danger`); `RbSettingsPageMessageStyle` (ContentControl showing its content inside `{DynamicResource RbInfoBarDangerStyle}`, collapsed when content is null or empty, `MaxWidth` 720, left-aligned). A `Place(IList<ResourceDictionary>)` static that inserts one instance before the last entry if absent, and an `EmptyToCollapsedConverter` (null/empty string → Collapsed) that views declare locally. -- the reusable layout 9.6 builds on.
- [x] `src/ReplayBuffer.App/Theme/ThemeService.cs` -- one line in `Apply` placing the dictionary.
- [x] `src/ReplayBuffer.App/MainWindow.xaml` -- `PageMessage` becomes a ContentControl with `RbSettingsPageMessageStyle`, content `SelectedPage.Message`, still hidden off the settings tab (e.g. a wrapper whose Visibility binds `IsSettingsTabSelected`, since a local value would beat the style's trigger).
- [x] `GeneralSettingsView.xaml` -- sections "Appearance" (Theme row: label column + combo, help text under the combo) then "Startup and Overlay" (check box, then "Show the Overlay's dot" label above its combo), as the renders.
- [x] `ReplayBufferSettingsView.xaml` -- sections "Replay Buffer" and "Output" as cards; drop the local implicit TextBox/ComboBox styles (use explicit row margins); rows on the shared label column; `EstimateWarning` as a caution info bar collapsed when empty; the Gray note and "up to {0}" as help/secondary text.
- [x] `RecordingsSettingsView.xaml` -- "Recordings" section card: monitor row (label, combo, Refresh) and help text.
- [x] `HotkeysView.xaml` -- "Hotkeys" section card: record-mode row, then the rows; drop the implicit Button style (explicit margins); row status in `text-secondary`, `danger` when `IsFailed`, via DynamicResource setters.
- [x] `FeedbackSoundsView.xaml` -- "Feedback Sounds" section card: `CapturedWarning` as `danger` inline text collapsed when empty, the device row, the sound rows (check box + Play button).
- [x] `tests/ReplayBuffer.App.Tests/SettingsLayoutTests.cs` -- (1) each of the five pages in MainWindow under both themes loads and shows its expected section headers (General: Appearance and Startup and Overlay); saves `settings-<page>-<fluent|gaming-dark>.png` beside the test binary; (2) a refused General update shows the danger info bar with the page's message, and it's collapsed after a successful update; (3) a source scan of the five views finds no literal colour (`Gray`, `DarkRed`, `#hex`) and no key-less `<Style TargetType=`.

**Acceptance Criteria:**
- Given the Main App under either theme, when the builder opens General, Replay Buffer, Recordings, Hotkeys or Feedback Sounds, then each shows its sections as a header above a card, labels in one column, help text muted, and no control in the default WPF look.
- Given a refused settings update, when the page reloads its values, then the reason shows in a danger info bar under the page, with today's text.
- Given an estimate above free RAM or disk, when the Replay Buffer page renders it, then the warning shows in a caution info bar.
- Given every existing settings view-model test, when the suite runs, then they pass unchanged.

## Implementation Notes

- `RbSettingsContentStyle` is applied to `theme:SettingsContent`, a StackPanel subclass in `SettingsLayout.cs` that measures to the full offered width up to its MaxWidth. A plain left-aligned StackPanel shrinks to its rows' desired widths (narrow text boxes), and a stretched one past its MaxWidth is centred by WPF.
- General now has a root `ScrollViewer` like the other views.
- Hotkeys' row status uses a view-local keyed style (`HotkeyStatus`) with DynamicResource setters, since an inline key-less style would fail the source scan.
- Setting labels inherit the font size, as the controls beside them do; giving them `BodyTextBlockFontSize` made them larger than the control text under both themes.

## Plan Change Log

## Review Triage Log

### 2026-11-04 — Review pass
- verdicts: 3 findings — high 0, medium 0, low 3, false 0, maybe-false 0
- findings:
  - `[low]` `[patch]` `EstimateText` used `RbSettingsLabelStyle`, a row-label style, on a full-width body line — dropped the style; the line sets `text-primary` directly.
  - `[low]` `[reject]` the section test checks `Header` values, not the rendered header ContentControl — lean tests; the header template is one element in `RbSettingsSectionStyle`, and the saved PNGs show it.
  - `[low]` `[reject]` no test hides the page bar on the Library page — that wrapper trigger is 9.3's existing `IsSettingsTabSelected` rule, moved unchanged; lean tests.

## Design Notes

The page message stays docked at the bottom of the content column (where 9.3 put it) rather than directly under the cards as in the render: the page content is a filling, scrolling area, so a bar under its last card would need each view to host it; docking keeps it in one place for all eight pages.

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings, 0 errors
- `dotnet test --project tests/ReplayBuffer.App.Tests -c Release --no-build` -- expected: all pass (the known order failure `ThemeTests.MicaIsBehindTheMainAppUnderFluentOnlyAndNeverBehindOtherWindows` after `SurfacesThemeTests` is a separate bugfix and may be noted)

**Manual checks (if no CLI):**
- Open the saved `settings-*.png` beside the test binary and compare with the renders.

## Auto Run Result

- **Summary:** the shared settings layout (`Theme/SettingsLayout.xaml` + `.cs`: content column, section header + card, label, help, status and error text, page-message danger bar, `SettingsContent`, `EmptyToCollapsedConverter`), merged by one line in `ThemeService.Apply`; General, Replay Buffer, Recordings, Hotkeys and Feedback Sounds restyled onto it; the shell's page message is the danger info bar.
- **Files:** `Theme/SettingsLayout.xaml`, `Theme/SettingsLayout.cs` (new layout); `Theme/ThemeService.cs` (one Place line); `MainWindow.xaml` (PageMessage only); the five `Settings/*View.xaml` (sections, tokens, no implicit control styles); `tests/.../SettingsLayoutTests.cs` (pages in both themes + PNGs, refused-update bar, source scan).
- **Review:** 1 low patched, 0 deferred, 2 low rejected (reasons in the triage log). Follow-up review: false (no high or medium patched).
- **Verification:** `dotnet build ReplayBuffer.slnx -c Release` 0 warnings; App tests 165 total, 147 passed, 18 explicit skipped, 0 failed (the known Mica order failure didn't occur).
- **Residual risks:** Game Profiles' Overrides embed the Replay Buffer view, so it now shows those cards inside 9.6's page (unreviewed visually until 9.6). The page bar docks at the bottom of the content column, not directly under the cards as in the render. HITL: the builder changes a setting on each page in both themes, sees the estimate and a Hotkey duplicate, and checks keyboard focus.
- 2026-11-04: labels take `BodyTextBlockFontSize` (DESIGN body 14) so they match Gaming's 14 px controls. In the offscreen PNGs Fluent's built-in combo and check box text renders at 12, so Fluent labels read larger; that's 9.2's control sizing, flagged for the builder's look.
