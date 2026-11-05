---
id: 14
type: epic
title: "In-app player"
parent: initiative-replay-buffer
covers: [R42]
sources: []
companions: []
after: []
refined: true
risk: medium
---

# In-app player

## Description

The Library plays its files itself. Enter or a double-click on a row opens a player page in the Main App, the way a game opens its page in Games. The picture comes from Windows' own media player (WinRT `Windows.Media.Playback.MediaPlayer`), drawn through its frame server into WPF, so the page's controls and both looks sit over the video. The first Track plays at open. A Track list switches Track mid-play by its name. A seek bar, one volume control and the keyboard control playback. Every new Clip and Recording plays to its last frame. A PC without the HEVC or AV1 extension gets a plain message and the default player instead.

## Outcome

The builder opens a Clip or Recording in the Library and hears each Track on its own, switching between them while it plays, without leaving the app (R42).

## Requirements

- R1 (parent R42, R28; architecture AD-24, AD-27): Enter or a double-click on a finished row in the Library opens a player page for it. It uses 10.10's drill-in shell piece: a breadcrumb header back to the Library, closing on Back, Esc, Alt+Left or the mouse's Back button, with focus back on its row. The row's existing "Open" action is renamed "Open in default player" and still hands the path to the Windows shell. A row still being written isn't played: Enter and a double-click do nothing on it, as rename does today.
- R2 (parent R42, R11, R12, R21; architecture AD-9, AD-27): Clips (faststart MP4), Recordings and Monitor Recordings (fragmented MP4) play in H.264, HEVC and AV1, up to 6400×2160. The video fills the page, scaled with its aspect kept and letterboxed, with the controls drawn over it in both looks. Playback starts paused on the first frame.
- R3 (parent R42): The page has play and pause, a seek bar with the elapsed and total time that seeks on click and while dragging, and one volume control with mute. Keys: Space plays and pauses, Left and Right seek 5 s, Up and Down change the volume, and Esc goes back. The volume is kept across runs with the Library's view state.
- R4 (parent R42, R15; architecture AD-27): The page lists the file's Tracks by their names, in file order, and shows which one is playing. The first Track, "Everything" in a new layout, plays at open, whatever Windows would pick. Choosing another Track switches to it mid-play, with only a brief gap of about 0.1 s and no restart or jump in position. One Track plays at a time.
- R5 (parent R42, R11, R12, R21; architecture AD-9, AD-27): A Recording and a Monitor Recording show their full length and play to their last frame. The engine writes a Recording's fragment duration (`mehd`) when it finalizes the file, and a file without one still plays up to its last fragment.
- R6 (parent R42, R38): When Windows can't decode the video (the HEVC or AV1 Video Extension isn't installed) or can't open the file, the page says so in plain words. It names the extension with a link to its Store page and offers "Open in default player". Nothing crashes, and Back still works.
- R7 (parent R42, R34; architecture AD-17): Leaving the page, or the file being renamed, deleted or cleaned up, stops playback and releases the file. Delete, rename and Cleanup then work as they do today, and opening and closing files many times doesn't grow memory.

## Constraints

- (builder decision 2026-11-05) Windows' own media player, WinRT `MediaPlayer` in frame-server mode into a `D3DImage`; not LibVLC, WebView2, FFME or our own FFmpeg decode.
- (architecture AD-27, amending AD-1) The App may open a *finished* media file read-only to play it, sharing read, write and delete, so the LibraryActor's rename, delete and Cleanup never fail on it. It still never captures, encodes or writes settings, and never opens a file being written.
- (architecture AD-2, AD-14) The App's D3D11 and D3D9Ex interop lives in `Platform.Desktop` through CsWin32; no new NuGet package. `Platform.Desktop` builds with the AOT analyzers, so its interop must pass them. The App itself isn't AOT.
- (architecture AD-22) Contracts are unchanged; the `mehd` is a file-format detail inside the engine's Recording muxer.
- (initiative Boundaries) No in-app editing: no trim, cut or export.
- (CLAUDE.md, memory) Lean tests. Playing a 720p H.264 fixture for a second is a default test. 6400×2160, HEVC, AV1 and real Recordings are Heavy (`Explicit`), run at night, one project at a time.

## Done when

- Enter on a Clip in the Library opens it in the app, in both looks, and Back returns to its row (R1, R2).
- On the builder's PC, the builder plays a Clip, switches to the Mic Track and back while it plays, and hears each (R4).
- A 6400×2160 Monitor Recording shows its full length, seeks, and plays to its last second (R2, R3, R5).
- With the decoder made unavailable, the page shows the message, the Store link and "Open in default player" (R6).
- A file closed in the player can be deleted at once, and an open-and-close loop doesn't grow memory (R7). Verified integrated on the builder's PC.

## Boundaries

- Out: mixing several Tracks at once and a volume per Track (parked; see the Notes).
- Out: frame step, playback speed and full screen (parked).
- Out: trimming, cutting or exporting (initiative Boundaries: no in-app editing).
- Out: playing a file still being written, and repairing or rewriting Recordings made before the `mehd` change.
- Out: installing codec extensions for the user.
- Touch point: the drill-in shell piece (10.10), extended: today the shell routes Back, Esc and the nav item only to the Settings pages' drill-ins, and this epic adds the Library's. The Library's rows, keys and Open action (10.2) are consumed. Owner: epic-ux-pass-2.
- Touch point: the HEVC and AV1 Video Extensions from the Microsoft Store, consumed only; owner: Windows.

## References

- parent — _bmad-output/initiative-replay-buffer/initiative-replay-buffer.md, Requirements R42 (and R11, R12, R15, R21, R28, R34, R38)
- research — _bmad-output/initiative-replay-buffer/research/in-app-player.md (measured results; Media Foundation gotchas §5; the frame-server design §6.1)
- architecture — _bmad-output/initiative-replay-buffer/architecture-replay-buffer/architecture-replay-buffer.md, AD-1, AD-2, AD-9, AD-14, AD-17, AD-22, AD-24, [AD-27](../../architecture-replay-buffer/architecture-replay-buffer.md#ad-27--in-app-player-winrt-mediaplayer-into-a-d3dimage-app-side-and-read-only)
- design — _bmad-output/initiative-replay-buffer/tickets/epic-ux-pass-2/ux-pass-2/EXPERIENCE.md (Drill-in pages, the Library, the glossary)
- code — `src/ReplayBuffer.App/Library/LibraryPage.cs` (`OpenSelection`, `OpenFocused`), `LibraryView.xaml(.cs)` (Enter and double-click), `LibraryShell.cs`, `LibraryViewState.cs`; `src/ReplayBuffer.App/Settings/DrillIn.cs`, `DrillInHeader.xaml`; `src/ReplayBuffer.Platform.Desktop/`; `src/ReplayBuffer.Platform.FFmpeg/FragmentedMp4Muxer.cs`

## Notes

- Decision: 2026-11-05, the builder's words: "add a new feature request. first research easiest approach for an in app media player that also allows us to switch audio tracks." Then, of the research's recommendation: "use the windows media player."
- Decision: 2026-11-05, its own epic: it adds a page, a rendering path and an engine file-format change. It runs before 8.11, so the hands-off run uses it, and it waits on nothing left in epics 11 to 13.
- Decision (builder, accepted 2026-11-05; was an unattended default): 2026-11-05, the player is a drill-in page from the Library, reusing 10.10's drill-in, opened by a double-click or Enter on a row. "Open in default player" stays as a row action.
- Decision (builder, accepted 2026-11-05; was an unattended default): 2026-11-05, one Track at a time, the first Track ("Everything" in a new layout) at open. Mixing several Tracks is parked as a later story.
- Decision (builder, accepted 2026-11-05; was an unattended default): 2026-11-05, one volume control. A volume per Track is parked; it needs mixing.
- Decision (builder, accepted 2026-11-05; was an unattended default): 2026-11-05, it plays Clips, Recordings and Monitor Recordings.
- Decision (builder, accepted 2026-11-05; was an unattended default): 2026-11-05, controls: play and pause, a seek bar with time, volume, and keys (Space, Left and Right, Up and Down, Esc). Frame step, speed and full screen are parked.
- Decision (builder, accepted 2026-11-05; was an unattended default): 2026-11-05, a missing codec gets a plain-words message with a link to the Store extension and "Open in default player".
- Decision (builder, accepted 2026-11-05; was an unattended default): 2026-11-05, trimming is parked and out of scope.
- Decision (builder, accepted 2026-11-05; was an unattended default): 2026-11-05, a row still being written isn't played, as it can't be renamed; the default player is still offered for it.
- Decision (builder, accepted 2026-11-05; was an unattended default): 2026-11-05, the volume is kept across runs in the Library's view state, App-side, with no settings key.
- Decision: 2026-11-05, the research measured Media Foundation picking the *last* Track at open and ending fragmented MP4 at its last `mfra` entry (up to one fragment early). R4 and R5 cover both; a hand-patched `mehd` fixed the second.
- Decision: 2026-11-05, entry 1 (hitl) is the AD-27 spine edit, run by the coordinator as an architecture update session, as with AD-26. It amends AD-1 (the App may read a finished file to play it) and AD-9 (`mehd` at finalize), so the tracer and entry 3 wait on it.
- Decision: 2026-11-05, the tracer is entry 2: a row's Enter opens the page, a Clip plays with its first Track forced, through the frame server, the `Platform.Desktop` interop and a `D3DImage`, with play and pause, and Back releases the file. Next is entry 3, the least certain part: an in-place `mehd` patch at finalize on real Recordings, with a plan checkpoint. It runs in the engine lane, in parallel with the App lane.
- Decision: 2026-11-05, the App lane is one ordered line, since every story edits the player page: tracer, Track picker (4), transport (5), missing codec (6). Then a Refactor sweep (7) and an end-to-end check on the builder's PC (8, hitl).
- Decision: 2026-11-05, the builder's words: "Yes, start epic 14." Every unattended default above is now the builder's decision.
- Decision: 2026-11-05 (14.1), the rule is recorded as [AD-27 — In-app player](../../architecture-replay-buffer/architecture-replay-buffer.md#ad-27--in-app-player-winrt-mediaplayer-into-a-d3dimage-app-side-and-read-only). AD-1 (the App may read a finished file to play it, shared for read, write and delete), AD-2 (the D3D interop in `Platform.Desktop`), AD-9 (`mehd` at finalize) and AD-17 (a playing file never blocks a delete) point to it. R42 is in the spine's binds and capability map, and the Stack lists Windows' playback and the codec extensions. 14.2 and 14.3 build on it.
- Decision: 2026-11-05, the builder tried 14.2 and said: "the player is missing basic player controls" and "all that exists is a play button". The transport (5) comes forward, before the Track picker (4): the App lane is now tracer (2), transport (5), Track picker (4), missing codec (6). Each is stacked on the last until 14.2 merges after its Heavy run.
- Parked: mixing several Tracks. The research's cheap idea is one extra `MediaPlayer` per extra Track on a shared `MediaTimelineController`; it's untested. Otherwise it needs our own FFmpeg decode (research §6.6).
- Decision (builder, accepted 2026-11-05; was an unattended default): 2026-11-05, 8.11's hands-off run waits on 14.8, like epics 12 and 13; 8.7 to 8.10 don't wait.
- Decision (builder, accepted 2026-11-05; was an unattended default): 2026-11-05, the page layout. The video fills the page under the breadcrumb header. A control bar runs along its bottom edge, over the video: play and pause, the seek bar with elapsed and total time, the Track list as a menu button, and volume. The controls always show; hiding them while playing is parked. No `bmad-ux` pass, since it reuses the drill-in page and the looks' existing controls.
- Decision: 2026-11-05 (set check), keys: a focused control's own keys win (the Track list's and the seek bar's arrows); the page's keys apply otherwise.
- Decision: 2026-11-05 (set check), the player sits behind a small seam that 14.2 adds, so 14.5 and 14.6 test against a fake player.
- Decision: 2026-11-05 (set check), the missing-codec page is checked by a dev-instance switch that forces an unsupported decoder status. It's never on in a normal run. 14.6 adds it, and 14.8 and the Done-when check use it.
- Decision: 2026-11-05 (set check), a file with no known length (a crash-cut Recording with no `mfra`) shows the elapsed time only, with seeking off (14.5).
- Open question: is a crash-cut Recording (no trailer, so no `mfra`) seekable in the player? Entry 3 checks it, looks at writing the `mehd` when the engine's start-up trims an interrupted Recording (`InterruptedRecordings`), and records the answer here. Default: it plays from the start with an unknown length (14.5).
