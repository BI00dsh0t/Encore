---
id: 11
type: epic
title: "Multi-monitor recording"
parent: initiative-replay-buffer
covers: [R39]
sources: []
companions: [_bmad-output/initiative-replay-buffer/tickets/epic-multi-monitor-recording/design-multi-monitor.md]
after: []
refined: true
risk: high
---

# Multi-monitor recording

## Description

A Monitor Recording records the screens the builder chooses, or all of them, instead of one. The selected monitors are composited into one video, side by side as Windows lays them out and scaled to fit, so a Monitor Recording stays one file with one set of Tracks on one encoder chain. Monitors are chosen beside the Monitor Recording key on Hotkeys, with a checkbox per screen and a layout preview. The design, the options weighed and the recommendation are in [design-multi-monitor.md](design-multi-monitor.md).

## Outcome

The builder presses the Monitor Recording key and gets one playable file holding every screen they chose, or every attached screen, with every Track, at no more encoder cost than today's one-monitor Recording (R39).

## Requirements

- R1 (parent R39, R21; architecture AD-25, AD-22): A Monitor Recording records either every attached monitor or a chosen set of one or more, kept in settings by a stable monitor id. One monitor records exactly as today, and an install that only ever set the old single-monitor setting keeps recording that monitor.
- R2 (parent R39, R21; architecture AD-25, AD-7): Several monitors are recorded as one video with one set of Tracks. Each monitor is placed as Windows arranges it, and the whole is scaled down, keeping its aspect ratio, to fit the codec's largest frame side (H.264 4096 px; HEVC and AV1 8192 px). There is no pixel budget: HEVC and AV1 keep each screen's native resolution (the builder's 2026-11-05 decision, spike 11.1). It runs at the Output Settings' frame rate on one encoder chain, within AD-7's budget, and is crash-safe and disk-full safe like any Recording.
- R3 (parent R39, R21; architecture AD-25, AD-12): Each monitor is captured with WGC, else with Desktop Duplication, independently. Monitors with different resolutions, refresh rates or DPI, and HDR monitors (recorded in SDR), all appear correctly in the one video.
- R4 (parent R39, R35; architecture AD-25): A recorded monitor removed mid-Recording leaves its area black and raises `monitor-removed`; the Recording ends, file kept, only when no recorded monitor is left. A monitor that arrives joins at the next Monitor Recording. A chosen monitor that's missing at the start is skipped with a notice, and none present means no Recording, with the usual failure notice. A GPU reset continues in a new file, as today.
- R5 (parent R39, R38): Beside the Monitor Recording key on Hotkeys, the builder chooses All screens or Chosen screens, ticks screens by number, name, resolution, refresh rate and primary, and sees a layout preview they can click and the size of the video they'll get. It follows monitors as they come and go, and is fully keyboard-reachable.

## Constraints

- (architecture AD-22) Additive only: the new `monitorRecordingScreens` key defaults to null, the old `monitorRecordingMonitor` is still read while it's null, `DisplayMonitor` gains optional fields, and new warning codes are added. Nothing is renamed or removed.
- (architecture AD-7, AD-25) One encoder chain for a Monitor Recording, whatever the monitor count; the Monitor session keeps its own D3D11 device as AD-25's exception.
- (architecture AD-12) WGC monitor items, with Desktop Duplication only as the per-monitor fallback; never for game capture.
- (initiative PRD §6) One GPU; HDR stays out of v1, so HDR monitors are recorded through the SDR colour path.
- (CLAUDE.md) Lean tests; GPU capture and encode runs are Heavy (`Explicit`), run one project at a time.

## Done when

- A Monitor Recording of two or three of the builder's monitors, and of All screens, gives one playable file with every Track, laid out as Windows has them, at the expected scaled size (R1, R2).
- The encoder-chain count with the Replay Buffer running is still 2, and FPS cost in a game with a multi-monitor Monitor Recording running stays within NFR-1 (R2).
- Unplugging a recorded monitor blacks its area and the file continues, a GPU reset gives a second playable file, and a Desktop Duplication fallback on one monitor still composites (R3, R4).
- The Hotkeys picker chooses screens and shows the layout and output size, in both themes (R5). Verified integrated on the builder's PC.

## Boundaries

- Out: one file per monitor (design option (a)) and offering both (option (c)): parked, see the Notes.
- Out: a configurable canvas size, per-monitor scaling choices, picture-in-picture layouts, and recording part of a monitor.
- Out: HDR output (PRD §6), and more than one GPU.
- Out: changes to game capture, the Replay Buffer, or game Recordings.
- Touch point: Windows' display configuration (DisplayConfig monitor device paths, the virtual-desktop layout), consumed only.

## References

- parent — _bmad-output/initiative-replay-buffer/initiative-replay-buffer.md, Requirements R39 (and R21, R35, R36)
- design — _bmad-output/initiative-replay-buffer/tickets/epic-multi-monitor-recording/design-multi-monitor.md
- design — _bmad-output/initiative-replay-buffer/tickets/epic-ux-pass-2/ux-pass-2/EXPERIENCE.md (Hotkeys, the glossary)
- architecture — _bmad-output/initiative-replay-buffer/architecture-replay-buffer/architecture-replay-buffer.md, AD-6, AD-7, AD-12, AD-19, AD-22, AD-25

## Notes

- Spike 11.1 report, 2026-11-04 (details in `tickets/spike-spike-composite-several-monitors-into-one-encoded-video-plan.md`): **GO for 11.3's design.**
  - **The PC:** DISPLAY1 is 2560x1440 at 240 Hz (primary, at 0,0) and DISPLAY2 is **3840x2160 at 144 Hz** (at 2560,0), not 2560x1440. A DPI-unaware reader sees a virtualized 2560x1440. The canvas is **6400x2160**, from each display's current mode (`EnumDisplaySettings`), never `rcMonitor`. The Meta Virtual Monitor adapter has no active output and never appears in the monitor list.
  - **FPS on the synthetic load** (80 fps GPU-bound, baseline 79.31; two balanced passes, 45 s each), all beside the game capture:

    | Condition | FPS loss |
    |---|---|
    | Game capture alone | **2.85 %** (NFR-1 pass) |
    | + today's one-monitor Monitor Recording | **5.15 %** |
    | + two monitors composited, HEVC 6400x2160 | **8.83 %** |
    | + the same in AV1 | **8.80 %** |
    | + H.264 fitted to 4096x1382 | **9.27 %** |
    | + Desktop Duplication, HEVC | **7.06 %** |

    The passes spread widely: the HEVC full-width figure is 9.99 and 7.67 %, AV1 9.97 and 7.63 %, and the game capture alone 2.27 and 3.43 %, so its NFR-1 pass is within noise of 3 %. HEVC pass 1 overlapped another process's NVENC sessions. The composite adds about 2.5 to 4.8 points over one monitor. Engine 3D stays ≤ 5.5 %, and NVENC use is about 30 % in total at 6400x2160 60 fps p5 (the summary's video-encode column is 3× over-counted). So the extra cost isn't the encode or the blits, and its cause isn't measured yet.
  - **Blits:** 10.9 µs per 2560x1440 copy and 22.0 µs per 3840x2160 copy.
  - **NVENC sessions:** 2 during each composite run (AD-7 held), except for 1 to 4 sessions from another process during HEVC pass 1.
  - **Codec limits (measured; the probe doesn't report them):** H.264 4096 px a side; HEVC and AV1 8192 px a side. Above that, the open fails with "No capable devices found".
  - **Mixed refresh** (240 Hz and 144 Hz, at 60 fps output): fresh frames in 98.5 to 99 % of slots, with per-source p99 intervals of 29 to 34 ms. No stutter.
  - **Desktop Duplication** composites the same way, but at 47 to 54 fps per source with 12 % missed slots: acceptable for a fallback.
  - **Cross-adapter:** not testable (one GPU).
  - **The files:** every composite file decoded with 0 errors and showed Windows' arrangement. VLC (installed under Program Files (x86)) played a 10 s composite to the end headless, and its frame showed the same layout.
- Decision: 2026-11-04 (spike 11.1), **the pixel budget, under the builder's full-width decision:** HEVC and AV1 encode the full canvas, limited only by 8192 px a side; a wider canvas is fitted to 8192, aspect kept. H.264 fits the canvas to 4096 px a side. AD-25's 3840x2160 area budget is dropped. AD-25's Canvas rule needs this edit before 11.3. The coordinator schedules that AD-25 spine edit (an architecture update session, AD-22) before 11.3 starts; this spike doesn't touch the architecture file. R2 above and 11.2's ticket already state the new rule.
- Decision: 2026-11-04 (spike 11.1), the per-codec frame limits are constants (NVENC's: H.264 4096, HEVC and AV1 8192 a side), not a probe result. Story 1.13's probe opens each encoder at 1280x720 only.
- Decision: 2026-11-04 (spike 11.1), 11.3's compositor hands the pacer one canvas per output slot, from its own slot clock, not one per source frame. The spike's per-frame push gave 160 to 946 queue drops and about 2,000 non-increasing stamps per 50 s run, and a full-canvas scale pass for each source frame.
- Decision awaiting review: 2026-11-04 (spike 11.1), NFR-1 and a Monitor Recording beside a game. Neither today's one-monitor Recording (5.15 % in total) nor the two-monitor composite (7.6 to 10 %, mean 8.8 %) meets 3 % beside a GPU-bound game. Unattended default: NFR-1 stays the game-capture target (2.85 %, pass). A Monitor Recording beside a game is reported as a known extra cost, and 11.3 re-measures after the per-slot compositing. The builder decides whether 11.3 needs a cap while a game runs. 11.3 first splits the cost by stage, since the encoder (about 30 %) and the blits aren't the cause. The Done-when's "within NFR-1" waits on this.
- Spike code kept as the base for 11.3:
  - `Platform.Capture`: `MonitorCanvas` and `CompositeCapture`, and `D3D11Device.CopyFrameTo` and `TimeCopies`;
  - `CaptureSessionOptions.CompositeMonitors` and `debug-capture --monitors` (dev only);
  - `measure-path-a.ps1 -mon<mode>` and `bench fps --conditions`.

  AD-25 places the compositor in Media and the canvas rule in Core, so 11.2 and 11.3 move them there.

- Decision: 2026-11-05, the builder chose **full width with HEVC or AV1** for several screens: when a Monitor Recording covers more than H.264's 4096 px, it uses HEVC (or AV1) so each screen keeps its native resolution in one file (this PC as first described: two 2560x1440 side by side, 5120x1440, on one RTX 5090. Spike 11.1 corrected this: DISPLAY2 is 3840x2160, so the canvas is 6400x2160). H.264 stays for a single screen, or when the canvas fits. The builder accepted (b), one composited file. 11.1 (the spike) runs now with GPU runs allowed, while 8.7's fps and the 8.8 soaks stay paused.

- Decision: 2026-11-05, the builder's words: "monitor recording needs to allow selecting which monitors to record, or capture all monitors. Plan that feature out."
- Decision: 2026-11-05, option (b), one composited file, over (a) one file per monitor and (c) both: (a) needs one encoder chain per monitor, which breaks AD-7's two-chain budget, multiplies disk use and audio, and needs grouped Library items and cross-file sync; (b) keeps one chain, one file, one set of Tracks and one Library item, and adds only a compositor. Unattended default, awaiting review.
- Parked: per-monitor files (option (a)), for full-resolution editing of each screen. It would be its own epic, after an AD-7 budget change, with Library grouping. The builder decides whether it's ever wanted.
- Decision: 2026-11-05, a new epic rather than stories in epic 10: it changes the engine's capture path, not only the UI. It runs after 10.9, which moves the screen picker to Hotkeys, and before 8.11, so the hands-off run uses it; 8.11's `after` names 11.6. 8.7 to 8.9 don't wait, and story 11.6 reruns 8.5's monitor-change tests against the new path.
- Decision: 2026-11-05, monitors are stored by DisplayConfig's monitor device path, not the GDI device name, which can change after a reboot or replug (R35). An old `monitorRecordingMonitor` value is matched by device name.
- Decision: 2026-11-05, the canvas never changes inside a file: an arriving monitor joins at the next Monitor Recording, a removed one leaves a black area, and a mode change is scaled into its area.
- Decision: 2026-11-05, the tracer is a spike (story 1): the compositor and a three-monitor encode on the builder's PC, measuring the GPU cost and the codec limits, because that's the least certain part. Then the settings and Contracts (2), the engine (3) and the picker (4) in order, a Refactor sweep (5) and an end-to-end check (6, hitl).
- Decision: 2026-11-05, the spike (11.1) has no prerequisite, so `next` lists it as ready now. It needs GPU runs (a capture, an encode and one bench fps run), so the coordinator schedules it when GPU work is allowed and the GPU runner is free; only 11.4 waits on 10.9.
- Open question: is a scaled side-by-side video what the builder wants, rather than a file per monitor? Under H.264, three monitors come out at most 4096 px wide. The builder decides; option (a) is parked meanwhile.
- Answered by spike 11.1: two, both on one RTX 5090 (2560x1440 at 240 Hz and 3840x2160 at 144 Hz); the Meta Virtual Monitor adapter has no active output. Was: open question: how many monitors does the builder's PC have, and on which adapters? The spike needs at least two attached; if there's only one, it runs on a virtual display and the end-to-end check waits for a second monitor.
