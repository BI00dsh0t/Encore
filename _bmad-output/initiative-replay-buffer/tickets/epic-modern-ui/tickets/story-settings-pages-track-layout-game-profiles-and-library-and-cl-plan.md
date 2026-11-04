---
title: 'Settings pages: Track Layout, Game Profiles, and Library and Cleanup'
type: 'feature'
ticket: '6'
created: '2026-11-04'
status: done
baseline_revision: 'ffb9c0ca36844851b0701aa15cee4c411e1f64a9'
route: 'full'
route_source: 'auto'
risk: 'medium'
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

**Problem:** Track Layout, Game Profiles, and Library and Cleanup are still plain stacks with hard-coded `Gray`/`DarkRed`/`DarkOrange` text, a default `ProgressBar` for the level meters, and (Library and Cleanup) key-less local `TextBox`/`CheckBox`/`Button` styles that replace Gaming dark's templates. EXPERIENCE.md wants them on story 5's shared settings layout in both themes (epic R5, R6).

**Approach:** Restyle the three views onto `Theme/SettingsLayout.xaml` (`RbSettingsContentStyle` + `theme:SettingsContent`, `RbSettingsSectionStyle`, `RbSettingsLabelStyle`, `RbSettingsHelpTextStyle`, `RbSettingsStatusTextStyle`, `RbSettingsErrorTextStyle`), add one shared `RbSettingsCautionTextStyle` there, and use `RbLevelMeterStyle` for the meters. Keep every binding, command, handler and string.

## Boundaries & Constraints

**Always:** every colour, corner and type from token keys via `DynamicResource`; every existing binding, command, `Click` handler, `x:Name` and text kept verbatim (only "Devices", "Tracks", "Games", "Add a game by hand", "Overrides for", "Output Folder", "Cleanup" move from SemiBold TextBlocks into section headers); each view keeps a root `ScrollViewer` (`SettingsPage.Embedded` strips it when Game Profiles embeds Track Layout and Replay Buffer); `IsEnabled="{Binding CanEdit}"` still disables the Track Layout editor when Use Global is on; keyboard focus visible; 0-warning build; existing App tests pass unchanged.

**Never:** change view models, `SettingsSender`, `Prompts` or engine messages; add a key-less `<Style TargetType=…>` in a view; add a literal colour; uppercase strings in XAML; change the device "unavailable" Note logic (8.6's known renumbered-device display gap stays); touch the five 9.5 views, `MainWindow.xaml` or `ThemeService.cs`; `StaticResource` an application-level resource from a view.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Track Layout loads | Two devices (one unavailable), two Tracks with levels 0.5 and ≥ 0.9, one Track with a Warning, under Fluent and Gaming dark | Sections "Devices" and "Tracks" as cards; Note in `danger`; meters use `RbLevelMeterStyle` (fill `success`, `caution` at the peak); ⚠ and warning text in `caution` | No binding error, no missing resource |
| Game Profiles with a selected profile | Two profiles, one Ignored, one selected | Sections "Games", "Add a game by hand", "Overrides for" as cards; the embedded Replay Buffer and Track Layout sections follow below, not nested inside a card | No error expected |
| Library and Cleanup | Default state; `CleanupBlockedText` null | Sections "Output Folder" and "Cleanup"; the blocked line collapsed when empty, in `danger` when set | A refused limit still shows the shell's danger bar |

</intent-contract>

## Code Map

- `src/ReplayBuffer.App/Theme/SettingsLayout.xaml` -- 9.5's shared styles. Add only `RbSettingsCautionTextStyle` (TextBlock, `SystemFillColorCautionBrush`, wrap), beside `RbSettingsErrorTextStyle`.
- `src/ReplayBuffer.App/Theme/SharedTheme.xaml` -- read-only. `RbLevelMeterStyle` (ProgressBar: height 8, `success` fill, `caution` when Value ≥ 0.9 × Maximum, disabled → `text-disabled`); `RbInfoBarCautionStyle`; `RbCardStyle`.
- `src/ReplayBuffer.App/Settings/ReplayBufferSettingsView.xaml`, `RecordingsSettingsView.xaml` -- read-only patterns: `xmlns:theme`, `theme:EmptyToCollapsedConverter x:Key="Collapsed"`, `theme:SettingsContent Style="{DynamicResource RbSettingsContentStyle}"`, `HeaderedContentControl Header=… Style="{DynamicResource RbSettingsSectionStyle}"`, label-column Grids with `SharedSizeGroup="Label"`, rows `Margin="0,4"`, CheckBoxes with `FontSize="{DynamicResource BodyTextBlockFontSize}"`.
- `src/ReplayBuffer.App/Settings/TrackLayoutView.xaml` -- bindings: `UsesGlobal`, `IsProfile`, `CanEdit`, `Devices`/`SelectedDevice` (item `Label`, `Note`, `IsMic`, `CanBeMic`), `RemoveDeviceCommand`, `AddDeviceCommand`, `Choices`/`SelectedChoice`, `Tracks`/`SelectedTrack` (item `Level`, `Name`, `Warning`, `HasWarning`, `MuteAffected`, `Checks` → `IsChecked`, `Device`), `AddTrackCommand`, `MoveUpCommand`, `MoveDownCommand`, `DeleteTrackCommand`, `SaveCommand`/`RevertCommand` with `IsEnabled="{Binding IsDirty}"`. Keyed `DeviceTemplate` and keyed `Row` button style may stay (keyed); drop `Padding="8,2"` so the theme's button padding applies.
- `src/ReplayBuffer.App/Settings/GameProfilesView.xaml` (+ `.xaml.cs` `Browse` handler) -- bindings: `Profiles` (item `Name`, `ProfileId`, `Ignored`, `RecordingsEnabled`), `AddCommand`, `AddPath`, `SelectedProfile`, `HasSelectedProfile`, `OverrideOutput.View`, `OverrideTrackLayout.View`.
- `src/ReplayBuffer.App/Settings/LibrarySettingsView.xaml` (+ `.xaml.cs` `Browse` handler) -- bindings: `UseDefaultFolderCommand`, `CanUseDefaultFolder`, `OutputFolderText`, `FolderSizeText`, `SizeLimitEnabled`/`SizeLimitText`, `AgeLimitEnabled`/`AgeLimitText`, `CleanupBlockedText`. Its four key-less styles go (explicit margins instead). The permanent-deletion confirmation is `LibrarySettingsPage.Confirm` → `Prompts.YesNo` (9.7's themed dialog): unchanged.
- `tests/ReplayBuffer.App.Tests/SettingsLayoutTests.cs` -- extend: `Views` list, `Offscreen`, `Descendants`, `Save` helpers. `ThemeTests` already checks every page loads with no binding error and every `DynamicResource` key resolves in both themes.
- `tests/ReplayBuffer.App.Tests/GameProfilesPageTests.cs:228` and `TrackLayoutPageTests.cs:507-526` -- how to fabricate a settings document (profiles; a `TrackLayout` under `Keys.TrackLayout`) and `AudioDeviceList` on `FakeEngineSession.State`. `TrackLayoutTrackItem.ShowPeak(float)` and its public `Warning` setter set levels and the warning directly.

## Tasks & Acceptance

**Execution:**
- [x] `src/ReplayBuffer.App/Theme/SettingsLayout.xaml` -- add `RbSettingsCautionTextStyle` -- the Track warning is `caution`, and no shared style has it.
- [x] `src/ReplayBuffer.App/Settings/TrackLayoutView.xaml` -- `SettingsContent` root inside the ScrollViewer; the Use Global check box above; inside the `CanEdit` panel a "Devices" section (list with Label, Note in `RbSettingsErrorTextStyle`, Mic check box; Remove; picker + Add) and a "Tracks" section (list rows: meter with `RbLevelMeterStyle` Width 80 bound `Level` Maximum 1, Name, ⚠ in caution with its ToolTip; Add Track/Up/Down/Delete; the selected-Track editor: "Name" label row + TextBox, Mic Mute check box, warning text in caution, "Devices on this Track" + checks); Save and Revert as a button row below the sections. `DeviceTemplate`'s Note in `danger`.
- [x] `src/ReplayBuffer.App/Settings/GameProfilesView.xaml` -- sections "Games" (each profile row: name TextBox, ProfileId in help style no-wrap with trimming and tooltip, Ignored and Recordings check boxes), "Add a game by hand" (path TextBox, Browse..., Add), "Overrides for" (the combo, then the note as help text, visible with the selection); then, outside any card and visible with the selection, the embedded Replay Buffer view, a "Track Layout" header (`RbSectionHeaderStyle` ContentControl) and the embedded Track Layout view.
- [x] `src/ReplayBuffer.App/Settings/LibrarySettingsView.xaml` -- drop the key-less styles; sections "Output Folder" (folder text, Browse…, Use default; size as help text) and "Cleanup" (label-column grid of the two check box + TextBox rows; the two notes as help text; `CleanupBlockedText` in `danger`, collapsed when empty).
- [x] `tests/ReplayBuffer.App.Tests/SettingsLayoutTests.cs` -- (1) a test that loads the three pages in MainWindow under both themes, Track Layout with fabricated devices (one unavailable), two Tracks, levels and a warning, Game Profiles with two profiles and one selected; asserts each loads with its expected section headers in cards, and the Track Layout meters use `RbLevelMeterStyle`; saves `settings-{track-layout,game-profiles,library-and-cleanup}-{fluent,gaming-dark}.png` beside the test binary; (2) the source scan covers all eight views (rename to `TheSettingsViewsHaveNoLiteralColourAndNoKeylessStyle`, and `DarkOrange` joins the colour pattern).

**Acceptance Criteria:**
- Given the Main App under either theme, when the builder opens Track Layout, Game Profiles or Library and Cleanup, then each shows its sections as a header above a card, labels in the shared column, help text muted, and no control in the default WPF look.
- Given Track Layout with an unavailable device, a double-counting Track and moving levels, when it renders, then the Note is `danger`, the ⚠ and warning are `caution`, and the meters are the themed level meters.
- Given a Game Profile selected, when its overrides show, then the embedded Replay Buffer and Track Layout sections follow the Overrides card and disable as before under Use Global.
- Given every existing settings view-model test, when the suite runs, then they pass unchanged.

## Implementation Notes

- Track Layout's keyed `Row` button style is gone, not kept: a keyed style with no `BasedOn` replaces Gaming dark's implicit Button style, so Remove, Add Track, Up, Down, Delete, Save and Revert rendered in the default WPF look. `BasedOn` would need a `StaticResource` to an application-level style, which Never forbids, so each button carries `Margin="0,0,6,0"` instead. The 9.6 page test now asserts no Button on the three pages sets a local Style.
- The Game Profiles note keeps its `HasSelectedProfile` visibility on its own, since it now sits in the "Overrides for" card while the embedded views follow in a separate panel with the same binding.

## Plan Change Log

- Code Map, `TrackLayoutView.xaml`: the keyed `Row` style is removed rather than kept (see Implementation Notes).

## Review Triage Log

### 2026-11-04 — Review pass
- verdicts: 2 findings — high 0, medium 0, low 2, false 0, maybe-false 0
- findings:
  - `[low]` `[reject]` On Game Profiles the embedded Track Layout's "Name" row has its own label scope, so it doesn't line up with the embedded Replay Buffer rows above it — real but cosmetic, and it is 9.5's existing embed behaviour (each embedded `SettingsContent` is its own `IsSharedSizeScope`); the fix means changing `SettingsPage.Embedded`, outside this restyle's bounds. Standalone Track Layout is aligned. "Devices on this Track" is a caption above its list, not a row.
  - `[low]` `[reject]` The new test doesn't assert the Note/⚠/warning brushes, the peak meter colour or the disabled embeds under Use Global — the epic's lean-tests decision makes the look the builder's check; the styles are one reference each, the PNGs show the danger Note, caution ⚠ and caution peak meter, and the `CanEdit` binding is unchanged.

## Design Notes

Embedded override views already carry their own section cards (9.5 made Replay Buffer's), so Game Profiles puts them after its "Overrides for" card rather than inside it; a card in a card would double the borders. The "Track Layout" heading above the embedded editor keeps its text as a section header.

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings, 0 errors
- `dotnet test --project tests/ReplayBuffer.App.Tests -c Release --no-build` -- expected: all pass

**Manual checks (if no CLI):**
- Open the six new `settings-*.png` beside the test binary and compare with the 9.5 renders.

## Auto Run Result

- **Summary:** Track Layout, Game Profiles, and Library and Cleanup restyled onto 9.5's shared settings layout (sections as header + card, label column, help/status/error text), plus one shared `RbSettingsCautionTextStyle`; meters use `RbLevelMeterStyle`. Bindings, commands, handlers and strings are unchanged.
- **Files:** `Theme/SettingsLayout.xaml` (caution text style); `Settings/TrackLayoutView.xaml` (Devices and Tracks sections, danger Note, caution warning, themed meters, no local button style); `Settings/GameProfilesView.xaml` (Games, Add a game by hand, Overrides for; embedded views after the card); `Settings/LibrarySettingsView.xaml` (Output Folder and Cleanup; key-less styles removed; blocked text in danger, collapsed when empty); `tests/.../SettingsLayoutTests.cs` (the three pages in both themes with fabricated devices, Tracks, levels and profiles, plus six PNGs; source scan over all eight views).
- **Review:** quick lens, 2 low findings, both rejected (reasons in the triage log); 0 patched, 0 deferred. Follow-up review: false.
- **Verification:** `dotnet build ReplayBuffer.slnx -c Release` 0 warnings, 0 errors; App tests 169 total, 151 passed, 18 explicit skipped, 0 failed. Renders checked by eye: Track Layout (Gaming dark) and Library and Cleanup (Fluent).
- **Residual risks:** the embedded Track Layout on Game Profiles has its own label column (as 9.5's Replay Buffer embed does). The PNGs capture only the top of each page. HITL: the builder edits a Track Layout with meters moving, renames and Ignores a profile, and sets a Cleanup limit through its confirmation, in both themes.
