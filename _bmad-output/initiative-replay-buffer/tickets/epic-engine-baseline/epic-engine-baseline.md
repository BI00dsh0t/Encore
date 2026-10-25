---
id: 1
type: epic
title: "Engine baseline and video capture"
parent: initiative-replay-buffer
covers: [R20, R22, R31, R33, R36]
sources: []
companions: []
after: []
refined: false
risk: high
---

# Engine baseline and video capture

## Description

This is the platform baseline that every other epic builds on. The .NET 10 solution has a separate engine process and a Main App/tray shell, connected over IPC, plus the settings store later epics extend. Its first path, and the riskiest one, is capture of the Target Game's window → encoder → fragmented MP4 under the chosen Output Settings. A benchmark measures FPS cost and A/V drift. It settles PLAN.md's stack comparison (§6) and milestone 1.

## Outcome

The builder can capture a game window to a playable fragmented MP4 file at the chosen Output Settings, and knows the FPS cost and drift from a repeatable benchmark (initiative R36, R33).

## Done when

- A game's window is captured to an H.264 fragmented MP4 that opens in the NFR-4 players, at any offered resolution (including ultrawide) up to 120 fps, with only GPU-supported codecs offered (R20, R22).
- A resolution or window-mode change mid-capture is scaled to the fixed output resolution with no break (R20).
- The benchmark reports FPS loss at 1080p60 in a GPU-bound game, and A/V drift over 60 minutes with two audio devices on independent clocks. Both are recorded against the provisional 3% and one-frame targets (R36, R33).
- Start with Windows (to the tray, no window) and the tray's open and quit work (R31).
- The stack choice (PLAN.md §6 Path A, or the fallback) is recorded, and the app runs on the builder's PC, integrated and verified.

## Boundaries

- Out: audio Track Layouts and device management (epic-multi-track-audio), Game Detection (epic-game-detection-profiles), the Replay Buffer and Clips (epic-replay-buffer-clips). This epic only needs one or two fixed audio devices for the drift benchmark.
- Owns the shared engine process, IPC and settings store that later epics extend.

## References

- parent — _bmad-output/initiative-replay-buffer/initiative-replay-buffer.md, Requirements R20, R22, R31, R33, R36; Constraints
- addendum — _bmad-output/initiative-replay-buffer/prd-replay-buffer/addendum.md, Inputs for architecture
- architecture input — PLAN.md, §1, §3, §6
