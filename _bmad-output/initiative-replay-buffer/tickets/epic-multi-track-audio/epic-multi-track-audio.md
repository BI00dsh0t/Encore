---
id: 2
type: epic
title: "Multi-Track Audio"
parent: initiative-replay-buffer
covers: [R14, R15, R16, R18, R19, R30, R33]
sources: []
companions: []
after: []
refined: true
risk: high
---

# Multi-Track Audio

## Description

This is the app's reason to exist: audio Tracks built from any combination of Windows Output and Input Devices. It covers the live device list, Global Track Layouts (named Tracks, the "Everything" Track first, a double-count warning), mixing without clipping, Mic Devices with the Mic Mute Hotkey, silent recovery from dropouts, and live meters. The Tracks are handed to epic-engine-baseline's encoder and muxer, in sync with video. This epic delivers Track Layouts and the Mic Device and mute setup in Global Settings, using epic-engine-baseline's override shape. The per-Game-Profile parts come in epic-replay-buffer-clips (R17, and R18's per-profile part). The Mic Mute Hotkey uses epic-engine-baseline's Hotkey registration.

## Outcome

With the builder's BEACN setup, captured files carry the configured, named Tracks in sync, with no clipping and no lost audio when a device drops out (R14, R15, R16, R18, R19, R30).

## Requirements

- R1 (parent R14): The app lists Windows Output and Input Devices live, with no restart. Devices with the same name are told apart by their parent device. Devices that aren't present are hidden, unless one is already in a Track Layout; then it's shown as unavailable.
- R2 (parent R15): A Global Track Layout holds any number of user-named Tracks, each built from any combination of Audio Devices. A device can be in several Tracks, and all Tracks stay in sync. A new layout starts with an "Everything" Track first. Settings warn, without blocking, when a Track may count the same audio twice.
- R3 (parent R16): Audio is captured as recorded, with no gain control. A single-device Track matches its device's level, and a mix of several devices never clips; the limiter acts only above full scale (architecture AD-13).
- R4 (parent R18, Global part): In the Global Track Layout the user marks Mic Devices and the Tracks that mute affects. The Mic Mute Hotkey silences only Mic Devices, only in those Tracks, and only in what the app records. The mute state persists across engine restarts, counts as muted if its file is missing (architecture AD-5), and is exposed as `micMuted` in `capture.stateChanged`.
- R5 (parent R19): A device dropout leaves silence from that device only. Its audio resumes within 2 s of the device returning, with no capture restart. The Overlay stays silent, but the dropout is raised as `warning.raised { code: device-dropout, overlay: false }` and logged.
- R6 (parent R30): The Main App shows a live level meter per Track for the layout being viewed, including an unsaved draft. The meters work with nothing being captured, and update at most 20 times a second.
- R7 (parent R33, named-Tracks part): A file captured with a multi-Track layout shows the named Tracks, in order, in VLC and DaVinci Resolve, all in sync.

## Constraints

- (parent NFR-2, NFR-3; architecture AD-13, AD-20) All audio goes through epic 1's AudioDeviceHub, with one client per endpoint and capture threads that never block.
- (architecture AD-4, AD-5) The Track Layout shape is story 1.4's `TrackLayout`. Rebinding a device is never written back to settings, and mic mute is runtime state, never a setting.
- (architecture AD-3) Device lists, meters and warnings travel only as Contracts messages in the `Audio.cs` area. Meters coalesce.

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

- Decision: 2026-10-25, the tracer bullet is story 1: the Global trackLayout → named AAC Track streams through the capture session, using a hand-edited settings.json.
- Decision: 2026-10-25, the sequencing:
  - The mixer lane runs 2 → 5 → 4 → 6, because stories 5, 4 and 6 all change story 2's mixer.
  - Story 3 owns AudioDeviceHub's device notifications, which story 5 reuses.
  - The UI lane runs 7 → 11 → 8, on epic 1's Settings host (story 1.7).
  - A Refactor sweep (10) closes the epic.
- Decision: 2026-10-25, the least-certain item is story 5 (dropout and rebind with the BEACN virtual devices), so it comes right after the mixer. Confirmed by the user.
- Decision: 2026-10-25, every device added to the layout joins the "Everything" Track unless the user removes it, and a Track with no devices records silence. Confirmed by the user.
- Decision: 2026-10-25, the double-count warning fires when a Track mixes Output Devices from different parent devices. It's a heuristic that can miss a mixer output that already includes a mic captured on its own. Confirmed by the user.
- Decision: 2026-10-25, meters show what is recorded, so a muted mic reads silent in the Tracks mute affects. Confirmed by the user.
- Decision: 2026-10-25, there's no `bmad-ux` pass for the Track Layout editor; the builder lays it out, and you check it by hand. Confirmed by the user.
- Decision: 2026-11-01, story 1 waits only on the epic 1 stories it builds on (1.4 settings store, 1.5 IPC, 1.6 capture session, 1.9 Hotkeys, 1.10 encoder, 1.12 AudioDeviceHub), not the whole epic, so epic 2 starts while 1.14's benchmarks finish. The builder said so. 1.15's sweep then runs over code epic 2 may already have changed.
- Waits on epic-engine-baseline because: Tracks attach to its capture-session API, settings store, and encoder/muxer, and the Mic Mute Hotkey uses its Hotkey registration.
