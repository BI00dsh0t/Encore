# Test backlog

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

| on merge | 14.9 Player restyle | **never run:** `PlayerViewThemeTests` (rewritten for 14.9), which also renders `player-`, `player-menu-` and `player-fullscreen-{fluent,gaming}.png` for review against the mockup |

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
