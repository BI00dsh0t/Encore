# Addendum: Replay Buffer PRD

Detail the user supplied that belongs downstream (architecture, test planning) rather than in the PRD itself. The source is `PLAN.md` (rev 2) in the project root, which stays the detailed record; this addendum points to it and records anything the conversation adds.

## Precedence: where PLAN.md is superseded by the PRD
On behaviour, the PRD wins. When reading PLAN.md, apply these overrides:
- **Hotkeys and Clips:** one Save Hotkey; no Post-roll; the Replay Buffer Resets after each Clip, behind a Save Cooldown (FR-4, FR-5). PLAN.md had several save hotkeys and post-roll.
- **Tracks:** each Track is any combination of Output and Input Devices, with no gain or volume controls, and a new Track Layout starts with an "Everything" Track (FR-16, FR-18). PLAN.md had one device per track, per-track gain, and `IncludeInMaster`.
- **Mic mute:** the Mic Mute Hotkey affects only the Tracks chosen per Game Profile (FR-20). PLAN.md muted the mic in both its own track and the master.
- **When capture runs:**
  - The Replay Buffer runs only while a detected game runs, and keeps running for 30 s after the Target Game exits (FR-1).
  - Monitor capture is only for a manual Monitor Recording (FR-23).
  - Game Detection replaces the allow-list and is sticky (FR-26); when several games run, one Target Game is chosen (FR-38).
- **Recordings:** opt-in per Game Profile; always one file (FR-8, FR-13). PLAN.md's "one file per ring generation" applies only inside the engine's buffer design, never to the user-visible Recording.
- **Encoder restarts:** a Recording continues in the same file, or ends cleanly with a new one started at once, and the user is told (FR-13). A Clip that spans an encoder restart follows PLAN.md §3 (one file per generation) and is reported through FR-7 feedback. [Open for architecture: whether a Clip can stay one file.]

## Inputs for architecture (`bmad-architecture`)
- **PLAN.md §0 Decisions log:** the container is fragmented MP4 (H.264/HEVC + AAC); the ring buffer uses reference-counted slabs with generations; a frame pacer keeps a constant frame rate; Hotkeys are registered in the engine process; one GPU is assumed (C1).
- **PLAN.md §1 Architecture:** a headless engine process (NativeAOT) plus a WPF tray UI, talking over a named pipe; the solution layout.
- **PLAN.md §2 Audio pipeline mechanics:** event-driven WASAPI on MMCSS threads, QPC timestamps, silence fill, resampling to 48 kHz, adaptive drift correction, re-binding through IMMNotificationClient.
- **PLAN.md §3 Video design:** WGC with a DXGI Desktop Duplication fallback (**used only for Monitor Recording**: Desktop Duplication captures a whole monitor, which FR-22 rules out for game capture), frame pacer (constant frame rate), fixed output resolution, ring generations, GOP of about 1–2 s, encoder probing (NVENC/AMF/QSV, x264 fallback).
- **PLAN.md §6 Interop & pipeline paths:**
  - Interop/encode stack candidates A–D. Path A (CsWin32 + CsWinRT + FFmpeg libav) is recommended first; C (native C++ core) is the fallback.
  - Color conversion options X1–X3 (BGRA straight into the encoder is tested first).
  - Comparison criteria and targets.
- **PLAN.md §7 Deferred items:**
  - Privacy and capture-leak work (capture exclusion for the app's own windows, default capture target).
  - GPU color path and priority decision.
  - Hybrid-GPU strategy (out of scope under constraint C1).
  - Facts still to verify.
- **Review-driven mechanism notes (from the PRD reviews):**
  - FR-4/FR-5: forcing a keyframe at the Save Hotkey press, so the next Clip starts on a keyframe, is one way to meet the "within one frame" tolerance. Audio is cut at the nearest AAC frame boundary.
  - FR-12: crash safety relies on fragmented MP4, with short fragments flushed to disk (at most about 1 s).
  - NFR-1/FR-6: an always-on-top Overlay can push a borderless game out of its fastest presentation path (independent flip). Measure this, and consider showing the Overlay only while it has something to show.
  - FR-20: the BEACN mixer's virtual inputs (VOD Track, Voice Chat Mic) are Input Devices that are **not** Mic Devices.

## Inputs for test planning / acceptance criteria
- **PLAN.md §8 Test catalog T-01…T-25:** failure modes and expected behaviour, to attach as acceptance criteria to the stories they belong to.

## Test reference: the builder's Audio Devices (surveyed 2026-10-25)
The app discovers devices itself at runtime (FR-15). This survey only provides a realistic set of devices for testing and architecture.
- **Mixer:** BEACN Mix Create.
  - **Outputs:** Game, Chat, Music, Browser, System, Aux 1 / Hardware, Aux 2 / Hardware, plus internal "Mic Relay (Do Not Use)" and "Clock Source (Do Not Use)".
  - **Inputs:** VOD Track, VOD Track 2, VOD Track 3, Audience Mix, Voice Chat Mic, plus "LDM Relay (Do Not Use)".
- **Mic:** BEACN Mic, which shows up as an Input Device ("Microphone") plus an Output Device ("Headphones").
- **Other active devices:** Schiit Modi 3E (DAC), NVIDIA HDMI outputs for two monitors, S/PDIF, a DualSense controller, an Oculus virtual audio device, and two copies each of the Steam Streaming Speakers and Microphone.
- **What this means for the design:**
  - Many devices share a name ("Speakers", "Microphone", "Internal AUX Jack"), so devices must be bound by endpoint ID and shown with their parent device's name.
  - There are dozens of "Not present" entries from old drivers and monitors; the picker hides those unless one is already in a Track Layout (FR-15).
  - Mechanism note: Output Devices are captured with WASAPI loopback and Input Devices with WASAPI capture (PLAN.md §2).

## Background
- **PLAN.md §9:** how OBS, ShadowPlay and similar tools capture. Game-hook injection was rejected because of anti-cheat risk.
