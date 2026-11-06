---
title: 'Streamline clip creation and expandable navigation'
type: 'feature'
ticket: ''
created: '2026-11-05'
status: 'draft'
baseline_revision: 'ca5e8e7d287b707982d3dcb9be341fbd3961d60d'
route: 'full'
route_source: 'auto'
risk: 'medium'
review: ''
review_source: ''
lenses_ran: []
review_loop_iteration: 0
context:
  - '{project-root}/CLAUDE.md'
  - '{project-root}/_bmad-output/initiative-replay-buffer/feature-requests/clip-creator-ux-pass/clip-creator-ux-pass.md'
  - '{project-root}/_bmad-output/initiative-replay-buffer/feature-requests/clip-creator-ux-pass/experience.md'
  - '{project-root}/_bmad-output/initiative-replay-buffer/feature-requests/clip-creator-ux-pass/mockup.html'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Permanent export configuration and a tall waveform crowd out the video; navigation cannot collapse or provide separate bottom Settings access.

**Approach:** Implement the accepted UX spec/mockup: dominant picture, compact bounded waveforms, symbol tools, split video/audio export action and type-specific modal options; expandable global icon navigation with bottom Settings.

## Boundaries & Constraints

**Always:** Preserve media semantics, millisecond rounding, one-way progress binding, source/destination safety, isolation, preview invalidation, cancellation and Library return context. Both looks use theme tokens and established icon fonts. Popup edits persist for preview; sidebar starts expanded and retains state within the session. Use actual destinations in their existing order, with ordered Settings section navigation inside Settings. A width toggle never navigates, reloads source or cancels work.

**Never:** New codecs/presets, capture changes, imports, multicuts, effects, uploads, invented navigation destinations or changed Settings content. Do not touch installed/running apps, real data/settings, system settings or unowned processes. No push. Preserve separate Heavy/night/go restrictions and the current clip-creator-only test clearance.

**Scope decision:** The builder explicitly included the sidebar and bottom Settings in this UX pass; keep editor and shell changes together as requested.

## I/O & Edge-Case Matrix

| Scenario | Input/state | Observable behavior | Failure handling |
|---|---|---|---|
| Workspace | Two/many tracks; 960×900 or smaller | Large picture, compact lanes, reachable trim/preview/export | Bound lane scrolling; smaller windows may scroll, without overlap |
| Export | Choose type/open/close options | Relevant format/name/tracks/mix and range; opening does not encode or choose a path | Restore trigger focus; audio with no track cannot save |
| Save | Confirm/cancel chooser; repeat clicks | One export only on confirmation; preview matches retained options | Preserve source/range; show existing progress/cancel/error/result |
| Navigation | Toggle rail or open Settings | Toggle preserves editor state/work; bottom Settings performs normal navigation | Accessible focus, tooltips and selected indication; full-screen exit restores rail choice |

</frozen-after-approval>

## Code Map

- `src/ReplayBuffer.App/Library/ClipCreatorPage.cs`: existing Format/Tracks/Mix setters invalidate preview; reuse service/worker contracts unchanged.
- `Library/ClipCreatorView.xaml(.cs)`: current permanent form/direct save picker; extract modal and destination-selection seam.
- `Library/WaveformView.cs`: ruler/selection share source coordinates; replace 210 px/minimum-growing lanes with compact bounded presentation.
- `MainWindow.xaml(.cs)`: NavColumn width, resize and full-screen restoration currently reset navigation width.
- `MainViewModel.cs`: NavItems mixes Library/header/Settings pages; SelectedNavItem, NavigateBy, welcome/tray/game links must retain their semantic routes.
- `Theme/ShellFluent.xaml`, `ShellGamingDark.xaml`: label-only nav templates, selected indicators and unsaved dots; `NavListBox.cs` hides section headers from automation.
- `PromptDialog.xaml(.cs)`, `ShellWindow.cs`: owned themed/capture-excluded modal conventions.

## Tasks & Acceptance

Paths below are relative to `src/ReplayBuffer.App/` unless explicitly rooted.

**Execution:**
- [ ] `Library/ClipCreatorView.xaml(.cs)`: responsive picture/transport/compact timeline composition, symbol pan/zoom with names/tooltips, header split action, focus restoration and conditional status/progress.
- [ ] `Library/ClipExportDialog.xaml(.cs)`, `Library/ClipCreatorPage.cs`: shared retained export configuration, name and type-specific summaries, track/mix eligibility, guarded Save and injectable chooser. Separate multiple MP3s use folder selection plus a new named directory; opening/dismissing never exports.
- [ ] `Library/WaveformView.cs`: compact lanes near 24 px, synchronized ruler/handles, bounded scrolling and keyboard reachability; preserve accurate source-time mapping on resize/zoom.
- [ ] `MainWindow.xaml(.cs)`, `MainViewModel.cs`: expanded/icon-rail state, stable width application, bottom Settings and secondary Settings navigation; preserve routes, drill-in lifecycle, shortcuts and full-screen restoration.
- [ ] `Theme/ShellFluent.xaml`, `Theme/ShellGamingDark.xaml`, `Theme/LibraryTheme.xaml`: icons, label/rail states, selection, popup and compact editor tokens in both looks.
- [ ] `tests/ReplayBuffer.App.Tests/ClipCreatorTests.cs`: update obsolete tall-lane assertions; cover split/options/chooser/preview parity, layout, focus, busy guards and sidebar state preservation using fake services. Write shell-specific checks in `MainWindowTests.cs`/`SurfacesThemeTests.cs` but defer execution.
- [ ] `_bmad-output/initiative-replay-buffer/test-backlog.md`, `coordinator-handoff.md`, `automation-state.md`: record actual scoped verification, shared-shell gaps and integration.

**Acceptance Criteria:**
- Given either look at reference size, when editing two tracks, then the larger picture, compact waveform and primary actions are reachable without scrolling past export settings.
- Given current range/configuration, when type/options/preview/chooser are used, then choices persist, preview/export agree and only confirmed Save starts one operation.
- Given pending work, when navigation width changes, then source/range/configuration/viewport/work survive; Settings selection instead follows normal navigation lifecycle.
- Given keyboard, many tracks or a narrow window, when interacting, then lanes/actions remain reachable, selection timing stays correct and dismissal restores focus.

## Implementation Notes

## Plan Change Log

## Review Triage Log

## Verification

- `dotnet build ReplayBuffer.slnx -c Release`: zero warnings/errors on feature and merged develop.
- Run only non-Explicit `ClipCreatorTests`, `ClipMediaWorkerTests`, `SharedMediaProcessTests`, one project at a time, using `--filter-class` and `--explicit off`; require nonzero execution and honest counts. No full suites.
- Defer shell/Settings test classes, real-media/Heavy, publish, benchmark, soak, real app and HITL verification. Independent code review before no-fast-forward integration; record remaining visual/shell acceptance gaps.
