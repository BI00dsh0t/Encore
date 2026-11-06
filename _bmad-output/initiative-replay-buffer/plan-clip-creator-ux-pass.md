---
title: 'Streamline clip creation and expandable navigation'
type: 'feature'
ticket: ''
created: '2026-11-05'
status: 'built'
baseline_revision: 'ca5e8e7d287b707982d3dcb9be341fbd3961d60d'
route: 'full'
route_source: 'auto'
risk: 'medium'
review: 'quick'
review_source: 'pinned'
lenses_ran: [quick]
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
- [x] `Library/ClipCreatorView.xaml(.cs)`: responsive picture/transport/compact timeline composition, symbol pan/zoom with names/tooltips, header split action, focus restoration and conditional status/progress.
- [x] `Library/ClipExportDialog.xaml(.cs)`, `Library/ClipCreatorPage.cs`: shared retained export configuration, name and type-specific summaries, track/mix eligibility, guarded Save and injectable chooser. Separate multiple MP3s use folder selection plus a new named directory; opening/dismissing never exports.
- [x] `Library/WaveformView.cs`: compact lanes near 24 px, synchronized ruler/handles, bounded scrolling and keyboard reachability; preserve accurate source-time mapping on resize/zoom.
- [x] `MainWindow.xaml(.cs)`, `MainViewModel.cs`: expanded/icon-rail state, stable width application, bottom Settings and secondary Settings navigation; preserve routes, drill-in lifecycle, shortcuts and full-screen restoration.
- [x] `Theme/ShellFluent.xaml`, `Theme/ShellGamingDark.xaml`, `Theme/LibraryTheme.xaml`: icons, label/rail states, selection, popup and compact editor tokens in both looks.
- [x] `tests/ReplayBuffer.App.Tests/ClipCreatorTests.cs`: update obsolete tall-lane assertions; cover split/options/chooser/preview parity, layout, focus, busy guards and sidebar state preservation using fake services. Write shell-specific checks in `MainWindowTests.cs`/`SurfacesThemeTests.cs` but defer execution.
- [x] `_bmad-output/initiative-replay-buffer/test-backlog.md`, `coordinator-handoff.md`, `automation-state.md`: record actual scoped verification, shared-shell gaps and integration.

**Acceptance Criteria:**
- Given either look at reference size, when editing two tracks, then the larger picture, compact waveform and primary actions are reachable without scrolling past export settings.
- Given current range/configuration, when type/options/preview/chooser are used, then choices persist, preview/export agree and only confirmed Save starts one operation.
- Given pending work, when navigation width changes, then source/range/configuration/viewport/work survive; Settings selection instead follows normal navigation lifecycle.
- Given keyboard, many tracks or a narrow window, when interacting, then lanes/actions remain reachable, selection timing stays correct and dismissal restores focus.

## Implementation Notes

- Shared retained page configuration drives the modal and Preview. An injected destination chooser is guarded separately from media work.
- Existing worker semantics retained: all separate MP3 exports (including one selected track) publish a new named directory; mixed MP3/MP4 use a file chooser. The popup describes the actual destination shape.
- Settings sections keep their existing ordered semantic routes in an in-content selector; the primary rail contains actual primary destinations and a separate bottom Settings action.
- Waveform height is 28 px ruler plus at most five 24 px lanes, with source-time mapping shared across scrolling and resize.
- Quick review completed: both findings patched and verified by follow-up inspection; integration follows. Shell/Settings test execution and visual/HITL acceptance remain deferred.

## Plan Change Log

## Review Triage Log

- **medium → patch — creator narrow toolbar:** confirmed the fixed horizontal tools exceed the content width at MainWindow's supported 480 px minimum (160 px expanded nav plus 48 px page padding). Fixed by wrapping individual existing actions; the new minimum-shell reachability regression passed in both looks.
- **medium → patch — Settings section reselection:** confirmed ComboBox selection of the current Games section does not change SelectedItem and bypasses ReselectNavItem, unlike the former navigation list. Fixed by restoring pointer/keyboard current-section reselection on the existing selector; a shell regression was written/compiled, with execution deferred under the user scope restriction.

## Verification

- `dotnet build ReplayBuffer.slnx -c Release`: zero warnings/errors on feature and merged develop.
- Run only non-Explicit `ClipCreatorTests`, `ClipMediaWorkerTests`, `SharedMediaProcessTests`, one project at a time, using `--filter-class` and `--explicit off`; require nonzero execution and honest counts. No full suites.
- Defer shell/Settings test classes, real-media/Heavy, publish, benchmark, soak, real app and HITL verification. Independent code review before no-fast-forward integration; record remaining visual/shell acceptance gaps.

### Implementation-worker verification (2026-11-05)

- Final feature Release build: 0 warnings/errors.
- Actual Library-hosted creator at a 960×900 client size, both looks/rail states: no scrolling with two 24 px waveform lanes and ≥390 px picture area. An initial 137 px overflow was fixed by consolidating rows and replacing the duplicate shell breadcrumb with creator Back.
- ClipCreatorTests: 10 passed, 0 failed, 0 skipped (initial focus regression fixed).
- ClipMediaWorkerTests: 1 passed, 0 failed, 2 Explicit skipped.
- SharedMediaProcessTests: attempted but not executed; automatic approval review rejected scope. Unchanged class retains prior QA evidence; coordinator directed no retry.
- Shell checks added/compiled but unrun; independent review and merged-develop build are the coordinator's remaining integration gates. Detailed commands and deferred acceptance are in the three checkpoint documents.

### Coordinator verification after review patches (2026-11-05)

- Release build: 0 warnings, 0 errors.
- ClipCreatorTests: 11 passed, 0 failed, 0 skipped (12 s). Includes the new actual 480×320 shell timeline-action reachability check in both looks, plus the reference client-area regression.
- No media/contracts/native changes; the implementation worker's 1 passing identity test and 2 Explicit skips remain the current Engine evidence. Unchanged quoting tests were not retried after automatic approval review rejected their scope.
- Matrix audit: workspace/export/save and creator width reflow have passing scoped coverage. Full pending-work navigation, standalone Settings/reselection/full-screen, high-scale and real chooser/media acceptance remain explicitly deferred; the user's test restriction takes precedence over the workflow's wider matrix execution requirement.
- Follow-up quick review confirmed both patches address their findings; no new directly caused defect found.
