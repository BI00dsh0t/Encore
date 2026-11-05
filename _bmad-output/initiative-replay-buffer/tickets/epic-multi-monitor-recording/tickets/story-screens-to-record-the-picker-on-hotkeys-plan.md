---
title: 'Screens to record: the picker on Hotkeys'
type: 'feature'
ticket: '4'
created: '2026-11-04'
status: done
baseline_revision: '2aef49f0f987746d59374bfaf9da0aa71b3fc20e'
route: 'full'
route_source: 'auto'
risk: 'medium'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/initiative-replay-buffer/tickets/epic-multi-monitor-recording/design-multi-monitor.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Hotkeys → Monitor Recording still offers 10.9's one-screen "Screen to record" combo box, which writes the old `monitorRecordingMonitor`. Epic 11 records several screens, chosen through `monitorRecordingScreens` (11.2).

**Approach:** Replace the combo box with design-multi-monitor.md's picker inside `MonitorPicker`, the Hotkeys page's component. It offers All screens or Chosen screens, a checkbox row per screen, a clickable layout preview, the output line from Core's `MonitorCanvasRule.Plan`, greyed "Not connected — skipped" rows, Refresh and live `monitors.changed` updates. Every choice sends one Global `monitorRecordingScreens` op through the page's settings.update sender.

## Boundaries & Constraints

**Always:** colours from theme tokens only, in both themes, with keyed styles; epic 10's glossary ("screens" in labels, "Monitor Recording" for the feature); at least one id stays chosen; the preview's tiles are tab stops toggled with Space, with UIA names matching the rows; the preview is hidden when any attached screen lacks X or Y.

**Never:** writing `monitorRecordingMonitor`; editing other pages, the shell or shared theme files (10.10 and 11.3 run in parallel); adding an App → Core project reference (AD-2's graph); engine or Contracts changes.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Legacy value | key null, old key a device name or null | Chosen screens with that screen ticked (else the primary) | none |
| All screens | radio picked | sends `{mode:all}`; every attached screen ticked and disabled | none |
| Tick a screen | Chosen, one ticked | sends `{mode:selected, monitors:[ids in list order]}`, id = `MonitorId` else `DeviceName` | none |
| Last tick | one chosen id left | untick refused, nothing sent, tooltip says why | none |
| Chosen screen unplugged | `monitors.changed` without it | row stays, greyed, "Not connected — skipped"; not in the preview | none |
| No positions | an engine without X/Y | preview hidden; output line still from `Plan` | none |
| Canvas over 4096 on H.264 | 1440p + 2160p, HEVC probed | "Recorded as one 6400 × 2160 video", noting HEVC | none |
| No HEVC/AV1 | same, neither probed | "4096 × 1382 … (scaled down)" with the HEVC hint | none |

</frozen-after-approval>

## Code Map

- `src/ReplayBuffer.App/Settings/MonitorPicker.cs` -- 10.9's component; rewritten as the picker. Keep `ShowState` (fetch per engine instance, `monitors.changed` by reference), `RefreshCommand`, the `_send` and `_message` callbacks.
- `src/ReplayBuffer.App/Settings/HotkeysPage.cs` -- owns `Monitor`; `OnRender` calls `Monitor.Render(document)`. It also passes `VideoOptions` from the state for the codec probe (`ShowMonitors(state)` already receives the snapshot).
- `src/ReplayBuffer.App/Settings/HotkeysView.xaml` -- the `ScreenRow` grid becomes the picker: radios, the preview (an ItemsControl on a Canvas of ToggleButtons), the checkbox rows, the output line and Refresh.
- `src/ReplayBuffer.Core/Monitors/MonitorCanvasRule.cs` -- `Plan(screens, outputCodec, canEncode)`; linked into the App as source (pure, depends on Contracts only).
- `src/ReplayBuffer.App/ReplayBuffer.App.csproj` -- add the linked `Compile` item.
- `Contracts`: `Keys.MonitorRecordingScreens`, `MonitorRecordingScreens.AllScreens` / `.Selected(ids)`, `DisplayMonitor` (`MonitorId`, `X`, `Y`, `RefreshHz`, `Hdr`), `StateSnapshot.VideoOptions.Codecs`, `Keys.Video` (Global codec).
- `tests/ReplayBuffer.App.Tests/MonitorPickerTests.cs` -- rewritten for the picker; `SettingsLayoutTests` already resolves every HotkeysView resource key in both themes.
- `tests/ReplayBuffer.App.Tests/RecordingsEndToEndTests.cs` -- Heavy (explicit); its combo-box steps move to the new API.

## Tasks & Acceptance

**Execution:**
- [ ] `src/ReplayBuffer.App/ReplayBuffer.App.csproj` -- link `MonitorCanvasRule.cs` -- one canvas rule for the engine and the picker, without a new reference edge
- [ ] `src/ReplayBuffer.App/Settings/MonitorPicker.cs` -- the mode, rows, tiles, output line, live updates and sends
- [ ] `src/ReplayBuffer.App/Settings/HotkeysPage.cs` -- pass the probe's codecs to the picker; update the doc comments
- [ ] `src/ReplayBuffer.App/Settings/HotkeysView.xaml` -- the picker's layout and keyed, token-only styles
- [ ] `tests/ReplayBuffer.App.Tests/MonitorPickerTests.cs` -- the ticket's verify, plus PNGs in both themes
- [ ] `tests/ReplayBuffer.App.Tests/RecordingsEndToEndTests.cs` -- compile against the new picker (not run: Heavy)

**Acceptance Criteria:**
- Given a fabricated list of three screens, when the builder picks All screens or ticks a set, then exactly one Global `monitorRecordingScreens` op is sent with that value, and `monitorRecordingMonitor` is never sent.
- Given the preview, when a tile is toggled, then the same row's tick follows, and the tile rectangles keep the screens' relative positions.
- Given the Hotkeys page under Fluent and Gaming dark, when it renders, then there are no binding errors or missing resources, and PNGs `hotkeys-screens-picker-fluent.png` and `-gaming-dark.png` are beside the test binary.

## Implementation Notes

- Implemented directly in the build session (the investigation context was already loaded), not by a subagent.
- `MonitorPicker` keeps row objects while the list of ids is unchanged, so a focused tile or check box keeps focus across a toggle.
- The help sentence reads "A Recording of your screens", from the picker mockup.
- `RecordingsEndToEndTests` (Heavy, not run) now picks All screens and checks `monitorRecordingScreens` across the restart.
- After review: nothing is called "Not connected" before the first `monitors.list`, and the output line reads "Looking for your screens…" until then.

## Plan Change Log

## Review Triage Log

Pass 1 (quick lens): 0 high, 2 medium, 3 low, 1 false, 1 rejected.

| # | Finding | Verdict | Route | Evidence / action |
|---|---|---|---|---|
| 1 | Linking `MonitorCanvasRule.cs` into the App puts Core code in the App assembly, against AD-2's graph | medium | intent_gap, kept under the unattended default | A logged design decision (Design Notes). Raised in the QA report for the builder or an AD-2 update; the alternative is an App → Core edge. |
| 2 | Before any `monitors.list`, chosen screens read "Unknown screen · Not connected" and the output line says the Recording won't start | medium | patch | Real: `_monitors` starts empty. Fixed with `_listed`; the line reads "Looking for your screens…" until a list arrives. |
| 3 | The test's resource-key check duplicates `SettingsLayoutTests.ViewResourceKeys` | low | patch | Real duplication under the lean-tests rule. Deleted. |
| 4 | No test drives a tile's `IsChecked` binding to the row | low | patch | Real gap. The render test now checks a tile and sees the row's check box tick. |
| 5 | The binding-error assertion only filters "Screen" or "Monitor" | low | patch | Now `Assert.Empty(errors.Messages)`; it passes. |
| 6 | Inline font sizes on the tile, not keyed styles | false | reject | The rule is colours from tokens with no key-less style. Font sizes aren't colours, and the view has no key-less style. |
| 7 | Tab order runs through the tiles before the check boxes | low | reject | Tab follows the visual order, preview then rows, and the design only asks that tiles be tab stops too. Changing it adds TabIndex plumbing for no clear gain. |
| 8 | Chosen screens with nothing to choose leaves the radios out of step | low | patch | Real edge case. The setter now raises both properties, so the radios return to All screens. |

## Design Notes

- **Linking the canvas rule (unattended decision):** the ticket and AD-25 say the picker shares Core's pure rule, but AD-2's graph has no App → Core edge. The App compiles `MonitorCanvasRule.cs` as a linked source file, so there is one rule and no new reference. App.Tests sees two `ReplayBuffer.Core.Monitors.MonitorCanvasRule` types (App's and, through the engine, Core's), so the tests never name it; they assert the text. For review: the alternative is an App → Core edge in AD-2.
- **The probe:** `canEncode` is the probed codec list; before the probe it assumes HEVC and AV1 can encode, as most PCs can.
- **Switching back to Chosen** restores the last chosen set from this session or the document, else ticks every attached screen.
- **Numbers** come from the `DISPLAYn` device name, else the position in the list. A missing screen keeps the name it was last seen with, else "Unknown screen".

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings
- `dotnet test --project tests/ReplayBuffer.App.Tests -c Release --no-build`, and the same for Core.Tests and Contracts.Tests -- expected: all pass

**Manual checks (if no CLI):**
- Open the two PNGs: the picker's preview, rows and output line read correctly in both themes.
