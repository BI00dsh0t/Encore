---
id: 7
type: epic
title: "Library and Cleanup"
parent: initiative-replay-buffer
covers: [R27, R28, R29]
sources: []
companions: []
after: []
refined: false
risk: low
---

# Library and Cleanup

## Description

This epic is the Main App's Library. It lists every Clip and Recording, grouped and filtered by game, date and Favourite, with a thumbnail, name, game, date, length and size, and reflects changes made outside the app. Its actions are: open in the Media Player, Favourite, rename, delete (to the Recycle Bin), show in Explorer, and copy to the clipboard for Discord. Cleanup deletes the oldest non-Favourite files permanently once a size or age limit is passed.

## Outcome

The builder finds, opens and shares any Clip from one window, and the Output Folder stays within its limit without losing a Favourite (R27, R28, R29).

## Done when

- A new Clip appears within 2 s, and files changed outside the app are reflected. Grouping and filtering by game, date and Favourite works (R27).
- Every action works. A copied Clip pastes into Discord as an attachment, deleting goes through a confirmation to the Recycle Bin, and files being written are locked (R28).
- Cleanup is off until a limit is confirmed, then removes the oldest non-Favourite files only, never a Favourite or a file being written (R29). Verified integrated on the builder's PC.

## Boundaries

- Out: writing files and choosing their names (epic-replay-buffer-clips, epic-game-detection-profiles).
- Handoff: Recordings and Monitor Recordings arrive through the same "file being written" signal, raised by epic-recordings. This epic verifies the locking with Clips and a sample Recording file, and epic-hardening's soak covers live Recordings.
- Touch point: the Windows default app for .mp4 (the Media Player), and Discord's paste, both consumed only.

## References

- parent — _bmad-output/initiative-replay-buffer/initiative-replay-buffer.md, Requirements R27, R28, R29
- architecture — _bmad-output/initiative-replay-buffer/architecture-replay-buffer/architecture-replay-buffer.md, AD-1 (shell actions), AD-9 (write protocol), AD-17, AD-21

## Notes

- Waits on epic-replay-buffer-clips because: it needs Clips written into per-game folders, and the "file being written" signal.
