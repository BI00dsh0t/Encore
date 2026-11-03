---
id: 4
type: epic
title: "Replay Buffer and Clips"
parent: initiative-replay-buffer
covers: [R1, R2, R3, R4, R5, R7, R17, R18, R32]
sources: []
companions: []
after: []
refined: true
risk: high
---

# Replay Buffer and Clips

## Description

This is the first usable version, end to end. It covers:
- The Replay Buffer's lifecycle: it starts on epic-game-detection-profiles' Target Game appeared event, and stops 30 s after the exited event.
- The Replay Buffer in Memory or Disk mode, with a Buffer Window that has no upper limit and a live storage estimate.
- The Save Hotkey writing a Clip that ends within one frame of the press.
- A Reset after each Clip with no gap, and the Save Cooldown debounce.
- Retryable failures, with up to two Clips writing at once.
- Configurable Hotkeys, built on epic-engine-baseline's registration.
- Per-Game-Profile overrides of the Track Layout, the Output Settings, the Mic Devices and the mute Tracks, which take effect at the next Reset.

It brings together epic-engine-baseline's encoder, epic-multi-track-audio's Tracks, and epic-game-detection-profiles' Target Game and file paths.

## Outcome

In any detected game, pressing the Save Hotkey produces a playable multi-Track Clip in that game's folder, every time (R1, R2, R3, R4, R5, R7, R17, R18, R32, toward SM-1).

## Requirements

- R1 (parent R1; architecture AD-19): The Replay Buffer session starts when a Target Game appears and runs while it exists. Minimizing, alt-tab and windowed mode don't stop or discard it. When the Target exits or crashes, the Save Hotkey still saves its last moments for 30 s, and after that nothing is held. Switching to another Target Game Resets the Replay Buffer and discards the previous game's unsaved content (PRD §10, FR-38). Setting Ignored on the Target ends it with no drain. Only the SessionOrchestrator starts, Resets and ends the session.
- R2 (parent R2, FR-3): The Buffer Window (`bufferWindow`) accepts any length of at least 1 s, with no upper limit, and a Clip never holds more than it. Settings show the RAM or disk use it will cost, updated as soon as the Buffer Window, an Output Setting, the number of Tracks or the storage mode changes: within ±20% of measured use for constant bitrate, and a worst case for variable bitrate. They warn, but allow the value, when the estimate exceeds free RAM or disk space.
- R3 (parent R3, FR-36; architecture AD-8): The replay ring holds encoded packets only, in reference-counted slabs with a keyframe index: native memory in Memory mode, a preallocated `ring-<generation>.bin` under `ringPath` in Disk mode. Both modes produce identical Clips, and Disk mode stays within its estimate. Memory mode never grows past a safe margin of free RAM: pinned bytes count, the oldest unpinned slabs go first, Clips get shorter, and `warning.raised { code: memory-margin }` tells the status line.
- R4 (parent R4; architecture AD-6, AD-10): A press of the Save Hotkey saves a Clip covering the Buffer Window, or back to the last Reset if that's shorter. Its last video frame is the last one with capture QPC ≤ the Hotkey QPC, its audio is cut at the Hotkey QPC, and every Track matches the video's length within one audio frame. With less than 1 s buffered, no Clip is made.
- R5 (parent R5; architecture AD-7, AD-8, AD-10): After each save the Replay Buffer Resets: the encoder is asked for a keyframe on the next frame, which starts the next Clip, and the GOP is otherwise at most 1 s. Consecutive Clips have no gap and overlap by at most one frame, and capture never stops. A forced encoder restart opens a new ring generation, which counts as a Reset. Presses within the Save Cooldown (`saveCooldown`, 5 s by default, measured from the Hotkey QPC) are ignored, with no Clip and no message.
- R6 (parent R7; architecture AD-9): A Clip is a complete faststart MP4 written to `<final>.rbpart` in its folder, then renamed; its path comes only from epic 3's OutputPathAllocator. A failed save removes the `.rbpart`, reports its reason as an event for epic-overlay-feedback, and doesn't Reset, so the next press retries with the same content plus anything captured since. Up to two Clips can be writing at once. Every writer calls `Library.BeginWrite` before creating its file and `EndWrite(outcome)` after, the "file being written" signal for epic-library-cleanup.
- R7 (parent R17; architecture AD-4, AD-5, AD-7): A Game Profile can override `trackLayout`, `video` and `bufferWindow`. The session reads one `EffectiveSettings` when it starts, and changes apply at the next Reset; when a change alters the `ChainKey`, the next chain is built in advance and switched to at the press frame, raising `warning.raised { code: reset-gap }` only if a gap can't be avoided.
- R8 (parent R18, per-Game-Profile part; architecture AD-4, AD-5): A Game Profile's own Track Layout carries its own Mic Devices (`isMic`) and mute Tracks (`muteAffected`), which replace the Global ones for that game. Mute stays engine runtime state shared by all profiles, and silences only the active layout's Mic Devices in its mute Tracks.
- R9 (parent R32; architecture AD-11): Every `HotkeyAction`'s key combination can be set in the Main App. A combination that fails to register is shown against its Hotkey while the others keep working, duplicates are refused before saving and by the engine, and Hotkeys work while a game has focus and with the Main App closed.

## Constraints

- (parent NFR-2; architecture AD-7) Nothing allocates managed memory per frame, per audio buffer or per packet in the ring; file I/O runs at low I/O priority; at most two video encoder chains run at once.
- (architecture AD-8) A save pins the slabs it reads and the writer never blocks the ring; a Clip holds one generation; resizing allocates a new ring and releases the old one when its last pin drops.
- (architecture AD-19) The SessionOrchestrator alone starts, Resets and ends sessions and emits `capture.stateChanged`; media and writer threads post completions back to its channel.
- (architecture AD-3, AD-22) Save, saving and failure events, and the storage estimate, travel only as Contracts messages in the `Capture.cs` area, added additively. The settings keys (`bufferWindow`, `replayStorage`, `ringPath`, `saveCooldown`, `video`, `trackLayout`, `hotkeys`) already exist in story 1.4's catalog.
- (architecture AD-21) Nobody but the OutputPathAllocator builds or caches an output path.

## Done when

- The Replay Buffer starts with the Target Game. A Clip can still be saved within 30 s of the game crashing, and nothing is held after that (R1).
- Two Clips saved 10 s apart have no gap and overlap by at most one frame. Each ends within one frame of its press, and its Tracks match the video's length (R4, R5).
- Presses within the Save Cooldown are ignored, a buffer holding less than 1 s makes no Clip, a failed save leaves no file that only looks playable and retries with no Reset, and two Clips can write at once (R5, R4, R7).
- Memory and Disk modes give identical Clips. The live estimate holds (±20% for constant bitrate), and Memory mode stays within its RAM margin (R2, R3).
- A game's own Track Layout and Output Settings apply from the next Reset (R17). Its own Mic Devices and mute Tracks override the Global ones (R18, per-profile part).
- Every Hotkey can be rebound. A failed registration is reported, duplicates are refused, and Hotkeys work in-game and with the Main App closed (R32). Verified integrated on the builder's PC.

## Boundaries

- Out: the Overlay and Feedback Sounds (epic-overlay-feedback; this epic emits the save events), Recordings (epic-recordings), and the Library (epic-library-cleanup).
- Handoff: save, saving and failure events and capture state go to epic-overlay-feedback. A "file being written" signal goes to epic-library-cleanup, and epic-recordings reuses it for Recordings.
- Touch point: the Global Output Settings page in the Main App. Epic-engine-baseline delivered the keys and encoder probing (R22) but no page; this epic owns the page, next to the Buffer Window and its estimate.

## References

- parent — _bmad-output/initiative-replay-buffer/initiative-replay-buffer.md, Requirements R1, R2, R3, R4, R5, R7, R17, R18, R32
- architecture — _bmad-output/initiative-replay-buffer/architecture-replay-buffer/architecture-replay-buffer.md, AD-4, AD-5, AD-7, AD-8, AD-9, AD-10, AD-11, AD-19, AD-21
- prd — _bmad-output/initiative-replay-buffer/prd-replay-buffer/prd-replay-buffer.md, §4.1 FR-1 to FR-7 and FR-36, FR-19, FR-20, FR-25, FR-37, §10 Confirmed Defaults (FR-38 Target switch)
- addendum — _bmad-output/initiative-replay-buffer/prd-replay-buffer/addendum.md, Review-driven mechanism notes (keyframe at the press)
- architecture input — PLAN.md, §0 decisions 3–7, §3

## Notes

- Decision: 2026-11-01, refined unattended from the initiative's lines, the PRD FRs and the architecture ADs the epic cites; no spike is needed, because the ring, cut and container rules are fixed in AD-8 to AD-10.
- Decision: 2026-11-01, the tracer bullet is story 1: the Target Game appears → a Memory-mode ring of encoded packets → the Save Hotkey → a faststart multi-Track Clip in the game's folder. The builder plays a game, presses Save and opens the Clip.
- Decision: 2026-11-01, the least-certain item is story 2 (the exact save cut and the gapless Reset with a forced keyframe), so it comes right after the tracer.
- Decision: 2026-11-01, the sequencing:
  - The engine lane runs 1 → 2 → 3 → 4 → 5 → 6 → 7 → 8 → 9, because all of them change the SessionOrchestrator's replay session or the ring.
  - The UI lane runs 10 → 11 and 10 → 12, since all three add pages to the Settings host.
  - A Refactor sweep (13) closes the build, and a closing end-to-end check on the builder's PC (14) follows it.
- Decision: 2026-11-01, 14 entries, past the usual 8 to 12, kept in one epic: R1 to R7 are the first usable version and deliver together, and splitting would change the initiative's epic set. Unattended default, awaiting review.
- Decision: 2026-11-01, Memory mode's safe margin: the ring stops growing when Windows' available physical memory would drop below the larger of 4 GB and 15% of installed RAM. A constant, not a setting. Unattended default, awaiting review.
- Decision: 2026-11-01, a press with less than 1 s buffered makes no Clip and is reported as a save failure with reason `clip-too-short` (so the Overlay and sound say why), with no Reset. Unattended default, awaiting review.
- Decision: 2026-11-01, a press after the Save Cooldown while two Clips are still writing fails as a retryable save with reason `save-busy`, with no Reset. Unattended default, awaiting review.
- Decision: 2026-11-01, the variable-bitrate worst case in the estimate uses the encoder's peak rate, 1.5× the target bitrate (as story 1.10's encoder sets it), plus each Track's AAC bitrate. Unattended default, awaiting review.
- Decision: 2026-11-01, the Global Output Settings page lands in this epic (story 10), since epic 1 delivered the keys but no page. Unattended default, awaiting review.
- Decision: 2026-11-01, `Library.BeginWrite`/`EndWrite` is a Core port with a no-op implementation until epic-library-cleanup provides the LibraryActor (story 5).
- Decision: 2026-11-01, there's no `bmad-ux` pass for the Replay Buffer, Game Profile override and Hotkeys pages; the builder checks them by hand, as in epics 2 and 3. Unattended default, awaiting review.
- Decision: 2026-11-03, from 4.6: `ReplayRingSink.Restart` opens a ring generation, but no engine path calls it yet. Its documented precondition is that no save is waiting for its keyframe or streams when it is called. Story 9's chain switch at the press frame breaks that precondition, so story 9 must do two things:
  - Drain in-flight saves before `Restart`, or convert `VideoMicros`/`AllStreamsPast` per generation.
  - Decide a cut's generation (and so its end) before `ClipWrite.Run` publishes its claim. Otherwise a fallback Clip just after a boundary, with a second write in flight, leaves `[boundary, key)` in neither Clip.
  - Both are recorded as `deferred` in 4.6's plan.
- Decision: 2026-11-03, from 4.7 (Disk mode; QA PASS with advisories, none fixed):
  - Stories 4.8 and 4.10 take the RAM and disk limits. While a long Clip writes, its pinned blocks can't return to the ring file, so new slabs spill to native memory (`PacketRing.MemorySlabs`, silent). The disk stays within the estimate. 4.10's estimate or 4.8's margin should allow for it, and a one-time warning when `MemorySlabs` first rises would help.
  - A Buffer Window change in Disk mode keeps the old and new ring files together until the old Clips finish writing, so disk use can briefly reach about twice the estimate. Accepted.
  - A ring file write that fails mid-session (the drive is gone) fails the session; only file creation falls back to memory. Accepted.
  - `RingFile.RemoveStale` deletes every `ring-*.bin` in the ring folder. It should match `ring-<digits>.bin` only (a foreign `ring-notes.bin` in a user-set `ringPath` would go). For the 4.13 sweep.
  - `DiskRingClipTests` runs a real NVENC encode in the default suite. CLAUDE.md marks GPU encode tests Heavy/Explicit; the Core-level Memory-vs-Disk test already covers identity. For the 4.13 sweep.
- Waits on epic-engine-baseline because: it needs the encoder and fragmented MP4 writer.
- Waits on epic-multi-track-audio because: it needs the Track Layouts and the mixed Track audio.
- Waits on epic-game-detection-profiles because: it needs the Target Game appeared and exited events, Game Profiles, and per-game folders and file names.
