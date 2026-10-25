---
id: 6
type: epic
title: "Recordings and Monitor Recording"
parent: initiative-replay-buffer
covers: [R9, R10, R11, R12, R13, R21]
sources: []
companions: []
after: []
refined: false
risk: high
---

# Recordings and Monitor Recording

## Description

This epic adds full sessions. In games with Recordings switched on, the Record Hotkey (one toggle key, or separate Start and Stop keys) runs a single-file Recording alongside the Replay Buffer. The Recording stops and saves when the game exits, survives crashes and power loss with at most 1 s lost, and handles a full disk and forced encoder restarts. A Monitor Recording of a chosen monitor runs by its own hotkey, with or without a game. It reuses epic-replay-buffer-clips' pipeline and epic-overlay-feedback's channel.

## Outcome

A whole session in an opted-in game ends as one playable file, even after a crash or a full disk (R9, R10, R11, R12, R13, R21).

## Done when

- With Recordings off, the Record Hotkey does nothing. Both hotkey modes work, and nothing starts a Recording automatically (R9).
- During a Recording the Overlay shows the solid dot, and the Recording started/stopped Feedback Sounds play, through epic-overlay-feedback's channel (initiative R6, R8, Recording parts).
- Saving a Clip mid-Recording leaves no glitch in either. The game exiting saves a complete file that shows in its folder (R10).
- Killing the engine mid-Recording leaves a file that plays with every Track in sync, losing at most 1 s (R11).
- A 4-hour Recording is one file. A forced encoder restart either continues the file or cleanly starts a new one, with notice (R12).
- When the disk fills, the Recording stops with a playable file, the red X and the sound. A Memory buffer keeps running; a Disk buffer stops with notice (R13).
- A Monitor Recording works with or without a game, alongside everything else, and is saved in "Monitor Recordings" (R21). Verified integrated on the builder's PC.

## Boundaries

- Out: rendering the Overlay and sounds (epic-overlay-feedback), Library display (epic-library-cleanup), and splitting a Recording by size or time (initiative Boundaries).
- Handoff: the "Recording running" flag that epic-game-detection-profiles' Target Game rule reads (R24), replacing its stub.

## References

- parent — _bmad-output/initiative-replay-buffer/initiative-replay-buffer.md, Requirements R9–R13, R21
- addendum — _bmad-output/initiative-replay-buffer/prd-replay-buffer/addendum.md, Precedence (encoder restarts), Review-driven mechanism notes (fragment flushing)
- architecture input — PLAN.md, §3, §8 (T-12, T-18)

## Notes

- Waits on epic-replay-buffer-clips because: it needs the capture, Track and encoding pipeline, and Hotkey registration.
- Waits on epic-overlay-feedback because: it needs the Overlay's solid dot, the red X, and the Feedback Sounds channel.
