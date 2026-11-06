# Clip creator focused QA — 2026-11-05

Recommendation: accept the opening/display bugfix and the non-Explicit clip-creator checks. Keep real-media/export acceptance provisional until the separate Explicit/Heavy night/go checks and remaining fixture/UI/packaging checks are cleared and completed.

Initial state: clean `develop` at `582e632fd1f5eca42a1e918dfab23778fcc58580`, containing implementation merge `60104c13eddc72abfb5c15fbf369ad8bf1d37d29`. Scoped clearance was recorded before testing in the handoff, backlog and state/log, committed as `33317aeb51fa18b22351ea70b4aa5209ad34eb48`.

## Commands and observed results

Run one test project at a time; Microsoft.Testing.Platform/xUnit class filtering, with Explicit tests explicitly off. Actual runner summaries below confirm nonzero execution; counts include the four quoting theory rows separately.

```powershell
dotnet build ReplayBuffer.slnx -c Release
dotnet test --project tests/ReplayBuffer.App.Tests -c Release --no-build --filter-class "*ClipCreatorTests" --explicit off
dotnet test --project tests/ReplayBuffer.Engine.Tests -c Release --no-build --filter-class "*ClipMediaWorkerTests" --explicit off
dotnet test --project tests/ReplayBuffer.Platform.Windows.Tests -c Release --no-build --filter-class "*SharedMediaProcessTests" --explicit off
```

| Run | Discovered/reported | Executed | Passed | Failed | Skipped |
|---|---:|---:|---:|---:|---:|
| Initial App, before fixes | 7 | 7 | 4 | 3 | 0 |
| App, after fixes | 7 | 7 | 7 | 0 | 0 |
| Engine identity check | 3 | 1 | 1 | 0 | 2 Explicit |
| Windows quoting theory | 5 | 4 | 4 | 0 | 1 Explicit |
| Final distinct cases | 15 | 12 | 12 | 0 | 3 Explicit |

The build command passed before testing, after the fix and after merge, each with **0 warnings and 0 errors**. The first sandboxed build attempt failed with MSB4184 because Windows SDK registry access was denied (0 warnings, 1 error); the same required build succeeded with elevated sandbox access. One Engine launch was rejected before execution because automatic approval review hit a usage limit; it executed successfully after the builder said resume. Neither event is counted as a test failure or pass.

## Failures investigated and fixed

- `EntryFocusUsesTheEnabledWaveformWhileInitializationIsPending` and `CreatorLayoutAndWaveformTokensResolveInBothLooksWithoutBindingErrors` reproduced the builder's exact `InvalidOperationException`: the progress bar's default TwoWay binding attempted to write private-set `ClipCreatorPage.Progress`. Set its binding to `Mode=OneWay`.
- `MillisecondSelectionSurvivesZoomAndPanAndRequestsFinerSourceBins` exposed a correct numeric start of 41.124 displaying as `00:00:41.123`. Round formatted times to the nearest millisecond before extracting components. Selection values and media requests are unchanged.

Existing regressions went from failing to passing, including both-look ten-lane layout/binding checks and enabled-waveform entry focus. Other passing App coverage checks matching fake preview/export track/mix/audio-sample requests, silent MP4 versus MP3 audio requirements, preview-storage failure recovery, embedded-player restrictions, entry/Back/disappearance and Library selection preservation. No new duplicate tests were needed. The test class summary now describes its fake-service scope without the obsolete unrun claim.

Fix branch: `bugfix/clip-creator-progress-binding`; fix commit `dd58b7b06aa931d18057835e9b676d1465a64d1c`; no-fast-forward develop merge `ec5064d1157376e655da4e01dc1f37f8101ef944`. Independent quick review reported no findings. No push.

## Remaining gaps

- Engine Explicit/Heavy: exact ranges, named separate/mixed MP4/MP3, delayed audio/sample onset, waveform and preview media; collision/cancellation cleanup.
- Windows Explicit/Heavy: inherited seekable shared source, live rename/delete and owned-process cancellation; unrelated-handle isolation assertion remains to be added during authorized platform verification.
- Real fixture sweeps remain pending: decoded first/last frames and PCM equivalence, sync, VFR/common origins/gaps, track modes, silent/odd-sized inputs, long-source viewport seeks, failures during each phase, atomic publication and capture independence.
- Real pointer/HITL, visual PNG inspection, audible playback and composed NativeAOT/packaging remain pending. Passing fake-service WPF layout checks do not verify actual decoding, codec output or media-worker performance.

Only temporary fixtures and owned off-screen test windows were used. No real media tool, capture/GPU/benchmark/soak test, old backlog, HITL 11.6/12.5/14.8, publish/install, installed app, running Dev App, real settings/Library, system settings or unowned process was touched.
