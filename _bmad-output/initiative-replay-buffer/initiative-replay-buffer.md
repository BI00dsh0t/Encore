---
type: initiative
title: "Replay Buffer v1 replaces ShadowPlay with multi-device audio Tracks"
parent: none
sources:
  - _bmad-output/initiative-replay-buffer/prd-replay-buffer/prd-replay-buffer.md
  - PLAN.md
companions:
  - _bmad-output/initiative-replay-buffer/prd-replay-buffer/addendum.md
  - _bmad-output/initiative-replay-buffer/architecture-replay-buffer/architecture-replay-buffer.md
after: []
risk: high
---

# Replay Buffer v1 replaces ShadowPlay with multi-device audio Tracks

## Description

A Windows app (.NET 10) that keeps a rolling Replay Buffer of the Target Game and saves a Clip on a hotkey, plus opt-in full Recordings. Every Clip and Recording carries the user's own Tracks, each built from any combination of Windows Output and Input Devices, so a multi-output mixer setup (BEACN Mix Create) finally lands in clips intact. It runs hands-off, never touches the game process, and organizes everything in a Library. The requirements come from the final PRD. PLAN.md supplies the technical design, and the PRD wins wherever the two disagree on behaviour.

## Outcome

For its builder and only user, ShadowPlay is replaced by an always-on replay whose Clips arrive playable, with every Track, at almost no FPS cost and with no anti-cheat trouble. The success signals are PRD SM-1 to SM-4.

## Requirements

- R1 (FR-1): The Replay Buffer runs only while a Target Game exists and for 30 s after it exits. Shown by a Clip saved within 30 s of the game crashing, and by no capture once those 30 s have passed.
- R2 (FR-2, FR-3): The Buffer Window can be set with no upper limit. Settings show the RAM or disk estimate live: within ±20% for constant bitrate, and a worst case for variable bitrate. They warn, but allow the value, when it exceeds free space.
- R3 (FR-36): The Replay Buffer can be held in Memory or on Disk, and both produce identical Clips. Memory mode never grows past a safe margin of free RAM: it keeps the newest content, and the status line says so.
- R4 (FR-4): The Save Hotkey saves a Clip covering the Buffer Window, or back to the last Reset if that's shorter, ending within one frame of the press. Every Track has the same length as the video, within one audio frame. With less than 1 s buffered, no Clip is made.
- R5 (FR-5): After each save the Replay Buffer Resets. Consecutive Clips have no gap and overlap by at most one frame, and capture never stops. Presses during the Save Cooldown (configurable, 5 s to start) are ignored.
- R6 (FR-6): The Overlay shows a hollow red dot while the Replay Buffer captures and a solid one during a Recording, a green saving icon within 250 ms of the press that stays through the Save Cooldown, and queued messages. It never appears in captured video, and never takes focus from or minimizes a game.
- R7 (FR-7): A failed save shows its reason in the Overlay and plays a sound, and leaves no file that only looks playable. It doesn't Reset the Replay Buffer, so the next press retries. Up to two Clips can be writing at once.
- R8 (FR-39): Feedback Sounds play for: Clip saved, save failed, Recording started or stopped, and disk full. Each can be switched off. The Main App status line shows state and recent warnings, so a Clip's outcome is knowable by sound alone in exclusive fullscreen.
- R9 (FR-8, FR-9): Recordings are possible only in games whose Game Profile has them switched on. The switch is off by default, the Record Hotkey does nothing while it's off, and switching it on never auto-starts a Recording. The Record Hotkey is one toggle key or separate Start Recording / Stop Recording keys.
- R10 (FR-10, FR-11): A Recording and the Replay Buffer run side by side, and neither affects the other; a Clip saved mid-Recording leaves no glitch. When the game exits, the Recording stops and is saved complete, and appears in the Library.
- R11 (FR-12): After a crash of the game, the app or Windows, or a power loss, the Recording plays with every Track in sync, losing at most 1 s at the end.
- R12 (FR-13): A Recording is one file, however long. A resolution or window-mode change never splits it. If an encoder restart is forced, the engine either continues the same file or ends it cleanly and starts a new Recording at once, and tells the user.
- R13 (FR-14): When the disk fills, the Recording stops and the file stays playable. A red X shows until dismissed or the next Recording, and the disk-full sound plays. A Memory buffer keeps running; a Disk buffer stops, with a notice.
- R14 (FR-15): The app lists the Windows Output and Input Devices live, with no restart needed. Devices with the same name are told apart by their parent device. Devices that aren't present are hidden, unless one is already configured; then it's shown as unavailable.
- R15 (FR-16, FR-17): A Track Layout holds any number of Tracks, each with a user-set name and any combination of Audio Devices. A device can be in several Tracks, and all Tracks stay in sync. New layouts start with an "Everything" Track first. Track names show as track titles, in order, in VLC and DaVinci Resolve. Possible double counting gets a warning, but isn't blocked.
- R16 (FR-18): Audio is captured as recorded, with no gain control: a single-device Track matches the device's own level, and a mix of several devices never clips.
- R17 (FR-19, FR-25): A Game Profile can override the Track Layout and Output Settings. Changes take effect at the next Reset, or with the next Recording; a Recording in progress keeps its settings.
- R18 (FR-20): Per Game Profile, the user marks Mic Devices and the Tracks the Mic Mute Hotkey affects. Mute silences only Mic Devices, only in those Tracks, and only in what the app records. The Overlay shows a mic-muted icon while it's on.
- R19 (FR-21): A device dropout leaves silence from that device only; its audio resumes within 2 s of the device returning, with no capture restart. The Overlay stays silent, and the Main App warns and logs which device dropped out, and when.
- R20 (FR-22): Capture covers only the Target Game's window. A change in its resolution or window mode is scaled to the fixed output resolution, keeping its aspect ratio, with no break.
- R21 (FR-23): A Monitor Recording of a chosen monitor is started only by the Monitor Record Hotkey. It works with or without a game running, alongside the Replay Buffer and game Recordings, uses the Global Settings, is crash-safe and handles a full disk like any Recording, and is saved in "Monitor Recordings".
- R22 (FR-24): Output Settings offer common resolutions including ultrawide, frame rates up to 120 fps, bitrate, and only the codecs the GPU can encode, with H.264 as the default. HEVC and AV1 are labelled as possibly not playing everywhere.
- R23 (FR-26): A game is detected within 5 s if it runs fullscreen or borderless, if Windows identifies it as a game, or if it was added by hand. It stays detected until its process exits, and the app's own windows are never detected.
- R24 (FR-38): With several games running, the one in focus for 10 consecutive seconds becomes the Target Game, unless the current Target Game has a Recording running. A switch Resets the Replay Buffer.
- R25 (FR-27, FR-28): A Game Profile is created on first detection, named from the executable's product name, renameable, and inheriting Global Settings. A profile marked Ignored never becomes the Target Game, and Ignored can be undone.
- R26 (FR-29): Files go into per-game folders under the Output Folder, named `<Game>_<yyyy-MM-dd_HH-mm-ss>.mp4`. Characters Windows doesn't allow are replaced, and no file is ever overwritten. Renaming a profile affects new files only.
- R27 (FR-30): The Library lists every Clip and Recording, grouped and filtered by game, date and Favourite. New Clips appear within 2 s, changes made outside the app are reflected, and each item shows a thumbnail, name, game, date, length and size.
- R28 (FR-31): Library actions: open in the Media Player, Favourite, rename, delete (which confirms, then uses the Recycle Bin), show in Explorer, and copy to the clipboard, pasteable into Discord. Files still being written can't be renamed or deleted.
- R29 (FR-32): Cleanup enforces a size and/or age limit on the Output Folder. It's off until a limit is set, and setting one asks for confirmation that deletion is permanent. It never deletes Favourites or files being written.
- R30 (FR-33): The Main App shows a live level meter per Track for the layout being viewed. The meters work with nothing being captured.
- R31 (FR-34, FR-35): A setting starts the app with Windows, in the tray, with no window. The tray icon opens the Main App and quits.
- R32 (FR-37): Every Hotkey's key combination is configurable. A failed registration is shown and the other Hotkeys keep working, duplicate combinations are refused, and Hotkeys work while a game has focus and with the Main App closed.
- R33 (NFR-4): Every H.264 Clip and Recording opens in Discord, Chrome, VLC, Windows Media Player and DaVinci Resolve, with every Track present, named and in sync. Drift is under one frame over 60 minutes.
- R34 (NFR-5): Over an 8-hour session, Clips stay correct with no memory growth, and the Hotkeys survive the Main App closing or crashing. A crashed engine restarts by itself, the Replay Buffer resumes, and the status line reports it.
- R35 (NFR-6): After first setup, nothing needs doing per session. Settings and device choices survive reboots, app restarts and mixer restarts.
- R36 (NFR-1): FPS loss stays under the provisional 3% at 1080p60 in a GPU-bound game, Overlay included, measured by benchmark. The benchmark confirms or replaces the target before v1 is done (Q-1).
- R37 (builder decision 2026-11-04; gap): The Main App offers two looks, chosen in Settings: Fluent (the default, following the Windows light or dark mode and accent colour) and Gaming dark. Switching applies at once to every window, with no restart, and is kept across restarts. Every surface the app shows, the tray menu, its dialogs and the Overlay's messages included, follows the chosen look.
- R39 (builder decision 2026-11-05; gap): A Monitor Recording records every attached monitor or a chosen set of them, as one video with every Track, each monitor placed as Windows arranges them and the whole scaled to fit, at no more encoder cost than one monitor. The choice survives reboots and replugs, a removed monitor leaves its area black while the Recording continues, and the old single-monitor choice keeps working.
- R38 (builder decision 2026-11-05; gap): The Main App works the way a gamer who isn't a developer expects. The Library selects several Clips and Recordings with the usual Windows mouse and keys, acts on them together (Favourite, copy, show in Explorer, and delete with one confirmation), and sorts and searches them; files being written are never changed, Favourites are never deleted, and deletes still confirm. Audio tracks are set up by picking sounds per track by name, with presets and a live level beside each choice, and a fresh install records the PC's sound and the default microphone with no setup. Games are listed with a page per game that shows plainly which settings it takes from the main ones and which it changes. Settings pages are named for what they control, every folder is chosen with a folder picker, and no text the user reads uses an internal term. An empty Library says how to save the first Clip.
- R40 (builder decision 2026-11-05; gap): Output Settings can record at the source's own resolution: Clips and game Recordings at the Target Game window's size, a Monitor Recording at the screen's size, with no scaling beyond the codec's largest frame side. Frame rate, format and bitrate still come from the Output Settings. It can be chosen globally and per game, and a fixed resolution keeps working as today. It is the exception to R20's fixed output resolution.
- R41 (builder decision 2026-11-05; gap): A Global setting decides whether a game found for the first time is captured; it is on by default, as today. With it off, a newly detected game's Game Profile starts Ignored ("Capture this game" off) and the user is told once; a game added by hand is always captured, and existing Game Profiles never change.
- R42 (builder decision 2026-11-05; gap): The Main App plays a Clip, Recording or Monitor Recording itself, opened from the Library, with Windows' own media player. The user hears one Track at a time, starting with the first, and can switch Track mid-play by name without restarting playback. Playback runs to the file's last frame, seeks, and follows both looks, with H.264, HEVC and AV1 up to a Monitor Recording's full size. When Windows can't decode the video, it says so in plain words and still offers the default player.

## Constraints

- (NFR-2) Never crash, freeze or starve a game: the app runs in its own processes, never loads code into a game process, and bounds its memory and GPU use.
- (NFR-3) Anti-cheat friendly: use only standard Windows capture and audio interfaces. No injection, no hooking, nothing drawn inside the game process, and no low-level keyboard hooks.
- (PRD §6) Audio Devices only, with no per-application audio capture. One GPU. Windows only. A personal tool that is never distributed.
- (PLAN.md §1) Built on .NET 10, with native interop where needed. The engine is a separate process from the Main App, so Hotkeys and capture survive a crash of the Main App.
- (PRD §0) Where PLAN.md and the PRD disagree on behaviour, the PRD wins. The overrides are listed in the addendum's Precedence section.

## Done when

- Every Save Hotkey press outside the Save Cooldown has produced a playable Clip with every Track, over a month of daily use (SM-1).
- Zero game crashes or freezes caused by the app over that month (SM-2).
- FPS loss is within the NFR-1 target in the games played, confirmed by benchmark (SM-3).
- Zero anti-cheat warnings, kicks or bans in the verified games (SM-4).
- The app runs on the builder's PC, starting with Windows, and ShadowPlay is uninstalled.

## Boundaries

- Not in v1, and not planned: per-application audio, injection or hooking, in-app video editing, uploading or streaming, multiple GPUs, distribution (PRD §6), Post-roll, more than one Save Hotkey, and splitting a Recording by size or time.
- Touch point: BEACN Mix Create app — its virtual Output and Input Devices, consumed only; owner: epic-multi-track-audio
- Touch point: Windows default app for .mp4 and Discord's paste — consumed by Library actions; owner: epic-library-cleanup
- Touch point: the HEVC and AV1 Video Extensions from the Microsoft Store — consumed by the in-app player's decoding; owner: epic-in-app-player
- Tracer path: Game Detection finds the game, which becomes the Target Game. The capture engine records its window and the Track Layout's devices into the Replay Buffer. The Save Hotkey writes a Clip into the game's folder, the Overlay and a sound confirm it, and the Library lists it, ready to open.

## References

- prd — _bmad-output/initiative-replay-buffer/prd-replay-buffer/prd-replay-buffer.md, §3 Glossary, §4 Features, §5 NFRs, §8 Success Metrics, §9 Open Questions
- addendum — _bmad-output/initiative-replay-buffer/prd-replay-buffer/addendum.md, Precedence; Inputs for architecture; Test reference
- architecture — _bmad-output/initiative-replay-buffer/architecture-replay-buffer/architecture-replay-buffer.md, Invariants & Rules AD-1–AD-25; Consistency Conventions; Stack
- architecture input — PLAN.md, §1 Architecture, §3 Video, §6 Interop & pipeline bake-off, §8 Test catalog T-01…T-25

## Notes

- Feature request: 2026-11-05, [Create clips with precise waveform trimming and audio export](feature-requests/clip-creator/clip-creator.md), with the builder's source input and audio-mode decision alongside it. Recorded for future planning, outside the current build order; the existing no-editing boundary is a scheduling/refinement conflict to reconcile rather than an instruction to discard the request.
- Open question: the FPS-loss target. The provisional 3% is confirmed or replaced by the first benchmark (PRD Q-1). The builder decides.
- Open question: the tray icon's behaviour beyond open and quit (PRD Q-2). The builder decides once the tray design is ready.
- Open question: which anti-cheat-protected games to verify against (PRD Q-3). Decided at test planning.
- Open question: what the Overlay can do over exclusive-fullscreen games, and whether it costs FPS in borderless (PRD Q-4). Settled after measurement.
- Decision: 2026-10-25, the PRD wins over PLAN.md on behaviour. Confirmed at PRD finalize.
- Decision: 2026-10-25, split into 8 epics, one per capability delivered together. The engine baseline goes first, as the platform baseline and the riskiest path (performance, anti-cheat, A/V sync). The Overlay and hardening are separate epics. Approved by the user.
- Decision: 2026-10-25, the first demo is epic 1's tracer bullet: a game window → fragmented MP4 with two audio devices on independent clocks (PLAN.md milestone 1).
- Decision: 2026-10-25, the dependency check moved R17 from epic 3 to epic 4 (it needs epic 2's Track Layouts), and made epic 6 also wait on epic 5 (red X and sounds).
- Decision: 2026-10-25, ownership fixes from the tree check, approved by the user:
  - R1 moves to epic 4.
  - Shared requirements, each epic delivering its own part: R33 (epics 1, 2, 8), R36 (epics 1, 5, 8), R18 (epic 2 Global, epic 4 per-Game-Profile).
  - Epic 1 owns Hotkey registration, the settings override shape and the capture-session API.
  - Epic 6's Recordings raise the "file being written" signal for epic 7.
  - Epics 2 and 8 emit warnings that epic 5's status line displays.
  - PLAN.md §7.2 and §7.4 go to epic 1, and §7.1 to epics 5 and 6.
- Decision: 2026-10-25, run `bmad-architecture` next, to settle the shared decisions below.
- Decision: 2026-10-25, the shared cross-epic decisions live in the architecture spine (adopted companion). AD-3 covers IPC, AD-4 settings, AD-14 the interop stack, AD-9 containers, AD-19 the capture sessions and AD-22 the Contracts. Epic 1 seeds AD-22's skeleton before epics 2 and 3 start.
- Decision: 2026-11-04, the builder added epic 9 "Modern UI" (epic-modern-ui, R37): a `bmad-ux` design pass, then stories per UI area. Both looks become selectable themes, Fluent by default. It runs after epics 6 and 7 and before epic 8's final conformance run (8.7), so 8.8, 8.9 and 8.11 wait too. Architecture AD-23 records the theming rule.
- Decision: 2026-11-05, the builder asked for another UX pass: "The library is missing basic list functionality, like multi select" and "The track layout is convoluted and makes no sense to an end user". Epic 10 "UX pass 2" (epic-ux-pass-2, R38) runs after epic 9 and before 8.11, so the hands-off run uses the final UI; 8.7 to 8.9 measure the engine and don't wait. It overrides epic 7's "actions act on one item at a time (no multi-select in v1)". The builder's later points (Game Profiles as a list with a page per game, the Recordings page, the Replay Buffer page's name, the Ring Folder and a folder picker) are folded in as R38's Games, settings-pages and plain-words parts. Architecture AD-24 records the App-side rules.
- Decision: 2026-11-05, the builder asked: "monitor recording needs to allow selecting which monitors to record, or capture all monitors." Epic 11 "Multi-monitor recording" (epic-multi-monitor-recording, R39) composites the chosen monitors into one video on one encoder chain (option (b) of its design note; per-monitor files are parked). It runs after 10.9 and before 8.11. Architecture AD-25 records the rule and settles AD-7's open Monitor-device question.
- Decision: 2026-11-05, the builder asked: "For output, we want to be able to specify recording in source quality", then: "We don't want to match the source framerate. Just the resolution. Output fps should always be determined by the output settings." Epic 12 "Source-resolution output" (epic-source-quality-output, R40) adds a Source resolution to Video quality. It runs after 11.5 and before 8.11, so the hands-off run uses it. Its unattended defaults (bitrate unchanged, no codec switch for games, the size follows a held change outside a Recording) are in the epic's Notes for the builder; AD-7 needs a spine edit before 12.2.
- Decision: 2026-11-05, the builder asked: "add a general setting for allowing the user to specify if they want new games to be captured by default. So if a new game is detected, its Capture this game setting will be on or off by default." Epic 13 "New games captured by default, or not" (epic-new-game-capture-default, R41) is one story: a Global `captureNewGames` key, the engine creating new profiles Ignored when it's off, and a switch on the General page. It has no prerequisite and runs before 8.11. Its unattended defaults are in the epic's Notes for the builder.
- Decision: 2026-11-05, the builder asked: "add a new feature request. first research easiest approach for an in app media player that also allows us to switch audio tracks." Research (`research/in-app-player.md`) compared six players. Then, of its recommendation: "use the windows media player", meaning WinRT `Windows.Media.Playback.MediaPlayer` drawn through its frame server into a WPF `D3DImage`. Epic 14 "In-app player" (epic-in-app-player, R42) adds a player page to the Library. It needs AD-27, which amends AD-1 (the App may read a finished media file to play it) and AD-9 (a Recording's `moov` gets an `mehd` so Windows plays its last fragment). It runs before 8.11, so the hands-off run uses it. Its unattended defaults (where it lives, one Track at a time, one volume, the controls, the missing-codec message, no trimming) are in the epic's Notes for the builder.
