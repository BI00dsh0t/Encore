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
- R2 (parent R39, R21; architecture AD-25, AD-7): Several monitors are recorded as one video with one set of Tracks. Each monitor is placed as Windows arranges it, and the whole is scaled down, keeping its aspect ratio, to fit the codec's largest frame and a pixel budget no larger than 3840×2160 or the Output resolution's area. It runs at the Output Settings' frame rate on one encoder chain, within AD-7's budget, and is crash-safe and disk-full safe like any Recording.
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

- Decision: 2026-11-05, the builder's words: "monitor recording needs to allow selecting which monitors to record, or capture all monitors. Plan that feature out."
- Decision: 2026-11-05, option (b), one composited file, over (a) one file per monitor and (c) both: (a) needs one encoder chain per monitor, which breaks AD-7's two-chain budget, multiplies disk use and audio, and needs grouped Library items and cross-file sync; (b) keeps one chain, one file, one set of Tracks and one Library item, and adds only a compositor. Unattended default, awaiting review.
- Parked: per-monitor files (option (a)), for full-resolution editing of each screen. It would be its own epic, after an AD-7 budget change, with Library grouping. The builder decides whether it's ever wanted.
- Decision: 2026-11-05, a new epic rather than stories in epic 10: it changes the engine's capture path, not only the UI. It runs after 10.9, which moves the screen picker to Hotkeys, and before 8.11, so the hands-off run uses it; 8.11's `after` names 11.6. 8.7 to 8.9 don't wait, and story 11.6 reruns 8.5's monitor-change tests against the new path.
- Decision: 2026-11-05, monitors are stored by DisplayConfig's monitor device path, not the GDI device name, which can change after a reboot or replug (R35). An old `monitorRecordingMonitor` value is matched by device name.
- Decision: 2026-11-05, the canvas never changes inside a file: an arriving monitor joins at the next Monitor Recording, a removed one leaves a black area, and a mode change is scaled into its area.
- Decision: 2026-11-05, the tracer is a spike (story 1): the compositor and a three-monitor encode on the builder's PC, measuring the GPU cost and the codec limits, because that's the least certain part. Then the settings and Contracts (2), the engine (3) and the picker (4) in order, a Refactor sweep (5) and an end-to-end check (6, hitl).
- Decision: 2026-11-05, the spike (11.1) has no prerequisite, so `next` lists it as ready now. It needs GPU runs (a capture, an encode and one bench fps run), so the coordinator schedules it when GPU work is allowed and the GPU runner is free; only 11.4 waits on 10.9.
- Open question: is a scaled side-by-side video what the builder wants, rather than a file per monitor? Under H.264, three monitors come out at most 4096 px wide. The builder decides; option (a) is parked meanwhile.
- Open question: how many monitors does the builder's PC have, and on which adapters? The spike needs at least two attached; if there's only one, it runs on a virtual display and the end-to-end check waits for a second monitor.
