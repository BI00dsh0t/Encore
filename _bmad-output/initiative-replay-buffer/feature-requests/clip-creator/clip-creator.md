---
type: epic
title: "Create clips with precise waveform trimming and audio export"
parent: initiative-replay-buffer
covers: []
sources:
  - _bmad-output/initiative-replay-buffer/feature-requests/clip-creator/request.md
companions: []
after: []
refined: false
risk: medium
---

# Create clips with precise waveform trimming and audio export

## Description

An unscheduled feature request for a clip creator reached through **Create clip** in the Library or the in-app player. A Twitch-style workflow combines a video preview, a timeline with start/end handles, zoomable audio waveforms, audio-track choices, and export. It creates a new media file from existing captured footage. This request is epic-sized, has no assigned ticket number or stories yet, and does not start development or change the current build order.

## Outcome

The builder can find a moment in existing footage, precisely select its range using the picture and audio waveform, preview the chosen audio, and export a video clip or audio-only excerpt without leaving ReplayBuffer.

## Requirements

- R1 (request, Original request): A **Create clip** action, or an equivalent clear label, is available for a Library item and the current item in the player; both open the same clip-creation workflow with that source loaded.
- R2 (request, Original request): The editor offers a Twitch-style video preview and timeline with draggable start/end handles, a playhead, visible selection duration, play/pause, and playback of the selected range so the builder can check the excerpt before exporting.
- R3 (request, Original request): The editor shows audio as a waveform against a time ruler, with start, end and playhead times displayed to milliseconds (`HH:MM:SS.mmm`). Start/end can be entered numerically and adjusted in 1 ms steps, alongside mouse trimming.
- R4 (request, Original request): The waveform supports zooming into a chosen section and panning through it while keeping the trim selection. Zoom reveals finer source-audio detail, rather than merely enlarging an overview image; the builder can locate an audio onset and adjust a boundary at the millisecond scale.
- R5 (request, Original request and Audio-mode clarification): Export audio can use one named source track or all source tracks. The builder explicitly chooses **Separate tracks** or **Combined mix**, and previews which tracks will be exported. Track names and order remain recognizable in the selection UI.
- R6 (request, Original request and Audio-mode clarification): **MP4 video** and **MP3 audio-only** exports are supported. MP4 with separate tracks retains the chosen tracks as separate named audio streams in one video file; MP4 with a combined mix contains one mixed audio stream. MP3 exports either one combined audio file or separate named audio files per chosen track, as explicitly selected; tracks are never silently dropped.
- R7 (request, Original request; precision implications): Export uses the selected range and audio mode, with picture and sound aligned. Audio boundaries follow decoded sample timing; video boundaries follow available source frames. The UI shows the effective export boundaries when frame alignment changes a requested millisecond value. Export must not silently widen the range to earlier/later video keyframes.
- R8 (request, Original request; output implications): The builder chooses an export name and destination, sees progress, can cancel, and receives a clear success location or actionable failure. Export creates new output and preserves the source; failure or cancellation leaves no partial file presented as a completed export.

## Constraints

- Both Fluent and Gaming dark use the app's theme tokens; waveform, selection, handles, time ruler and export controls must remain legible in both looks.
- The existing .NET 10 WPF App/NativeAOT engine separation, capture continuity, source-file lifecycle and bounded resource use must be respected. Export ownership and any Contracts changes need an architecture decision before implementation; this request does not authorize putting encoding into the read-only player path.
- The builder's test hold remains in force. Recording this request runs no builds, playback probes, default tests, GPU/Heavy tests, benchmarks or soaks. Verification below is future work, not permission to run it.

## Done when

- The same source opens in the creator from both a Library action and the player, in both looks (R1–R2).
- The builder can zoom around an audio event, enter and nudge millisecond boundaries, and preview the exact effective selection without losing it while zooming or panning (R2–R4, R7).
- A fixture with distinguishable named tracks produces MP4 and MP3 exports with the chosen range and audio: a single track, all tracks kept separate, and a combined mix. No unselected audio leaks into an export (R5–R7).
- Exported files play independently with the expected duration and synchronized audio; cancellation and a failed write preserve the source and do not leave an apparent completed export (R7–R8).
- The builder accepts the integrated clip-creation workflow against the intended Twitch-style interaction, after authorizing the relevant verification runs.

## Boundaries

- Proposed first scope: one contiguous range from one finished Clip, Recording or Monitor Recording; save a new export rather than edit the source in place.
- Out of this request: a full multi-clip timeline, transitions, effects, uploading, Twitch account integration or streaming. “Twitch-style” describes the editing workflow, not a connection to Twitch.
- Must not change: Save Hotkey/replay-buffer semantics, existing captures, the installed app, the running Dev App, or the builder's real files/settings during development.
- Touch point: Library item selection and actions — owner: epic-library-cleanup / epic-ux-pass-2.
- Touch point: player page and its current source/position — owner: epic-in-app-player.
- Touch point: media decode, waveform extraction, accurate cutting, mixing and encoding — ownership to settle before slicing.

## References

- request — [Builder input and audio-mode decision](request.md).
- parent — ../../initiative-replay-buffer.md, Boundaries and R15/R27/R28/R37/R42.
- existing player — ../../tickets/epic-in-app-player/epic-in-app-player.md, Constraints and Boundaries.
- architecture — ../../architecture-replay-buffer/architecture-replay-buffer.md, AD-1, AD-2, AD-9, AD-17, AD-22 and AD-27.
- format support — [FFmpeg formats](https://ffmpeg.org/ffmpeg-formats.html), MOV/MP4, MP3, WAV and FLAC muxers.
- stream selection and precision — [FFmpeg documentation](https://ffmpeg.org/ffmpeg.html), stream mapping, `-ss` and accurate seeking.
- audio trimming and mixing — [FFmpeg filters](https://ffmpeg.org/ffmpeg-filters.html), `atrim` and `amix`.
- MP3 stream limit — [FFmpeg MP3 muxer source](https://www.ffmpeg.org/doxygen/7.1/mp3enc_8c_source.html), one MP3 audio stream per output.

## Notes

- Decision: 2026-11-05, the builder subsequently said "start implementation". Planning is underway on `feature/clip-creator` in `../../plan-clip-creator.md`; the BMad plan-approval checkpoint precedes product-code changes. The original request below remains the intake history.
- Decision: 2026-11-05, builder requested this feature and explicitly chose **both separate tracks and a combined mix**. The request is recorded; development, story slicing, scheduling and test runs have not been started.
- Source conflict: the original initiative excludes in-app video editing, and epic 14 excludes trimming/cutting/export. This new request reopens that capability for future planning; it does not expand the scope of already-completed player stories or rewrite their acceptance history. Amend the initiative boundary and architecture when this request is scheduled.
- Assumption: **Create clip** is the working action name; the exact UI and visual design still need refinement against the builder's Twitch-style reference.
- Assumption: the initial scope is one contiguous range from a finished Clip, Recording or Monitor Recording. On entry from the player, start near its current playhead; the initial selection length is still to be designed. An unfinished source is unavailable for export with a clear explanation.
- Assumption: waveform lanes follow the selected audio tracks and share one time ruler/playhead. A combined-mix preview must match the exported mix, while original named tracks remain identifiable.
- Assumption: the proposed default video preset is **MP4 with H.264 video and AAC audio**. This is a compatibility recommendation informed by the project's existing H.264 default and FFmpeg support, not a codec choice already approved by the builder. **WAV/FLAC** are optional audio-export candidates for further editing; additional video formats are not committed scope.
- Open question: export resolution, frame rate, quality/bitrate presets and whether HEVC/AV1/source-codec exports are offered alongside the compatibility preset. Settle these before stories promise formats or performance.
- Open question: how exported files appear in the Library, which folder defaults to the export destination, and how Cleanup treats exports. The Library currently centers on captured video; MP3 indexing must not be assumed.
- Open question: mixed-track gain/normalization behavior and overlapping source tracks. An “Everything” track can already contain the sounds in other tracks; the UI must make the included tracks clear and flag possible double counting rather than silently choosing a mix for the builder.
- Open question: representative long-recording size, waveform loading/zoom responsiveness and export resource limits. Set measurable targets during refinement, including how export behaves during active capture.
- Planning prerequisite: settle waveform/media/export ownership, exact-cut strategy and source rename/delete/Cleanup handling before implementation. Stream copy alone must not be assumed to provide arbitrary accurate cuts.
- Verification deferred: future stories should include lean checks for both entry points, range/zoom persistence, track mapping and mix behavior, decoded boundary accuracy and A/V sync, cancellation/failure, and both looks. Add concrete unrun tests to `test-backlog.md` when written; hardware/performance runs remain night-only after the builder's go.
