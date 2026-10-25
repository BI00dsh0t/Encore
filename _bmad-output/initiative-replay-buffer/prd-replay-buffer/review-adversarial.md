# Adversarial Review: PRD Replay Buffer

- **Artifact:** `prd-replay-buffer.md` (draft, 2026-10-25), with `addendum.md` and the `PLAN.md` (rev 2) sections it points to
- **Method:** `skill:bmad-review lenses=adversarial,edge-cases`. Content class: docs that define behavior. The adversarial lens (`[ADV]`) hunts for what's wrong and what's missing. The edge-case lens (`[EDGE]`) walks the state transitions (game detected or lost, save, reset, cooldown, failure, device change, crash, disk full) and lists the ones the PRD doesn't handle. Severity tiers were added because the caller asked for them; the lenses don't rank on their own. When both lenses hit the same problem, the finding carries both tags.
- **Verdict:** Don't hand this to slicing yet. The core save semantics (exact cut, no overlap, no gap) can't be met with the GOP-based encoder that PLAN.md commits to. Game Detection based on window state can throw away the buffer in the middle of a session. The one-file Recording rule contradicts the ring-generation design that the PRD itself defers to. And every piece of user feedback goes through an Overlay whose feasibility is still an open question (Q-4).

**Counts:** Critical 6 · High 14 · Medium 15 · Low 10 · Total 45

---

## Critical

### C-1 Game Detection is non-sticky, so a window-mode change or minimize discards the session `[ADV][EDGE]`
- **Location:** §3 Glossary "Game Detection"; FR-1; FR-26; FR-22; FR-11
- **Problem:** A game counts as detected only while it is "fullscreen or borderless". FR-1 stops the Replay Buffer and discards its content "when no detected game is running". Alt-tabbing out of exclusive fullscreen usually minimizes the game, and switching the game to windowed mode in its own settings menu makes it a plain window. In both cases detection is lost. FR-22 only promises continuity for a switch between fullscreen and borderless, and FR-11 ends a Recording whenever the game "exits", a term the PRD never defines.
- **Consequence:** An alt-tab to Discord throws away the unsaved buffer, and may end or split a Recording that should be one file (FR-13). This is exactly the "losing a recording" abandonment trigger in §2.2.
- **Fix:** Detection should only be the trigger. Once a process is detected, it stays the session's game until that process exits. Window-state changes such as minimize, windowed mode or loss of focus never stop the Replay Buffer or a Recording. Define "game exit" as process exit, after a short grace period so that launcher-to-game hand-offs survive. Add consequences for minimize, alt-tab and fullscreen ↔ windowed.

### C-2 "Ends exactly at the press" plus "no overlap" plus "no gap" is impossible with a 1–2 s GOP `[ADV][EDGE]`
- **Location:** FR-4; FR-5 (consequences 1–2); §3 "Reset", "Clip"; addendum → PLAN §0 / §3 ("pin slabs … from prior keyframe", GOP of about 1–2 s)
- **Problem:** An encoded clip has to start on a keyframe. PLAN.md cuts saves "from the prior keyframe", so Clip N+1 would start up to 2 s before Clip N's press. That is overlap, and it breaks FR-5's "shares no frames". Cutting after the press instead leaves a gap, which breaks "no frames are missing". Audio is framed too: an AAC frame is 1024 samples, about 21.3 ms at 48 kHz, so "exactly" and "same length on every Track" (FR-4) can't hold at sample level either.
- **Consequence:** The test in FR-5 ("second Clip is 10 s long and shares no frames") fails by design. The architecture will either quietly break the PRD or push for a re-encode on save, which costs GPU time and conflicts with NFR-1.
- **Fix:** Make it a requirement that the encoder forces an IDR frame at the press timestamp, so the press becomes both the last frame of Clip N and a clean start for Clip N+1. State tolerances, for example video exact to the frame and each audio Track within one AAC frame (≤ 21.3 ms) of the video length. Rewrite FR-4/FR-5 against those tolerances.

### C-3 "One file per Recording" and "no break" contradict the ring-generation design the PRD defers to `[ADV]`
- **Location:** FR-13; FR-22 consequence 2; FR-25; addendum → PLAN §0 row 3, §3 "Ring generations", T-01, T-09
- **Problem:** PLAN.md says an encoder restart (codec, HDR or bit-depth change, or GPU device removed/TDR in T-01) starts a new generation, and that "a save that spans generations writes one file per generation". The PRD says a Recording is "always a single file, however long it runs" and that capture continues "with no break". It never mentions HDR, TDR or a settings change in the middle of a Recording.
- **Consequence:** Architecture inherits two contradictory sources of truth. A 4-hour Recording that crosses an HDR toggle or a driver reset either breaks FR-13 or needs a container-level discontinuity (a new `stsd` sample entry mid-fMP4), which many players handle badly. That in turn breaks NFR-4.
- **Fix:** Pick one rule and write it down. Option A: FR-13 holds, and the encoder is configured to never change format mid-session (HDR is tone-mapped to SDR at capture, Output Settings changes are deferred to the next session, and a TDR is recovered with the same encoder parameters). Option B: FR-13 gets an explicit exception list. Add T-01, T-08 and T-09 as consequences of FR-13 and FR-22.

### C-4 Every feedback requirement depends on an Overlay whose feasibility is unresolved (Q-4) `[ADV][EDGE]`
- **Location:** FR-6, FR-7, FR-8, FR-14, FR-20; §3 "Overlay"; §9 Q-4; NFR-1; NFR-3
- **Problem:** Save state, failure, the disk-full red X, mic mute and capture status are shown only in the Overlay. FR-6 sets testable timings (≤ 250 ms, ≤ 1 s) for a window that, per Q-4, may not render at all over exclusive fullscreen. Two more problems go unstated. A topmost window over a flip-model borderless game can knock the game out of independent flip into DWM composition, which adds latency and frame cost and works against NFR-1. Showing or focusing a window over exclusive fullscreen can force a mode switch or minimize the game, which then triggers C-1.
- **Consequence:** In exclusive fullscreen the user gets no feedback at all. A disk-full stop (FR-14) or a failed save (FR-7) goes unseen, and this is the "losing a recording" abandonment trigger. In borderless mode, the Overlay could be the main source of FPS loss.
- **Fix:** Resolve Q-4 before slicing. Make the Overlay a no-activate, click-through layered window (`WS_EX_NOACTIVATE | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW`) that is shown only for messages, and drop the always-visible dot by default. Add a sound cue as a requirement for every Overlay message. Add an NFR-1 consequence: the Overlay must not move a borderless game out of independent flip (check with PresentMon). State that a disk-full stop or a failed save is also reported in the tray and the Main App after the fact.

### C-5 JTBD promises automatic Recordings; FR-8 forbids them `[ADV]`
- **Location:** §2.1 "Record a whole session" ("or have it happen automatically for the games I choose"); FR-8 consequence 2 ("doesn't start a Recording automatically when the game launches")
- **Problem:** This is a direct contradiction. The user's stated job includes auto-recording for chosen games, and the FR explicitly rules it out. No "Out of scope" line or Non-Goal acknowledges that it was dropped.
- **Consequence:** Slicing will follow the FR and quietly remove a job the user asked for. Or the builder implements the JTBD and fails FR-8's test.
- **Fix:** Add a per-profile setting "Auto-start Recording when the game is detected" (off by default), with its own consequences, or remove the clause from the JTBD and record the decision.

### C-6 An unbounded Buffer Window in Memory mode lets the app freeze the game it must never freeze `[ADV][EDGE]`
- **Location:** FR-2; FR-3 consequence 3 ("warn but still allow"); FR-36; NFR-2; NFR-5
- **Problem:** A 2-hour buffer at 1440p and 50 Mbps is about 45 GB, and the PRD accepts it in RAM with only a warning shown at Settings time. Free RAM at Settings time says nothing about free RAM once the game has loaded. The PRD has no runtime guard: nothing about committed memory, paging, or what happens when allocation fails. On top of that, a save holds its content while the new window fills, so peak use can reach about 2× the window (see H-13).
- **Consequence:** System-wide paging stalls or an OOM. The game hitches or hangs (breaking NFR-2), or the engine crashes and loses the buffer (breaking SM-1). The PRD makes this a supported configuration.
- **Fix:** Keep "no upper limit" for Disk mode only. In Memory mode, add a runtime ceiling (for example a percentage of physical RAM, configurable) and a defined behavior when it is reached: shrink the effective window and show a message, never page the game out. Budget pinned memory during saves in the FR-3 estimate. Add a consequence: "With the Memory ceiling reached, the game's frame time does not regress beyond the NFR-1 target."

---

## High

### H-1 The DXGI Desktop Duplication fallback contradicts window-only capture `[ADV]`
- **Location:** FR-22 consequence 1; §3 "Monitor Recording" ("The Replay Buffer never captures a monitor"); addendum → PLAN §3 ("WGC with a DXGI Desktop Duplication fallback")
- **Problem:** DDA captures a whole output: other windows, notifications, the Overlay, and the Main App. If WGC fails (exclusive fullscreen, protected content, older APIs), the fallback breaks FR-22 and the Glossary.
- **Consequence:** Private content (Discord DMs, notifications on the game monitor) shows up in Clips, or the architecture drops the fallback and some games produce no capture at all.
- **Fix:** State which wins. Either the fallback is allowed and FR-22 is reworded ("other windows may appear only when the DDA fallback is active, and the Overlay says so"), or the fallback is removed and the "WGC can't capture this game" case gets a defined failure message.

### H-2 NFR-4's player list conflicts with the offered codecs and with fragmented MP4 `[ADV]`
- **Location:** NFR-4; FR-24 (codec H.264/HEVC/AV1); §2.1 "Share as-is"; addendum → PLAN §0 row 2 (fragmented MP4), PLAN §7 "Facts still to verify"
- **Problem:** HEVC playback in Windows' built-in player depends on an optional, paid codec extension. HEVC and AV1 playback in Discord and Chrome depends on the client and on hardware decode support. Several editors are known to handle fragmented MP4 badly, and PLAN.md still lists multi-track fMP4 playback as unverified. NFR-4 says "Every Clip and Recording opens in … Discord, Chrome, … Windows Media Player, DaVinci Resolve" for every codec choice.
- **Consequence:** NFR-4 fails as soon as the user picks HEVC or AV1, so the requirement is unsatisfiable as written. "Share as-is" quietly turns into "share as-is if H.264".
- **Fix:** Scope NFR-4 per codec (the full matrix applies to H.264 only; HEVC and AV1 list the players they're guaranteed in), or show a compatibility note next to the codec picker. Write Clips as regular faststart MP4 (remux on save is cheap, and Clips don't need crash safety). Keep fMP4 only for in-progress Recordings, and finalize them to a regular MP4 on a clean stop.

### H-3 The crash-safety bound (≤ 1 s lost, power loss included) has no supporting mechanism `[ADV][EDGE]`
- **Location:** FR-12; §2.1 "Record a whole session"; addendum → PLAN T-18; GOP of about 1–2 s
- **Problem:** Losing at most 1 s needs fragments of ≤ 1 s, each flushed to stable storage. Fragments that begin on keyframes with a 1–2 s GOP can't be ≤ 1 s. FR-12 names "the power goes off", but the only test is killing the process, which leaves the OS write cache intact. Power loss also loses unflushed cache and file-size metadata.
- **Consequence:** The ≤ 1 s promise is broken on power loss, and possibly on process kill too if fragments are 2 s long.
- **Fix:** Set the fragment duration (≤ 1 s, which means GOP ≤ 1 s or fragments that don't start on a keyframe, verified in target players) and an explicit flush cadence (`FlushFileBuffers` per fragment, or write-through). Either add a power-loss or hard-reset test, or downgrade the power-loss bound (for example ≤ 5 s).

### H-4 Summing devices with "no gain" means clipping and double counting `[ADV][EDGE]`
- **Location:** FR-16 consequence 3 ("sum of its devices"); FR-18; §3 "Track" / "Everything" Track; addendum (BEACN survey: "Headphones" output, "VOD Track"/"Audience Mix" inputs); PLAN §0 row 10 / phase 3 "overlap warning"
- **Problem:** An "Everything" Track that sums Game, Chat, Music, a monitor output (BEACN "Headphones" carries the whole mix) and mixer virtual inputs (VOD Track, Audience Mix, which are themselves mixes of outputs) counts the same audio two or three times. With no gain control at all, the sum goes past 0 dBFS. PLAN.md's overlap warning didn't make it into the PRD.
- **Consequence:** A distorted, phasey or comb-filtered first Track, which is the one that gets shared. That's the "broken audio" abandonment trigger. FR-18 also forbids the obvious fix.
- **Fix:** Add a requirement for a clipping-safe sum (float mix with a soft limiter on overs, or −x dB headroom per added source; document it as the only processing). Bring back the overlap warning: when a Track holds a device whose content is known to include another device in the same Track, the Main App warns. Add a consequence: "A Track summing N full-scale devices never clips."

### H-5 Mic mute silences every Input Device, including mixer virtual inputs that carry game or chat audio `[ADV][EDGE]`
- **Location:** FR-20; §3 "Hotkey" (Mic Mute); addendum BEACN inputs (VOD Track 1–3, Audience Mix, Voice Chat Mic)
- **Problem:** Mute is defined by device kind ("silences the Input Devices in those Tracks"). On the builder's own mixer, several Input Devices are mixes that contain game, chat or music. FR-20's consequence "Muting never silences Output Devices" doesn't stop a muted Track from losing its game audio.
- **Consequence:** Pressing mute takes the game audio out of a VOD-Track-based Track. This is silent data loss in the recording.
- **Fix:** Make mute a per-device-per-Track flag ("muted by Mic Mute: yes/no" for each device in each Track), defaulting to "yes" only for physical mic endpoints. Rewrite the Glossary entry. Specify which Game Profile applies when no game is running (Monitor Recording), and whether the mute state survives a game exit or switch.

### H-6 Device dropouts with no indication break the "silent track" abandonment criterion `[ADV][EDGE]`
- **Location:** FR-21 ("The Overlay shows nothing"); FR-15 consequence 3; §2.2 ("a missing or silent track")
- **Problem:** Silent recovery is fine for a 10 s unplug. It isn't fine when the endpoint ID changes for good: a driver reinstall, a USB port change, or a mixer software update. The Track then records silence forever, and nothing anywhere says so. The case where a game starts while every device in a Track is already unavailable isn't handled either.
- **Consequence:** A month of Clips with a silent "Comms" Track, discovered when you go to edit one. That's exactly §2.2.
- **Fix:** Keep the Overlay quiet for transient dropouts, but require a visible state when a session starts with a Layout device missing, or when a device has been gone for more than N seconds: an unavailable badge in the Main App and tray, plus one Overlay message at session start. Add a re-bind flow ("this device looks like the old one", matched by name and parent).

### H-7 Settings changed mid-session aren't defined for content already in the buffer `[EDGE]`
- **Location:** FR-19 consequence ("Clips and Recordings that start after the change"); FR-25; FR-36; FR-2/FR-3
- **Problem:** A Clip "starts" at the last Reset, but its content was already encoded with the old Track Layout and Output Settings. The PRD doesn't say whether a change mid-game (Layout, resolution, codec, bitrate, Buffer Window, Memory ↔ Disk) restarts the buffer and drops content, produces a mixed-format Clip, or waits until the game exits. A change to Global Settings that an open profile inherits isn't covered at all.
- **Consequence:** Undefined behavior that each story will implement differently. A likely outcome is a Clip that crosses an encoder restart (see C-3).
- **Fix:** Add a rule: settings that change the encoded format take effect at the next session (game detection) and the Main App shows "applies next session". Or the change restarts the buffer, the unsaved content is lost, and the Main App warns before you apply it. Settings that don't change the format (Buffer Window growing, hotkeys) take effect immediately. Give a consequence for each class.

### H-8 Two games at once: the "foreground wins" assumption breaks capture, settings and folders `[EDGE]`
- **Location:** FR-26 assumption 3; FR-22; FR-29; FR-11; FR-19/FR-25
- **Problem:** If the target follows the foreground, alt-tabbing between two detected games (or between a game and a fullscreen browser) moves capture to a different window in the middle of the buffer, with a different profile, Track Layout and Output Settings. It isn't defined which profile and folder a Clip that spans the switch belongs to, or whether a Recording of game A follows the switch to game B.
- **Consequence:** Mixed-content Clips filed under the wrong game, encoder restarts (C-3), and Recordings that capture the wrong window.
- **Fix:** Bind the session to the first detected game, which stays the target until it exits (consistent with C-1). Later detections queue behind it. Or define an explicit rule: one session per game, and only the session game is captured. State the folder and profile for any Clip.

### H-9 Engine crash: no restart requirement and no blind-spot detection `[ADV][EDGE]`
- **Location:** NFR-2, NFR-5; SM-1 (100% target); FR-6; addendum → PLAN §1 (engine and UI processes), T-17
- **Problem:** The PRD covers the UI dying (NFR-5) but not the engine dying outside a Recording. Nothing says that the engine restarts, re-registers its Hotkeys and restarts the buffer for a game that is still running. Nothing says the UI tells you the buffer is gone. And since the Overlay probably lives in the UI process, a UI crash takes all feedback with it while Hotkeys keep saving.
- **Consequence:** After one engine crash, every later Save press does nothing for the rest of the session, which quietly breaks SM-1.
- **Fix:** Add an FR: the engine and UI watch each other and each restarts the other within N seconds. After an engine restart, the buffer resumes for a game that's still running and the Overlay or a sound reports "buffer restarted". Add tests.

### H-10 Favourites and Cleanup risk permanent data loss `[ADV][EDGE]`
- **Location:** FR-30 consequence 2 (external changes reflected); FR-32; §3 "Favourite", "Cleanup"; FR-31 (Recycle Bin)
- **Problem:** The PRD doesn't say where the Favourite flag lives. If it's keyed by path, renaming or moving a file outside the app drops the flag, and Cleanup then deletes your favourite. Files the user drops into the output folder (exported edits) show up in the Library, and the PRD doesn't say whether Cleanup may delete them. It also doesn't say whether Cleanup deletes permanently or to the Recycle Bin. The Recycle Bin frees no space on the same drive, and a permanent delete is irreversible.
- **Consequence:** Cleanup permanently deletes something the user meant to keep. That's the one irreversible failure in the product.
- **Fix:** Store Favourite with the file (an NTFS alternate data stream or MP4 metadata) or key it by NTFS file ID. Limit Cleanup to files the app created (tracked by file ID or a metadata tag). State permanent versus Recycle Bin explicitly, and add a Cleanup log or preview. Add a consequence: "Renaming a Favourite in Explorer keeps it a Favourite."

### H-11 NFR-1 is untestable, and NFR-2 has no enforcement on a single shared GPU `[ADV]`
- **Location:** NFR-1 (the number is "still open", Q-1); NFR-2; SM-3; §6 Non-Goals (one GPU); addendum C1
- **Problem:** The top abandonment trigger (FPS loss) has no threshold, no test method, and no reference game or settings. NFR-2 promises the app can never freeze a game, but on a single GPU, capture, color conversion and encode compete with the game for GPU time and VRAM, and Disk mode competes for I/O. None of this has a requirement: GPU scheduling priority, VRAM budget, I/O priority, CPU affinity or priority.
- **Consequence:** "Done" can't be decided. The architecture has no budget to design against, and NFR-2 is a wish, not a requirement.
- **Fix:** Set a provisional number now (for example ≤ 3% average FPS and ≤ 5% 1%-low at 1440p/60/H.264 in two named games, measured with PresentMon) and revise it after the bake-off. Add NFR-2 consequences: encoder and capture run at below-normal GPU priority, the app has a VRAM budget, Disk-mode writes use low-priority I/O, and none of these get worse under load.

### H-12 The MVP includes everything: a scope trap for a hobby build `[ADV]`
- **Location:** §7.1, §7.2 ("Nothing in §4 is deferred"); §0 ("FR-1 to FR-35"); PLAN §5 phases
- **Problem:** v1 contains the Library with thumbnails, Cleanup, Monitor Recording, per-game overrides of every setting, live meters, Disk mode and Recordings, with no phase cut. PLAN.md itself was phased (spike, ring and save, full audio). Also, §0 and §7.1 scope "FR-1 to FR-35", so FR-36 (Memory/Disk) and FR-37 (Hotkeys) are literally outside the MVP.
- **Consequence:** Sofia can't slice a walking skeleton that proves the differentiator (multi-device Tracks in a saved Clip) early. The project risks stalling on Library UI before the core is proven. FR-36 and FR-37 may get dropped.
- **Fix:** Define an MVP-0 (one game, Memory buffer, Save Hotkey, multi-device Tracks, Clip in a folder, Overlay or sound) and an MVP-1 with the rest. Renumber FRs in order, or change §0 and §7.1 to "FR-1 to FR-37".

### H-13 Save failure, retry and overlapping saves are underspecified `[EDGE]`
- **Location:** FR-7 consequence 2 ("retries with the same content (plus anything captured since)"); FR-5; FR-6 consequence 2; §3 "Save Cooldown"; addendum → PLAN T-11
- **Problem:**
  1. With a 30 s window, a retry 20 s later can't hold "the same content". The oldest 20 s are gone unless they're held, and the PRD doesn't say they are.
  2. A 2-hour Clip written from Disk mode can take far longer than the 5 s cooldown. The PRD doesn't say what the Overlay shows after the cooldown (FR-6 says the icon "stays for the whole Save Cooldown", while §4.1 says it shows "while the Clip is written"), or what a second press does while the first save is still writing.
  3. If save 1 fails after save 2 has started, it isn't defined where Clip 2 begins.
  4. Does a failed save start the cooldown?
  5. Memory or disk held during a save comes on top of the FR-3 estimate.
- **Consequence:** Conflicting Overlay states, Clips silently truncated on retry, and RAM or disk use above the estimate during long saves (which also breaks the FR-36 consequence).
- **Fix:** Define a save as an immutable snapshot (the held span from Reset or the window start to the press), with a lifecycle of queued → writing → saved or failed. On failure, keep the held span until it's retried or until the next save succeeds. Show "saving" until the write completes, independent of the cooldown. Allow concurrent saves (default 2) and queue the rest. Include held memory in the estimate.

### H-14 Game exit discards the buffer immediately, which loses the "game just crashed" moment `[EDGE]`
- **Location:** FR-1 consequences 2–3; §2.1 "Catch the moment"
- **Problem:** A game crash or exit drops the Replay Buffer at once, with no grace window for a Save press. The hotkey is pressed after the event, so a moment at the very end of the session (a crash, the final kill before quitting) can never be saved.
- **Consequence:** The press does nothing and shows nothing. The Clip is lost.
- **Fix:** Keep the buffer for N seconds (for example 10 s, configurable) after game exit, so the Save Hotkey still works, then discard it. Add the case to FR-1's consequences.

---

## Medium

### M-1 Game Detection false positives, including the app detecting itself `[EDGE]`
- **Location:** FR-26; §3 "Game Detection"; FR-28
- **Problem:** "Any fullscreen or borderless app" catches browser fullscreen video (and DRM content captures black), presentations, custom-chrome apps maximized with no title bar, game launchers and splash screens, and possibly the app's own full-screen Overlay window or Main App. "Windows identifies it as a game" doesn't name a source (the Game Bar list or GameConfigStore?), so it can't be tested.
- **Consequence:** Buffers start up behind video players (RAM and GPU cost, NFR-1), the profile list fills with junk, and the app may detect itself.
- **Fix:** Exclude the app's own processes and shell windows (explorer, lock screen) explicitly. Name the Windows game source. Require the window to cover the monitor *and* the process to use D3D/Vulkan, or ship a default Ignored list (browsers, media players). Add those as consequences.

### M-2 Profile keying by executable, and process access on anti-cheat-protected games `[ADV][EDGE]`
- **Location:** §3 "Game Profile" ("keyed by its executable"); FR-27; NFR-3
- **Problem:** It isn't defined whether the key is the full path or the file name. Launcher and shipping executables (for example a game's launcher versus its `-Win64-Shipping.exe`), versioned install paths, shared hosts (`javaw.exe`, emulators) and x86/x64 variants all create duplicate or merged profiles. Reading the product name requires opening the game process. Anti-cheat drivers watch and strip handles to protected processes, and the PRD doesn't limit the access rights it asks for.
- **Consequence:** Duplicate profiles with split folders and settings, or a detection approach that itself triggers anti-cheat (breaking NFR-3).
- **Fix:** Key by the normalized image path, with a "merge with profile" action. Require the app to open game processes only with `PROCESS_QUERY_LIMITED_INFORMATION` (never VM read or handle duplication), and read version info from the file on disk instead of the process.

### M-3 Folder naming: illegal characters, collisions, and a reserved "Desktop" `[EDGE]`
- **Location:** FR-29; FR-27 assumption; FR-23 assumption ("Desktop" folder)
- **Problem:** Product names contain characters that are invalid in paths (`:`, `?`, trailing dots) and reserved names. Two profiles can sanitize to the same name. A game called "Desktop" collides with the Monitor Recording folder. The FR-29 example ("Valorant") doesn't match the product-name assumption, which typically yields "VALORANT".
- **Consequence:** A save fails on an invalid path (FR-7), or two games share a folder and Library grouping is wrong.
- **Fix:** Specify sanitization and de-duplication (suffix `(2)`), reserve a name for Monitor Recording that can't collide (for example `_Desktop`), and fix the example.

### M-4 Several "testable" consequences can't be tested as written `[ADV]`
- **Location:** FR-17 (track titles in DaVinci Resolve); FR-36 ("identical Clips"); FR-18 ("matches the device's own level"); NFR-5 ("no memory growth"); NFR-3 ("without … bans"); NFR-1
- **Problem:**
  - Whether Resolve shows MP4 track titles is unverified, and if it doesn't, the requirement depends on a third party.
  - Byte-identical Clips are impossible because timestamps differ.
  - A device's "own level" has no defined reference: loopback is captured before or after endpoint volume depending on the device.
  - Memory grows by design until the buffer is full.
  - A ban can arrive weeks later, and testing for one risks the user's account.
- **Consequence:** Stories either can't close or close on a reinterpretation.
- **Fix:**
  - FR-17: verify against named players and keep a fallback list.
  - FR-36: say "equivalent: same duration, Tracks and frame count".
  - FR-18: "bit-exact to the captured endpoint stream when the Track holds one device".
  - NFR-5: "steady-state working set stays within X MB once the buffer is full".
  - NFR-3: "no anti-cheat warning or kick across N sessions in the named titles", with bans tracked as an SM, not as a test.

### M-5 The ±20% storage estimate doesn't hold under VBR `[ADV]`
- **Location:** FR-3 consequence 2; FR-24 ("bitrate")
- **Problem:** The PRD doesn't specify the rate-control mode. With VBR or CQP, size depends on content: a static menu and a fast-paced firefight differ by well over 20%.
- **Consequence:** The test fails depending on scene content, or the estimate misleads you into a RAM or disk overrun (C-6).
- **Fix:** Require CBR, or VBR with a hard maxrate, and estimate from the maxrate. State the reference scene for the ±20% test.

### M-6 Disk full outside a Recording, restarting after it, and dismissing the red X `[EDGE]`
- **Location:** FR-14 consequences 2–3; FR-36 (Disk mode); FR-32
- **Problem:** A Disk-mode Replay Buffer filling the disk while no Recording is running isn't covered. After FR-14 stops the Disk buffer, nothing says it restarts when space frees up (for example after Cleanup). "Until I dismiss it" needs an input method, and the Overlay is a non-activating window over a game (C-4), so it can't take a click without stealing focus. Cleanup timing (does it run before writes, during a session?) isn't defined.
- **Consequence:** The buffer stays dead for the rest of the session, or clicking to dismiss the red X minimizes an exclusive-fullscreen game.
- **Fix:** Cover disk full for the Disk buffer in its own FR. Auto-resume once free space is above a threshold. Dismiss the red X from the tray or Main App, or let it time out; never by clicking the Overlay. Run Cleanup at session start and when free space falls below a threshold.

### M-7 The Record Hotkey fails silently in games with Recordings off `[ADV]`
- **Location:** FR-8 consequence 1
- **Problem:** "Does nothing, and the Overlay shows nothing." You press Record, believe you're recording, and play a 3-hour session.
- **Consequence:** A lost Recording, which is the §2.2 abandonment trigger, caused by design.
- **Fix:** Show "Recordings are off for <game>" (Overlay plus sound). It's one message and costs nothing.

### M-8 The "Everything" Track is ambiguous and easy to break `[ADV][EDGE]`
- **Location:** §3 "Track" ("every selected device mixed"); FR-16 consequence 4; §2.1 "Share as-is"
- **Problem:** "Every selected device" isn't defined: the union of devices in the other Tracks, or a snapshot? Does it update when you add a device to another Track? If you move it out of first place, Discord and Chrome play a partial mix, and nothing warns you. Whether mic mute should affect it by default isn't stated either.
- **Consequence:** A shared Clip is missing audio, which defeats the product's pitch.
- **Fix:** Define it as a live union by default ("auto"), with an option to edit manually. Warn when Track 1 doesn't contain every device in the Layout. Make mic mute affect "Everything" by default.

### M-9 Concurrent Monitor Recording, game Recording and buffer aren't specified `[EDGE]`
- **Location:** FR-23; FR-10; NFR-1; §3 "Hotkey" (no Monitor Recording hotkey listed)
- **Problem:** The PRD doesn't say whether a Monitor Recording can run alongside a game Recording and the Replay Buffer. That could mean two or three video encodes on one GPU, against consumer encoder session limits. It also doesn't say whether the game Recording shares the buffer's encoded stream or uses its own encoder, what the Overlay dot shows for a Monitor Recording, or whether the Monitor Recording includes the Overlay (it would, unless capture exclusion is applied).
- **Consequence:** A session limit is hit unexpectedly (Recording fails to start), the FPS cost doubles, or the Overlay is baked into the desktop video.
- **Fix:** State that game Recordings reuse the buffer's encoded stream (one encode), and whether a Monitor Recording can run alongside a game session or is refused with a message. Require capture exclusion on every app window (PLAN §7.1) as an FR, not a deferred item.

### M-10 Sleep, lock, UAC, TDR and driver updates have no FR coverage `[EDGE]`
- **Location:** §4 generally; addendum → PLAN T-01, T-04 (the T catalog is "indexed", not required)
- **Problem:** The PRD says each Consequence is testable, but the system events that most often break capture tools (sleep and resume mid-Recording, a timestamp jump, secure-desktop black frames, GPU device removed) only exist in a catalog that the addendum calls something to "attach later".
- **Consequence:** Slicing may never pull them into stories, and A/V sync after resume (NFR-4) goes unverified.
- **Fix:** Add one FR, "System interruptions", whose consequences reference T-01, T-04 and sleep/resume, with expected behavior (for example: on sleep, an in-progress Recording is closed cleanly and a new one starts on resume, or stays one file with a gap).

### M-11 "One GPU" is ambiguous on a desktop with the iGPU enabled `[EDGE]`
- **Location:** §6 Non-Goals ("PCs with more than one GPU"); addendum C1
- **Problem:** Many desktops with Intel CPUs expose an enabled iGPU next to the discrete card. The PRD doesn't say what the app does when it finds two adapters: refuse to run, pick one, or do something undefined.
- **Consequence:** A cross-adapter capture fails or copies frames silently and expensively on a machine that isn't a "laptop with hybrid graphics".
- **Fix:** Define the behavior: use the adapter that drives the game's monitor, and if capture and encode adapters differ, show an error in the Main App instead of degrading.

### M-12 "Share as-is" ignores Discord's upload size limit `[ADV]`
- **Location:** §2.1 "Share as-is"; FR-31 consequence 2 (paste into Discord); §6 (no uploading)
- **Problem:** A 30 s Clip at a typical high bitrate is well over a hundred MB, which is far beyond Discord's free-tier attachment limit. The copy-to-clipboard flow will usually fail at the Discord step.
- **Consequence:** A job in §2.1 can't be done with default settings, and the FR-31 test passes only for tiny Clips.
- **Fix:** Either scope the JTBD ("plays with all audio once uploaded where size allows"), or add a "Copy as shareable" action that transcodes the first Track plus video to a target size (and mark that as out of scope if it's deferred).

### M-13 Exclusive-mode devices and default-device changes `[EDGE]`
- **Location:** FR-15; FR-21; §3 "Audio Device"
- **Problem:** If another app opens a device in WASAPI exclusive mode (a DAW, some games), capture or loopback fails. The PRD doesn't say whether that counts as "unavailable" under FR-21. Tracks bind to specific endpoints, and no "Default output/input" choice is offered, so switching your Windows default device (headphones to speakers) quietly records the wrong device.
- **Consequence:** Silent Tracks with no signal anywhere (the H-6 consequence).
- **Fix:** Treat exclusive-mode loss as unavailable, with the H-6 indication. Consider offering "Default Output" and "Default Input" as pseudo-devices that follow Windows.

### M-14 Aspect-ratio scaling and protected (black) capture aren't specified `[EDGE]`
- **Location:** FR-22 consequence 2 ("scaled to the fixed output resolution"); FR-24
- **Problem:** A competitive game at stretched 4:3 into a 16:9 output: stretch or letterbox? A game or window that blocks capture (display affinity, DRM) gives black frames, and the app doesn't detect it.
- **Consequence:** Distorted Clips, or a session of black Clips with no warning.
- **Fix:** State the scaling rule (letterbox, preserving aspect ratio, by default). Detect frames that stay all black for N seconds and show a message.

### M-15 "Any setting can be configured for that game" is undefined scope `[ADV]`
- **Location:** §3 "Game Profile" ("any setting can be configured"); §4.5 description
- **Problem:** The PRD doesn't say whether Hotkeys, Buffer Window, storage mode, Save Cooldown, output folder or Cleanup are per-game, or whether "Start with Windows" can be (it can't). The §4.5 description lists only four overridable groups, and the Glossary says "any".
- **Consequence:** Over-building the override system, or arguments during slicing. A per-game Buffer Window or storage mode also makes FR-3 estimates depend on the profile.
- **Fix:** List the overridable settings explicitly (the §4.5 set plus anything else chosen), and mark the rest as global-only.

---

## Low

### L-1 FR numbering is out of order and doesn't match §0 and §7.1 `[ADV]`
- **Location:** §0, §7.1; FR-36 placed after FR-2, FR-37 after FR-34
- **Fix:** Renumber in order, or update the stated range to FR-1 to FR-37. See H-12 for the scope consequence.

### L-2 The Glossary is missing terms the FRs use `[ADV]`
- **Location:** §3 "Hotkey" (no Monitor Recording hotkeys); §3 "Overlay" (no green saving state, red X or mic-muted icon); "saving icon" vs "green saving state"
- **Fix:** Add the Monitor Recording hotkeys to the Hotkey kinds, list every Overlay element in the Glossary, and use one name for the saving state.

### L-3 The JTBD promises zero loss on a crash; FR-12 allows 1 s `[ADV]`
- **Location:** §2.1 "everything recorded up to that point plays"; FR-12 consequence 2
- **Fix:** Align the JTBD wording with FR-12's bound (or H-3's revised one).

### L-4 Save Hotkey with no game running, or with a nearly empty buffer `[EDGE]`
- **Location:** FR-4; FR-1; FR-23
- **Problem:** The PRD doesn't say what a press with no game running does (silence, or a message), or what a press 0.3 s after detection does, before any keyframe exists. There's no minimum Clip length.
- **Fix:** Show "No game detected" on a press with no game. Define a minimum Clip length (for example 1 s) and the message shown below it.

### L-5 Hotkey bounce and duplicate assignments `[EDGE]`
- **Location:** FR-9 (toggle mode); FR-37
- **Problem:** Key repeat or a double press in toggle mode starts and immediately stops a Recording. The same combination can be assigned to two Hotkey kinds.
- **Fix:** Debounce the toggle (for example 500 ms), and reject duplicate combinations in Settings.

### L-6 The cooldown's reference point is ambiguous `[EDGE]`
- **Location:** §3 "Save Cooldown" ("for a set time after a save"); FR-5 consequence 3
- **Fix:** State that it runs from the accepted press, not from save completion.

### L-7 One-file Recordings on file systems with size limits `[EDGE]`
- **Location:** FR-13; output folder selection
- **Problem:** FAT32's 4 GB per-file limit (for example on a USB output drive) makes FR-13 impossible.
- **Fix:** Require NTFS or exFAT for the output and buffer folders, and validate in Settings.

### L-8 Media Player: "the one I choose" vs the Windows default `[ADV]`
- **Location:** §2.1 "Find and organize" ("the media player I choose"); §3 "Media Player"; FR-31 consequence 1
- **Fix:** Say "chosen through Windows default apps", or add an in-app player override.

### L-9 Mic mute affects only the recording, and that isn't stated `[ADV]`
- **Location:** FR-20; §3 "Hotkey"
- **Problem:** A "mic muted" icon over the game may lead you to believe Discord can't hear you, while the mute only applies to the recording.
- **Fix:** State it explicitly, and label the icon "Rec mic muted" or similar.

### L-10 Library actions on locked files `[EDGE]`
- **Location:** FR-31 (rename, delete); FR-32 (Cleanup)
- **Problem:** A file open in the Media Player or an editor can't be renamed or deleted, and Cleanup will hit the same lock.
- **Fix:** Show a "file in use" message in the Library. Cleanup skips locked files and retries later.

---

*Overlap note: C-1, C-2, C-4, C-6, H-3, H-4, H-5, H-6, H-9, H-10, M-2 and M-8 were raised independently by both lenses, so treat them as the strongest signals.*
