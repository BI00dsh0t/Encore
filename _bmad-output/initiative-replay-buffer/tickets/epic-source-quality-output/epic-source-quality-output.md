---
id: 12
type: epic
title: "Source-resolution output"
parent: initiative-replay-buffer
covers: [R40]
sources: []
companions: []
after: []
refined: true
risk: medium
---

# Source-resolution output

## Description

Video quality gains a resolution choice, "Source", beside today's fixed sizes. With it, Clips and game Recordings are encoded at the Target Game window's own size, and a Monitor Recording at the screen's own size, with no scaling. Frame rate, format and bitrate still come from the Output Settings. It works globally and per game, like every other Video quality setting.

## Outcome

The builder chooses "Source" once and gets Clips and Recordings at the size the game is drawn, for example 2560×1440 on the main screen, instead of a downscale to 1080p (R40).

## Requirements

- R1 (parent R40, R22; architecture AD-4, AD-22): The `video` key gains an optional `matchSource` flag, default false, so every existing setting keeps today's fixed size. With it on, `width` and `height` are kept as the size the page falls back to when "Source" is turned off. Frame rate, codec, bitrate and rate control are unchanged by it. It is overridable per game like the rest of `video` (R17), and applies at the next session, Reset or Recording (AD-5).
- R2 (parent R40, R20; architecture AD-7): With `matchSource` on, the game capture chain is sized from the Target Game's captured content at its first frame, rounded down to even sides and scaled down, aspect kept, only to fit the codec's largest side (H.264 4096 px; HEVC and AV1 8192 px; the constants from spike 11.1). The codec is never switched for game capture. The Replay Buffer and a game Recording share that chain as today (AD-7's ChainKey), so the encoder budget is unchanged.
- R3 (parent R40, R12, R20; architecture AD-7): When the game's size changes mid-session and the new size holds for 3 s, the Replay Buffer moves to a chain at the new size the way a settings change does at a Reset (AD-7), raising `reset-gap` if a gap can't be avoided. While a game Recording runs, the size stays fixed and the new content is letterboxed into it (R12: a Recording is never split); the move happens after the Recording ends. Too small a window (under 640×360) never sets the size.
- R4 (parent R40, R21, R39; architecture AD-25): A Monitor Recording of one screen with `matchSource` on records at that screen's physical mode size (`EnumDisplaySettings`), through `MonitorCanvasRule.Plan`, so over 4096 px under H.264 it takes AD-25's switch to HEVC or AV1 like a multi-screen canvas. Several screens keep AD-25's canvas, which is already full size; nothing changes for them.
- R5 (parent R40, R38; architecture AD-24): The Video quality section, on the Clips page and on a game's page, offers "Source — the game's or screen's own size" first in the Resolution list. With it chosen, a line says what it records at, that frame rate and bitrate still apply, and when the format is H.264 that anything over 4096 px is scaled down to fit. Labels follow epic 10's glossary.

## Constraints

- (architecture AD-22) Additive only: `matchSource` defaults to false, and `width` and `height` stay required. Nothing is renamed or removed.
- (architecture AD-7) At most two encoder chains; moving to a new size replaces the Replay Buffer's chain, never adds one.
- (builder decision 2026-11-05) Source means resolution only. The output frame rate always comes from the Output Settings.
- (initiative R33) Clips stay H.264 by default; source size never switches a game chain's codec.
- (CLAUDE.md, memory) Lean tests. GPU capture, encode and bench runs are Heavy (`Explicit`) and run at night only, one project at a time.

## Done when

- With "Source" chosen, a Clip and a game Recording of a 2560×1440 game come out at 2560×1440, at the Output frame rate, and play in VLC and Discord (R1, R2).
- A game that starts in a small launcher window and then goes fullscreen gives Clips at the fullscreen size; a size change during a Recording leaves one unbroken file (R3).
- A one-screen Monitor Recording of the 3840×2160 screen comes out at 3840×2160 (R4).
- The FPS cost of the Replay Buffer at 2560×1440 source size on the synthetic load is measured against NFR-1 and recorded (R2).
- The Clips page and a game's page offer "Source" in both themes (R5). Verified integrated on the builder's PC.

## Boundaries

- Out: matching the source frame rate (the builder's 2026-11-05 decision).
- Out: a constant-quality or lossless rate control, and a bitrate that scales with resolution: parked, see the Notes.
- Out: switching a game chain to HEVC or AV1 when the source is wider than 4096 px.
- Out: HDR (PRD §6), and cropping or upscaling.
- Touch point: the Target Game's window size from WGC's content size, consumed only; owner: epic-engine-baseline's capture path.

## References

- parent — _bmad-output/initiative-replay-buffer/initiative-replay-buffer.md, Requirements R40 (and R12, R17, R20, R21, R22, R33)
- architecture — _bmad-output/initiative-replay-buffer/architecture-replay-buffer/architecture-replay-buffer.md, AD-4, AD-5, AD-7, AD-22, AD-24, AD-25
- design — _bmad-output/initiative-replay-buffer/tickets/epic-ux-pass-2/ux-pass-2/EXPERIENCE.md (Clips page, a game's page, the glossary)
- code — `src/ReplayBuffer.Contracts/Settings/SettingValues.cs` (`VideoSettings`), `Keys.cs` (`IsValidVideo`); `src/ReplayBuffer.Engine/Sessions/EncoderChain.cs` (`OutputSize`), `CaptureSession.cs` (`OnFrame`, `StartOutput`); `src/ReplayBuffer.Platform.Capture/FrameScaler.cs`; `src/ReplayBuffer.Core/Monitors/MonitorCanvasRule.cs`; `src/ReplayBuffer.App/Settings/ReplayBufferSettingsPage.cs` and its view

## Notes

- Decision: 2026-11-05, the builder's words: "For output, we want to be able to specify recording in source quality." Then: "We don't want to match the source framerate. Just the resolution. Output fps should always be determined by the output settings."
- Decision: 2026-11-05, a new epic rather than stories in epic 1 or 10: it changes the engine's chain sizing and the settings shape, not only the page. Epic 12 runs after 11.5, whose cleanup touches `CaptureSession.Open`. It doesn't wait on epic 8: 8.8 and 8.9 are measurement only, and 8.10 waits behind tonight's soak. 8.11's hands-off run waits on 12.5, so it uses the final build.
- Decision for the builder (unattended default): 2026-11-05, "Source" is a choice in the Resolution list, not a separate switch, and the stored fixed size stays as the fallback. Pick: one list, one place to look.
- Decision for the builder (unattended default): 2026-11-05, bitrate stays what the builder set; it isn't raised for a larger source. The storage estimate (R2 of the initiative) stays bitrate-based and correct. The page's line says bitrate still applies, so a 4K source at the default 40 Mbps is the builder's call.
- Decision for the builder (unattended default): 2026-11-05, a game window wider than 4096 px under H.264 (a 5120×1440 super-ultrawide, say) is scaled down to fit, and the codec stays H.264, so Clips still paste into Discord (R33). A Monitor Recording keeps AD-25's switch to HEVC or AV1. The builder's screens (2560×1440 and 3840×2160) fit H.264 either way.
- Decision for the builder (unattended default): 2026-11-05, a mid-session size change that holds for 3 s moves the Replay Buffer to the new size, as a Reset does, so the seconds before the move aren't in the next Clip. The alternative, keeping the first size until the next save, would letterbox a launcher-sized buffer until then. A Recording never moves (R12).
- Decision for the builder (unattended default): 2026-11-05, the size comes from the captured content (the client area WGC delivers), rounded down to even sides; a window under 640×360 never sets it.
- Decision: 2026-11-05, applies to Clips, game Recordings and Monitor Recordings alike: the first two share one chain, and a Monitor Recording uses the Global `video` key as today.
- Decision: 2026-11-05, no spike. The least certain part, a chain move on a size change outside a Reset, reuses AD-7's build-ahead switch; story 2 carries a plan checkpoint instead.
- Decision: 2026-11-05, the tracer is story 1: the key, Core's size rule and the engine sizing the game chain at its first frame, set through `settings.update` with no page yet. Then the AD-26 spine edit (6, hitl), mid-session changes and the Monitor Recording (2), the page (3, parallel with 6 and 2), a Refactor sweep (4) and an end-to-end check (5, hitl).
- Decision: 2026-11-05 (set check), the rule goes in a new AD-26 "Source output size", pointed to from AD-7 and AD-25, rather than an edit to AD-7. Entry 6 (hitl) is that spine edit; the coordinator runs it as an architecture update session, and story 2 waits on it. The chain move on a held size change is started by the engine, not by AD-7's ChainKey comparison, since with `matchSource` on the key's width and height are only the fallback.
- Decision for the builder (unattended default): 2026-11-05 (set check), a one-screen Monitor Recording over 4096 px under H.264 (an 8K screen) takes AD-25's switch to HEVC or AV1, as a multi-screen one does, rather than scaling like a game. Low stakes: the builder's screens fit H.264.
- Source conflict: initiative R20 says a size change is "scaled to the fixed output resolution". R40 makes the output size follow the source when "Source" is chosen; R20 still holds for fixed sizes and inside a Recording. The initiative's R40 records the exception.
- Parked: a constant-quality rate control (NVENC `cq`, AMF `qvbr`, QSV `icq`, x264 `crf`) as a "Source quality" preset, and a bitrate scaled by pixel count. The builder decides whether either is wanted; each would be its own story here.
- Open question (changes scope): should bitrate scale with the source size, so 4K isn't starved at 40 Mbps? Default: no, the builder's bitrate stands.
- Open question (changes scope): should a game wider than 4096 px switch to HEVC like a Monitor Recording? Default: no, scale to fit H.264.
- Open question (changes scope): should the size follow a mid-session change (default) or stay fixed until the next save?
