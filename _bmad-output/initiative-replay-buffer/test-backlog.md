# Test backlog

## Taller waveform lanes (2026-11-06)

- Increased each track lane from 24 to 40 pixels; proportional waveform envelope grows with it and track labels are vertically centered. Existing five-lane scrolling and responsive player allocation remain. Two-track timeline is now 108 pixels including the ruler. Updated existing layout expectations for the deliberate extra height; no new tests added for this small visual change.
- `dotnet build ReplayBuffer.slnx -c Release --no-restore -v quiet`: **0 warnings/errors**. `dotnet test --project tests/ReplayBuffer.App.Tests -c Release --no-build --filter-class "*ClipCreatorTests" --explicit off`: **15 passed, 0 failed/skipped**, 16.026 s. Both-look/rail player growth, ten-track scrolling and minimum-window controls checked. Inspected generated Gaming expanded fake-media PNG: taller distinct lanes, centered labels, visible editing controls. Ordinary diff review clear.
- Real media, high DPI and actual pointer/HITL remain deferred; no real Dev App/data changes, unrelated tests or Heavy runs.

## Per-track waveform amplitude scaling (2026-11-06)

- Approved `plan-clip-creator-waveform-amplitude-scaling.md` implemented directly without BMad execution. App display arrays scale against each track's validated whole-source maximum from the existing single-peak cache level. Silent tracks stay zero; zoom/pan/track inclusion retain the denominator. Renderer styling, source/cache bytes, worker and audio playback/export unchanged.
- Final Release build **0 warnings/errors**. `dotnet test --project tests/ReplayBuffer.App.Tests -c Release --no-build --filter-class "*ClipCreatorTests" --explicit off`: **15 executed/passed, 0 failed/skipped**, 14.864 s. Synthetic full/half maxima, silence, zoom/pan/inclusion, unchanged peak files and one preparation verified; existing corruption test extended for NaN summary. Initial new-test viewport assumption corrected after a 14-pass/1-fail run; final whole-class rerun passed. Ordinary diff review found no remaining issue.
- Existing real-media, long-source resource, NativeAOT/publish, high DPI and pointer/chooser HITL checks remain deferred; no broader test clearance, media tools or real Dev App use. Audio/worker unchanged, so no additional Engine/Heavy run introduced.

## Clip creator seek/cache/responsive-player follow-up (2026-11-05)

- Implemented per approved `plan-clip-creator-waveform-interaction.md`: separate draggable ruler seek handle, full-source all-track 1 ms disk peak cache with coarser levels and bounded local reads, no repeat waveform decoding/clearing on edits or zoom/pan, no routine loading/checking text, and finite responsive player height. AD-28 records cache ownership/protocol. Initial uncached preparation still takes time.
- Final `dotnet build ReplayBuffer.slnx -c Release`: **0 warnings/errors**. Final `dotnet test --project tests/ReplayBuffer.App.Tests -c Release --no-build --filter-class "*ClipCreatorTests" --explicit off`: **14 executed/passed, 0 failed/skipped**, 17.781 s, after review patches. Synthetic temporary peak fixtures/fake services cover precision/cache reuse, corruption, cancellation/retry, preview seek mapping, keyboard/lost capture and both-look/rail resizing. Root inspected generated Fluent expanded/Gaming rail previews.
- Quick review's persistent waveform error/validation feedback finding fixed; coordinator's suspended cache-read cancellation finding fixed; follow-up inspection clear. No actual media, Engine/native/shared-shell suites, publishing, benchmarks, soak or HITL ran. Do not retry unchanged Windows quoting tests.
- Pending when separately cleared: real `prepareWaveforms` media fixture with delayed tracks, gaps and nonzero origin; exact 1 ms bucket/onset behavior and complete duration; source rename/delete/change during preparation; cancellation/disk-full cleanup; long-source RAM/disk/read latency; NativeAOT/source-generated worker JSON. Existing real-media/Heavy/night/go restrictions still apply, as do high-DPI and real pointer/chooser acceptance gaps.

## Clip creator control styling follow-up (2026-11-05)

- Approved styling contract R10/R11 implemented on `feature/clip-creator-control-styling`; see `plan-clip-creator-control-styling.md`. Scoped templates, vector icons, joined Export, grouped millisecond fields/duration, responsive whole-group layout, rounded/dimmed focus-aware handles, quieter popup and sidebar treatment.
- Final `dotnet build ReplayBuffer.slnx -c Release`: **0 warnings/errors**. Final `dotnet test --project tests/ReplayBuffer.App.Tests -c Release --no-build --filter-class "*ClipCreatorTests" --explicit off`: **11 executed/passed, 0 failed/skipped** (13.032 s), after all four independent-review patches. Follow-up inspection confirmed all fixes and no remaining directly caused defect.
- Fake-service Fluent/Gaming expanded/rail/narrow PNGs and Gaming options popup were generated beside the Release test binary and visually inspected. This scoped fake-window rendering supersedes the old blanket no-render wording for these artifacts only. Real pointer/hover/focus/disabled visual matrix, high DPI, standalone shell/Settings tests, native chooser/HITL and real-media/Heavy tests remain pending; no expanded clearance inferred. Windows quoting was not retried; no Engine/native/media changes required retesting there.

**Current exception (2026-11-05):** builder cleared tests, then limited clearance to clip creator only. Run only its non-Explicit App/Engine/Windows classes, one project at a time, after a zero-warning Release build. This does not authorize §1/§2, HITL, publish/install, or the clip creator's Explicit/Heavy media/shared-handle checks; those retain the night/go rule. Focused QA results will be recorded in the clip-creator section below.

The builder, 2026-11-05 evening: "resume dev work. don't run any tests until later. make a backlog of tests to run. Merge to dev when changes are ready."

Development goes on without test runs. Builds (0 warnings) still run, and QA is code review only. Stories merge into develop once their code review passes, and the tests they would have run are listed here. When the builder says tests may run again, work through this list top to bottom. Default tests run first, by day or night. Heavy, GPU, capture, bench and soak runs happen at night only, one at a time, with every other session paused for fps or bench measurements. Any failure becomes a bugfix.

## 1. Default suites on develop (first)

Run once on develop's tip, one project at a time, with failure names visible:

    dotnet build ReplayBuffer.slnx -c Release
    dotnet test --solution ReplayBuffer.slnx -c Release --no-build

Plus the NativeAOT publish: `pwsh tools/publish-engine.ps1`.

These cover the merges made without a post-merge run:

| Merged | Story | Last default run before the merge |
|---|---|---|
| `242e947` | 12.2 Source size changes | passed on its branch at `d890811` |
| `6a8340a` | 14.3 `mehd` at finalize | passed on its branch at `e0ad4c0` |
| `de6f653` | 14.2 Player tracer | passed on its branch at `27ebd41` |
| `8ed5775` | 14.5 Transport | passed on its branch at `c7257a3` |
| `de23206` | 14.4 Track picker | passed on its branch at `7c22d82` |
| `d2273f9` | 14.6 When Windows can't play | passed on its branch at `c20c1a4` |
| `14.7 merge` | 14.7 Player sweep | **never run:** `PlayerTransportTests.AViewFileFromBeforeThePlayerLoadsFullAndUnmuted`, `TheVolumeStepsWithoutDriftAndAnyChangeUnmutes`, `TheVolumeAndMuteAreKeptAcrossRuns`; `PlayerViewThemeTests.APageWithNoPictureInTimeSaysTheFileCantBePlayed`, `ThePlayerPageShowsInBothLooksAndTheHiddenListIgnoresItsKeys` |
| later | 12.4 Source-resolution sweep | **never run** (from `feature/12.4-source-sweep`): Engine above all, for `CaptureSession.NewChain`; its changed Heavy test (`ARecordPressWhileAMoveBuildsCancelsItAndTheRecordingKeepsItsChain`, now with an arm delay and a cancel check) is in §2 #4, and a fixed-size Clip check is `--filter-class "*ReplayClipCaptureTests"` with `--explicit only` |

| `a38758d` | 14.9 Player restyle | **deferred:** the original `PlayerViewThemeTests` (rewritten for 14.9), which also renders `player-`, `player-menu-` and `player-fullscreen-{fluent,gaming}.png` for review against the mockup |
| bugfix/player-blank-controls | 14.9 Blank control icons and small follow-ups | **not run:** expanded `PlayerViewThemeTests.ThePlayerPageShowsInBothLooksAndTheHiddenListIgnoresItsKeys`, covering the centred menu heading, right-side accent check, saved page margin, and close-to-tray/full-screen restoration. **Only the builder-authorized focused render ran:** `ThePlayerIconsRenderInBothLooks` passed (1 test, 2026-11-05), with real-glyph/font/foreground assertions for Play/Pause, speaker/mute, gear and enter/exit full screen in both looks; `player-icons-{fluent,gaming}.png` beside the Release test binary were visually inspected. No other tests ran. |

Player HITL follow-up: with the gear menu open, click the gear again and check that the menu stays closed. The possible outside-click dismissal/reopen race remains unchanged; resolve it with a real pointer check when the builder permits that work (or during 14.8).

## 2. Heavy and GPU, at night, in this order

| # | Story | Command or run | Notes |
|---|---|---|---|
| 1 | 8.8 | The 8-hour **Disk soak**, from `feature/8.8-soaks`, with the command in its plan | The Memory soak ran 5 h 9 min; engine growth was 1.87 MB/h against a 2 MB/h limit, so watch it |
| 2 | 11.5 | `dotnet test --project tests/ReplayBuffer.Engine.Tests -c Release --no-build --explicit only --filter-class "*MonitorRecordingCaptureTests"` | One-screen and two-screen recordings |
| 3 | 12.1 | `dotnet test --project tests/ReplayBuffer.Engine.Tests -c Release --no-build --explicit only --filter-method "*ARealCaptureOfA2560By1440WindowWithMatchSourceRecordsAt2560By1440"` | Needs a primary screen of at least 2560×1440 |
| 4 | 12.2 | `dotnet test --project tests/ReplayBuffer.Engine.Tests -c Release --no-build --explicit only --filter-method "*ARealWindowResizedTo2560By1440MovesTheReplayBufferAndTheNextClipHasIt" --filter-method "*AResizeDuringAGameRecordingKeepsItsSizeAndMovesAfterItStops" --filter-method "*ARecordPressWhileAMoveBuildsCancelsItAndTheRecordingKeepsItsChain" --filter-method "*AMonitorRecordingStartsBesideAGameRecordingAfterAResize" --filter-method "*AOneScreenMonitorRecordingOfThe3840By2160ScreenRecordsAtThatSize"` | Read the Record-during-build test's log: it can pass without exercising the cancel |
| 5 | 12.2 | **Cost run**, every session paused, with an overlap log: `bench\ReplayBuffer.Bench\bin\Release\net10.0-windows10.0.26100.0\ReplayBuffer.Bench.exe fps --repo <worktree> --presentmon %USERPROFILE%\Documents\repos\ReplayBuffer\native\presentmon\PresentMon-2.6.0-x64.exe --seconds 45 --passes 2 --settle 10 --conditions baseline,capture,capture-ms` | Record it in epic 12's Notes against NFR-1 |
| 6 | 14.3 | `dotnet test --project tests/ReplayBuffer.Engine.Tests -c Release --no-build --explicit only --filter-method "*AFiveSecondRecordingPlaysToItsEndInMediaPlayer" --filter-method "*ATwoScreenMonitorRecordingOf60Seconds*"` | AV1 needs an AV1 encoder; `NaturalDuration` is the decisive check |
| 7 | 14.3 | Crash-cut seek check: a hand-patched crash-cut copy through `MediaPlayer`, seeking mid-file | For epic 14's Notes; decides the crash-cut `mehd` follow-up |
| 8 | 14.2–14.7 | `dotnet test --project tests/ReplayBuffer.App.Tests -c Release --no-build --explicit only --filter-class "*PlayerPlaybackTests"` | 5 tests: a Clip on Track 1, 6400×2160 HEVC, 200 opens, the heard Track and a paused switch, a fragmented Recording. They play muted |
| 9 | Follow-up | `RecordingWriter`'s fragment cadence under load (one loaded run had 0.7 s against a 0.5 s limit) | From full-run-flaky-tests |
| 10 | Follow-up | The video ending about 1 s short of the audio at a stop under starvation | From full-run-flaky-tests |

## 3. Known default-suite flakes to watch

`LibraryThemeTests.TheLibraryLoadsInBothThemes…`, `SessionOrchestratorTests.ARunningRecordingHoldsASwitch…`, `ReplayClipCaptureTests.ASavePressWrites…` and `RecordingCrashTests.ARecordingKilledThreeSecondsIn…` have each failed once under load and passed alone. If one fails again, it goes to a flaky-test bugfix.

## Clip creator — focused default QA passed; Heavy/media pending (2026-11-05)

Scoped QA ran only the three non-Explicit clip-creator classes. Final result: **12 passed, 0 failed; 3 Explicit/Heavy cases skipped**. Initial App run reproduced two progress-binding crashes and one millisecond-display failure; both causes are fixed and the 7 App cases now pass. Release builds and independent bugfix review passed. See [qa-clip-creator.md](qa-clip-creator.md) for exact commands/counts and revisions. No real-media probes, PNG inspection, publishes or soaks ran.

- Passed (7): `ClipCreatorTests` (fake media/player): millisecond entry/nudges, zoom/pan retaining selection with finer viewport requests; explicit track selection and matching preview/export requests; silent MP4 vs required MP3 audio; Library/player entry, selection preservation, Back and disappearance; preview failure, embedded actions and both-look layout/focus.
- Passed: `ClipMediaWorkerTests.ChangedAndMissingSourceIdentityIsRejectedWithoutLaunchingTools` (1); `SharedMediaProcessTests.QuotesWindowsArgumentsIncludingTrailingBackslashes` (4 theory rows).
- Explicit Heavy: `ClipMediaWorkerTests.ExactRangeNamedStreamsSeparateMp3AndMixPreserveOffsets`: distinguishable Game/Mic fixture, delayed audio origin, source-frame alignment, stream names/order/count, normalized mix, all separate MP3 files, decoded leading silence and duration. `CollisionAndCancellationLeaveSourcesAndExistingDestinationsIntact`: existing destination, cancellation and scratch cleanup.
- Explicit Heavy: `SharedMediaProcessTests.SeekableInheritedSourceCanBeRenamedAndDeletedAndOwnedDecodeCanBeCancelled`: shared inherited seekable input, seek, live rename/delete, cancellation. Add an inherited-unrelated-handle isolation assertion during authorized platform verification.
- Pending real fixture sweep: single/all/none chosen tracks, separate/mix in both MP4 and MP3, preview/export decoded PCM equivalence (including delayed streams and intentional gaps), variable-frame-rate and nonzero common-origin input, exact decoded first/last frames and A/V sync, silence/no-audio input, odd source dimensions/even padding, long recording late viewport seek and finer waveform bins. Verify no keyframe expansion.
- Pending failure sweep: cancel each phase and during active encoding, source rename/delete/change during every operation, disk-full/permission failure, destination collision at publication, separate-MP3 directory atomic rename, scratch deletion, navigation/hide while work is pending, process launch failure and pipe consumer failure; verify capture remains independent and no unowned process is stopped.
- Pending UI/HITL in Fluent and Gaming dark: waveform/ruler/handles/selection and progress/error legibility, pointer dragging, Home/End plus 1 ms keys and numeric entry, zoom/pan source detail, playhead and rendered selection zero mapping, named track audition/mix overlap warning, save/folder naming dialogs, source-context/Library focus preservation, hide pauses and cancels work without losing the range. No renders permitted until authorized.
- Pending NativeAOT/composed packaging: `tools/publish-engine.ps1` and `tools/publish.ps1`; require ffmpeg.exe/ffprobe.exe beside the engine, exclude ffplay.exe, retain executable allowlist and validate worker JSON source generation and inherited-handle protocol in the published engine.

- Clip precision regression: real-media Explicit check remains unrun — MP3 request 0.113–1.417 on a 30 fps source retains 48 kHz sample boundaries; decoded delayed-track MP3 has 62,592 stereo frames and the expected onset relative to 0.113. Its temporary MP4 selection preview retains the same audio origin/onset while picture frames follow source timing. **Passed:** fake-service tests assert MP3 preview preserves audio-range mode and the UI identifies sample boundaries; millisecond nudge display now rounds correctly.

- Clip creator review regressions **passed**: embedded source/selection players suppress unsupported full-screen, default-player and Store actions while ordinary playback retains them; entry focus lands on the enabled waveform before initialization; preview-folder failure is reported and restores editor controls; both-look layout uses ten selected waveform lanes and keeps the time controls below the measured waveform. Both view checks initially reproduced the builder's Progress binding exception; the OneWay binding fixes it.

## Clip creator UX implementation checkpoint (2026-11-05)

- Implemented on `feature/clip-creator-ux-pass`; independent review and integration are pending. No commits, merge or push performed by the implementation worker.
- Editor: larger 480 px aspect-preserving player (including its transport), compact symbol pan/zoom tools, retained type/options dialog, conditional progress/cancel/status, 24 px waveform lanes bounded to five visible lanes, scrollbar and Up/Down/PageUp/PageDown lane access. Selection and ruler use the same source-time mapping after resize; constrained gutters are safe below 100 px.
- Export: opening options/type selection never chooses a destination or encodes. Name/track/mix edits survive dismissal; Save uses an injectable guarded chooser. MP4/mixed MP3 uses a file picker; separate MP3 uses a parent-folder picker plus the retained new directory name. Existing worker contract uses a directory even for one separate MP3; the dialog describes this explicitly. No worker/codec/media semantics changed.
- Shell: expanded state starts true and lasts through navigation in the session; 58 px icon rail survives resize/full-screen restoration, with bottom Settings and ordered existing Settings sections inside Settings. Existing routes and drill-in lifecycle remain in the model. Explicit glyph TextBlocks avoid the previous Gaming implicit-font issue.
- Verification: `dotnet build ReplayBuffer.slnx -c Release` passes with **0 warnings, 0 errors** after the final code/test edits. Sandbox-only build initially failed on Windows SDK registry access; escalated builds succeeded.
- `dotnet test --project tests/ReplayBuffer.App.Tests -c Release --no-build --filter-class ReplayBuffer.App.Tests.ClipCreatorTests --explicit off`: final **10 passed, 0 failed, 0 skipped**. Covers retained options/focus/type menu, chooser cancellation/duplicate guards, preview parity, both-look compact many-lane layout, narrow-gutter safety, existing precision/entry/lifecycle checks. An initial 8/9 run caught trigger focus blocked by refresh; fixed and rerun green. The integrated reference test then caught 137 px overflow; compacted duplicated toolbar text/rows and moved Back into the creator while hiding the duplicate shell breadcrumb. Final fake-service actual-Library-host test passes with a 960×900 client area, both looks and both rail widths: zero creator scrolling, 76 px two-lane waveform and ≥390 px allocated picture area. A null fake-player Image assertion was corrected to check its allocated picture row.
- `dotnet test --project tests/ReplayBuffer.Engine.Tests -c Release --no-build --filter-class ReplayBuffer.Engine.Tests.ClipMediaWorkerTests --explicit off`: **1 passed, 0 failed, 2 Explicit skipped** (identity validation executed).
- Attempted `SharedMediaProcessTests --explicit off` **did not execute**: automatic approval review rejected it, stating the class was not clearly clip-creator-specific and could conflict with the scope restriction. No bypass/retry. The implementation coordinator confirmed this unchanged quoting class need not repeat; its four prior passing theory rows remain historical evidence, not a new run.
- Deferred: newly written `MainWindowTests.NavigationWidthRetainsCurrentViewAndSettingsUsesASeparateBottomRoute` and `SurfacesThemeTests.BottomSettingsAndPrimaryIconsResolveInBothNavigationWidthsAndLooks` (compiled only); all other shell/Settings classes, full-screen/state-under-work integration, high scale, native chooser interaction, pointer/keyboard HITL, actual media/Heavy/night checks, publishes, benches and soaks. No installed/running app, real Library/settings or unowned process touched.

### UX pass post-review verification (2026-11-05)

- Final scoped ClipCreatorTests: **11 passed, 0 failed, 0 skipped** (12 s), including both-look minimum-shell timeline-button bounds/scroll reachability. Feature and merged-develop Release builds: **0 warnings/errors**. Quick review's narrow toolbar and Settings reselection findings fixed; follow-up inspection clear.
- Additional compiled/unrun shell test: `MainWindowTests.CurrentSettingsSectionReselectionClosesDrillInWhileEscapeOnlyDismissesTheMenu` (pointer, Enter/Space and Escape). Run only after shared-shell clearance, together with the other two new shell tests recorded above.
- Full pending-work navigation, high-scale, native chooser/HITL and real-media/Heavy checks remain deferred. No unrelated tests ran; rejected unchanged quoting run was not retried. Merge: `4dfbebaa8c55f5fb3415a5317d74962b9bf87e07`.
