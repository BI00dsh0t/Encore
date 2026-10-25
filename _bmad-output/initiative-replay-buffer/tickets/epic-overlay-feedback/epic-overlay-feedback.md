---
id: 5
type: epic
title: "Overlay and Feedback Sounds"
parent: initiative-replay-buffer
covers: [R6, R8]
sources: []
companions: []
after: []
refined: false
risk: medium
---

# Overlay and Feedback Sounds

## Description

This epic tells the user what happened without breaking their focus. It provides:
- An always-on-top Overlay, outside the game process: a hollow or solid dot, a green saving icon through the Save Cooldown, a message queue, the mic-muted icon, and the disk-full red X.
- Feedback Sounds, each of which can be switched off.
- The Main App status line.

It starts by measuring Q-4: which of the builder's games run in exclusive fullscreen, and whether the Overlay costs FPS in borderless.

## Outcome

In any game, the builder knows a Clip saved or failed, by sight where the Overlay can show and by sound everywhere, with no FPS cost beyond NFR-1 (R6, R8).

## Done when

- The Q-4 measurement is recorded, and the Overlay's behaviour in exclusive fullscreen is decided from it.
- The dot appears or disappears within 1 s of a state change, the saving icon within 250 ms of the press, and messages queue. The Overlay never appears in captured video and never takes focus from a game (R6).
- In an exclusive-fullscreen game, a saved Clip and a failed save can be told apart by sound alone. Each sound can be switched off, and the status line shows state and warnings (R8).
- The Overlay's FPS cost is included in the NFR-1 benchmark. Verified integrated on the builder's PC.

## Boundaries

- Out: the events themselves (epic-replay-buffer-clips and epic-recordings emit them; epic-multi-track-audio supplies the mute state).
- Handoff: the Overlay and sound channel used by epic-recordings for the solid dot, the red X, and the Recording started/stopped notices. This epic verifies those with a stub Recording state; epic-recordings verifies them for real.

## References

- parent — _bmad-output/initiative-replay-buffer/initiative-replay-buffer.md, Requirements R6, R8; Notes (Q-4)
- addendum — _bmad-output/initiative-replay-buffer/prd-replay-buffer/addendum.md, Review-driven mechanism notes (Overlay and independent flip)

## Notes

- Waits on epic-replay-buffer-clips because: it displays that epic's Clip save, saving and failure events and its capture state.
