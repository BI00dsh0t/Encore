# Replay Buffer App — Plan (rev 2)

A ShadowPlay-style "instant replay" for Windows, built on .NET 10. It records continuously into a rolling buffer you can configure, and saves the last N seconds when you press a hotkey. What sets it apart: **a separate audio track per audio device you choose** (mixer buses, mics, line-ins), plus a mixed master track.

---

## 0. Decisions log

| # | Topic | Decision |
|---|---|---|
| 1 | OBS overlap | Not a concern. We're building our own because OBS is too finicky for this job. |
| 2 | Container | **Fragmented MP4** (H.264/HEVC + AAC). |
| 3 | Mid-buffer format changes | Encode at a **fixed output resolution** (GPU scale or letterbox). If the encoder has to restart anyway, start a new **ring generation**, and save each generation as its own file. |
| 4 | Ring overwrite during save | **Ring made of reference-counted slabs**. A save pins the slabs it needs, and the writer allocates new slabs instead of overwriting pinned ones. Limit on saves running at once. |
| 5 | Ring storage | **Both modes**: `Memory` and `Disk` (preallocated file, sequential writes, low I/O priority). The UI shows the estimated size. |
| 6 | Variable frame rate from capture | A **frame pacer** at the configured FPS re-submits the last texture when nothing changes. |
| 7 | Hotkeys | Registered in the **engine process** with `RegisterHotKey`, so saving still works if the UI crashes. No low-level keyboard hook. |
| 8 | Clashes with ShadowPlay hotkeys | Not a concern. Log a failed registration and show it in the UI. |
| 9 | Audio sources | **v1: device-level only.** Render-endpoint loopback (mixer buses, speakers) and capture endpoints (mics, line-in). **No per-process capture.** |
| 10 | Mic | The mic can be recorded to **its own track** and included in the master (configurable per track). **Mic status overlay** plus a mic mute hotkey. |
| 11 | Capture privacy leaks | **Tracked, deferred** (§7.1). |
| 12 | GPU color conversion path | **Tracked, to be compared** in the spike (§6.2). |
| 13 | Hybrid GPU | **Assume one GPU** for now (constraint C1). |
| 14 | Failure modes | **Tracked as a test catalog** (§8). |
| 15 | Interop / encode stack | **Candidate paths listed, one recommended to test first, compared against set criteria** (§6). |
| 16 | Licensing | **Not distributed**, so licensing doesn't apply. A GPL FFmpeg build (libx264 etc.) is allowed. |
| 17 | Schedule risk | Milestone 1 includes **2 independent-clock audio sources plus an AOT build** (hardest problems first). |
| 18 | Extra features | Post-roll, clip folder quota, game allow-list, per-track VU meters: included (§4). |

### Constraints
- **C1** One GPU. Capture, color conversion and encode all run on the same adapter.
- **C2** Windows 11 is the primary target. Windows 10 support is best-effort.
- **C3** Personal use, not distributed.

---

## 1. Architecture

```
┌──────────── Replay.Engine (headless, NativeAOT, High priority) ─────────────────────────┐
│ Video:  WGC (DXGI Desktop Duplication fallback) ─► frame pacer (CFR) ─► fixed-res scale  │
│         ─► color path (§6.2) ─► HW encoder (NVENC / AMF / QSV, x264 fallback)            │
│ Audio:  N × WASAPI device sources (loopback | capture) ─► silence fill ─► resample 48k   │
│         ─► drift correction ─► per-track encode  +  master mixer ─► master encode        │
│ Clock:  QPC master clock; every frame/packet stamped in QPC ticks                        │
│ Ring:   ref-counted slabs (Memory | Disk), keyframe index, generations                   │
│ Save:   pin slabs [now − SaveSeconds, now + PostRoll] from prior keyframe ─► fMP4 mux    │
│ Input:  RegisterHotKey on message-only window (save, mic mute)                           │
└──────────────────────────────▲──────────────────────────────────────────────────────────┘
                               │ named pipe: commands, status, VU levels, device lists
┌──────────────────────────────┴───────────────────────────────┐
│ Replay.App (WPF tray): settings, track editor, VU meters,    │
│ mic overlay, "clip saved" toast                              │
└──────────────────────────────────────────────────────────────┘
```

### Solution layout
| Project | Contents |
|---|---|
| `Replay.Core` | Config + JSON schema, QPC clock, slab ring, keyframe index, models |
| `Replay.Interop` | Native bindings (stack chosen per §6) |
| `Replay.Capture.Video` | WGC, DXGI Desktop Duplication fallback, frame pacer, scaler |
| `Replay.Capture.Audio` | Endpoint loopback, capture devices, silence fill, resampling, drift correction, mixer, VU metering |
| `Replay.Encoding` | Video/audio encoders, fMP4 muxer |
| `Replay.Engine` | Orchestration, hotkeys, save pipeline, IPC server, game detection |
| `Replay.App` | WPF tray app, settings, overlays |
| `Replay.Tests` | Unit and integration tests, soak tests (§8) |
| `Replay.Bench` | Spike harness to compare interop and color paths (§6) |

---

## 2. Audio design (v1 = devices only)

```
Track  = { Name, Device, Gain, Muted, IncludeInMaster, Enabled }
Device = RenderLoopback(endpointId)    // e.g. Voicemeeter "VAIO3", Wave Link "Game", Sonar "Chat", speakers
       | Capture(endpointId)           // mic, line-in
```

- **Track order in the file:** track 1 is the **master mix** (the sum of tracks with `IncludeInMaster`). Tracks 2..N are the device stems, named in the MP4 metadata.
- **Pipeline per device:**
  1. Event-driven WASAPI shared mode on an MMCSS ("Pro Audio") thread
  2. Timestamps from `u64QPCPosition`
  3. Silence fill when loopback stops delivering packets
  4. Resample to 48 kHz float
  5. Adaptive drift correction against QPC (e.g. `swr_set_compensation`)
  6. AAC encode
- **Re-binding:** devices are matched by endpoint ID, with the friendly name as a fallback. `IMMNotificationClient` re-attaches devices when a mixer app restarts or a USB mic reconnects. Each track remembers its device even while that device is missing; the stem records silence and the UI shows a warning.
- **Overlap warning (heuristic):** if both a mixer bus *and* the physical device that bus feeds are in the master, warn that audio may be counted twice. This is a warning only; the user decides.
- **Mic:**
  - A mic can be its own stem and/or part of the master.
  - **Mic overlay:** a small always-on-top indicator showing live / muted. It is excluded from capture (§7.1).
  - **Mute hotkey:** silences the mic in both the stem and the master.

---

## 3. Video design
- **Capture:** WGC (monitor or window), with DXGI Desktop Duplication as the fallback for monitor capture.
- **Frame pacer:** fixed output FPS. When no new frame arrives, it re-submits the last texture with a correct timestamp.
- **Fixed output resolution:** the GPU scales or letterboxes, so a game changing resolution doesn't restart the encoder.
- **Ring generations:** a codec, HDR or bit-depth change that forces an encoder restart starts a new generation. A save that spans generations writes one file per generation (`clip_part1.mp4`, `clip_part2.mp4`).
- **GOP** of about 1–2 s. A save starts at the keyframe before the requested start.
- **Encoders:** probe NVENC, AMF and QSV at startup by trial-opening each; fall back to x264 (allowed, since nothing is distributed).

---

## 4. Features (v1 scope)

| Feature | Notes |
|---|---|
| Rolling buffer you can configure | `BufferSeconds` (maximum kept), and `Memory` or `Disk` storage with the estimated size shown |
| Several save hotkeys | Each with its own `SaveSeconds` (e.g. 30 s / 120 s) |
| **Post-roll** | Optional `PostRollSeconds` per hotkey: the save finishes N s after the press |
| Multi-track audio | Devices only (§2); master on track 1 |
| Mic overlay + mute hotkey | Overlay is excluded from capture |
| **Per-track VU meters** | In the tray flyout and settings, so you can check routing before you need a clip |
| **Game allow-list** | Optional "buffer only while an allowed game is running" (watching processes by exe name). Clips are named after the game. |
| **Clip folder quota** | Maximum GB and/or maximum age. Oldest clips are deleted first (to the Recycle Bin, configurable). |
| Clip naming | Template, e.g. `{game}_{yyyy-MM-dd_HH-mm-ss}.mp4` |
| "Clip saved" toast | Excluded from capture |

### Config sketch
```jsonc
{
  "buffer": { "seconds": 300, "storage": "Disk", "diskPath": "D:\\ReplayTemp", "maxMemoryMB": 2048 },
  "video": { "capture": "Monitor", "monitor": 0, "fps": 60, "width": 1920, "height": 1080,
             "encoder": "auto", "codec": "h264", "bitrateMbps": 40, "gopSeconds": 1 },
  "tracks": [
    { "name": "Game",  "device": { "kind": "RenderLoopback", "id": "{...}" }, "includeInMaster": true },
    { "name": "Chat",  "device": { "kind": "RenderLoopback", "id": "{...}" }, "includeInMaster": true },
    { "name": "Music", "device": { "kind": "RenderLoopback", "id": "{...}" }, "includeInMaster": false },
    { "name": "Mic",   "device": { "kind": "Capture",        "id": "{...}" }, "includeInMaster": true }
  ],
  "hotkeys": [
    { "keys": "Alt+F10", "action": "Save", "saveSeconds": 30,  "postRollSeconds": 0 },
    { "keys": "Alt+F11", "action": "Save", "saveSeconds": 120, "postRollSeconds": 5 },
    { "keys": "Alt+M",   "action": "ToggleMicMute" }
  ],
  "output": { "folder": "D:\\Clips", "nameTemplate": "{game}_{yyyy-MM-dd_HH-mm-ss}",
              "quotaGB": 200, "maxAgeDays": 0 },
  "games": { "bufferOnlyWhenRunning": false, "allowList": ["cs2.exe", "valorant.exe"] }
}
```

---

## 5. Milestones

1. **Spike + stack bake-off.** WGC → encoder → fMP4, with **2 audio devices on independent clocks** (e.g. a Voicemeeter bus and a USB mic). NativeAOT build. Run the §6 comparisons.
   - **Exit criteria:** A/V and audio-to-audio drift < 1 frame over 60 min; stack chosen.
2. **Ring + save.** Slab ring (Memory and Disk), generations, keyframe index, hotkeys in the engine, post-roll, pinned saves, toast.
3. **Full audio.** N device tracks, master mixer, silence fill, drift correction, re-binding on hot-plug, overlap warning, mic mute and overlay.
4. **UI.** Tray app, track editor, VU meters, encoder and resolution settings, size estimate, naming template, quota.
5. **Game awareness.** Allow-list, buffer-only-while-running, naming clips after the game.
6. **Hardening.** Work through the §8 catalog and the §7 deferred items.

---

## 6. Interop & pipeline paths — bake-off

### 6.1 Interop / encode stack candidates

| Path | Win32/COM/D3D | WinRT (WGC) | Audio | Encode/mux | Pros | Cons |
|---|---|---|---|---|---|---|
| **A (recommended first)** | **CsWin32** (D3D11, DXGI, WASAPI, MMDevice) | CsWinRT | CsWin32 direct | **FFmpeg libav** (Sdcb.FFmpeg / FFmpeg.AutoGen) | One interop stack; AOT-friendly source generators; full control; libav gives every HW encoder plus fMP4 | Most hand-written COM code; harder to get going |
| B | Vortice.Windows (D3D11/DXGI/MF) | CsWinRT | NAudio or CsWin32 | FFmpeg libav | Nicer D3D API, quicker to get going | AOT support uncertain; NAudio allocates on every buffer and isn't tuned for AOT; mixes two stacks |
| C | **Native C++ core DLL** (capture + color + encode + ring) with a flat C API, called via `LibraryImport` | C++/WinRT | C++ WASAPI | libav or NVENC SDK in C++ | Best performance and no GC in the hot path; easiest to borrow known C++ patterns | Two languages; harder to debug; .NET becomes only the shell |
| D | CsWin32 | CsWinRT | CsWin32 | **Media Foundation** (HW MFTs + MP4 sink) | Uses only what ships with Windows; no FFmpeg DLLs | Weak control over HW encoders; fragmented-MP4 support is shaky; multi-track muxing is painful |

**Recommendation:** build **A** first. Fall back to **C** for the hot path (capture → color → encode) only if A misses the performance targets. B and D are reference points, not front-runners. Benchmark B only if A turns out too slow to develop.

### 6.2 Color conversion candidates (red-team #12)

| Option | Description | What to measure |
|---|---|---|
| **X1 (test first)** | Feed **BGRA straight into the encoder** (NVENC/AMF accept ARGB and convert in hardware) through a libav D3D11 `hw_frames_ctx` | GPU 3D-engine use stays near 0 |
| X2 | D3D11 **VideoProcessor** BGRA→NV12 | Uses the video-processing engine, which may stay away from the 3D queue the game uses |
| X3 | Compute or pixel shader BGRA→NV12 (OBS style) | Most control (matrices, HDR later); competes with the game on the 3D queue |

Scaling to the fixed output resolution (§3) must be folded into whichever option wins (encoder-side scaling, VideoProcessor, or the shader).

### 6.3 Comparison criteria (run in `Replay.Bench`)
Each run is 30 min at 1080p60, under a GPU-bound load (a game, or a stress test pinned at 99% GPU):

| Metric | Target |
|---|---|
| Dropped or duplicated frames (excluding intentional pacer repeats) | < 0.1% |
| Capture→encoded latency, p99 | < 1 frame |
| Engine CPU | < 3% of one modern CPU |
| GPU 3D-engine use caused by the engine | as low as possible (compare X1/X2/X3) |
| Game FPS loss compared with no capture | < 3% |
| Managed allocations per second in steady state | ~0 |
| Gen0/1/2 GC count over 30 min | report; Gen2 should be 0 |
| NativeAOT build + startup | must build; report startup time and binary size |
| Lines of interop code, and how hard it was to debug | qualitative |

---

## 7. Deferred / tracked items

### 7.1 Privacy & capture leaks (red-team #11), deferred
- [ ] Apply `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` to **every window the app creates**: mic overlay, toast, tray flyout, settings.
- [ ] Decide the default capture target: window/game vs monitor (monitor capture picks up notifications, password managers and alt-tab).
- [ ] Consider "pause buffer while the secure desktop is shown / the session is locked".
- [ ] Consider pausing the buffer automatically when non-allowed apps are focused (if the allow-list is on).

### 7.2 GPU color path (red-team #12)
- [ ] Run the X1/X2/X3 comparison (§6.2). Choose one, and record the choice in the decisions log.
- [ ] Check GPU scheduling priority (`IDXGIDevice::SetGPUThreadPriority`; `D3DKMTSetProcessSchedulingPriorityClass` needs elevation). Decide whether elevation is worth it.

### 7.3 Hybrid GPU (red-team #13)
- [ ] Out of scope (C1). If it's ever needed: detect the adapter that owns the output, then do an explicit cross-adapter copy or encode with QSV on the integrated GPU.

### 7.4 Facts to verify
- [ ] Whether `u64QPCPosition` is reliable on the target devices, including virtual mixer endpoints (Voicemeeter / Wave Link / Sonar).
- [ ] WGC border removal (`IsBorderRequired`) for an unpackaged app on the target Windows 11 build.
- [ ] NVENC/AMF BGRA input through libav's D3D11 hwframes, with no hidden CPU round-trip.
- [ ] fMP4 multi-track playback in: Discord embed, Chrome, Windows Media Player, VLC, DaVinci Resolve, Premiere.

---

## 8. Test catalog (red-team #14 + the rest), to turn into tests later

| ID | Scenario | Expected behaviour | Type |
|---|---|---|---|
| T-01 | GPU driver reset / `DXGI_ERROR_DEVICE_REMOVED` | Engine rebuilds the device and encoder, starts a new ring generation, keeps buffered clips saveable | Integration (manual trigger via `dxcap -forcetdr` or a driver update) |
| T-02 | Sleep → resume | Capture and audio recover; no packets with negative or huge timestamps | Manual / soak |
| T-03 | Monitor connected / removed / primary changed | Capture re-targets or reports a clear error; no crash | Integration |
| T-04 | UAC prompt / lock screen / Ctrl+Alt+Del | Black or held frames; recovers afterwards; no generation break unless the format changes | Manual |
| T-05 | Mixer app restarts (endpoints removed and recreated) | Tracks re-bind by ID/name; stems contain silence during the gap; sync is kept | Integration |
| T-06 | USB mic unplugged / replugged | Same as T-05 | Integration |
| T-07 | Default device changes | Tracks bound to specific devices are unaffected | Integration |
| T-08 | Game changes resolution / window mode | Output resolution stays fixed; no encoder restart; no generation break | Integration |
| T-09 | HDR toggle / codec change mid-buffer | New generation; a save spanning the change produces 2 playable files | Integration |
| T-10 | Save while the ring is under write pressure (long window, slow disk) | Pinned slabs aren't overwritten; the file is complete; capture never stalls | Stress |
| T-11 | Several saves at once (hammering the hotkey) | Respects the limit on concurrent saves; extras queue or are rejected with a toast | Stress |
| T-12 | Post-roll save, then the app quits during post-roll | Partial clip is finished and playable | Integration |
| T-13 | Static screen (no new frames for 60 s) | Pacer keeps a constant frame rate; audio stays in sync | Unit / integration |
| T-14 | Loopback device with no audio playing | Silence fill; no drift once audio resumes | Unit / integration |
| T-15 | Two devices on independent clocks, 8 h | Drift < 1 frame at the end | **Soak** |
| T-16 | 8 h buffer running, Memory and Disk modes | No memory, handle or GDI growth; disk file stays at a fixed size | **Soak** |
| T-17 | UI process killed | Engine keeps buffering; hotkeys still save | Integration |
| T-18 | Engine crash mid-save | fMP4 written so far is playable up to the last fragment | Integration |
| T-19 | Clip folder quota exceeded | Oldest clips removed per policy; a clip being written is never deleted | Unit |
| T-20 | Allow-listed game starts/stops | Buffer starts/stops (if enabled); clip named after the game | Integration |
| T-21 | Hotkey registration fails (combo already taken) | Logged; shown in the UI; other hotkeys still work | Unit |
| T-22 | Mic mute hotkey | Mic silenced in both the stem and the master within ≤ 1 audio packet; overlay updates | Integration |
| T-23 | Overlay / toast exclusion | App windows absent from captured frames (once §7.1 is done) | Integration |
| T-24 | Output file compatibility | Plays with all tracks in the §7.4 player/editor list | Manual |
| T-25 | Overlap heuristic | Warning shown when a bus and its physical output are both in the master | Unit |

---

## 9. Background: how other tools capture (reference)
- **OBS Game Capture:** DLL injection (`SetWindowsHookEx` or `CreateRemoteThread` + `LoadLibrary`). Detours on `Present` / `ResizeBuffers` / `wglSwapBuffers` (vtable offsets found by a helper process). Frames passed back through shared textures, or shared memory as a fallback. Vulkan via an implicit layer. **Not used here**: anti-cheat risk.
- **OBS Display/Window Capture:** DXGI Desktop Duplication and Windows.Graphics.Capture. **This is our approach.**
- **OBS audio:** WASAPI loopback and capture per device; per-process loopback in Application Audio Capture (left out of v1 here); silence fill using QPC.
- **OBS replay buffer:** ring of encoded packets limited by time and size, trimmed at keyframes, muxed out of process. **Our ring follows the same idea**, with slabs, generations and disk mode added.
