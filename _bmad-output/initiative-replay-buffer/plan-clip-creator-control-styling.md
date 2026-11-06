---
title: 'Match clip creator controls to the approved styling contract'
type: 'feature'
ticket: ''
created: '2026-11-05'
status: 'draft'
baseline_revision: 'c5a03a342576bd6ed72d28c012aacfbf77cd130e'
route: 'full'
route_source: 'auto'
risk: 'medium'
review: 'quick'
review_source: 'pinned'
lenses_ran: []
review_loop_iteration: 0
context:
  - '{project-root}/CLAUDE.md'
  - '{project-root}/_bmad-output/initiative-replay-buffer/feature-requests/clip-creator-ux-pass/clip-creator-ux-pass.md'
  - '{project-root}/_bmad-output/initiative-replay-buffer/feature-requests/clip-creator-ux-pass/experience.md'
  - '{project-root}/_bmad-output/initiative-replay-buffer/feature-requests/clip-creator-ux-pass/mockup.html'
  - '{project-root}/_bmad-output/initiative-replay-buffer/coordinator-handoff.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** The earlier UX implementation is merged, but existing WPF styles still differ from the approved mockup. The split Export action is visually disconnected; range nudges remain separate buttons; waveform handles, popup and shell controls do not meet the later R10/R11 styling contract.

**Approach:** Finish the approved UX pass by implementing the control styling contract across the creator, export surfaces and sidebar/Settings entry. Preserve working navigation, export and media behavior. This is one visual-fidelity follow-up to the already integrated UX pass, not a fresh redesign.

## Boundaries & Constraints

**Always:** Match the contract's rounded compact buttons/fields, thin icons, integrated groups, selection treatment and normal/medium typography. Use semantic theme colors in Fluent and Gaming dark. Keep icon rendering explicit against implicit text styles. Preserve source/destination safety, one-way Progress binding, millisecond display rounding, requested versus effective boundaries, preview invalidation, single-operation guards and capture independence. Sidebar width changes retain selection, viewport and work. Settings remains a separate bottom entry opening current content.

**Never:** Change codecs, worker/IPC behavior, Settings content, navigation order, installed/running apps, real data/settings, system settings or unowned processes. No push, Heavy/media/publish/bench/soak/old backlog/HITL runs. Only non-Explicit ClipCreatorTests are needed for changed App code; shell/Settings-specific test execution stays deferred. Do not retry the unchanged Windows quoting class previously rejected by approval review.

## I/O & Edge-Case Matrix

| Scenario | Input/state | Observable result | Failure handling |
|---|---|---|---|
| Styling | Both looks; ordinary/hover/focus/disabled controls | Contract geometry and hierarchy; joined Export, quiet fields/menu/popup, slim waveform handles | No invisible glyphs or implicit-style overrides |
| Range | Numeric/Enter/focus-loss edits, 1 ms nudges, drag/keyboard, invalid endpoints | One synchronized In/Out/duration row and waveform selection; requested/effective range visible | Invalid input retains valid selection and explains; active work blocks all edit paths |
| Layout | Reference/narrow viewport, many tracks, expanded/collapsed shell | Compact groups remain reachable, larger picture survives, Settings remains bottom anchored | Wrap whole range groups; retain bounded lane scrolling |
| Export | Open/type/dismiss/save, pending work | Existing type/track/mix/chooser behavior and retained choices; styled controls | No encoding on open/dismiss, duplicate jobs or hidden cancellation |

</frozen-after-approval>

## Code Map

- `src/ReplayBuffer.App/Theme/LibraryTheme.xaml`: current RbClipToolButtonStyle changes dimensions only, retaining mismatched templates. Introduce scoped button/input/group/menu/dialog styles and explicit thin icon resources; leave unrelated Library/player styles intact.
- `src/ReplayBuffer.App/Library/ClipCreatorView.xaml(.cs)`: existing functional split/type/options flow; replace separate field/buttons with grouped In/Out controls and trailing duration, style the joined export action and timeline tools; commit numeric edits on Enter without stealing text-editing arrows.
- `src/ReplayBuffer.App/Library/WaveformView.cs`: 24 px lanes and bounded scrolling already work. Replace 3 px selection lines plus rectangular top blocks with contract outline/fill/rounded handles/grips; preserve mapping and keyboard lane access. Audit drag edits during active work.
- `src/ReplayBuffer.App/Library/ClipExportDialog.xaml(.cs)`: existing owned ShellWindow modal, eligibility and chooser seam; style approximately 440-wide popup with quiet grouped content/actions, without changing retention/save behavior. `ClipCreatorView.ChooseType` constructs the menu and needs matching scoped item/container styles.
- `src/ReplayBuffer.App/Theme/ShellFluent.xaml`, `ShellGamingDark.xaml`, `MainWindow.xaml`: existing rail/bottom Settings works; align row heights, rounded soft selection, normal-case text and thin glyphs with R10, retaining actual destinations, focus/unsaved indicators and existing shell lifecycle.
- `tests/ReplayBuffer.App.Tests/ClipCreatorTests.cs`: existing 11 scoped fake-service tests cover layout, millisecond/preview behavior, popup/chooser and minimum-shell reachability; adapt structural assumptions to grouped fields and add lean direct coverage for new range interaction/styling failures.

## Tasks & Acceptance

**Execution:**
- [ ] Add explicit scoped visual resources and apply them to creator buttons, grouped time inputs, split export and type menu.
- [ ] Recompose the range row/duration and finish handle/selection rendering plus consistent validation/busy interaction.
- [ ] Apply matching export-popup and shell/Settings control treatment without changing routes or media operations.
- [ ] Update lean clip-creator regressions, build Release with zero warnings, and run only authorized non-Explicit ClipCreatorTests. Record unrun shell/media/visual checks honestly.
- [ ] Complete independent quick review, patch verified findings, commit with Conventional Commits, then integrate with serialized no-fast-forward merge and zero-warning Release build.

**Acceptance Criteria:**
- Given either look, when viewing and interacting with affected controls, then shapes, spacing, thin icons, fields and groups follow the R10 contract rather than the former templates.
- Given range edits by each supported input method, when values change or are rejected, then handles/fields/duration remain synchronized and valid, effective alignment remains visible, and preview refresh prevents stale exports.
- Given narrow/reference windows and either sidebar state, when the editor reflows, then whole field groups/actions remain reachable and width changes preserve creator state.
- Given the export dialog/type menu or Settings, when actions are used, then existing navigation, retained configuration, save/cancel and isolation behavior remain intact.

## Implementation Notes

- Investigation confirmed the baseline is clean develop, with earlier UX feature 8898f2c8e3899b06168b5559a8f98fca75394004 already integrated. Full route: coordinated visual changes exceed 100 lines and span multiple templates and interaction paths.

## Plan Change Log

## Review Triage Log

## Verification

- `dotnet build ReplayBuffer.slnx -c Release`: zero warnings/errors.
- `dotnet test --project tests/ReplayBuffer.App.Tests -c Release --no-build --filter-class "*ClipCreatorTests" --explicit off`: require actual nonzero execution and report counts.
- Visual comparisons only through authorized fake-service clip-creator windows; no real Dev App launch, real media or unrelated shell tests. Record any screenshot/visual acceptance not performed in the backlog rather than claim a visual pass from structural assertions.
