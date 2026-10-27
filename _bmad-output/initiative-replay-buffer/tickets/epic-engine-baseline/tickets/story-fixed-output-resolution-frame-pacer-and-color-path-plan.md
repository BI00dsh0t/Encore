---
title: 'Fixed output resolution, frame pacer and color path'
type: 'feature'
ticket: '11'
created: '2026-10-26'
status: 'in-progress'
baseline_revision: '5f62f2caef6e55279838097e27b9f84269d8972f'
route: 'full'
route_source: 'auto'
risk: 'high'
review: ''
review_source: ''
lenses_ran: []
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/initiative-replay-buffer/architecture-replay-buffer/architecture-replay-buffer.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** A session copies each frame to the top left of the output (cropped or padded), encodes only when WGC delivers (a static window gives almost no frames; a 78 fps game gave ≈ 55 fps), holds ≈ 128 ms (p99) of frames inside NVENC, and allocates ≈ 200 KB/s with 4 Gen2 GCs a minute (R1, R11, AD-7; spike 1.2's Decision).

**Approach:** Scale every frame on the GPU into the fixed output size, letterboxed. A pacer on the encoder thread emits exactly one frame per slot of the configured fps, repeating the last frame when nothing new arrived. NVENC returns each packet as soon as it's encoded. The per-frame WGC calls stop allocating.

## Boundaries & Constraints

**Always:**
- Output size, fps, codec: the snapshot's `video`, as story 10 made it. Letterbox: content fitted inside the output with its aspect ratio kept, centred, black bars, even-aligned rect. Content equal to the output is a plain `CopySubresourceRegion`.
- Pacer: slot n is at anchor + n/fps; PTS = n × 90000/fps. A new frame takes the slot nearest its capture QPC if that slot is free, else it's held for the next slot, where a newer held frame replaces it. A slot with no new frame by its time + ½ slot repeats the last frame. More than 4 slots behind (encoder overloaded): jump to the current slot, counting skipped slots. PTS stay strictly increasing.
- Capture→encoded latency is recorded only for new frames: packet out minus the frame's capture QPC.
- Decision (2026-10-26, user): scaling uses the D3D11 VideoProcessor, BGRA → BGRA, with the letterbox as its dest rect and a black background; NVENC still gets BGRA, so colour path X1 is kept and the VP is used only for scaling (content equal to the output skips it). Recorded in the epic Notes. `--color x2` stays a working debug option (the same blit writes NV12).
- Decision (2026-10-26, user): the WGC projection stays for setup and the `FrameArrived` subscription; the per-frame calls (`TryGetNextFrame`, `ContentSize`, `SystemRelativeTime`, `Surface`, `Close`) go through raw ABI vtables as AD-14 hand-offs. Recorded in the epic Notes, no spine change.
- Decision (2026-10-26, user): in the verify, "fullscreen" is a borderless window covering the whole monitor; exclusive fullscreen isn't tested.
- NVENC `delay 0`; QSV `async_depth 1`. AMF and libx264 keep their defaults.
- WGC `MinUpdateInterval` = ½ slot, so a game faster than the fps fills every slot with a new frame.
- Nothing managed allocated per frame or per audio buffer on any capture, pacer, encode or audio thread (AD-7). Gen2 = 0 in steady state.
- `debug-capture` keeps its options, exit codes and report; `--encode-thread` is accepted and has no effect (the encoder thread is always on); `--stages wgc|copy` still stop before draw / before encode. The stats report gains `duplicates` (repeated slots), `freshSlots`, `skippedSlots` and `heldReplaced`, additively.
- Results (latency, alloc/s, GCs, fresh-slot %) from the verify runs are recorded in the epic's Notes, with the game rerun left to story 14.
- 0 warnings; NativeAOT publish 0 IL2xxx/IL3xxx, no new suppressions. P/Invoke only in `Platform.*`; the pure pacer and letterbox maths in `ReplayBuffer.Media`. Contracts unchanged.

**Never:** Changing `SessionSpec`, the orchestrator or IPC. Retarget changes. Audio Track work (story 12). Benchmark FPS-loss runs in a game (story 14). New Output Settings. Per-codec tuning beyond the delay options above. Elevation or GPU priority changes.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Window = output | 1920×1080 window, 1920×1080 output | full-frame copy, no bars | No error expected |
| Narrower content | 1440×1080 into 1920×1080 | 240 px black bars left and right | No error expected |
| Wider content | 2560×1080 into 1920×1080 | 1920×810, bars top and bottom | No error expected |
| Resize mid-capture | 1280×720 → 1920×1080 borderless → monitor-sized → 1024×768 bordered | one file, 1920×1080 throughout, PTS step exactly 1/fps | No error expected |
| Static window | window paints once, 5 s at 30 and 60 fps | frames = 5 × fps ± 2 %, uniform PTS | No error expected |
| Game faster than fps | window repainting ≥ 2× fps | ≥ 95 % of slots fresh | No error expected |
| Encoder overload | pacer > 4 slots behind | jumps to now, `skippedSlots` counts it | No error expected |
| VP input view refused | WGC texture can't take a VP input view | copy into an owned texture, then scale | logged once |

</frozen-after-approval>

## Code Map

- `src/ReplayBuffer.Engine/Sessions/CaptureSession.cs` -- `OnFrame` (draw + submit under `_gate`), `Draw` (X1 copy / X2 converter), `StartOutput` (pool, encoder, encoder thread), `End` (completes the encoder thread before `Finish`), `WriteStats`. Main rewiring point.
- `src/ReplayBuffer.Platform.Capture/WindowCapture.cs` -- `OnFrameArrived` uses projection objects per frame; `GetTexture` (`MarshalInterface.FromManaged`). `MinUpdateInterval` from `fps`.
- `src/ReplayBuffer.Platform.Capture/D3D11Device.cs` -- `CopyFrame` (crop/pad), `Clear`, `Device`/`Context` internals. `Nv12Converter.cs` -- VP setup, cached output views (AddRef per texture); generalise into the scaler.
- `src/ReplayBuffer.Platform.FFmpeg/VideoEncoder.cs` -- `TryAcquire(qpc)` assigns PTS from QPC and drops non-increasing; `Encode`/`Drain` record latency from packet PTS; `SetOptions` per family. Keep the VFR API for the probe and FFmpeg tests; add slot-PTS submit with a capture QPC.
- `src/ReplayBuffer.Platform.FFmpeg/EncoderThread.cs` -- replaced by the paced encoder thread. `src/ReplayBuffer.Media/BoundedHandoff.cs` -- needs a non-blocking take.
- `src/ReplayBuffer.Engine/CaptureStatsReport.cs` -- `VideoReport`, `ResourceSnapshot` (alloc + GC counts, warm-up 10 s). `src/ReplayBuffer.Engine/CommandLine.cs` -- `--encode-thread`, `--stages`, `--color`.
- `src/ReplayBuffer.Media/FrameCadence.cs`, `QpcClock.cs` -- integer slot maths to reuse.
- Tests: `tests/ReplayBuffer.Engine.Tests/OutputSettingsTests.cs` (pwsh window, orchestrator session, ffprobe, VLC helpers to reuse), `CaptureEndTests.cs:157,218` (assert `arrived == encoded + duplicates`, to redefine), `tests/ReplayBuffer.Platform.FFmpeg.Tests/EncodeExperimentTests.cs` (uses `Nv12Converter`, `EncoderThread`).
- `tools/measure-path-a.ps1` -- `-thread` suffix maps to `--encode-thread`; keep working.
- Builder's PC: RTX 5090, two 2560×1440 monitors. Known flaky: `HotkeyServeTests` latency test when injected keys aren't delivered.

## Tasks & Acceptance

**Execution:**
- [ ] `src/ReplayBuffer.Media/FramePacer.cs`, `Letterbox.cs` -- pure slot/hold/repeat/skip decisions and PTS; fit rect maths.
- [ ] `src/ReplayBuffer.Platform.Capture/FrameScaler.cs` (from `Nv12Converter.cs`) -- VP scaler: input views cached per WGC texture, processor rebuilt on content-size change, source rect = content ∩ texture, dest rect = letterbox, black background, BGRA or NV12 output; copy-in fallback.
- [ ] `src/ReplayBuffer.Platform.Capture/WindowCapture.cs` (+ `NativeMethods.txt`) -- raw-ABI per-frame calls, `MinUpdateInterval` ½ slot.
- [ ] `src/ReplayBuffer.Platform.FFmpeg/PacedVideoEncoder.cs` (replaces `EncoderThread.cs`), `VideoEncoder.cs`, `NativeMethods.txt` -- encoder thread waits on a posted frame or a high-resolution waitable timer at the next deadline; holds one pending and the last emitted frame (`av_frame_ref`) for repeats; NVENC `delay 0`, QSV `async_depth 1`; latency per new frame.
- [ ] `src/ReplayBuffer.Engine/Sessions/CaptureSession.cs`, `CaptureStatsReport.cs`, `CommandLine.cs` -- wire scaler + paced encoder, stages, new stats fields, `--encode-thread` no-op.
- [ ] Find any remaining per-frame/per-buffer allocation (allocation-tick `EventListener` in a stats run) and remove it.
- [ ] `tests/ReplayBuffer.Media.Tests/FramePacerTests.cs`, `LetterboxTests.cs` -- every matrix row's maths, 30/50/60/120 fps PTS.
- [ ] `tests/ReplayBuffer.Engine.Tests/OutputResolutionTests.cs` -- the story verify: a pwsh window cycling the resize/mode row during a 1920×1080 60 fps session → one file, 1920×1080, ffmpeg decode with no errors, every PTS step 1/60, frame count matches duration, plays in VLC, and a 4:3 phase frame has black bars and the window colour centred; static window at 30 and 60 fps; a 20 s stats run on a fast window with audio: latency p99 < 1 slot, steady Gen0/1/2 = 0, alloc < 2 KB/s, fresh ≥ 95 %.
- [ ] Update `CaptureEndTests`, `EncodeExperimentTests`, `OutputSettingsTests` for the paced counts; epic Notes with the colour-path decision and measured results.

**Acceptance Criteria:**
- Given the builder's PC, when the verify tests run, then the resize/mode run yields one playable 1920×1080 file with no PTS break, a static window yields the configured fps, and the stats run meets the latency, GC and allocation bounds.
- Given the solution, when `dotnet build ReplayBuffer.slnx`, `dotnet test --solution ReplayBuffer.slnx` and `pwsh tools/publish-engine.ps1` run, then 0 warnings and all tests pass (the hotkey latency flake noted, not counted).

## Implementation Notes

## Plan Change Log

## Review Triage Log

## Design Notes

**Why repeats are real-time, not lazy.** Filling missing slots only when the next frame arrives would burst hundreds of encodes after a static stretch and leave a replay ring short at save time. Monitor waits round to the 15.6 ms timer tick, so the thread uses `CreateWaitableTimerExW(HIGH_RESOLUTION)`; PTS come from the slot index, never from wake time.

**Why `delay 0` is enough for latency.** Spike 2: preset, X2 and an encode thread didn't move p99; the packets sat in NVENC's async queue until later frames pushed them out. With `delay 0`, `avcodec_receive_packet` right after `send_frame` returns that frame's packet, on the encoder thread, so capture never waits.

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx` -- expected: 0 warnings, 0 errors
- `dotnet test --solution ReplayBuffer.slnx` -- expected: all pass (hotkey latency flake noted)
- `pwsh tools/publish-engine.ps1` -- expected: 0 IL2xxx/IL3xxx warnings
