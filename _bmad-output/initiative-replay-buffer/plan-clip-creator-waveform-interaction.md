---
title: 'Make clip seeking, waveforms and player resizing fluid'
type: 'feature'
ticket: ''
created: '2026-11-05'
status: 'built'
baseline_revision: '629e93dfcdae57b2803c21f90ab9505e37057c33'
route: 'full'
route_source: 'auto'
risk: 'medium'
review: 'quick'
review_source: 'pinned'
lenses_ran: [quick]
review_loop_iteration: 0
context:
  - '{project-root}/CLAUDE.md'
  - '{project-root}/_bmad-output/initiative-replay-buffer/coordinator-handoff.md'
  - '{project-root}/_bmad-output/initiative-replay-buffer/feature-requests/clip-creator-ux-pass/experience.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** The waveform has no separate draggable seek handle, repeated viewport decoding clears it and displays loading messages, and a fixed 480 px player wastes space when the window grows.

**Approach:** Add a distinct waveform playhead for click/drag seeking without trimming. Prepare every source track's complete waveform once into an owned, disk-backed peak cache; use it for local zoom/pan/track changes. Remove routine loading/checking text. Let the aspect-preserving player consume available window height while compact editing controls remain reachable.

## Boundaries & Constraints

**Always:** Preserve the styling contract, exact requested/effective export ranges, preview/export audio parity, named tracks, source identity/delete sharing, cancellation and capture independence. Whole-source waveform preparation streams through the isolated worker with bounded RAM and includes delayed audio/silence on the common source timeline. Store peaks at 1 ms resolution plus coarser reduction levels; do not pass full-source arrays through JSON. Once prepared, zoom/pan/trim/format/track changes must not launch another waveform decode or clear displayed peaks. Initial uncached preparation still takes time; suppress routine text, never hide actionable failures or export progress/cancellation.

**Never:** Trim when scrubbing, enable unsupported creator true-player fullscreen, change codecs/live capture, load full decoded audio into RAM, promise zero first-open work, or erase errors to conceal failure. No real/running app, user Library/settings, system settings or unowned process changes. No push. Only authorized non-Explicit ClipCreatorTests execute; real-media/Heavy, shared-shell tests, publish, bench, soak and old backlog remain held.

## I/O & Edge-Case Matrix

| Scenario | Input/state | Observable result | Failure handling |
|---|---|---|---|
| Seek | Drag distinct ruler playhead or click waveform; source/rendered preview | Position changes continuously, range unchanged; tick cannot fight drag | Lost capture ends scrub; seeking outside rendered selection returns to source playback |
| Waveform | Open, then zoom/pan/trim/change format or included tracks | One complete preparation for all tracks; bounded local viewport data with true millisecond detail, no routine loading text | Generation checks reject stale reads; never cancel preparation for ordinary refresh |
| Lifecycle | Navigate/hide/dispose, source change, cache write/read failure | Only owned work/cache affected; source remains intact | Cancel worker before deleting its scratch, reject incomplete/corrupt cache, show actionable errors |
| Resize | Reference, small, maximized window; either rail state/look | Picture grows with spare height; aspect ratio retained; timeline/range/export reachable | Minimum editor extent can scroll in short windows; no infinite-measure star-row layout |

</frozen-after-approval>

## Code Map

- `src/ReplayBuffer.App/Library/ClipCreatorPage.cs`: Changed/SetViewport clear Waveforms; RefreshAsync couples range probes with decode and WorkAsync cancellation. Separate one-time preparation, effective-range refresh and bounded cached viewport queries. Keep previous peaks until replacement; reject stale completion. Remove Opening/Checking/routine preview status messages while keeping failures/results.
- `Library/WaveformView.cs`: existing X/Time mapping, trim hit targets and single-click Seek; add separate ruler seek-thumb/drag mode, lost-capture cleanup and independent keyboard seek access. Suppress player Tick position overwrite during scrubbing; preserve trim keyboard behavior.
- `Library/ClipCreatorView.xaml(.cs)`: fixed PlayerView Height inside vertically unconstrained StackPanel/ScrollViewer. Give the content a finite viewport height and grow the picture with surplus height, retaining a scrollable minimum for short windows and existing scoped player styles.
- `Library/ClipMediaService.cs`, new `Library/ClipWaveformCache.cs`: worker serialization and 4 MiB response cap; prepare into a unique creator-owned temporary folder, then query versioned peak files off the UI/render thread. Explicitly order cancellation/worker completion/cache disposal.
- `src/ReplayBuffer.Engine/ClipMediaWorker.cs`: existing WaveformAsync clamps to 4096 bins per request. Add prepareWaveforms to stream complete tracks through AudioTrim(0..Duration, Origin), aggregate 48 stereo frames per 1 ms peak, and write coarse max-reduction levels with constant buffers. Return small descriptors only after validation/completion; leave existing export path intact.
- `src/ReplayBuffer.Contracts/Messages/ClipMedia.cs`: additive private-worker cache descriptors/source-generated JSON, no live IPC catalog changes. Shared binary-format helpers may be linked pure source as the existing MonitorCanvasRule precedent permits; add no App-to-Engine/Core assembly edge.
- Architecture `architecture-replay-buffer.md` AD-28: record disk-backed full-source preparation and bounded viewport reads before changing the protocol/resource rule.
- `tests/ReplayBuffer.App.Tests/ClipCreatorTests.cs`: replace repeated viewport-decode expectations with single preparation plus cached reads; keep precision, previews, cancellation and both-look checks. Adapt structural layout casts and add maximized-size growth and independent seek checks.

## Tasks & Acceptance

**Execution:**
- [x] Amend AD-28; implement additive cache descriptors, versioned peak format, streaming worker preparation and owned cleanup.
- [x] Integrate one-time preparation and bounded background viewport reads; decouple range probes, preserve peaks and remove routine loading text.
- [x] Implement independent scrub handle/seek lifecycle and source/selection-preview mapping.
- [x] Replace fixed-height composition with finite responsive layout and small-window fallback.
- [x] Build Release at zero warnings; run only non-Explicit ClipCreatorTests with synthetic cache/fake media fixtures; record real-media checks as deferred. Complete quick review, patches, commit/no-ff integration and post-merge build.

**Acceptance Criteria:**
- Given a source or rendered preview, when the playhead is dragged, then playback seeks while trim values stay unchanged and source-time position remains coherent after release.
- Given prepared waveforms, when any editing/viewport/audio choice changes, then no extra waveform decode occurs, peaks remain visible and zoom reveals cached millisecond detail.
- Given cancellation/failure/source disappearance, when work ends, then no partial cache is accepted, owned files are cleaned after workers stop, and errors stay visible.
- Given either look/rail state, when window height increases, then the video picture area grows; at minimum size, controls remain scroll-reachable without overlap.

## Implementation Notes

Builder authorized implementation of this plan in chat. Keep the frozen intent and captured baseline unchanged.

Read-only recon confirmed repeated worker decode, startup-cancellation coupling, Tick scrub races and infinite-measure layout hazards. Full route: coordinated cache/protocol, state and layout work exceeds 100 lines.

- Prepared all tracks using 1 ms binary base peaks plus 16:1 maximum-reduction levels. App/Engine link one pure format helper without a new assembly edge. Responses contain source-bound descriptors; viewport reads run off-thread with cancellation and generation checks and retain previous peaks until replacement.
- Added independent ruler thumb/click-drag seeking, P plus Left/Right for keyboard seeking, lost-capture release and Tick suppression while scrubbing. Outside-selection seeks detach rendered preview and return to the source player without altering trim values.
- ClipCreatorPanel measures controls first and gives the picture the finite viewport remainder; small windows scroll a minimum editor. Removed routine opening/checking/waveform text, retaining failure messages and export progress/cancel.
- Completed preparation survives hide/resume; incomplete preparation is cancelled, cleaned only after worker completion, and retried on resume without duplicating tracks or resetting selection. Resolved validation feedback no longer erases unresolved preparation/read failures.

## Plan Change Log

## Review Triage Log

- Quick review: medium → patch — waveform preparation/read failures can be overwritten by range feedback and then erased on correction, leaving a blank cache without explanation. Preserve the underlying actionable failure separately from transient range feedback; verify invalid-then-corrected edits restore it.
- Coordinator audit: low → patch — hide cancels worker/range work but not the local viewport read, and RefreshAsync can start another read after cancellation. Cancel the viewport token and gate reads/completion on the suspended state.
- Follow-up quick inspection confirmed persistent-error restoration and hide cancellation patches; no remaining directly caused defect found.
## Verification

- `dotnet build ReplayBuffer.slnx -c Release`: 0 warnings/errors.
- `dotnet test --project tests/ReplayBuffer.App.Tests -c Release --no-build --filter-class "*ClipCreatorTests" --explicit off`: require nonzero executed count, passing cache/seek/layout/precision/lifecycle coverage and honest results. Tests use temporary synthetic peak files, fake services and owned windows only.
- Write/defer real-media prepared-cache checks for delayed/common-origin/gapped tracks, long sources, cancellation, memory/disk behavior and published worker JSON. No unapproved media tools, publishes or Engine/shared-shell suites.

### Final scoped verification

- Feature commit `c83ccecf43219c21adf53f97eda7939716043e2e`, integrated with no-ff merge `53872864d6fbffef9930cc227b290e98d6b0983a` into develop. Post-merge `dotnet build ReplayBuffer.slnx -c Release --no-restore -v quiet` succeeded with **0 warnings, 0 errors**. No push or Dev App restart.

- Coordinator `dotnet build ReplayBuffer.slnx -c Release` after review patches: **0 warnings, 0 errors**.
- Coordinator class command above after patches: **14 discovered/executed, 14 passed, 0 failed, 0 skipped**, 17.781 s. Worker earlier whole-class runs also passed; the patch-specific run selected 2 relevant tests and passed. No full suites or Engine/native/Heavy tests ran.
- Matrix mapping: Seek → IndependentScrubbingKeepsTrimAndMapsRenderedPreviewBackToSource plus both-look P/arrow/lost-capture assertions; Waveform → millisecond cached detail and one-preparation assertions across edits/viewport/audio changes; Lifecycle → preparation cancellation/retry, corrupt preparation/read, disposal cleanup and persistent-error/suspended-read tests; Resize → actual Library-host both-look/rail test grows height by 300 px and requires equal player growth, with existing minimum-window reachability checks. All covering tests executed in the 14-case class.
- Root inspected fake-service Fluent expanded and Gaming rail PNGs beside the Release App test binary: separate ruler thumb visible, cached synthetic peaks drawn, compact controls legible and larger picture fills the available editor height. Screenshots use fake media, not the real Dev App.
- Real worker PCM timing/delay/gap fidelity, long-source resource measurements, NativeAOT/published JSON, high DPI and real pointer/chooser HITL remain deferred under the standing clearance; no runtime media claims are made from synthetic cache tests.
