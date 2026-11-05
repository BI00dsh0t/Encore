---
name: Multi-monitor Monitor Recording — design
status: final
updated: 2026-11-04 (spike 11.1)
ticket: epic-multi-monitor-recording.md
---

# Multi-monitor Monitor Recording — design

The builder's words (2026-11-05): "monitor recording needs to allow selecting which monitors to record, or capture all monitors."

**Today** (stories 6.9 to 6.11, 8.3, 8.5):

- A Monitor Recording captures one monitor (`monitorRecordingMonitor`, a GDI device name, null for the primary). It uses a WGC monitor item, with DXGI Desktop Duplication as the fallback.
- It encodes on its own chain in the Monitor session, which opened its own D3D11 device. That's an open deviation from AD-7's "one device".
- It has the Global Track Layout's Tracks, and writes one crash-safe file into Monitor Recordings.
- A removed monitor ends it with `monitor-removed`, and a GPU reset restarts it into a new file.

## The question: one file per monitor, or one composited file?

| | (a) One file per monitor | (b) One composited file | (c) Both, as a setting |
|---|---|---|---|
| **What you get** | N files started and stopped together, each a full-resolution video of one monitor | One file with every selected monitor side by side as Windows lays them out, scaled to fit | A choice between (a) and (b) |
| **Engine** | N WGC monitor sources, N pacers and scalers, **N encoder chains** | N WGC monitor sources feeding one **compositor** (a GPU blit of each latest frame into one canvas texture), then the existing pacer, scaler and **one chain** | Both paths, with the compositor and N chains |
| **Encoder budget (AD-7: at most 2 chains)** | **Breaks it.** With the Replay Buffer's chain, 2 monitors need 3 chains and 3 need 4. Consumer NVENC also caps concurrent sessions in the driver (lower on older drivers), and a game Recording beside it needs another. | **Keeps it:** one chain, whatever the monitor count, so Replay Buffer + Monitor Recording stays 2 | (a)'s problem, whenever (a) is chosen |
| **GPU cost (NFR-1, R36)** | Grows with each monitor: N encodes and N scalers | One blit per monitor (cheap) plus one encode; the encode costs the same as today's one monitor at the same output size | (a)'s, when (a) is chosen |
| **Audio** | One set of Tracks. Either every file carries them (N× the audio, all identical) or only the first does (the others are silent, which is confusing) | One set of Tracks, once, as today | Both behaviours to specify and test |
| **Sync** | Each file starts at its own first frame. An editor must line them up by hand, unless every file is cut to a shared start QPC, which is new muxer work. | Inherent: one timeline (AD-6) | (a)'s problem |
| **Library** | A group: N items that are one Recording. That needs a group id in `LibraryItem` and `library.json` (additive), grouped rows, group-aware delete, Favourite and copy, and Cleanup treating the group as one | One item, as today | (a)'s work, plus both views |
| **Disk use** | N × the bitrate: three 1440p monitors at 20 Mbps is 60 Mbps | One bitrate for the whole canvas | Depends on the choice |
| **A monitor removed mid-recording (8.5)** | That monitor's file ends, and the others continue | Its region goes black, with `monitor-removed`, and the file continues. It ends only when no selected monitor is left. | Both rules |
| **A monitor arriving (All monitors)** | A new file could start for it, out of step with the others | The canvas size can't change inside a file, so a new monitor joins at the next Monitor Recording (an info notice) | Both rules |
| **GPU reset (8.3)** | N chains rebuild and N files restart (6.6's rule each) | One chain rebuilds and one file restarts | Both |
| **Desktop Duplication (6.10)** | A fallback per monitor | A fallback per monitor source, feeding the same compositor | Same |
| **Mixed refresh rates** | Each file at the Output Settings' fps, each source repeating its last frame (1.11's pacer) | The compositor runs at the Output Settings' fps and takes each source's latest frame; a 60 Hz monitor beside a 144 Hz one is sampled the same way | Same |
| **Mixed resolutions and DPI** | Each file has its own aspect ratio, letterboxed into the fixed output resolution (R20's rule), so detail is lost on odd sizes unless each file gets its own size | Laid out in virtual-desktop pixels, with the canvas's empty areas black; scaled once to fit | Same |
| **HDR** | Today's single-monitor colour path per source (HDR is out of v1, PRD §6) | The same colour path per source before the blit, so every region is SDR | Same |
| **Build and test cost** | High: N chains, budget changes, grouping in the Library and Cleanup, audio rules, and sync | Medium: one compositor, one new layout rule, and the existing chain and file | Highest: both |

### The canvas size under (b)

- **Native size:** the canvas is the bounding box of the selected monitors in virtual-desktop pixels. For example, 2560×1440 + 2560×1440 side by side is 5120×1440.
  - The positions and sizes are physical: each display's current mode (`EnumDisplaySettings`: `dmPosition`, `dmPelsWidth`, `dmPelsHeight`), never `rcMonitor`. The engine declares no DPI awareness, so GDI rectangles are virtualized. On the builder's PC, a DPI-unaware reader sees DISPLAY2 as 2560×1440 when it is 3840×2160 (spike 11.1). The builder's canvas is 2560×1440 + 3840×2160 = **6400×2160**.
- **Scaling:** it's scaled down, keeping its aspect ratio, only to fit **the codec's maximum frame size**:
  - Spike 11.1 measured NVENC on the RTX 5090: H.264 encodes up to **4096 px a side**, and HEVC and AV1 up to **8192 px a side**. Above that, the open fails with "No capable devices found".
  - Story 1.13's probe doesn't report a limit (it opens each encoder at 1280×720), so the limits are constants per codec.
  - The pixel budget (the Output area, or 3840×2160) is **dropped**, by the builder's 2026-11-05 decision that several screens are recorded at full width with HEVC or AV1. Spike 11.1 measured 6400×2160 HEVC and AV1 at 60 fps: 89 % and 76 % NVENC video-encode use, and about 3.7 points more FPS loss than one monitor.
- **Even sizes:** both sides are rounded down to even numbers (4:2:0, Keys' rule).
- **Examples:**
  - One monitor is recorded exactly as today.
  - Two 1440p monitors under HEVC give 5120×1440.
  - The builder's 1440p + 2160p under HEVC or AV1 give 6400×2160, and under H.264 4096×1382.
  - Three 1440p monitors under H.264 give 4096×768, so the picker says "Recorded as one 4096 × 768 video (scaled down)", and suggests HEVC for more detail.

### Recommendation: (b), one composited file

(b) keeps AD-7's two-chain budget and today's single-file Recording, Library item, audio and sync. Its only new engine part is a compositor in front of the existing pacer and scaler, and the GPU cost doesn't grow with each monitor beyond a blit.

(a)'s real advantage, full resolution per monitor for editing, costs extra chains the budget forbids, N× the disk, and grouping work across the Library and Cleanup. So it's **parked**, not planned. If the builder wants it later, it becomes its own epic, with an AD-7 budget change first.

(c) carries both costs and is rejected.

**For the builder:** is a scaled side-by-side video what you want? Separate per-monitor files would mean (a) and a budget change. Under H.264, three monitors come out at most 4096 px wide.

## Settings

- **The new key** is `monitorRecordingScreens`, Global-only, applied at the next session, additive under AD-22:
  - `{ mode: "all" }`: every attached monitor at the start of each Monitor Recording;
  - `{ mode: "selected", monitors: [<monitor id>, …] }`, with at least one id.
  - It defaults to null, meaning "not set".
- **Compatibility:** while `monitorRecordingScreens` is null, the engine reads the old `monitorRecordingMonitor` exactly as today: one monitor by device name, null for the primary. The App writes only the new key, and leaves the old one as it is. The old key keeps working, and nothing is migrated or removed.
- **Monitor ids:** the selection is stored by a **stable monitor id**, not the GDI device name. `\\.\DISPLAY2` can name a different monitor after a reboot or replug, which breaks R35.
  - The id is the monitor's device interface path from DisplayConfig (`DISPLAYCONFIG_TARGET_DEVICE_NAME.monitorDevicePath`). It's stable for the same monitor on the same port.
  - The engine matches by id first, then by device name for a value saved under the old key.
- **A selected monitor that isn't attached** when a Recording starts is skipped, with `warning.raised { code: monitor-missing, overlay: false }` (new, additive). If none of the selection is attached, the Recording doesn't start and raises `monitor-recording-no-screens` (new), with the usual failure sound and Overlay red X (R21's notices).
- **`DisplayMonitor`** (`monitors.list`, `monitors.changed`) gains optional fields, additively: `MonitorId`, `X` and `Y` (virtual-desktop position), `RefreshHz`, and `Hdr`. An engine that doesn't fill them leaves them at their defaults, and the App then hides the layout preview.

## The picker

The picker sits on Hotkeys → Monitor Recording, replacing 10.9's "Screen to record" combo box. It's the same section, beside the Monitor Recording key.

```
MONITOR RECORDING
  A Recording of your screens instead of a game, for anything that isn't detected as a game.
  Monitor Recording     [ Alt + Shift + F9 ]  [Set] [Clear]  Working

  Screens to record     (•) All screens   ( ) Chosen screens
                        ┌────────────────────────────────────────┐
                        │  ┌──────────┐┌──────────┐ ┌──────┐     │   layout preview: Windows' arrangement,
                        │  │    1     ││    2     │ │  3   │     │   selected screens in the accent fill,
                        │  │ primary  ││          │ │      │     │   numbered; click a screen to toggle it
                        │  └──────────┘└──────────┘ └──────┘     │
                        └────────────────────────────────────────┘
                        [✓] 1  Dell U2723QE · 2560 × 1440 · 144 Hz · primary
                        [✓] 2  LG 27GP850 · 2560 × 1440 · 165 Hz
                        [ ] 3  Portable · 1920 × 1080 · 60 Hz · HDR
                        Recorded as one 5120 × 1440 video.          [Refresh]
```

- **All screens** records every attached monitor, laid out as Windows has them, and follows monitors as they come and go between Recordings. The checkboxes then show every screen ticked and disabled.
- **Chosen screens** enables the checkboxes and the clickable preview. At least one screen stays ticked: unticking the last one isn't allowed, and a tooltip says why.
- **The rows** show the number Windows uses in Display settings, the friendly name, the resolution, the refresh rate, primary, and HDR.
  - An HDR monitor's caption adds "Recorded in SDR". HDR is out of v1.
- **The output line** gives the canvas size from the rule above, with "(scaled down)" and a hint to use HEVC when the codec limit bites.
- **Live updates:** the list and preview follow `monitors.changed` (8.5). A chosen screen that isn't attached stays listed, greyed, as "Not connected — skipped".
- **Keyboard:** the radio buttons, then each checkbox. The preview's screens are also tab stops, toggled with Space, with UIA names matching the rows.
- **Wording** follows epic 10's glossary: "screens" in labels, "Monitor Recording" as the feature's name.

## Engine shape (for the spike and story 3)

- **The Monitor session** gets one `ICaptureSource` per selected monitor: WGC, else that monitor's Desktop Duplication fallback.
  - Each source keeps its latest frame as a texture on the Monitor session's device.
  - A `MonitorCompositor` (Media) blits each latest frame into a canvas texture at its scaled layout rectangle, once per output frame at the Output Settings' fps, and hands the canvas to the existing pacer, scaler and one encoder chain.
    - The canvas goes to the pacer **once per slot, from the compositor's own slot clock**, never once per source frame. Spike 11.1 pushed on every source frame and got 160 to 946 hand-off drops and about 2,000 non-increasing stamps per 50 s run.
    - A blit costs 11 µs (1440p) to 22 µs (2160p) of GPU time.
  - With one monitor, the compositor is a pass-through, so today's output is unchanged.
- **Device:** the Monitor session's own D3D11 device, on the adapter of the primary selected monitor. This settles AD-7's open item as an explicit exception (AD-25).
  - Under the PRD's one-GPU constraint, every monitor is on that adapter.
  - A monitor on another adapter (a laptop's iGPU output) is captured through WGC's cross-adapter copy. If that fails, the monitor is skipped with `monitor-missing`, never as a crash.
- **Removal:** the source ends, its region stays black (its last frame isn't held, which would look live), and `monitor-removed` is raised. The Recording ends at its last frame only when no source is left (8.5's file-kept rule).
- **Arrival:** ignored until the next Monitor Recording, with `warning.raised { code: monitor-added-next-recording, overlay: false }` (new) under All screens.
- **GPU reset (8.3):** every source and the compositor are rebuilt with the device, and the Recording continues in a new file (6.6's rule), as today.
- **Monitor mode change** (resolution, rotation or position) during a Recording: the layout rectangle is kept and the source's new frames are scaled into it. The canvas never changes inside a file.
