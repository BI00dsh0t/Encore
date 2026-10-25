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
- Tracer path: Game Detection finds the game, which becomes the Target Game. The capture engine records its window and the Track Layout's devices into the Replay Buffer. The Save Hotkey writes a Clip into the game's folder, the Overlay and a sound confirm it, and the Library lists it, ready to open.

## References

- prd — _bmad-output/initiative-replay-buffer/prd-replay-buffer/prd-replay-buffer.md, §3 Glossary, §4 Features, §5 NFRs, §8 Success Metrics, §9 Open Questions
- addendum — _bmad-output/initiative-replay-buffer/prd-replay-buffer/addendum.md, Precedence; Inputs for architecture; Test reference
- architecture — _bmad-output/initiative-replay-buffer/architecture-replay-buffer/architecture-replay-buffer.md, Invariants & Rules AD-1–AD-22; Consistency Conventions; Stack
- architecture input — PLAN.md, §1 Architecture, §3 Video, §6 Interop & pipeline bake-off, §8 Test catalog T-01…T-25

## Notes

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
