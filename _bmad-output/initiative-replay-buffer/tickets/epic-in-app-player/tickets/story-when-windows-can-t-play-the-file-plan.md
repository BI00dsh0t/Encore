---
title: "When Windows can't play the file"
type: 'feature'
ticket: '6'
created: '2026-11-05'
status: built
baseline_revision: '7c22d82'
route: 'quick'
risk: 'low'
---

# Story 14.6: When Windows can't play the file

Built on 14.4's tip `7c22d82` (`feature/14.6-cant-play` from `feature/14.4-track-picker`), at the builder's word: "continue the epic". App only; AD-27.

## What Windows reports (probe, this PC)

- **Video subtypes:** `H264`, `HEVC` and `AV1`.
- **A file cut short** (the first 200 kB of a Clip) still opens with every decoder supported. So only the no-picture timer can catch it.
- **A FLAC audio Track** in an MP4 is `UnsupportedSubtype`: a real case of a Track Windows can't decode.

## Changes

- **`PlayerMedia`** gains `VideoCodec` (Windows' subtype name) and `TracksPlayable` (null when all can play), both additive with defaults.
  - `WindowsMediaPlayer` fills them, counting `FullySupported` and `Degraded` as playable.
  - It takes a `forcedUnsupported` codec, which reports that codec undecodable whatever the file holds.
- **`CodecExtension`** (new) maps a codec to its Store extension:
  - "HEVC" to the HEVC Video Extension, `ms-windows-store://pdp/?ProductId=9NMZLZ57R3T7`;
  - "AV1" to the AV1 Video Extension, `ms-windows-store://pdp/?ProductId=9MVZQVXJBQ9V`.
- **`PlayerPage`:**
  - **Video Windows can't decode:** the page fails at once, with no first frame. The message is "This video is {HEVC|AV1}, which Windows can't play without the {extension}. Get it from the Microsoft Store, then open the file again. The file itself is fine." `StoreLinkText` ("Get the {extension}") and `OpenStoreCommand` open the Store page through the Library's shell, whose failure shows the usual error line.
  - **Another undecodable codec:** "Windows can't play this video's format. The file itself is fine." There's no Store link.
  - **A file that won't open, or no picture:** "This file couldn't be played."
  - **Every failure** offers "Open in default player". Back, Esc and the keys still work.
  - **A Track Windows can't decode** shows as "{name} (unavailable)" and can't be chosen.
  - **The log:** each open writes "player: opened {file}: video {codec} decodable|NOT decodable, {n} tracks, {m} not decodable". A failed open and a missing picture each write a line too, through `ShellApp.Log` to `logs\app.log`.
- **`PlayerView`:** a "Get the {extension}" button in the bar, before "Open in default player". The message wraps at 560 px. A no-picture timer fails the page if no frame is shown within 5 s of opening, which catches a file cut short.
- **`ShellApp`:** `--player-unsupported=hevc|av1` on any instance but the installed one makes the player report that codec undecodable. 14.8 and the Done-when check use it. It's ignored on the installed instance and for any other codec.
- **Test harness:** `PlayerViewThemeTests` now uses one `ThemeService` for its window, as the App does. A second `ForWindow` makes its own dictionaries and can't swap the first's out, so a Gaming dark to Fluent switch had rendered Gaming's buttons.

## Tests

- **Default, fake player:** `PlayerFailureTests`:
  - `AMissingExtensionIsNamedWithItsStorePageAndTheDefaultPlayer` (theory, HEVC and AV1): the message, the link text, no first frame, the log line, the Store URI and the file opened through the shell, and Back closing.
  - `AnUnknownFormatOrAFileThatWontOpenSaysSoWithNoStoreLink`.
  - `ATrackWindowsCantDecodeIsUnavailableAndCantBeChosen`.
  - `TheDevSwitchNamesACodecOnAnyInstanceButTheInstalledOne`.
- **`PlayerViewThemeTests`:** the HEVC message, its Store button and "Open in default player" in both looks, in `player-codec-fluent.png` and `player-codec-gaming.png`.
- **Not added:** a default test of the 5 s no-picture timer. It's view-level and would wait 5 s; the 14.8 end-to-end check covers a file cut short.

## Verification

- `dotnet build ReplayBuffer.slnx -c Release`: 0 warnings, 0 errors.
- Default suites, 0 failed (started 16:37): Platform.Desktop 15 of 15; App 248, with 224 passed and 24 skipped (Explicit/Heavy).

## Log

- 2026-11-05: branch `feature/14.6-cant-play` from `feature/14.4-track-picker`; built and verified by 16:38.
