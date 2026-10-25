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

This is the platform baseline that every other epic builds on. The .NET 10 solution has a separate engine process and a Main App/tray shell, connected over IPC. It also owns the pieces epics 2 and 3 would otherwise each build in parallel:
- the settings store, with the Global Settings → Game Profile override shape;
- the capture-session API that other epics attach Tracks to and point at a Target Game;
- Hotkey registration in the engine.

Its first path, and the riskiest one, is capture of the Target Game's window → encoder → fragmented MP4 under the chosen Output Settings. A benchmark measures FPS cost and A/V drift. It settles PLAN.md's stack comparison (§6), the GPU color path and scheduling priority (§7.2), and the facts still to verify (§7.4).

## Outcome

The builder can capture a game window to a playable fragmented MP4 file at the chosen Output Settings, and knows the FPS cost and drift from a repeatable benchmark (initiative R36, R33).

## Done when

- A game's window is captured to an H.264 fragmented MP4 that opens in the NFR-4 players, at any offered resolution (including ultrawide) up to 120 fps, with only GPU-supported codecs offered (R20, R22).
- A resolution or window-mode change mid-capture is scaled to the fixed output resolution with no break (R20).
- The benchmark reports FPS loss at 1080p60 in a GPU-bound game, and A/V drift over 60 minutes with two audio devices on independent clocks. Both are recorded against the provisional 3% and one-frame targets. This is the baseline part of R36 and R33; epics 2, 5 and 8 deliver the rest.
- Start with Windows (to the tray, no window) and the tray's open and quit work (R31).
- A Hotkey registered through the engine fires while a game has focus and with the Main App closed. It's the registration that epic 2's Mic Mute Hotkey and epic 4's rebinding (R32) build on.
- The settings store saves and reloads Global Settings, with a Game Profile overriding one value and inheriting the rest.
- The shared skeleton (architecture AD-22) is in place before epics 2 and 3 start:
  - the complete settings key catalog, with types, defaults, overridability and when each key applies (AD-4);
  - `SessionSpec { kind, target, snapshot, audio }`;
  - the SessionOrchestrator's single-reader `Channel` (AD-19);
  - the AudioDeviceHub port (AD-20);
  - the IPC `seq` and `state.snapshot` mechanism, with the bounded writer queue (AD-3);
  - one Contracts message file per epic area (`Audio.cs`, `Game.cs`, `Capture.cs`, `Library.cs`, `Recording.cs`, `Feedback.cs`).
- The engine publishes with NativeAOT, with no IL2xxx/IL3xxx warnings, and still does a working encode and mux; otherwise the fallback to a framework-dependent engine is recorded (AD-14).
- The stack choice (PLAN.md §6 Path A, or the fallback), the color path and GPU-priority decision (§7.2), and the §7.4 fact checks are recorded. The app runs on the builder's PC, integrated and verified.

## Boundaries

- Out: audio Track Layouts and device management (epic-multi-track-audio), Game Detection (epic-game-detection-profiles), the Replay Buffer and Clips (epic-replay-buffer-clips). This epic only needs one or two fixed audio devices for the drift benchmark.
- Owns the shared engine process, IPC, settings store and override shape, capture-session API and Hotkey registration that later epics extend.
- Handoff: the benchmark harness, which epic-overlay-feedback reruns with the Overlay on (R36 Overlay part) and epic-hardening uses for the final conformance run (R33, R36).

## References

- parent — _bmad-output/initiative-replay-buffer/initiative-replay-buffer.md, Requirements R20, R22, R31, R33, R36; Constraints
- architecture — _bmad-output/initiative-replay-buffer/architecture-replay-buffer/architecture-replay-buffer.md, AD-1–AD-4, AD-6, AD-7, AD-9, AD-11, AD-12, AD-14, AD-16, AD-19, AD-20, AD-22; Stack; Structural Seed
- addendum — _bmad-output/initiative-replay-buffer/prd-replay-buffer/addendum.md, Inputs for architecture
- architecture input — PLAN.md, §1, §3, §6
