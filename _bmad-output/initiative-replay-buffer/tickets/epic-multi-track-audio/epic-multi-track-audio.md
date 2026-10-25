---
id: 2
type: epic
title: "Multi-Track Audio"
parent: initiative-replay-buffer
covers: [R14, R15, R16, R18, R19, R30]
sources: []
companions: []
after: []
refined: false
risk: high
---

# Multi-Track Audio

## Description

This is the app's reason to exist: audio Tracks built from any combination of Windows Output and Input Devices. It covers the live device list, Global Track Layouts (named Tracks, the "Everything" Track first, a double-count warning), mixing without clipping, Mic Devices with the Mic Mute Hotkey, silent recovery from dropouts, and live meters. The Tracks are handed to epic-engine-baseline's encoder and muxer, in sync with video. Per-Game-Profile layouts come later, in epic-replay-buffer-clips (R17). This epic delivers them in Global Settings, with a Mic Device and mute setup that a profile can override later.

## Outcome

With the builder's BEACN setup, captured files carry the configured, named Tracks in sync, with no clipping and no lost audio when a device drops out (R14, R15, R16, R18, R19, R30).

## Done when

- The device picker lists every present Output and Input Device live, tells devices with the same name apart, and hides non-present ones unless configured (R14).
- A file captured with a multi-Track layout shows the named Tracks in order in VLC and DaVinci Resolve, all in sync, with a single-device Track at the device's level and no clipping in mixes (R15, R16).
- Mic mute silences only the Mic Devices, in only the marked Tracks, and shows its icon state (R18).
- A device unplugged for 10 s leaves 10 s of silence from that device only, and comes back within 2 s with no restart. The Main App logs the dropout (R19).
- Live meters move for every Track containing a playing device, with nothing being captured (R30). Verified integrated on the builder's PC.

## Boundaries

- Out: per-Game-Profile Track Layout overrides (R17, epic-replay-buffer-clips), and the Overlay's mic icon rendering (the Overlay itself is epic-overlay-feedback; this epic supplies the mute state).
- Touch point: the BEACN Mix Create app's virtual devices, consumed only.

## References

- parent — _bmad-output/initiative-replay-buffer/initiative-replay-buffer.md, Requirements R14, R15, R16, R18, R19, R30
- addendum — _bmad-output/initiative-replay-buffer/prd-replay-buffer/addendum.md, Test reference (the builder's audio devices)
- architecture input — PLAN.md, §2

## Notes

- Waits on epic-engine-baseline because: Tracks attach to its engine process, settings store, and encoder/muxer.
