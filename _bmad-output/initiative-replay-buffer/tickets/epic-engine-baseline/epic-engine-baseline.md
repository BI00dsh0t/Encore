---
id: 1
type: epic
title: "Engine baseline and video capture"
parent: initiative-replay-buffer
covers: [R20, R22, R31, R33, R36]
sources: []
companions: []
after: []
refined: true
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

## Requirements

- R1 (parent R20): Capture covers only the Target Game's window. A change in its resolution or window mode is scaled to the fixed output resolution, keeping its aspect ratio, with no break.
- R2 (parent R22): Output Settings offer common resolutions including ultrawide, frame rates up to 120 fps, bitrate, and only the codecs the GPU can encode. H.264 is the default; HEVC and AV1 are labelled as possibly not playing everywhere.
- R3 (parent R31): A setting starts the app with Windows, in the tray, with no window. The tray icon opens the Main App and quits.
- R4 (parent R33, baseline part): The benchmark measures A/V and audio-to-audio drift over 60 minutes, with two audio devices on independent clocks, against the one-frame target.
- R5 (parent R36, baseline part): The benchmark measures FPS loss at 1080p60 in a GPU-bound game, against the provisional 3% target, and can be rerun by later epics.
- R6 (architecture AD-22): The shared skeleton exists before epics 2 and 3 start: the complete settings key catalog, `SessionSpec`, the SessionOrchestrator's single-reader channel, the AudioDeviceHub port, the IPC `seq` and `state.snapshot` mechanism, and one Contracts message file per epic area.
- R7 (architecture AD-3, AD-16): The engine and the Main App talk over the per-user named pipe, with numbered events and a snapshot on connect. Each restarts the other after a crash. Tray Quit shuts the engine down, and closing the window only hides it.
- R8 (architecture AD-11, AD-6): Hotkeys registered through the engine fire while a game has focus and with the Main App closed, each stamped with the QPC of the press.
- R9 (architecture AD-4, AD-5): The settings store applies revision-checked updates and writes atomically. One `Resolve` produces effective settings, where a Game Profile override wins and everything else is inherited.
- R10 (architecture AD-14): The engine publishes with NativeAOT, with no IL2xxx/IL3xxx warnings, and encodes and muxes; otherwise the framework-dependent fallback is recorded.
- R11 (PLAN.md §6, §7.2, §7.4): The interop stack, the color conversion path, GPU priority and the facts still to verify are each decided from measurement, and recorded.

## Constraints

- (parent NFR-2, NFR-3) The app never destabilizes the game, and is anti-cheat friendly. It's bound here by architecture AD-1, AD-2, AD-7 (resource budget), AD-11 and AD-12.
- (parent PLAN.md §1) .NET 10, with the engine as a separate process from the Main App. The Stack table and Structural Seed in the architecture spine pin it.

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

## Notes

- Decision: 2026-10-25, the tracer bullet is story 1 (game window → fragmented MP4 with two audio devices on independent clocks).
- Decision: 2026-10-25, the NativeAOT pass criterion (spine Deferred) is proved by spike 3 right after the tracer, not inside story 1, so the tracer stays the thinnest path. Story 15's sweep re-checks the publish.
- Decision: 2026-10-25, the sequencing:
  - The Path A measurement spike (2) runs before any Path A adapter is built on.
  - Stories that share code form lanes: settings 4 → IPC 5 → shell 7 → supervision 8 → Hotkeys 9, and media 6 → 10 → 11 → 12.
  - Install (13) waits on the AOT outcome (3).
  - A Refactor sweep (15) closes the epic.
- Decision: 2026-10-25, the least-certain item is the Path A measurement (spike 2), so it runs before the AOT spike (3). Confirmed by the user.
- Decision: 2026-10-25, the tray menu is Open, a Start with Windows toggle and Quit, until the initiative's Q-2 settles the rest. There's no `bmad-ux` pass for this shell. Confirmed by the user.
- Decision: 2026-10-25, the epic isn't split, despite 15 entries: it's one platform-baseline deliverable. Confirmed by the user.
- Decision: 2026-10-26, Path A is kept (spike 2, R11). Measured in Cyberpunk 2077, borderless at 2560×1440 (the builder's monitor, not R5's 1080p), RT Overdrive, Frame Generation off, with PresentMon over 3 balanced passes of 5 min per condition plus a 30 min run. Confirmed by the user.
  - FPS loss with capture is 0.85 % (78.07 vs 78.74 fps; 1 % low 63.6 vs 64.1), inside the provisional 3 % target. X2, NVENC p1 with low-latency tuning, and a separate encoder thread all landed within 0.8–1.8 %, none better beyond the run-to-run spread.
  - With the NVIDIA performance overlay's frame counter also running, capture caused 15–22 % loss as single-frame hitches. Benchmarks run with that overlay off; epic-overlay-feedback re-checks it.
  - Colour path X1 (BGRA straight into NVENC) is kept: no hidden CPU round-trip (engine 0.3–0.4 % of the machine, 3D ≈ 1 %, VideoEncode ≈ 31 %). X2 costs the same and adds code; story 11 keeps X1 unless scaling needs the VideoProcessor anyway.
  - `u64QPCPosition` is reliable on Game (BEACN Mix Create) and Microphone (BEACN Mic) (PLAN.md §7.4): no error, zero or backward stamps, unbroken over 30 min, jitter p99 0.11–0.15 ms. Drift against QPC is −0.8 ppm for the game and +54 ppm for the mic, an independent clock.
  - GPU priority stays at the default: `SetGPUThreadPriority` 0, +7 and −7 all apply unelevated, with no FPS difference beyond the spread (PLAN.md §7.2). No elevation.
  - The tracer's misses go to the stories that replace its shortcuts: capture→encoded p99 ≈ 128 ms against < 1 frame, from NVENC's frames in flight (story 11); capture at ≈ 55 fps against a 78 fps game, 15 % of 60 fps slots empty, until the pacer (stories 6 and 12); ≈ 200 KB/s allocated with 4 Gen2 GCs a minute against Gen2 = 0 (story 11). No duplicate frames.
  - The figures and run folders are in spike 2's plan, `tickets/spike-path-a-measurement-fps-cost-bgra-encoder-input-and-qpc-stamp-plan.md`.
- Decision: 2026-10-26, the engine ships NativeAOT, not framework-dependent (spike 3, R10, AD-14). The spine's Deferred pass criterion is met.
  - `pwsh tools/publish-engine.ps1` (`PublishAot`, win-x64, Release, warnings as errors, which ILC inherits) publishes with 0 IL2xxx/IL3xxx warnings. No suppressions were needed. The exe is 8.9 MB, with the FFmpeg DLLs beside it in `publish/engine/`.
  - Against that exe, with `REPLAYBUFFER_ENGINE_DIR=publish/engine`, all 128 Engine tests pass. The 13 that launch the exe are among them: `serve` with the IPC snapshot, settings update and shutdown; the capture-end runs, whose file ffprobe reads with all 3 streams; and the exit codes. All 298 JIT tests also pass.
  - WGC via the SDK projection, CsWin32 COM (D3D11, DXGI, MMDevice, WASAPI) and the FFmpeg.AutoGen DllImports all work under AOT. A 5 s `debug-capture` gives H.264 3840×2088 + 2 AAC tracks, 0 dropped frames, and packet timestamps strictly increasing, the same as the JIT build.
  - Process start to first frame is 245–289 ms on AOT, against 411–1061 ms on a JIT Debug build (3 interleaved runs each; PLAN.md §6.3 row).
  - The NativeAOT link step needs `vswhere.exe` on PATH; the script adds the VS Installer folder. Story 13's publish layout builds on the script, and trims what `Directory.Build.targets` copies today: `ffmpeg.exe`, `ffplay.exe` and the PDBs. Story 15's sweep re-runs it.
- Decision: 2026-10-26, story 11 scales with the D3D11 VideoProcessor, BGRA → BGRA, with the letterbox as its dest rect and a black background. NVENC still gets BGRA, so colour path X1 is kept, and the VideoProcessor is used only for scaling; content equal to the output skips it. `--color x2` stays a working debug option: the same blit writes NV12. Confirmed by the user.
- Decision: 2026-10-26, the WGC projection stays for setup and the `FrameArrived` subscription. The per-frame calls (`TryGetNextFrame`, `ContentSize`, `SystemRelativeTime`, `Surface`, `Close`) go through raw ABI vtables as AD-14 hand-offs, so no spine change. Confirmed by the user.
- Decision: 2026-10-26, in story 11's verify "fullscreen" is a borderless window covering the whole monitor; exclusive fullscreen isn't tested. Confirmed by the user.
- Story 11 results, on the builder's PC, test windows only. The game rerun (FPS loss, fresh slots and latency in Cyberpunk 2077) is left to story 14.
  - Capture→encoded latency p99 is 1.6–2.2 ms (JIT and NativeAOT), against ≈ 128 ms in spike 2: NVENC's `delay 0` returns each packet as soon as its frame is encoded. Measured on a 20 s stats run with a fast-painting 1920×1080 window at 60 fps and both audio devices.
  - Steady state allocates 53–58 B/s on JIT and 96–353 B/s on NativeAOT, with 0 GCs of any generation, against ≈ 200 KB/s and 4 Gen2 GCs a minute. The AOT build first showed 7.5 KB/s: FFmpeg.AutoGen's `AVERROR(EAGAIN)`/`AVERROR_EOF` allocate on every read under AOT, and are now read once.
  - The pacer filled 99.8 % of slots with a new frame (1199 of 1202), with 0 skipped. A static window gives 152 frames at 30 fps and 302 at 60 fps over 5 s.
  - A window going from 1280×720 to 1920×1080, to monitor-sized borderless, to bordered 1024×768 gives one 1920×1080 file with every PTS step exactly 1/60 s. It decodes cleanly and plays in VLC, and the 4:3 phase is pillarboxed.
