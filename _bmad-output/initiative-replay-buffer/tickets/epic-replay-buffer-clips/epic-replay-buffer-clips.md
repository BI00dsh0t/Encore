---
id: 4
type: epic
title: "Replay Buffer and Clips"
parent: initiative-replay-buffer
covers: [R2, R3, R4, R5, R7, R17, R32]
sources: []
companions: []
after: []
refined: false
risk: high
---

# Replay Buffer and Clips

## Description

This is the first usable version, end to end. It covers:
- The Replay Buffer in Memory or Disk mode, with a Buffer Window that has no upper limit and a live storage estimate.
- The Save Hotkey writing a Clip that ends within one frame of the press.
- A Reset after each Clip with no gap, and the Save Cooldown debounce.
- Retryable failures, with up to two Clips writing at once.
- Configurable Hotkeys.
- Per-Game-Profile overrides of the Track Layout and Output Settings, which take effect at the next Reset.

It brings together epic-engine-baseline's encoder, epic-multi-track-audio's Tracks, and epic-game-detection-profiles' Target Game and file paths.

## Outcome

In any detected game, pressing the Save Hotkey produces a playable multi-Track Clip in that game's folder, every time (R2, R3, R4, R5, R7, R17, R32, toward SM-1).

## Done when

- Two Clips saved 10 s apart have no gap and overlap by at most one frame. Each ends within one frame of its press, and its Tracks match the video's length (R4, R5).
- Presses within the Save Cooldown are ignored, a buffer holding less than 1 s makes no Clip, a failed save leaves no file that only looks playable and retries with no Reset, and two Clips can write at once (R5, R4, R7).
- Memory and Disk modes give identical Clips. The live estimate holds (±20% for constant bitrate), and Memory mode stays within its RAM margin (R2, R3).
- A game's own Track Layout and Output Settings apply from the next Reset (R17).
- Every Hotkey can be rebound. A failed registration is reported, duplicates are refused, and Hotkeys work in-game and with the Main App closed (R32). Verified integrated on the builder's PC.

## Boundaries

- Out: the Overlay and Feedback Sounds (epic-overlay-feedback; this epic emits the save events), Recordings (epic-recordings), and the Library (epic-library-cleanup).
- Handoff: save, saving and failure events and capture state go to epic-overlay-feedback. A "file being written" signal goes to epic-library-cleanup.

## References

- parent — _bmad-output/initiative-replay-buffer/initiative-replay-buffer.md, Requirements R2, R3, R4, R5, R7, R17, R32
- addendum — _bmad-output/initiative-replay-buffer/prd-replay-buffer/addendum.md, Review-driven mechanism notes (keyframe at the press)
- architecture input — PLAN.md, §0 decisions 3–7, §3

## Notes

- Waits on epic-engine-baseline because: it needs the encoder and fragmented MP4 writer.
- Waits on epic-multi-track-audio because: it needs the Track Layouts and the mixed Track audio.
- Waits on epic-game-detection-profiles because: it needs the Target Game, Game Profiles, and per-game folders and file names.
