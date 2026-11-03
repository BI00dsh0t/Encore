---
title: 'Fixed output resolution, frame pacer and color path'
type: 'feature'
ticket: '11'
created: '2026-10-26'
status: 'done'
baseline_revision: '5f62f2caef6e55279838097e27b9f84269d8972f'
route: 'full'
route_source: 'auto'
risk: 'high'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
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
- [x] `src/ReplayBuffer.Media/FramePacer.cs`, `Letterbox.cs` -- pure slot/hold/repeat/skip decisions and PTS; fit rect maths.
- [x] `src/ReplayBuffer.Platform.Capture/FrameScaler.cs` (from `Nv12Converter.cs`) -- VP scaler: input views cached per WGC texture, processor rebuilt on content-size change, source rect = content ∩ texture, dest rect = letterbox, black background, BGRA or NV12 output; copy-in fallback.
- [x] `src/ReplayBuffer.Platform.Capture/WindowCapture.cs` (+ `NativeMethods.txt`) -- raw-ABI per-frame calls, `MinUpdateInterval` ½ slot.
- [x] `src/ReplayBuffer.Platform.FFmpeg/PacedVideoEncoder.cs` (replaces `EncoderThread.cs`), `VideoEncoder.cs`, `NativeMethods.txt` -- encoder thread waits on a posted frame or a high-resolution waitable timer at the next deadline; holds one pending and the last emitted frame (`av_frame_ref`) for repeats; NVENC `delay 0`, QSV `async_depth 1`; latency per new frame.
- [x] `src/ReplayBuffer.Engine/Sessions/CaptureSession.cs`, `CaptureStatsReport.cs`, `CommandLine.cs` -- wire scaler + paced encoder, stages, new stats fields, `--encode-thread` no-op.
- [x] Find any remaining per-frame/per-buffer allocation (allocation-tick `EventListener` in a stats run) and remove it.
- [x] `tests/ReplayBuffer.Media.Tests/FramePacerTests.cs`, `LetterboxTests.cs` -- every matrix row's maths, 30/50/60/120 fps PTS.
- [x] `tests/ReplayBuffer.Engine.Tests/OutputResolutionTests.cs` -- the story verify: a pwsh window cycling the resize/mode row during a 1920×1080 60 fps session → one file, 1920×1080, ffmpeg decode with no errors, every PTS step 1/60, frame count matches duration, plays in VLC, and a 4:3 phase frame has black bars and the window colour centred; static window at 30 and 60 fps; a 20 s stats run on a fast window with audio: latency p99 < 1 slot, steady Gen0/1/2 = 0, alloc < 2 KB/s, fresh ≥ 95 %.
- [x] Update `CaptureEndTests`, `EncodeExperimentTests`, `OutputSettingsTests` for the paced counts; epic Notes with the colour-path decision and measured results.

**Acceptance Criteria:**
- Given the builder's PC, when the verify tests run, then the resize/mode run yields one playable 1920×1080 file with no PTS break, a static window yields the configured fps, and the stats run meets the latency, GC and allocation bounds.
- Given the solution, when `dotnet build ReplayBuffer.slnx`, `dotnet test --solution ReplayBuffer.slnx` and `pwsh tools/publish-engine.ps1` run, then 0 warnings and all tests pass (the hotkey latency flake noted, not counted).

## Implementation Notes

- **Pacer** (`ReplayBuffer.Media.FramePacer`): pure, integer slot maths on QPC (`Int128`), driven by the encoder thread as "poll until nothing is due, then offer the frame". A frame whose nearest slot is still free is encoded at once, up to ½ slot ahead of the slot's time; a held frame is encoded at its slot's time; a frame not after the newest one taken is dropped (`StaleFrames`, logged, not in the report). A frame nearer a later slot than the next free one takes the next free one, so PTS never jump except by an overload skip.
- **Paced encoder** (`PacedVideoEncoder`, replaces `EncoderThread`): an auto-reset event for posted frames plus a `CREATE_WAITABLE_TIMER_HIGH_RESOLUTION` timer at `FramePacer.NextDeadline`, through `WaitForMultipleObjects`. Repeats are `av_frame_clone` of the last emitted frame (native, no managed allocation). It doesn't poll the clock until the anchor frame has been offered: the first run showed slots 1–3 counted as repeats with nothing to repeat while `StartOutput` was still opening the audio encoders, which left a PTS gap. `VideoEncoder.PoolSize` grows from 12 to 14 (the held and last frames), and `TryAcquireFrame` returns false instead of throwing when the pool is exhausted (counted into `queueDrops`).
- **Latency** is looked up per packet in a 32-entry PTS → capture-QPC ring, 0 for a repeat. With `delay 0` NVENC returns every packet from the `avcodec_receive_packet` after its `send_frame` (checked directly: 0 frames of lag), so `flushed` is now 0, and `SyntheticEncodeTests` asserts that instead of "NVENC holds frames until flush".
- **Scaler** (`FrameScaler`, from `Nv12Converter`): source rect = content ∩ texture, dest rect = `Letterbox.Fit`, output target rect = the whole output, background black (RGB). Processor, input views and output views are rebuilt together on a content-size change (views belong to their enumerator). The copy-in fallback copies the whole WGC texture into an owned BGRA texture; `ForceCopyIn` (internal, visible to the FFmpeg tests) exercises it. Bars that can't split evenly put the extra 2 px at the bottom or right (2560×1080 → 1920×810 at y = 134).
- **WGC ABI**: the pool's `IDirect3D11CaptureFramePool` pointer is taken once (`MarshalInspectable<object>.FromManaged` + QI); per frame `TryGetNextFrame`, `get_ContentSize`, `get_SystemRelativeTime`, `get_Surface` and `IClosable.Close` go through vtable slots. `Recreate` on a resize stays on the projection (rare). `MinUpdateInterval` is ½ slot, so a fast window delivers ≈ 2× fps and ≈ 1 held frame per slot is replaced (`heldReplaced` ≈ `freshSlots`).
- **Stats report**: `duplicates` now counts repeated slots and `duplicatePercent` is repeated ÷ encoded slots (the old meaning, PTS-not-increasing drops, no longer happens on the paced path); `freshSlots`, `skippedSlots` and `heldReplaced` are new; `queueDrops` includes pool misses; `encoder.encodeThread` is true whenever anything is encoded. `CaptureEndTests` now checks `framesEncoded = freshSlots + duplicates` and `latency.count + flushed = freshSlots`.
- **Allocation hunt**: the JIT build was already at ≈ 55 B/s steady once the WGC calls went raw. The NativeAOT build was at 7.5 KB/s with no GC. The allocation-tick listener can't see that rate (it ticks per ≈ 100 KB), so per-thread `GC.GetAllocatedBytesForCurrentThread` probes (temporary, removed) found it: ≈ 2.9 KB/s on the encoder thread and ≈ 2.2 KB/s on each WASAPI thread, all in `ffmpeg.AVERROR(ffmpeg.EAGAIN)` / `ffmpeg.AVERROR_EOF`, which FFmpeg.AutoGen recomputes, allocating, on every read under AOT. They are now read once (`FFmpegLibrary.ErrorAgain`/`ErrorEof`). AOT steady state after the fix: 96–353 B/s, GC 0/0/0.
- **Tests**: `OutputSettingsTests`' window, session, ffprobe and VLC helpers moved to `SessionTestKit` for `OutputResolutionTests` to share; both run in the non-parallel `OutputSettingsCollection`. `EncodeExperimentTests`' paced test asserts latency p50 < 1 slot, not p99: under the full parallel run another GPU test pushed one of its 30 frames to 29 ms. The p99 bound is the stats verify's, which runs alone. `FrameScalerTests` covers narrower, wider and equal content, X1, X2 and copy-in, and a texture larger than its content.
- **Measured on the builder's PC** (also in the epic Notes):
  - Stats run, 20 s, fast 1920×1080 window at 60 fps, both audio devices: latency p50 0.8 ms, p99 1.6–2.0 ms (JIT) and 1.9–2.2 ms (AOT), against ≈ 128 ms before. Steady state allocated 53–58 B/s (JIT), GC 0/0/0. Fresh slots 1199 of 1202 (99.8 %), 0 skipped, 0 queue drops.
  - Resize/mode run: one 1920×1080 file, 543 frames over 9 s, every PTS step 1500/90000, ffmpeg decodes it with no error, VLC plays it. The bordered 1024×768 phase (captured by WGC at 1010×761) is pillarboxed at 242,0, 1434×1080.
  - Static window: 152 frames at 30 fps and 302 at 60 fps, over 5 s.
  - All 516 JIT tests pass; all 180 Engine tests pass against the NativeAOT publish (9.5 MB, 0 IL2xxx/IL3xxx).
- WGC's `SystemRelativeTime` can be later than QPC at packet out: for a window repainting at ≈ 40 fps on a WinForms timer, 149 of 158 samples came out negative and were recorded as 0. It looks like the composition target time, not the paint. For a fast window none did.
- Re-verification after the review fixes (`15319dd`): build 0 warnings, publish 0 IL2xxx/IL3xxx. One full run failed `EngineConnectionTests.ASecondAppConnectsToTheRunningEngineWithoutLaunchingOne` ("engine didn't answer within 30 s") while the GPU-heavy Engine tests ran beside it; the App tests passed 53/53 twice alone, and the next full run passed 517/517. It reads as a load-timing flake, not this change; noted for story 15.

## Plan Change Log

## Review Triage Log

**Step 3 matrix audit:** every matrix row has a passing test: `LetterboxTests` and `FrameScalerTests` (window = output, narrower, wider, copy-in), `OutputResolutionTests` (resize/mode, static 30 and 60 fps, fast window ≥ 95 % fresh), `FramePacerTests` (overload skip). 516/516 ran, 0 skipped.

**Pass 1 (quick, 2026-10-26):** high 0, medium 1, low 5, false 0, maybe-false 0.

| # | Finding | Verdict | Route | Evidence / action |
|---|---------|---------|-------|-------------------|
| 1 | The anchor frame reaches the encoder thread only after `StartOutput`, so at 120 fps the first catch-up is > 4 slots late and is treated as overload: PTS gap after slot 0, counted as `skippedSlots` | medium | patch | `StartOutput` took ≈ 3 slots at 60 fps (Implementation Notes), so ≈ 6 at 120. Fix: the overload skip applies only once the pacer has caught up once; start-up lateness is filled with repeats. Test `ALateAnchorIsFilledWithRepeatsNotSkipped`. Commit `15319dd`. |
| 2 | X2 with empty content clears an NV12 pool texture through a single-format RTV, which D3D11 refuses | low | reject | Real, but only under the `--color x2` debug option, and only when WGC reports zero-size content. Fixing it needs a new NV12 clear path. |
| 3 | `av_frame_clone` throwing in `PacedVideoEncoder.Emit` leaks the frame and ends the thread, leaking frames posted after it | low | reject | Only on native out-of-memory, when the session fails anyway. Fixing it means restructuring the loop's fault handling. |
| 4 | `Drain` counts every packet drained at flush as `flushed`, repeats included, breaking `freshSlots == latency.count + flushed` for encoders that hold frames (AMF, libx264) | low | patch | Stats-only. Fix: count a flushed packet only when its PTS maps to a new frame's capture QPC. Commit `15319dd`. |
| 5 | `duplicatePercent` changes meaning, not just additively; `measure-path-a.ps1` compares it | low | reject | The intent gives `duplicates` its new meaning (repeated slots), and the percentage follows it. Within one script run every condition uses the new meaning; the old-path meaning can't happen on the paced path. |
| 6 | `VideoEncoder.cs:224` comment names the deleted `Nv12Converter` | low | patch | Direct correction to name FrameScaler. Commit `15319dd`. |

## Design Notes

**Why repeats are real-time, not lazy.** Filling missing slots only when the next frame arrives would burst hundreds of encodes after a static stretch and leave a replay ring short at save time. Monitor waits round to the 15.6 ms timer tick, so the thread uses `CreateWaitableTimerExW(HIGH_RESOLUTION)`; PTS come from the slot index, never from wake time.

**Why `delay 0` is enough for latency.** Spike 2: preset, X2 and an encode thread didn't move p99; the packets sat in NVENC's async queue until later frames pushed them out. With `delay 0`, `avcodec_receive_packet` right after `send_frame` returns that frame's packet, on the encoder thread, so capture never waits.

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx` -- expected: 0 warnings, 0 errors
- `dotnet test --solution ReplayBuffer.slnx` -- expected: all pass (hotkey latency flake noted)
- `pwsh tools/publish-engine.ps1` -- expected: 0 IL2xxx/IL3xxx warnings
