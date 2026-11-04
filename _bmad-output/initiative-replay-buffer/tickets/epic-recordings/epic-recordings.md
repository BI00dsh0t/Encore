---
id: 6
type: epic
title: "Recordings and Monitor Recording"
parent: initiative-replay-buffer
covers: [R9, R10, R11, R12, R13, R21]
sources: []
companions: []
after: []
refined: true
risk: high
---

# Recordings and Monitor Recording

## Description

This epic adds full sessions. In games with Recordings switched on, the Record Hotkey (one toggle key, or separate Start and Stop keys) runs a single-file Recording alongside the Replay Buffer. The Recording stops and saves when the game exits, survives crashes and power loss with at most 1 s lost, and handles a full disk and forced encoder restarts. A Monitor Recording of a chosen monitor runs by its own hotkey, with or without a game. It reuses epic-replay-buffer-clips' pipeline and epic-overlay-feedback's channel.

## Outcome

A whole session in an opted-in game ends as one playable file, even after a crash or a full disk (R9, R10, R11, R12, R13, R21).

## Requirements

- R1 (parent R9, FR-8, FR-9; architecture AD-5, AD-11, AD-16): A Recording starts only in the Target Game, and only while its Game Profile's `recordingsEnabled` is on, read live when the Record Hotkey is pressed. It is off by default; while it is off the Record Hotkey does nothing and nothing shows. Switching it on never starts a Recording. `global.recordHotkeyMode` picks one toggle key (`recordToggle`: the first press starts, the second stops) or separate keys (`recordStart` does nothing while a Recording runs, `recordStop` nothing while none does). After an engine restart no Recording resumes by itself, and `recording-interrupted` is raised for each one that was running.
- R2 (parent R10, FR-10, FR-11; architecture AD-7, AD-19): A Recording runs beside the Replay Buffer: a Clip save, a Reset or a ring generation never stops, pauses or glitches the Recording, and the Recording never changes a Clip. A running game Recording holds the Target Game; a Target switch waits and is re-evaluated when it stops. When the Target Game exits, or is set Ignored, the Recording stops and is finished as a complete file in the game's folder.
- R3 (parent R11, FR-12; architecture AD-9): A Recording is fragmented MP4 written in place, with every Track interleaved in each fragment and a fragment flushed to disk at least every 1 s. After the engine is killed, or Windows crashes or loses power, the file plays in common players with every Track in sync, losing at most 1 s at the end.
- R4 (parent R12, FR-13, FR-22): A Recording is one file however long it runs; a resolution or window-mode change never splits it. A forced encoder restart ends the file cleanly and starts a new Recording at once, with a notice and a sound, and every file stays playable.
- R5 (parent R13, FR-14): When the disk fills, the Recording stops and the file written so far plays with every Track. A `disk-full` warning shows the red X and plays the disk-full sound, and stays until dismissed or the next Recording starts. A Memory-mode Replay Buffer keeps running; a Disk-mode one stops, with a notice.
- R6 (parent R21, FR-23; architecture AD-7, AD-12, AD-21): A Monitor Recording of `global.monitorRecordingMonitor` (null is the primary monitor) is started and stopped only by the Monitor Record Hotkey, in toggle or separate mode as in R1. It runs with or without a game, beside the Replay Buffer and a game Recording, never holds the Target Game, uses the Global Track Layout and Output Settings, is crash-safe and handles a full disk like a Recording, and is saved in "Monitor Recordings". It captures with a WGC monitor item, with DXGI Desktop Duplication as the fallback, and the app's own windows never appear in it (PLAN.md §7.1).
- R7 (parent R6, R8, Recording parts; architecture AD-3, AD-15): While a Recording or Monitor Recording runs, `capture.stateChanged` reports it running, so the Overlay shows the solid dot. Starting and stopping raise epic-overlay-feedback's `recording-started` and `recording-stopped` notices and sounds, with the reason a Recording stopped.
- R8 (architecture AD-9, AD-17, AD-19; handoffs): Every Recording writer calls `Library.BeginWrite` before creating its file and `EndWrite(outcome)` after its final flush, through epic-replay-buffer-clips' port, so the Library can lock it. The orchestrator's real Recording state replaces epic-game-detection-profiles' stub Recording-running flag in the Target Game rule.

## Constraints

- (architecture AD-19) The SessionOrchestrator alone starts and stops Recordings and Monitor Recordings, on its channel; writers and encoders post completions back and never block it.
- (architecture AD-5) A Recording reads one `EffectiveSettings` snapshot when it starts; later changes apply to the next Recording.
- (architecture AD-7) At most two video encoder chains run at once; a request for a third is refused with `encoder-budget`. Sinks with the same `ChainKey` share a chain. File I/O runs at low I/O priority.
- (architecture AD-21) Recording paths come only from the OutputPathAllocator, from the profile's current name and the local time of the start QPC; Monitor Recordings go in the reserved "Monitor Recordings" folder.
- (architecture AD-22) Contracts change additively only, in `Recording.cs`, `Capture.cs` and `ErrorCodes`; new Hotkey actions and keys carry defaults.
- (architecture AD-12, NFR-3) Game capture stays a WGC window item; whole-monitor capture is only for a Monitor Recording.

## Done when

- With Recordings off, the Record Hotkey does nothing. Both hotkey modes work, and nothing starts a Recording automatically (R9).
- During a Recording the Overlay shows the solid dot, and the Recording started/stopped Feedback Sounds play, through epic-overlay-feedback's channel (initiative R6, R8, Recording parts).
- Saving a Clip mid-Recording leaves no glitch in either. The game exiting saves a complete file that shows in its folder (R10).
- Killing the engine mid-Recording leaves a file that plays with every Track in sync, losing at most 1 s (R11).
- A 4-hour Recording is one file. A forced encoder restart either continues the file or cleanly starts a new one, with notice (R12).
- When the disk fills, the Recording stops with a playable file, the red X and the sound. A Memory buffer keeps running; a Disk buffer stops with notice (R13).
- A Monitor Recording works with or without a game, alongside everything else, and is saved in "Monitor Recordings". The app's own windows never appear in it (R21, PLAN.md §7.1).
- Recordings in progress raise epic-replay-buffer-clips' "file being written" signal, so the Library locks them. Verified integrated on the builder's PC.

## Boundaries

- Out: rendering the Overlay and sounds (epic-overlay-feedback), Library display (epic-library-cleanup), and splitting a Recording by size or time (initiative Boundaries).
- Handoff: the "Recording running" flag that epic-game-detection-profiles' Target Game rule reads (R24), replacing its stub. The "file being written" signal for Recordings goes to epic-library-cleanup.

## References

- parent — _bmad-output/initiative-replay-buffer/initiative-replay-buffer.md, Requirements R9–R13, R21
- architecture — _bmad-output/initiative-replay-buffer/architecture-replay-buffer/architecture-replay-buffer.md, AD-3, AD-5, AD-7, AD-8, AD-9, AD-11, AD-12, AD-15, AD-16, AD-17, AD-18, AD-19, AD-21, AD-22
- addendum — _bmad-output/initiative-replay-buffer/prd-replay-buffer/addendum.md, Precedence (encoder restarts), Review-driven mechanism notes (fragment flushing)
- prd — _bmad-output/initiative-replay-buffer/prd-replay-buffer/prd-replay-buffer.md, FR-8 to FR-14, FR-22, FR-23
- architecture input — PLAN.md, §3, §7.1, §8 (T-12, T-18)

## Notes

- Decision: 2026-11-02, refined unattended from the initiative's lines, the PRD FRs and the architecture ADs the epic cites. The Contracts already hold the `recordingsEnabled` switch, the `recordToggle`, `recordStart`, `recordStop` and `monitorRecordToggle` Hotkeys, `recordHotkeyMode`, `monitorRecordingMonitor`, `recording-interrupted` and an empty `Recording.cs`, and story 1.1's fragmented MP4 muxer already cuts 1 s fragments.
- Decision: 2026-11-02, the tracer bullet is story 1: in a game with Recordings on, the Record Hotkey starts a Recording sink on the Replay Buffer's encoder chain, written as fragmented MP4 to the allocator's path, and a second press finishes it. The builder records a minute of a game and plays the file.
- Decision: 2026-11-02, the least-certain item is crash safety with at most 1 s lost and every Track in sync, so story 2 follows the tracer and measures it by killing the engine.
- Decision: 2026-11-02, the sequencing: stories 1 to 8 all change the orchestrator's Recording code, so they run as one lane. The Monitor Recording lane (9 → 10 → 11) follows, because it reuses the writer, crash safety and disk-full handling. A Refactor sweep (12) closes the build, and a closing end-to-end check on the builder's PC (13) follows it.
- Decision: 2026-11-02, 13 entries, past the usual 8 to 12, kept in one epic: Recordings and the Monitor Recording share the writer, crash safety and disk-full path, and deliver together. Unattended default, awaiting review.
- Decision: 2026-11-02, a Recording starts at a keyframe the encoder is asked for on the first frame after the press, with each Track's audio from that frame's QPC; it doesn't reach back into the ring. Unattended default, awaiting review.
- Decision: 2026-11-02, each fragment is followed by `FlushFileBuffers`, so power loss also loses at most about 1 s; a real power cut is checked only by the builder, and only if they choose to. Unattended default, awaiting review.
- Decision: 2026-11-02, while a game Recording runs, the Replay Buffer defers settings changes that would alter its `ChainKey` until the first Reset after the Recording stops, so the two keep sharing one chain and a Monitor Recording always fits the two-chain budget. Unattended default, awaiting review.
- Decision: 2026-11-02, a forced encoder restart always ends the Recording cleanly and starts a new one at once (FR-13's second option), raising `recording-restarted` with the Recording-started sound; continuing the same file isn't attempted, because a new chain's codec headers can differ. Unattended default, awaiting review.
- Decision: 2026-11-02, on disk full the writer truncates the file to the end of its last complete fragment. A Disk-mode Replay Buffer stopped by a full disk raises `replay-stopped-disk-full` and restarts by itself once the ring's drive has room for the ring again, checked every 30 s. Unattended default, awaiting review.
- Decision: 2026-11-02, FR-23's separate Start and Stop keys for the Monitor Recording are new `monitorRecordStart` and `monitorRecordStop` Hotkey actions (unbound by default) that follow `recordHotkeyMode`, added additively under AD-22; AD-11's list of actions should gain them at the next spine update. Unattended default, awaiting review.
- Decision: 2026-11-02, the monitor picker goes on a new Recordings page in the Settings host and the Monitor Record keys on story 4.12's Hotkeys page; there's no `bmad-ux` pass, and the builder checks them by hand, as in epics 2 to 5. Unattended default, awaiting review.
- Decision: 2026-11-02, a Recording started while a ChainKey-changing setting is pending shares the Replay Buffer's current chain and settings, and the change waits until the Recording stops. While a Monitor Recording runs, a Replay Buffer chain change at a Reset tears down the old chain before building the new one, raising `reset-gap`, so three chains never run; a third chain is refused with `encoder-budget`, built in story 9. Unattended default, awaiting review.
- Decision: 2026-11-02, the set check's findings applied: story 1 waits on 3.6 for the Recordings switch, story 2 on 2.4 for runtime.json, story 7 on 5.7 for the red X; story 9 also covers a Monitor Recording's interrupted warning and full disk. Declined: splitting story 7's Disk-mode stop and restart into its own story, as it is small beside the Recording's disk-full path it shares; story 11 keeps its wait on 10 because both touch monitor enumeration in Platform.Capture.
- Decision: 2026-11-02, a closing end-to-end story on the builder's PC (hitl) follows the sweep, because the Done when ends "Verified integrated on the builder's PC".
- Decision: 2026-11-04, story 6.6 built the forced encoder restart (`CaptureSession.RestartChain`: tear the chain down, rebuild the same spec into a new ring generation, then the orchestrator ends the Recording with `encoder-restart` and starts a new one at once, raising `recording-restarted`), but **no production path calls it yet**: there is no device-lost or driver-reset detection, so only tests trigger a restart. A later story must wire a real trigger (and run the restart from the orchestrator's side, closing the window between a save's `Save` and `Write`). Epic 4's refactor sweep had removed the ring `Restart` path as unused; 6.6 restored it.
- Decision: 2026-11-04, story 6.10 added the Desktop Duplication fallback (`DesktopDuplicationCapture` behind `ICaptureSource`, chosen only in the Monitor session when WGC can't be created or started; one `monitor-capture-fallback` warning). QA notes left open: `DesktopDuplicationCapture.Failed`'s own doc still says capture continues although its thread ends (the port's doc is right); and its constructor makes one `DuplicateOutput` call, so a start while the secure desktop is up (a UAC prompt) fails the session instead of retrying. A device removal or monitor detach was never exercised on real hardware, and the cursor and rotated outputs aren't handled.
- Waits on epic-replay-buffer-clips because: it needs the capture, Track and encoding pipeline, and the "file being written" signal.
- Waits on epic-overlay-feedback because: it needs the Overlay's solid dot, the red X, and the Feedback Sounds channel.
