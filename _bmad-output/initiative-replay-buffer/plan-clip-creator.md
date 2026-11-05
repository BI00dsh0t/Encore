---
title: 'Create precise clips from the Library and player'
type: 'feature'
ticket: ''
created: '2026-11-05'
status: 'draft'
baseline_revision: 'ef5ff01d3f479e7459970e66268a15f7fc933f54'
route: 'full'
route_source: 'auto'
risk: 'medium'
review: ''
review_source: ''
lenses_ran: []
review_loop_iteration: 0
context:
  - '{project-root}/CLAUDE.md'
  - '{project-root}/_bmad-output/initiative-replay-buffer/feature-requests/clip-creator/clip-creator.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Finished footage cannot be trimmed/exported inside ReplayBuffer. The builder needs precise waveform selection and explicit audio choices.

**Approach:** One Library/player creator: video, draggable selection, detailed waveforms, zoom/pan, millisecond entry/nudges, matching selection preview, MP4/MP3 export, one/all named tracks, separate or mixed.

## Boundaries & Constraints

**Always:** Preserve originals/existing destinations and Library selection; reject unfinished/changed sources; cancel on disappearance/navigation. Both looks, theme tokens, independent capture. Release builds: zero warnings. Write/backlog tests; run none, including renders/media probes/publishes/soaks. Leave installed/running apps, real data/settings and unowned processes alone. Local git flow; no push.

**Never:** Multi-cut editing, effects, uploads, imports, silent track loss or keyframe-expanded cuts. WAV/FLAC/additional codecs remain future work.

**Defaults:** One finished Clip/Recording/Monitor Recording; initial range up to 30 seconds around the playhead; Back returns to Library. Software H.264/AAC MP4 preserves source size/timing (even padding if needed); MP3 is stereo 48 kHz/192 kbps. User chooses name/destination; MP4 in Library is discovered normally, MP3 remains external. Separate MP3s publish as one directory. Mix explicitly included tracks with normalization/limiting and overlap warning; preview matches export. One background operation per creator, two codec threads, bounded bins/buffers, cancellable progress.

## I/O & Edge-Case Matrix

| Scenario | Input/state | Expected behavior | Failure handling |
|---|---|---|---|
| Precision | Trim/nudge/zoom/pan | Preserve selection; fetch finer detail | Reject empty ranges; display effective source-frame boundaries |
| Export | Single/all; separate/mix | Named streams/files or matching mix | Silent MP4 allowed; MP3 needs audio |
| Lifecycle | Missing/changed source, cancel, disk-full, collision | Stop owned work; preserve originals | Clean owned scratch; publish completed output by rename |

</frozen-after-approval>

## Code Map

- `src/ReplayBuffer.App/Library/`: existing selection/drill-in/lifecycle and `IPlayer`/`PlayerView`; WinRT previews only one track, so mixed preview needs temporary media.
- `src/ReplayBuffer.Platform.FFmpeg/SharedInput.cs`: ordinary FFmpeg file input blocks deletion; inherited seekable handles must retain delete sharing.
- `src/ReplayBuffer.Engine/Program.cs`: worker must dispatch before instance/settings/log initialization.
- `Directory.Build.targets`, `tools/publish.ps1`: dev copies tools; publish strips them.

## Tasks & Acceptance

**Execution:**
- [ ] `_bmad-output/initiative-replay-buffer/architecture-replay-buffer/architecture-replay-buffer.md`, `initiative-replay-buffer.md`: record worker ownership, atomic exports and editing exception before code.
- [ ] `src/ReplayBuffer.Contracts/Messages/ClipMedia.cs`: identity, metadata, bins, requests/progress/results/errors and source-generated worker JSON; preserve live IPC.
- [ ] `src/ReplayBuffer.Platform.Windows/SharedMediaProcess.cs`, `NativeMethods.txt`: hidden tool launcher; explicit inherited handles, seekable shared stdin, quoting, drained pipes and owned-process cancellation.
- [ ] `src/ReplayBuffer.Engine/ClipMediaWorker.cs`, `CommandLine.cs`, `Program.cs`: probe, viewport PCM bins, source-frame queries, exact transcode, track mapping/mix, bounded progress and atomic output/cleanup.
- [ ] `src/ReplayBuffer.App/Library/{ClipMediaService,ClipCreatorPage,WaveformView}.cs`, `ClipCreatorView.xaml(.cs)`: async client, timing/selection, accessible waveform interaction, track choices, dialogs/cancel/progress and temporary selection preview.
- [ ] `src/ReplayBuffer.App/Library/{LibraryPage,PlayerPage}.cs`, `LibraryView.xaml(.cs)`, `PlayerView.xaml`, `MainViewModel.cs`, `Theme/LibraryTheme.xaml`: entry points, navigation/focus/disappearance/hide lifecycle and theme tokens.
- [ ] `tools/{fetch-ffmpeg,publish}.ps1`: require/retain ffmpeg/ffprobe; exclude ffplay; keep packaging executable checks.
- [ ] `tests/ReplayBuffer.App.Tests/ClipCreatorTests.cs`, `tests/ReplayBuffer.Engine.Tests/ClipMediaWorkerTests.cs`, `tests/ReplayBuffer.Platform.Windows.Tests/SharedMediaProcessTests.cs`: write fake-service and explicit real-media regressions; backlog all unrun checks.

**Acceptance Criteria:**
- Given either entry point, when opened/closed, then source context and Library selection survive.
- Given a waveform, when zoomed/trimmed/nudged, then detail and millisecond selection agree.
- Given named tracks, when previewed/exported in each mode, then effective range, isolation/mix and sync agree.
- Given interruption/failure, when stopped, then sources/capture/existing files remain intact.
- Given either look, when editing, then waveform/ruler/selection/controls remain legible.

## Implementation Notes

## Plan Change Log

## Review Triage Log

## Design Notes

FFmpeg `fd:` uses inherited synchronous, delete-shared stdin for seekable access without source copies. Worker stdin/stdout JSON stays separate from live service IPC. Query frames near endpoints, trim decoded samples/frames, stream viewport PCM. Generate mixed selection preview using the export path; map preview time zero to effective source start. Invalidate preview after range/audio changes; detach/dispose playback before replacing/deleting scratch. Publish separate MP3s through directory rename.

## Verification

- `dotnet build ReplayBuffer.slnx -c Release`: feature/develop, zero warnings/errors; independent code review before no-fast-forward merge.
- No tests/media processes. Backlog NativeAOT/packaging, real-fixture boundaries/mix/sync, seek/delete-sharing/cancel, waveform/UI/resource verification; report unverified behavior.
