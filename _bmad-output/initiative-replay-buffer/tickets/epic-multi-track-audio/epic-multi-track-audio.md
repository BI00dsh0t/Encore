---
id: 2
type: epic
title: "Multi-Track Audio"
parent: initiative-replay-buffer
covers: [R14, R15, R16, R18, R19, R30, R33]
sources: []
companions: []
after: []
refined: false
risk: high
---

# Multi-Track Audio

## Description

This is the app's reason to exist: audio Tracks built from any combination of Windows Output and Input Devices. It covers the live device list, Global Track Layouts (named Tracks, the "Everything" Track first, a double-count warning), mixing without clipping, Mic Devices with the Mic Mute Hotkey, silent recovery from dropouts, and live meters. The Tracks are handed to epic-engine-baseline's encoder and muxer, in sync with video. This epic delivers Track Layouts and the Mic Device and mute setup in Global Settings, using epic-engine-baseline's override shape. The per-Game-Profile parts come in epic-replay-buffer-clips (R17, and R18's per-profile part). The Mic Mute Hotkey uses epic-engine-baseline's Hotkey registration.

## Outcome

With the builder's BEACN setup, captured files carry the configured, named Tracks in sync, with no clipping and no lost audio when a device drops out (R14, R15, R16, R18, R19, R30).

## Done when

- The device picker lists every present Output and Input Device live, tells devices with the same name apart, and hides non-present ones unless configured (R14).
- A file captured with a multi-Track layout shows the named Tracks in order in VLC and DaVinci Resolve, all in sync, with a single-device Track at the device's level and no clipping in mixes (R15, R16). This is the named, in-sync Tracks part of R33.
- Mic mute silences only the Mic Devices, in only the marked Tracks of the Global Track Layout, and exposes its mute state (R18, Global part).
- A device unplugged for 10 s leaves 10 s of silence from that device only, and comes back within 2 s with no restart. The dropout is logged and emitted as a warning event (R19).
- Live meters move for every Track containing a playing device, with nothing being captured (R30). Verified integrated on the builder's PC.

## Boundaries

- Out: per-Game-Profile Track Layout overrides and per-profile Mic Devices (R17, R18 per-profile part, epic-replay-buffer-clips), and the Overlay's mic icon rendering (the Overlay itself is epic-overlay-feedback; this epic supplies the mute state).
- Handoff: the dropout warning event goes to epic-overlay-feedback's Main App status line (R8), which displays it.
- Touch point: the BEACN Mix Create app's virtual devices, consumed only.

## References

- parent — _bmad-output/initiative-replay-buffer/initiative-replay-buffer.md, Requirements R14, R15, R16, R18, R19, R30, R33
- architecture — _bmad-output/initiative-replay-buffer/architecture-replay-buffer/architecture-replay-buffer.md, AD-4 (Track Layout shape), AD-5 (mic mute), AD-6, AD-13, AD-20
- addendum — _bmad-output/initiative-replay-buffer/prd-replay-buffer/addendum.md, Test reference (the builder's audio devices)
- architecture input — PLAN.md, §2

## Notes

- Waits on epic-engine-baseline because: Tracks attach to its capture-session API, settings store, and encoder/muxer, and the Mic Mute Hotkey uses its Hotkey registration.
