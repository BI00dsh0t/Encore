# Refactor phase 2 findings: App views and controls

Scope: the XAML, code-behind, custom controls and theme dictionaries of `src/Encore.App`. Written before any code changed, as the plan asks. Each finding names the file, the problem and the fix. The last section lists what is left for a later phase.

Measured on `develop` at `0264f65` (line counts include blank lines):

| File | Lines | Lines with several elements | Lines of 140 or more characters |
|---|---|---|---|
| `Theme/LibraryTheme.xaml` | 630 | 43 | 39 |
| `Library/ClipCreatorView.xaml` | 100 | 30 | 40 |
| `Theme/ShellFluent.xaml` | 148 | 11 | 10 |
| `Theme/ShellGamingDark.xaml` | 148 | 11 | 11 |
| `Theme/SharedTheme.xaml` | 130 | 11 | 2 |
| `Library/ClipExportDialog.xaml` | 31 | 8 | 12 |
| `Theme/GamingDarkTheme.xaml` | 1,141 | 5 | 23 |
| `MainWindow.xaml` | 110 | 5 | 6 |
| the other 22 XAML files | | 12 | 85 |

Code-behind and controls: `PlayerView.xaml.cs` 451, `LibraryView.xaml.cs` 372, `MainWindow.xaml.cs` 265, `TrackLayoutView.xaml.cs` 178, `ClipCreatorView.xaml.cs` 99, `WaveformView.cs` 401, `MonitorPicker.cs` 468, `FolderPicker.cs` 307.

## 0. Safety net

- **T1. `tests/Encore.App.Tests/ShellTests.cs`, `TheShellLoadsInBothThemesAndTheNavOpensEveryPage`.** It selected every nav item in the nav list, looked for the Settings label at index 1, read the pressed colour from the item's own background and expected uppercase nav text. Today the list holds only the Library, the settings pages are in the section list, the pressed colour is on the template's `Fill` border and nav text is sentence case in both themes. Fix: test today's layout. Done in `b85e7b4`.
- **T2. `tests/Encore.App.Tests/MainWindowTests.cs`, `CurrentSettingsSectionReselectionClosesDrillInWhileEscapeOnlyDismissesTheMenu`.** It raised `PreviewMouseLeftButtonUp`, which is a direct event, on the list item, so the section list's handler never ran; and it pressed Enter before the open list had highlighted its current section. The product code is right. Fix: raise the tunnelling `PreviewMouseUp`, and pump the dispatcher once after the list opens. Done in `b85e7b4`.
- **T3. The test host ends with "Foreground threads were left running, forcing process exit".** All tests pass, but the run's exit code is 1. It was there at the baseline too. Not fixed here (see "Left for a later phase").
- **T4. The tests write 72 screenshots** (`shell-fluent.png`, `library-gaming-dark.png`, …) beside the test binary. Phase 2 keeps a copy from before the XAML steps and compares the pixels after each step, so "nothing the user sees changes" is checked, not only read.

## 1. Names

- **N1. Every resource key starts with `Rb`** (137 keys, in 34 XAML files, 6 C# files and 3 test files; `DESIGN.md` 17 lines, `EXPERIENCE.md` 1). `Rb` stood for ReplayBuffer, the app's old name. Fix: rename the prefix to `Encore` everywhere, in one commit. Not renamed: the meter-probe salt `replaybuffer-meter-probe`, `LegacyData`, `RunKey`, the `RB_…` environment variables of the tests, and the old plan and story files.
- **N2. `Library/ClipCreatorView.xaml.cs`: handler names don't say they are handlers** (`KeyboardHelp`, `CommitTime`, `StartMinus`, `Export`, `ChooseType`), unlike every other view (`OnMoreClick`). Fix: rename to `On…Click` / `On…KeyDown`.
- **N3. `Library/WaveformView.cs`: two flags, `_seekFocus` and `_endHandle`, together say which of three handles the keyboard is on.** A reader has to work out that `_endHandle` is ignored while `_seekFocus` is true. Fix: one enum, `TimelineHandle { Playhead, In, Out }`.
- **N4. `Library/WaveformView.cs`: `X(…)` and `Time(…)`,** `dc`, `d`, `m`, `w`, `g` and similar one-letter names in the files this phase touches. Fix: whole words (`XOf`, `TimeAt`, `drawing`, `monitor`).

## 2. Copied XAML

- **C1. `Theme/ShellFluent.xaml` and `Theme/ShellGamingDark.xaml` are the same file twice.** Of 148 lines, 120 are identical. They differ in: the two nav paddings, the nav layer and content layer styles, the status line's weight and case, the section label's margin, brush and case, the nav item's text brushes (normal, hover, selected, pressed) and an explicit `FontWeight`, and two radii. The Settings button style is identical in both. Fix: a new `Theme/ShellShared.xaml` holds the nav item style, the section label template, the Settings button style and the status line style once; each theme's file keeps only its values (brushes, paddings, weights and the two layer styles), which the shared templates read through `DynamicResource` keys.
- **C2. Both nav item templates and the Settings button template hold a `Pill` border that nothing ever shows.** The trigger that showed it went when the selected fill replaced it. Fix: delete the three dead elements. The empty line left in the `IsSelected` trigger goes too.
- **C3. `Theme/GamingDarkTheme.xaml`: the `Button` and `ToggleButton` styles carry the same 14-line template.** Fix: one `ControlTemplate` for `ButtonBase`, used by both.
- **C4. `Theme/GamingDarkTheme.xaml` and `Theme/ShellGamingDark.xaml` each make their own `UppercaseConverter`** (`RbUppercase`, `RbShellUppercase`). They are separate dictionaries, and a `StaticResource` can't cross them. Fix: none needed once C1 leaves one use in the shell file; noted so nobody merges them and breaks the lookup.
- **C5. `Theme/LibraryTheme.xaml`: `RbClipJoinedButtonStyle` and `RbClipJoinedArrowButtonStyle` are the same template** with the rounded corners on opposite sides. Fix: one template that reads its corners from the style (`Border.CornerRadius`), and two styles that set only the corners.
- **C6. `Library/ClipCreatorView.xaml`: the In and Out groups are the same 8 lines twice,** and the minus, plus and arrow icons repeat their path data up to four times. Fix: name each icon's geometry once as a resource. The In and Out groups stay as two blocks: a shared template would need a new control for 8 lines.
- **C7. `Theme/FluentTheme.xaml` and `Theme/GamingDarkTheme.xaml` both define `RbTextStyleBase` and the three type styles.** They differ on purpose (font, weight, uppercase template), and each is short. Fix: none; listed so the review shows it was looked at.
- **C8. `Theme/SharedTheme.xaml`: `RbNavWidth` is defined and never used** (the window sets the width in code). Fix: delete it. `RbSpacingXS` to `RbSpacingXXL` are also unused, but `DESIGN.md` names them as the spacing tokens, so they stay.
- **C9. The settings views** (`RewindSettingsView`, `LibrarySettingsView`, `GeneralSettingsView`, `FeedbackSoundsView`, `HotkeysView`, `TrackLayoutView`) already share their card, header and row styles through `Theme/SettingsLayout.xaml`. What still repeats is each view's own `BooleanToVisibilityConverter` resource (11 views declare one, under four different keys). Fix: left; see "Left for a later phase".

## 3. XAML layout

- **X1. Several elements or setters share a line** in the files of the table above; `ClipCreatorView.xaml` also indents by one space and `LibraryTheme.xaml`'s first 95 lines pack whole templates into one line. Fix: one element per line, four-space indents, attributes wrapped one per line when a tag doesn't fit, lines under 140 characters. Done with a script that never reorders attributes (order sets the order properties are applied in) and never touches the inside of a `TextBlock` that holds `Run`s (white space there is text). The script's own check: the element tree, attributes and text of every file are equal before and after.
- **X2. Lines of 140 or more characters in the other XAML files** (85 lines in 22 files, mostly one long tag). Fix: the same script wraps the tag's attributes.
- **X3. `Library/ClipCreatorView.xaml` and `Library/ClipExportDialog.xaml` have no opening comment** saying what the view is. Fix: add the one line.

## 4. Logic in code-behind

- **L1. `MainWindow.xaml.cs` decides what Esc, Alt+Left, the mouse Back button and the Settings button do.** `OnKeyDown` picks between leaving full screen and closing a drill-in; `OnSettingsClick` decides between re-selecting and selecting. Fix: `MainViewModel.HandleEscape()`, `GoBack()` and `OpenSettings()`; the window only calls them.
- **L2. `MainWindow.xaml.cs`, `ApplyNavigationWidth`: the nav pane's width rule** (200, 160 below 640 px, 58 as a rail) is worked out in the window with a nested conditional. Fix: a small `NavPaneLayout` with one pure function, and a test.
- **L3. `MainWindow.xaml.cs`, `ApplyFullScreen` is 44 lines doing two jobs,** and the constructor holds two multi-line lambdas. Fix: `EnterFullScreen` and `LeaveFullScreen`, and named handlers.
- **L4. `MainViewModel.cs` holds a second class, `NavItem`.** Fix: its own file. (The view model itself is phase 3's.)
- **L5. `Library/LibraryView.xaml.cs`, `OnListPreviewKeyDown` (60 lines) maps 14 keys to page actions.** Fix: the navigation keys go through one small key-to-move table in the view (they need the viewport's row count); the other shortcuts move to `LibraryPage.HandleKey`, with a test.
- **L6. `Library/LibraryView.xaml.cs` decides when a click opens an item** (a double click with no modifier), **when the context menu may open** (keyboard: prepare the focused row; mouse: only on a row; never with nothing selected) **and which row gets focus when focus enters the list.** Fix: `LibraryPage.ClickRow`, `LibraryPage.OpenContextMenu` and `LibraryPage.FocusForEntry`; the view keeps the hit testing and the focus calls.
- **L7. `Library/LibraryView.xaml.cs` carries four general tree helpers** (`Within<T>`, `ParentOf`, `Descendant<T>`, `IsInList`). Fix: a `VisualTreeSearch` helper class. `OnMoreClick` and `TrackLayoutView.OpenMenu` are the same "open this button's menu under it" code: one `ButtonMenu.OpenBelow`.
- **L8. `Library/PlayerView.xaml.cs` does three jobs:** it wires the controls, it owns the Direct3D surface and presents frames, and it runs the "no picture after 5 seconds" timer with three fields of state. Its summary needs "and". Fix: `PlayerPicture` (the surface and the present), `NoPictureWatch` (the timer and its state), and the view wires them. `Fit`, a pure size rule, moves to `PictureFit`.
- **L9. `Library/PlayerView.xaml.cs`, `OnKeyDown` maps the player's keys to actions.** Fix: `PlayerPage.HandleKey`, with a test.
- **L10. `Library/PlayerView.xaml.cs`, `OpenSettingsMenu` builds the gear's menu in one 35-line method.** It must stay in the view (it makes controls), but as short helpers.
- **L11. `Library/ClipCreatorView.xaml.cs` has no blank lines, writes every type with its full namespace, builds the export-type menu inline with two long path strings, and holds a second class,** `ClipExportDestination`. Fix: usings and spacing as the other views; the menu in short helpers with the two icons as named geometry resources; `ClipExportDestination` in its own file.
- **L12. `Settings/TrackLayoutView.xaml.cs`: the picker's Enter and Escape rule is written twice** (search box and list), and `OnMenuButtonClick` and `OnPresetButtonClick` are the same handler. Fix: one `HandlePickerKey`, one click handler.
- **L13. `Library/WaveformView.cs` (401 lines) is a control that draws, hit-tests and decides.** `OnRender` is 65 lines with the ruler, lanes, veil, selection, handles and playhead in one body; `CreateWaveformDrawing` nests four deep; `OnKeyDown` nests conditionals inside a `switch`; the time-to-pixel mapping is inline in a dozen places. Fix: a `TimelineScale` value (pure, tested) for the mapping; `OnRender` split into one method per layer; the Tab rule as a pure `TimelineHandle` step; the file split into the control (`WaveformView.cs`), its drawing (`WaveformView.Drawing.cs`) and its pointer and key input (`WaveformView.Input.cs`).
- **L14. `Settings/MonitorPicker.cs` holds three types** (`MonitorPicker`, `ScreenRow`, `ScreenTile`), and `Show` builds labels, rows, the preview and the output line in one 43-line method with one-letter names. It is a view model, not a control, so no logic has to leave it. Fix: one type per file; `Show` split into named helpers.
- **L15. `Settings/FolderPicker.cs` mixes the picker's state with three Windows jobs:** the folder and file dialogs, the drive's free space and the "can we write here" probe. Fix: `FolderDialogs` and `FolderProbe`, two small static services; the picker keeps its state and its replaceable `Func` seams, so tests don't change.
- **L16. `Library/ClipExportDialog.xaml.cs` and `PromptDialog.xaml.cs`** are short and only wire; the export dialog has several statements per line. Fix: format only.

## 5. Left for a later phase

- **The stray foreground thread at the end of the App test run (T3).** Finding which test leaves it is test-infrastructure work, outside the views.
- **`DESIGN.md` still says Gaming dark's nav items are uppercase, 13 px, SemiBold.** The shell has drawn them in sentence case at 14 px since `68899fb`. One of the two is wrong; choosing is a product decision, not a refactor.
- **`Settings/GamePageView.xaml.cs` keeps the name box's edit buffer** (commit on Enter or on leaving, revert on Esc). Moving it to `GamePage` means a new draft property on a phase 3 view model; phase 3 already reworks the settings pages' drafts.
- **Each view declares its own `BooleanToVisibilityConverter` (C9).** One shared key would need the views to look it up across dictionaries with `DynamicResource`, which a `Binding.Converter` can't use; it needs a different mechanism (an `x:Static` instance), and touches every view.
- **`Theme/ControlGallery.xaml`** is a test-only page in the shipping project. Whether it ships is not a view refactor.
- **`MainViewModel`, `LibraryPage`, `PlayerPage`, `ClipCreatorPage`** get only the methods this phase moves into them. Their own clean-up (long constructors, many fields, `System.Windows` types, the `…ViewModel` rename) is phase 3.
- **`Overlay/OverlayWindow.cs`** (237 lines) is a window built in code; it was read and has no decision logic beyond placing itself. Not changed.

## 6. Result

Branch `claude/refactor-2-views-43e224`, cut from `develop` at `0264f65`. Not merged, not pushed.

**Checks.** Release build: 0 warnings, 0 errors after each step. `Encore.App.Tests`: 289 tests, 264 passed, 25 skipped (explicit Heavy), 0 failed; the baseline was 284 with 2 failed. The run still ends with exit code 1 from the stray foreground thread (T3). `Encore.Platform.Desktop` was not touched. After every XAML step the 72 test screenshots were compared pixel by pixel with copies taken before the phase: all match, except three that differ on every run of unchanged code (`game-page.png`, `tray-menu-fluent.png`, `library-cleanup-settings.png`), which were left out of the comparison. The reformat (X1, X2) was also checked by script: every XAML file has the same elements, attributes in the same order, comments and text as before.

**Done, by finding.**

- T1, T2: fixed first; the App suite was green before any refactor.
- N1: 137 keys renamed `Rb…` → `Encore…` (560 uses), with `DESIGN.md` and `EXPERIENCE.md`.
- N2, N3, N4: handlers renamed; `TimelineHandle` replaces the two flags; whole-word names in the files touched.
- C1, C2: `ShellShared.xaml` holds the nav item and Settings button styles once; each theme keeps only its paddings, layer styles and text colours. Also deleted, as unused: the `Pill` borders, the status state style, the Settings section label template and its trigger (the nav list holds no section item since Settings became a button), the narrow-pane tooltip trigger (it repeated the style's own tooltip), the shell's second uppercase converter, and `EncoreNavWidth`. The unsaved dot's radius is 3 in both themes; a test render showed 3, 4 and 6 draw the same 6 px circle. `NavListBox` stays: it hides section items from screen readers, and the screenshots showed it also changes the list's look.
- C3, C5, C6: one Gaming button template; the export arrow's style is built on the export button's; the clip creator's repeated icons are named geometries.
- X1, X2, X3: done by the script, with the exceptions below.
- L1–L4: `MainViewModel.HandleEscape`, `GoBack`, `OpenSettings`; `NavPaneLayout`; `EnterFullScreen` and `LeaveFullScreen`; `NavItem.cs`.
- L5–L7: `LibraryPage.ClickRow`, `TryPrepareContextMenu`, `FocusForEntry`, `HandleKey`; `VisualTreeSearch`; `ButtonMenu.OpenBelow`.
- L8–L10: `NoPictureWatch`, `PictureFit`, `PlayerPage.HandleKey`, `PlayerPage.DrawFrame`; the gear menu in short helpers.
- L11, L12, L16: the clip creator's code-behind and dialogs tidied; `ClipExportDestination.cs`; one `HandlePickerKey`.
- L13: `WaveformView.cs`, `WaveformView.Drawing.cs`, `WaveformView.Input.cs`, `TimelineScale`, `TimelineHandle`.
- L14, L15: `ScreenRow.cs`, `ScreenTile.cs`, `Show` in helpers; `FolderDialogs`, `FolderProbe`.
- `ShellTheme.cs` held four types: `NavPress.cs` and one code file per dictionary. `SettingsContent` left `SettingsLayout`'s file. The dictionaries' code files are named `.xaml.cs`.
- New happy-path tests: the shell's Esc, Back and Settings rules and the nav width; the Library list's click, menu, entry focus and keys; the player's keys; the timeline's scale and Tab order.

**Code-behind and controls, lines before → after** (physical lines; the after counts include the blank lines and one-statement-per-line layout the rules ask for):

| File | Before | After |
|---|---|---|
| `Library/PlayerView.xaml.cs` | 451 | 395 (plus `NoPictureWatch` 81, `PictureFit` 19) |
| `Library/LibraryView.xaml.cs` | 372 | 280 |
| `MainWindow.xaml.cs` | 265 | 283 |
| `Settings/TrackLayoutView.xaml.cs` | 178 | 178 |
| `Library/ClipCreatorView.xaml.cs` | 99 | 142 (plus `ClipExportDestination` 38) |
| `Library/WaveformView.cs` | 401 | 250 + 240 + 425 in three partial files |
| `Settings/MonitorPicker.cs` | 468 | 411 (plus `ScreenRow`, `ScreenTile`) |
| `Settings/FolderPicker.cs` | 307 | 241 (plus `FolderDialogs`, `FolderProbe`) |

`MainWindow.xaml.cs` grew because its one-line `if`s and two inline lambdas became braced blocks and named handlers; the decisions left it.

**Left as they are, with reasons** (in addition to section 5):

- Six XAML lines are over 140 characters because each holds one long piece of text (help text, an info bar, a caption). Breaking the attribute would change the text. `TrackLayoutView.xaml` line 174 is a binding nested 18 levels deep; shortening it means moving the track card's context menu into a resource, which changes how the menu is shared.
- `ClipCreatorView.xaml`'s In and Out groups stay two blocks (C6), and C7, C8's spacing tokens and C9 are unchanged, for the reasons given above.
- The keys that move the Library's focused row stay in `LibraryView`: they need the viewport's row count.
- `NavPress.GetTrack`, `SetTrack` and `GetIsPressed` look unused to a search, but are the accessors WPF's XAML needs for an attached property.
- The view models this phase added methods to (`LibraryPage`, `PlayerPage`, `MainViewModel`) and the pages touched only by the rename or a call site (`RewindSettingsPage`, `LibrarySettingsPage`, `ThemeService`, `OverlayWindow`) still have long lines, long methods and several types per file. They are phase 3's.
- The pixel check covers what the screenshots show. The gear menu and the export-type menu are built in code and were checked by reading and by their tests, not by pixels.
