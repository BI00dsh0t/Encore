---
id: 3
type: epic
title: "Game Detection and Game Profiles"
parent: initiative-replay-buffer
covers: [R1, R23, R24, R25, R26]
sources: []
companions: []
after: []
refined: false
risk: medium
---

# Game Detection and Game Profiles

## Description

This epic decides what is being captured, and where files go. It detects games (fullscreen or borderless, Windows-identified, or added by hand), keeps a game detected until it exits, and picks one Target Game by the 10 s focus rule unless a Recording holds the current one. It runs the Replay Buffer's lifecycle: start, and stop 30 s after exit. It creates Game Profiles automatically, inheriting Global Settings, handles Ignored, and resolves the per-game folder and file name that later epics write to.

## Outcome

Launching any game makes it the Target Game within 5 s, with its own Game Profile and folder, and the capture follows it through alt-tab and window-mode changes (R1, R23, R24, R25, R26).

## Done when

- A borderless game is detected within 5 s, a hand-added windowed game is detected, and the app's own windows never are. Minimizing or switching to windowed mode doesn't end detection (R23).
- With two games running, 10 s of continuous focus switches the Target Game, but a running Recording blocks the switch. This is verified with a stub Recording flag until epic-recordings provides the real one (R24).
- The Replay Buffer's start and stop follow the Target Game, including the 30 s after it exits (R1).
- The first detection creates a renameable Game Profile, named after the product and inheriting Global Settings. Ignored stops the app from treating it as a game, and can be undone (R25).
- The resolved path is `<Output Folder>\<Game>\<Game>_<yyyy-MM-dd_HH-mm-ss>.mp4`, with characters Windows doesn't allow replaced and no overwrites; renaming a profile affects new files only (R26). Verified integrated on the builder's PC.

## Boundaries

- Out: what the Replay Buffer stores and how Clips are saved (epic-replay-buffer-clips); the per-profile Track Layout and Output Settings overrides (R17, epic-replay-buffer-clips); Recordings (epic-recordings).
- Handoff: this epic exposes the Target Game, the active Game Profile, and the resolved file path to epics 4, 6 and 7.

## References

- parent — _bmad-output/initiative-replay-buffer/initiative-replay-buffer.md, Requirements R1, R23, R24, R25, R26
- prd — _bmad-output/initiative-replay-buffer/prd-replay-buffer/prd-replay-buffer.md, §3 Glossary (Game Detection, Target Game, Game Profile)

## Notes

- Waits on epic-engine-baseline because: it needs the engine process and settings store, and window capture to point at the Target Game.
