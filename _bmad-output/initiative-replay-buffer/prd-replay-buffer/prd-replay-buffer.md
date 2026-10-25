---
title: Replay Buffer
status: final
created: 2026-10-25
updated: 2026-10-25
---

# PRD: Replay Buffer
*Working title, to be confirmed.*

## 0. Document Purpose
This PRD defines what Replay Buffer v1 must do. It is written for its builder and only user, and for the downstream BMad work: Sofia slicing it into epics and stories, `bmad-architecture`, and `bmad-build`. Terms in §3 Glossary are used exactly throughout. Features are grouped in §4, with globally numbered FRs (FR-1 to FR-39). IDs are stable, so they don't always run in order within a section. Requirements that apply across the whole app are NFR-1 to NFR-6 in §5. Defaults confirmed during finalize are listed in §10. The PRD builds on `PLAN.md` (rev 2) in the project root. The technical design, the stack comparison and the T-01…T-25 test catalog stay there and are indexed in `addendum.md`; this PRD doesn't repeat them. **Where PLAN.md and this PRD disagree about behaviour, this PRD wins.** `addendum.md` lists the known conflicts.

## 1. Vision
Replay Buffer is an always-on instant replay for Windows. Like ShadowPlay, it keeps the last few minutes of gameplay and saves a clip when you press a hotkey. Unlike ShadowPlay, it records **the audio Tracks you define, each built from any combination of your Output and Input Devices**. If you run an audio mixer with separate outputs for game, chat and music, plus a mic, the Clip finally contains everything you heard, split the way you want.

ShadowPlay worked fine before multi-output audio mixers were part of the setup. Now it captures game audio at best, with no way to include Discord or the other outputs. OBS can record multiple tracks, but it takes too much manual intervention to be an always-on replay tool.

A Clip is ready to share as soon as it's saved, because its first Track mixes everything and plays anywhere. It's just as ready to edit: each source can sit on its own Track, so you can remove, extract or rebalance any of them without missing outputs and without one device's audio bleeding over another.

## 2. Target User
The builder is the only user: one person on one Windows PC, gaming with an audio mixer that has several Output Devices (e.g. game, chat, music) plus a mic.

### 2.1 Jobs To Be Done
- **Catch the moment.** Something great happens, I press the Save Hotkey, and the last few minutes are saved without breaking my focus or costing me frames.
- **Record a whole session.** In the games where I've switched Recordings on, I start and stop a full Recording with the Record Hotkey, while the Replay Buffer keeps running. If the game or the app crashes, the file plays up to the moment of the crash.
- **Share as-is.** Send a Clip to Discord or friends, and it plays with all the audio.
- **Edit cleanly.** Open a Clip in an editor with my sources (each Output Device and my mic) on their own Tracks, so I can remove, extract or rebalance one without the others bleeding in.
- **Find and organize my Clips.** Browse my Clips and Recordings by game, by date and by Favourite in a simple UI, and open any of them in the Media Player.
- **Forget it's there.** Once it's set up, it just works every session, with nothing to start, select or fix.

### 2.2 What Makes Me Abandon It
- **FPS loss** while recording or buffering.
- **Malformed Clips or broken audio:** a file that won't open, a missing or silent Track, or audio out of sync.
- **Losing a Recording** to a crash.

## 3. Glossary
*The rest of this PRD and every downstream document use these terms exactly.*

- **Game Detection**: how the app decides that a running application is a game. Any fullscreen or borderless app counts, as does any app Windows identifies as a game, and any game you add by hand. Once a process is detected, it stays detected until it exits, even if it's minimized, alt-tabbed or switched to windowed mode. An app whose Game Profile is marked **Ignored** never counts, and neither does Replay Buffer itself.
- **Target Game**: the detected game that the Replay Buffer and Recordings capture. There is only one at a time; see FR-38.
- **Replay Buffer**: the rolling store of the Target Game's most recent gameplay (video plus every Track). It holds at most the Buffer Window. It runs while a Target Game exists, and for 30 s after the Target Game exits. It **Resets** after each saved Clip.
- **Buffer Window**: how much time the Replay Buffer keeps, and therefore the longest a Clip can be, e.g. 30 s or 5 min. It has no upper limit; Settings show the RAM or disk use it will cost. Content older than the window is discarded continuously.
- **Reset**: emptying the Replay Buffer after a Clip is saved, so the next Clip starts at the previous press and overlaps the previous Clip by at most one frame.
- **Clip**: a video file saved from the Replay Buffer by the Save Hotkey. It ends at the press, going back as far as the Buffer Window or the last Reset, whichever is more recent. Replay Buffer content that is never saved as a Clip is discarded.
- **Save Cooldown**: a debounce on the Save Hotkey. For a set time after a save (5 s to begin with; to be tuned in testing), further presses are ignored and the Overlay keeps showing the saving icon. The Replay Buffer meanwhile restarts for the next Clip as soon as possible.
- **Recording**: a full-session video file that runs from a start until a stop, independently of the Replay Buffer and at the same time as it. Recordings are **opt-in per game**: they are possible only for games whose Game Profile has Recordings switched on.
- **Monitor Recording**: a Recording of a whole monitor instead of a game's window, started only by the Monitor Record Hotkey. The Replay Buffer never captures a monitor.
- **Audio Device**: a Windows audio endpoint, discovered by the app while it runs. Either an **Output Device** (speakers, headphones, a mixer's virtual output such as "Game" or "Chat"), captured as what it plays, or an **Input Device** (mic, line-in, a mixer's virtual input).
- **Mic Device**: an Input Device that a Game Profile marks as a mic. The Mic Mute Hotkey silences only Mic Devices.
- **Track**: one named audio stream in a Clip or Recording. Each Track is any combination of one or more Audio Devices (Output, Input or both), mixed together as recorded: there is no volume or gain control, and a mix never clips. The same Audio Device can be in any number of Tracks. Each Track's name is set by the user and is written into the file. A new Track Layout starts with an **"Everything" Track** first (every selected device mixed), because many players and Discord play only the first track. You can rename, change or reorder it.
- **Track Layout**: the ordered list of Tracks, with their names and Audio Devices, plus which Tracks the Mic Mute Hotkey affects. It's set for a Game Profile, or in Global Settings.
- **Hotkey**: a global key combination. Kinds:
  - **Save Hotkey**: saves a Clip; there is exactly one.
  - **Record Hotkey**: either one toggle key, or separate **Start Recording** and **Stop Recording** keys, chosen in Settings.
  - **Monitor Record Hotkey**: the same toggle-or-separate choice, for Monitor Recordings.
  - **Mic Mute Hotkey**: silences the Mic Devices in the Tracks the Track Layout marks as affected by mute.
- **Overlay**: an always-on-top, ShadowPlay-style notification area on top of the game, separate from the game process. It shows a status dot (**hollow red** while the Replay Buffer is capturing, **solid red** while a Recording is running) and a queue of messages (Clip saving, saved, failed, disk full). It may not be visible over games in exclusive fullscreen; Feedback Sounds cover that case.
- **Feedback Sounds**: short sounds for key events (Clip saved, save failed, Recording started or stopped, disk full). Each can be switched on or off in Settings.
- **Game Profile**: settings for one game or application, keyed by its executable. A Game Profile is created automatically the first time a game is detected. It has its own settings sub-menu with the Recordings switch (off by default), the Track Layout, the Mic Devices, the Output Settings and Ignored. It inherits Global Settings and overrides only what you change.
- **Global Settings**: the default settings that apply to every game without its own value in a Game Profile.
- **Output Settings**: resolution, frame rate, bitrate and codec for Clips and Recordings. Set in Global Settings and overridable per Game Profile.
- **Output Folder**: the folder, chosen in Settings, where Clips and Recordings are saved, in a subfolder per Game Profile.
- **Main App**: the app's window, opened from the tray. It holds the Library, Settings (Global Settings and every Game Profile), the live Track meters and a status line (what's being captured, recent warnings).
- **Library**: the part of the Main App for browsing Clips and Recordings by game, date and Favourite, and acting on them.
- **Favourite**: a flag on a Clip or Recording, set in the Library. Favourites are never removed by Cleanup.
- **Cleanup**: permanent deletion of the oldest non-Favourite Clips and Recordings once the Output Folder passes a size limit or an age limit set in Settings.
- **Media Player**: the application the Library opens files with: the Windows default app for the file type.

## 4. Features

### 4.1 Replay Buffer & Clips
**Description:** When a Target Game exists, the Replay Buffer captures it, keeping the last Buffer Window of video and every Track. Pressing the Save Hotkey saves a Clip ending at the press. The Replay Buffer then Resets, so the next Clip starts fresh. When the Target Game exits, the Replay Buffer keeps running for 30 s, so I can still save the moment if the game crashed. After that, unsaved content is discarded. The Overlay and Feedback Sounds tell me when a Clip is saving, saved or failed.

**Functional Requirements:**

#### FR-1: Replay Buffer follows the Target Game
The Replay Buffer runs while a Target Game exists, and for 30 s after it exits.

**Consequences (testable):**
- The Replay Buffer starts when a game becomes the Target Game.
- Minimizing, alt-tabbing or switching the game to windowed mode doesn't stop or discard the Replay Buffer (see Game Detection).
- For 30 s after the Target Game exits or crashes, the Save Hotkey still saves a Clip of its last moments. After that, unsaved content is discarded.
- No video or audio is held while no Target Game exists and the 30 s have passed.

#### FR-2: Buffer Window with no upper limit
I can set the Buffer Window to any length; the app does not cap it.

**Consequences (testable):**
- With a 30 s Buffer Window, a Clip never holds more than 30 s.
- Very large values (e.g. 2 h) are accepted.

#### FR-36: Memory or Disk storage
In Settings I can choose whether the Replay Buffer is held in **Memory** (RAM) or on **Disk** (a folder I choose).

**Consequences (testable):**
- Both modes produce identical Clips.
- In Disk mode, the Replay Buffer's disk use stays within the estimate (FR-3) for as long as it runs.
- In Memory mode, the Replay Buffer never grows past a safe margin of the free RAM. If it reaches that margin, it keeps the newest content, so Clips get shorter than the Buffer Window, and the Main App status line says so. The game is never starved of memory (NFR-2).

#### FR-3: Storage estimate while setting the Buffer Window
While I set the Buffer Window, Settings show the RAM or disk use it will cost, based on the current Output Settings, the number of Tracks and the storage mode.

**Consequences (testable):**
- Changing the Buffer Window or any Output Setting updates the estimate right away.
- With constant-bitrate output, the estimate is within ±20% of the measured usage. For variable bitrate, the estimate shows a worst case, not ±20%.
- If the estimate exceeds the free RAM or disk space, Settings warn but still allow the value.

#### FR-4: Save a Clip up to the press
Pressing the Save Hotkey saves a Clip covering the Buffer Window, or back to the last Reset if that's shorter, ending at the press.

**Consequences (testable):**
- The Clip ends within one frame of the press, and contains nothing captured after that frame.
- The Clip's video and every Track have the same length, within one audio frame.
- Pressing the Save Hotkey when the Replay Buffer holds less than 1 s makes no Clip.

#### FR-5: Reset after save, with no gap
After a Clip is saved, the Replay Buffer Resets, and the next Clip starts at the previous press.

**Consequences (testable):**
- Two Clips saved 10 s apart: the second Clip is 10 s long and overlaps the first by at most one frame.
- Capture is never interrupted by a Reset: no frames are missing between the end of one Clip and the start of the next.
- Presses during the Save Cooldown (5 s) after a save are ignored: they create no Clip and no extra message.
- The Replay Buffer is capturing for the next Clip as soon as possible after the press, even while the Save Cooldown runs.
- The Save Cooldown is a configurable value, so it can be tuned in testing.

#### FR-6: Overlay status and save feedback
The Overlay shows a hollow red dot while the Replay Buffer is capturing, a solid red dot while a Recording is running, and a green saving icon while a Clip is being written. It queues its messages like ShadowPlay.

**Consequences (testable):**
- Where the Overlay can show (borderless and windowed games), the dot appears within 1 s of capture starting and disappears within 1 s of it stopping. It is solid whenever a Recording is running, and hollow otherwise.
- The green saving icon appears within 250 ms of the Save Hotkey press and stays for the whole Save Cooldown.
- Messages that arrive close together are queued, not overwritten.
- The Overlay never appears in captured video.
- The Overlay never minimizes, takes focus from, or interrupts a game.

#### FR-7: Save failure is visible and retryable
If a Clip fails to save, the Overlay shows a failure message with a short reason (e.g. "disk full"), and the save-failed Feedback Sound plays.

**Consequences (testable):**
- A failed save never produces a file in the Library that appears playable but isn't.
- A failed save does not Reset the Replay Buffer; pressing the Save Hotkey again retries with the same content, plus anything captured since.
- A press after the Save Cooldown works even while the previous Clip is still being written; up to two Clips can be writing at once.

#### FR-39: Feedback Sounds and status line
Key events play a Feedback Sound, and the Main App status line shows the current state and recent warnings.

**Consequences (testable):**
- In an exclusive-fullscreen game where the Overlay can't show, I can still tell from sound alone that a Clip saved or failed.
- Each Feedback Sound can be switched off in Settings.

**Out of scope:** Post-roll; more than one Save Hotkey.

### 4.2 Recordings
**Description:** For games whose Game Profile has Recordings switched on, I can start and stop a full Recording with the Record Hotkey, set up either as one toggle key or as separate Start Recording and Stop Recording keys. A Recording runs alongside the Replay Buffer: I can still save Clips during it. A Recording is one file, and stops and saves when the game exits. If the game, the app or the PC crashes, the file plays up to a point as close to the crash as possible. If the disk fills, the Recording stops, the Overlay shows a red X and the disk-full Feedback Sound plays.

**Functional Requirements:**

#### FR-8: Recordings are opt-in per game
A Recording can only be started in a game whose Game Profile has Recordings switched on. The switch is off by default.

**Consequences (testable):**
- In a game with Recordings off, the Record Hotkey does nothing, and the Overlay shows nothing.
- Switching Recordings on only allows the Record Hotkey to work; it doesn't start a Recording automatically when the game launches.

#### FR-9: Record Hotkey mode
In Settings I can choose whether the Record Hotkey is one toggle key or separate Start Recording and Stop Recording keys.

**Consequences (testable):**
- Toggle mode: the first press starts a Recording and the second stops it.
- Separate mode: Start Recording does nothing while a Recording is running, and Stop Recording does nothing while none is.

#### FR-10: Recording runs alongside the Replay Buffer
A Recording never stops, pauses or Resets the Replay Buffer, and saving a Clip never affects a Recording.

**Consequences (testable):**
- Saving a Clip during a Recording leaves no gap or glitch in the Recording.
- The Overlay shows a solid red dot for the whole time a Recording runs.

#### FR-11: Game exit ends the Recording
When the game exits during a Recording, the Recording stops and is saved as a complete file.

**Consequences (testable):**
- The file appears in the Library under that game.

#### FR-12: Crash-safe Recording
If the game, the app or Windows crashes, or the power goes off, during a Recording, the file on disk plays up to a point as close to the crash as possible.

**Consequences (testable):**
- After the engine process is killed mid-Recording, the file opens in common players, with every Track, and is in sync.
- Content lost at the end is at most 1 s. (How the engine guarantees this is an architecture acceptance test.)

#### FR-13: One file per Recording
A Recording is a single file, however long it runs.

**Consequences (testable):**
- A 4-hour Recording is one playable file.
- A game changing resolution or window mode never splits the Recording (FR-22).
- If something forces the encoder to restart (e.g. a GPU driver reset, or an HDR or codec change), the engine either continues the same file or ends the Recording cleanly and starts a new one at once. Either way the Overlay and a Feedback Sound tell me, and every file stays playable.

#### FR-14: Disk full stops the Recording
When the disk fills during a Recording, the Recording stops and the file so far stays playable. The Overlay shows a red X for disk full, and the disk-full Feedback Sound plays.

**Consequences (testable):**
- The file written before the disk filled plays, with every Track.
- The red X stays until I dismiss it (by clicking it, or in the Main App) or a Recording starts again.
- The Replay Buffer keeps running if it uses RAM. If it uses disk, it stops too, and the Overlay says so.

**Out of scope:** Splitting a Recording into several files by size or time; resuming a Recording automatically after a crash.

### 4.3 Multi-Track Audio
**Description:** While it runs, the app lists every Windows Output and Input Device. For each Game Profile (or in Global Settings), I build a Track Layout: any number of Tracks, each with a name I choose and any combination of Audio Devices. For example, "Game" = the Game output; "Comms" = the Chat output plus my mic; "Everything" = every device. Clips and Recordings carry these Tracks as recorded, with no volume changes, and mixes never clip. The Mic Mute Hotkey silences my Mic Devices only in the Tracks I've marked for that game. If a device disappears, its Tracks carry on, and its audio comes back by itself when the device returns. The Overlay shows nothing; the Main App records the dropout.

**Functional Requirements:**

#### FR-15: Live device list
The app finds the Output and Input Devices available in Windows while it runs, and keeps the list up to date.

**Consequences (testable):**
- A device added or enabled in Windows appears in the device picker without restarting the app.
- Devices with the same name are told apart (e.g. by their parent device: "Speakers (Steam Streaming Speakers)" vs "Speakers (Schiit Modi 3E)").
- Devices Windows reports as not present are hidden from the picker, unless one is already in a Track Layout. In that case it is shown, marked as unavailable.

#### FR-16: Tracks from any combination of devices
I can create any number of Tracks, and each one can contain any combination of Output and Input Devices.

**Consequences (testable):**
- One Track can combine an Output Device and an Input Device (e.g. Chat + Microphone).
- The same device can be in several Tracks, and its audio appears in each.
- A Track's audio is the sum of its devices, in sync with the video and with every other Track.
- A new Track Layout starts with an "Everything" Track in first place, which I can rename, edit or move.
- Settings warn when a Track contains a mixer output together with a physical device that mixer may feed, because that audio could be counted twice. The warning doesn't block the Track.

#### FR-17: Named Tracks
I can name each Track, and the name is visible as the track title in media players and editors that show track titles.

**Consequences (testable):**
- A Clip opened in VLC or DaVinci Resolve shows the configured Track names, in the configured order.

#### FR-18: Audio as recorded, without clipping
Audio is captured as each device delivers it; the app has no per-Track volume or gain control.

**Consequences (testable):**
- A Track with a single device matches the device's own level.
- A Track that mixes several loud devices never clips. A limiter acts only when the sum would exceed full scale, and is otherwise inaudible.

#### FR-19: Track Layout per Game Profile
Each Game Profile can have its own Track Layout. Without one, Global Settings apply.

**Consequences (testable):**
- A change to a game's Track Layout or Output Settings takes effect at the next Reset of the Replay Buffer, and for the next Recording started. A Recording in progress keeps its settings.

#### FR-20: Mic mute per Track per game
In a Game Profile, I mark which Input Devices are Mic Devices and which Tracks the Mic Mute Hotkey affects. Pressing it silences the Mic Devices in those Tracks only; pressing it again unmutes them.

**Consequences (testable):**
- With mute set to affect "Comms" but not "Mic Raw", muting silences the mic in Comms while Mic Raw keeps recording.
- Muting never silences Output Devices, or Input Devices that aren't Mic Devices (e.g. a mixer's "VOD Track" input).
- Muting affects only what Replay Buffer records, not the mic itself in Windows or other apps such as Discord.
- The Overlay shows a mic-muted icon for as long as mute is on.

#### FR-21: Devices drop out and come back
If a device in a Track becomes unavailable, its Tracks keep going without that device's audio. When the device returns, its audio resumes automatically. The Overlay shows nothing, but the Main App status line shows a warning and the dropout is logged.

**Consequences (testable):**
- Unplugging a USB mic for 10 s mid-Clip: the Clip keeps the same length on every Track, with 10 s of silence from that device only.
- Restarting the mixer app: its devices' audio resumes within 2 s of them reappearing, without restarting the Replay Buffer or Recording.
- After a session with a dropout, the Main App shows which device dropped out and when.

**Out of scope:** Capturing individual applications' audio (devices only); per-Track volume; audio effects or noise suppression.

### 4.4 Video Capture & Quality
**Description:** The Replay Buffer and Recordings capture the Target Game's window. Separately, I can start a Monitor Recording of a chosen monitor with the Monitor Record Hotkey. That's manual only; the Replay Buffer never captures a monitor. Output Settings cover resolution (common resolutions), frame rate (up to 120), bitrate and codec. They're set in Global Settings and any Game Profile can override them. The output resolution stays fixed even if the game changes resolution or window mode.

**Functional Requirements:**

#### FR-22: Capture the game's window
The Replay Buffer and game Recordings capture the Target Game's window.

**Consequences (testable):**
- Other windows and other monitors never appear in a Clip or game Recording.
- If the game changes resolution or switches between fullscreen and borderless, capture continues, scaled to the fixed output resolution with its aspect ratio kept (letterboxed if needed), with no break in the Clip or Recording.

#### FR-23: Monitor Recording by Hotkey only
I can start and stop a Monitor Recording of a monitor I choose in Settings, using the Monitor Record Hotkey (toggle or separate Start/Stop, as in FR-9).

**Consequences (testable):**
- A Monitor Recording works whether or not a game is running, and alongside the Replay Buffer and a game Recording.
- Nothing starts a Monitor Recording automatically.
- It uses the Global Settings Track Layout and Output Settings, is crash-safe and handles a full disk like any Recording (FR-12, FR-14), and is saved in a "Monitor Recordings" folder in the Output Folder.

#### FR-24: Output Settings
In Settings I can choose:
- resolution, from common resolutions (e.g. 720p, 1080p, 1440p, 2160p, and ultrawide variants such as 3440×1440);
- frame rate, up to 120 fps;
- bitrate;
- codec (H.264, HEVC or AV1, as the GPU supports).

**Consequences (testable):**
- Frame rates above 120 can't be selected.
- Codecs the GPU can't encode are not offered.
- HEVC and AV1 are labelled as possibly not playing everywhere (e.g. Discord); H.264 is the default.
- The storage estimate (FR-3) updates when any Output Setting changes.

#### FR-25: Output Settings per game
A Game Profile can override any Output Setting.

**Consequences (testable):**
- A game with a 1080p override records at 1080p, while other games use the Global Settings resolution.

### 4.5 Game Profiles
**Description:** The app treats any fullscreen or borderless app as a game. It also counts apps that Windows identifies as games, and games I add by hand. Once detected, a game stays detected until it exits. If two games run at once, the one I've been focused on for 10 s becomes the Target Game, unless the current Target Game has a Recording running. The first time the app sees a game, it creates a Game Profile with its own settings sub-menu. Anything detected wrongly can be marked Ignored. Each game's Clips and Recordings go into its own folder.

**Functional Requirements:**

#### FR-26: Game Detection
An app is treated as a game if it runs fullscreen or borderless, if Windows identifies it as a game, or if I've added it by hand. Once detected, it stays detected until its process exits.

**Consequences (testable):**
- A borderless game is detected within 5 s of its window appearing.
- A game I've added by hand is detected even when windowed.
- Minimizing, alt-tabbing or switching a detected game to windowed mode doesn't end its detection.
- Replay Buffer's own windows are never detected as a game.

#### FR-38: One Target Game at a time
When more than one detected game is running, the one that has been in the foreground with focus for 10 consecutive seconds becomes the Target Game. The exception: if the current Target Game has a Recording running, it stays the Target Game until that Recording stops.

**Consequences (testable):**
- Game A is the Target Game; I focus game B for 9 s and switch back: the Target Game stays A.
- I focus game B for 10 s: B becomes the Target Game, and its Game Profile's settings apply.
- During a Recording in game A, focusing game B for any length of time doesn't change the Target Game.
- Switching the Target Game Resets the Replay Buffer, and game A's unsaved content is discarded.

#### FR-27: Game Profile created automatically
The first time a game is detected, the app creates its Game Profile, named after the game, with every setting inherited from Global Settings.

**Consequences (testable):**
- The new Game Profile appears in Settings with its own sub-menu, without restarting the app.
- The name comes from the executable's product name; I can rename it.

#### FR-28: Ignore an app
I can mark any Game Profile as Ignored. An Ignored app never becomes the Target Game.

**Consequences (testable):**
- After I mark a fullscreen video player as Ignored, playing fullscreen video no longer starts the Replay Buffer.
- I can undo Ignored.

#### FR-29: Per-game folders and file names
Each game's Clips and Recordings are saved in a subfolder of the Output Folder named after its Game Profile.

**Consequences (testable):**
- A Clip from Valorant is saved under `<Output Folder>\Valorant\`.
- Files are named `<Game>_<yyyy-MM-dd_HH-mm-ss>.mp4`, with characters Windows doesn't allow replaced. Two files with the same name never overwrite each other.
- Renaming a Game Profile affects only files saved after the rename; existing folders are left as they are.

### 4.6 Library
**Description:** The Library in the Main App shows every Clip and Recording, organized by game, by date and by Favourite. From there I can open a file in the Media Player, mark it as a Favourite, rename or delete it, show it in Explorer, or copy it to the clipboard to paste into Discord. Cleanup keeps the Output Folder under a size or age limit by permanently deleting the oldest non-Favourite files.

**Functional Requirements:**

#### FR-30: Browse by game, date and Favourite
The Library lists all Clips and Recordings, and I can group or filter them by game, by date and by Favourite.

**Consequences (testable):**
- A newly saved Clip appears in the Library within 2 s, without a refresh.
- Files added, renamed or removed in the Output Folder outside the app are reflected in the Library.
- Each item shows a thumbnail, its name, game, date, length and size.

#### FR-31: Library actions
For any item I can: open it in the Media Player, toggle Favourite, rename it, delete it, show it in Explorer, and copy it to the clipboard.

**Consequences (testable):**
- "Open" starts the Windows default app for the file.
- A file copied to the clipboard can be pasted as an attachment into Discord.
- Delete asks for confirmation, then sends the file to the Recycle Bin.
- A file that is still being written (a Clip saving, or a Recording in progress) can't be renamed or deleted.

#### FR-32: Cleanup with Favourites protected
In Settings I can set a size limit and/or an age limit for the Output Folder. When a limit is passed, Cleanup permanently deletes the oldest non-Favourite Clips and Recordings until the folder is back within it.

**Consequences (testable):**
- Cleanup is off until a limit is set, and setting a limit asks me to confirm that deletion is permanent.
- A Favourite is never deleted, even if it's the oldest file or the folder stays over the limit.
- A file being written (a Clip saving or a Recording in progress) is never deleted.

### 4.7 Main App, Tray & Startup
**Description:** The app sits in the tray. The Main App window holds the Library, Settings, live meters for each Track (so I can check a Track Layout picks up sound before I play) and a status line. Starting with Windows is a setting. The tray icon's design is still being worked out.

**Functional Requirements:**

#### FR-33: Live Track meters
The Main App shows a live level meter for each Track in the Track Layout being viewed (Global Settings or a Game Profile).

**Consequences (testable):**
- Playing sound on a device moves the meter of every Track that contains it.
- Meters work while no game is running and nothing is being captured.

#### FR-34: Start with Windows
A setting controls whether the app starts with Windows, in the tray, without opening the Main App.

**Consequences (testable):**
- With the setting on, the app is running after sign-in, with no window shown.
- With it off, the app doesn't start with Windows.

#### FR-37: Configurable Hotkeys
I can set the key combination for every Hotkey in Settings.

**Consequences (testable):**
- If a combination can't be registered (e.g. another app already owns it), Settings show which Hotkey failed, and the other Hotkeys keep working.
- Settings refuse the same combination for two Hotkeys.
- Hotkeys work while a game has focus, and while the Main App window is closed.

#### FR-35: Tray presence
The app has a tray icon that opens the Main App and lets me quit.

**Notes:** The rest of the tray icon's behaviour (status, quick toggles) is still being worked out; see Q-2.

## 5. Cross-Cutting Non-Functional Requirements

### NFR-1: Minimal performance cost
The Replay Buffer and Recordings cost as little game performance as possible.

**Consequences (testable):**
- Provisional target: under 3% FPS loss at 1080p60 in a GPU-bound game, taken from PLAN.md §6.3. The benchmark experiments confirm or replace this number before v1 is done, and every release is measured against it (Q-1).
- The Overlay is part of what's measured: it must not push a game out of its fastest presentation path.

### NFR-2: Never crash or destabilize the game
Nothing the app does may crash, freeze or hang a game. The app runs in its own processes and never loads code into a game process.

**Consequences (testable):**
- If the app crashes, the game keeps running unaffected.
- The app never starves the game of memory (FR-36) or of the GPU. GPU scheduling choices are made in the architecture and verified in the benchmark.

### NFR-3: Anti-cheat friendly
Clashes with anti-cheat software are kept to a minimum. Capture uses only standard Windows capture and audio interfaces. No injection, no hooking, nothing drawn inside the game process, and no low-level keyboard hooks.

**Consequences (testable):**
- The app runs alongside common anti-cheat-protected games (e.g. Valorant/Vanguard, EAC and BattlEye titles) without warnings, kicks or bans. The games to verify against are chosen during testing (Q-3).

### NFR-4: File integrity
Every H.264 Clip and Recording opens in common players and editors (Discord, Chrome, VLC, Windows Media Player, DaVinci Resolve), with every Track present, named and in sync. HEVC and AV1 files carry the same guarantees in players that support those codecs.

**Consequences (testable):**
- Audio and video drift is less than one frame over a 60-minute Recording.

### NFR-5: Consistency over long sessions
The app keeps producing correct Clips across an 8-hour session, with no memory growth. The Hotkeys keep working when the Main App window is closed or crashes.

**Consequences (testable):**
- If the engine (the process that captures and saves) crashes, it restarts automatically, the Replay Buffer resumes, and the Main App status line shows that it happened.

### NFR-6: Hands-off operation
After first setup, nothing has to be done per session.

**Consequences (testable):**
- The Replay Buffer starts by itself when a game is detected, and Game Profiles are created automatically.
- Settings and device choices survive reboots, app restarts and mixer restarts with nothing to re-select.

## 6. Non-Goals (Explicit)
- Capturing individual applications' audio: v1 captures Audio Devices only.
- Injecting into or hooking game processes (see NFR-3).
- Editing video in the app: editing happens in your own editor.
- Uploading, sharing services or live streaming.
- PCs with more than one GPU (laptops with hybrid graphics, or a desktop using its integrated GPU for a display).
- Distributing the app to anyone else: it is a personal tool.

## 7. MVP Scope

### 7.1 In Scope
- Every feature in §4 (4.1–4.7, FR-1 to FR-39) and every NFR in §5.
- Epics are ordered so that the core Clip pipeline (4.1, 4.3, 4.4) works end-to-end first.

### 7.2 Out of Scope for MVP
- Nothing in §4 is deferred. The Non-Goals (§6) and each feature's "Out of scope" lines are not planned for any version yet.

## 8. Success Metrics
Success: **the app consistently produces Clips without costing much performance, never crashes a game, and stays clear of anti-cheat**, so it fully replaces ShadowPlay.

**Primary**
- **SM-1: Clip success rate.** Every press of the Save Hotkey outside the Save Cooldown produces a playable Clip with every Track. Target: 100% over a month of use. Validates FR-4, FR-7, NFR-4.
- **SM-2: No game crashes.** Zero game crashes or freezes caused by the app. Validates NFR-2.

**Secondary**
- **SM-3: Performance cost.** FPS loss within the NFR-1 target in the games you play. Validates NFR-1.
- **SM-4: Anti-cheat.** Zero anti-cheat warnings, kicks or bans. Validates NFR-3.

**Counter-metrics (do not optimize)**
- **SM-C1: Lowest possible overhead at the cost of integrity.** Don't trade Clip integrity or crash safety (FR-12) for small FPS gains. Counterbalances SM-3.
- **SM-C2: Smallest file size.** Don't drop quality or Tracks to shrink files; the storage estimate (FR-3) handles that trade-off. Counterbalances FR-24.

## 9. Open Questions
1. **Q-1: The FPS-loss target** for NFR-1. Confirm or replace the provisional 3% from the benchmark experiments.
2. **Q-2: The tray icon's behaviour** beyond open and quit: status display, quick toggles (FR-35).
3. **Q-3: Which anti-cheat-protected games to verify against** (NFR-3).
4. **Q-4: How much the Overlay can do in exclusive fullscreen.** Feedback Sounds (FR-39) cover the case where it can't show. Measure which of your games run in exclusive fullscreen, and whether the Overlay costs FPS in borderless (NFR-1).

## 10. Confirmed Defaults
*Inferred during drafting and confirmed by the user at finalize; listed so they're easy to revisit.*
- §4.1 FR-3: for variable bitrate, the storage estimate shows a worst case instead of ±20%.
- §4.1 FR-4: a press when the Replay Buffer holds less than 1 s makes no Clip.
- §4.3 FR-18: a limiter acts only when a mix would exceed full scale.
- §4.4 FR-23: Monitor Recording uses the Global Settings Track Layout and Output Settings, is crash-safe like any Recording, and is saved in a "Monitor Recordings" folder.
- §4.5 FR-26: a borderless game is detected within 5 s.
- §4.5 FR-38: switching the Target Game Resets the Replay Buffer and discards the previous game's unsaved content.
- §4.5 FR-27: a Game Profile is named from the executable's product name, and can be renamed.
- §4.5 FR-29: files are named `<Game>_<yyyy-MM-dd_HH-mm-ss>.mp4`, with characters Windows doesn't allow replaced and no overwrites.
- §4.5 FR-29: renaming a Game Profile affects only new files; existing folders aren't moved.
- §4.6 FR-30: a new Clip appears in the Library within 2 s.
- §4.6 FR-30: each Library item shows a thumbnail, name, game, date, length and size.
- §4.6 FR-31: Delete in the Library sends files to the Recycle Bin after a confirmation.
- §5 NFR-1: 3% FPS loss is the provisional target.
- §5 NFR-3: the anti-cheat games to verify against are chosen during testing.
