---
title: 'Source in the Video quality section'
type: 'feature'
ticket: '3'
created: '2026-11-05'
status: built
baseline_revision: 'fea3ac6'
route: 'quick'
risk: 'low'
---

# Story 12.3: Source in the Video quality section

App only, on 12.1's `VideoSettings.MatchSource` and AD-26.

## What changed

| File | Change |
|---|---|
| `ReplayBufferSettingsPage.cs` (the shared Video quality view model of the Clips page and a game's page) | `ResolutionItem` gains `IsSource`. "Source — the game's or screen's own size" (`SourceLabel`) is first in `Resolutions`. `SelectedResolution` is Source while `matchSource` is on. Choosing Source sends `video` with `matchSource` true and the width and height kept; choosing a fixed size sends that size with `matchSource` false. Under Source, the current fixed size isn't added to the list. A new `SourceHelp` is null unless Source is chosen. It says what Source records at and that frame rate and bitrate still apply, and adds the 4096 px line only for H.264. It refreshes with the other video fields, so changing the format updates it. |
| `ReplayBufferSettingsView.xaml`, `GameVideoEditor.xaml` | The Resolution combo box and the help line (help-text style, collapsed when null) share the row. The Resolution label is top-aligned with the box, as Theme and Cache folder already are. |
| `GamePage.cs` | `VideoSummary` reads "Source" in place of the size under `matchSource` (e.g. "H.264 · Source · 60 fps · 40,000 kbps"). Without this, a game using the main setting would have shown the fallback size. |

A game's own Video quality and its reset are unchanged in code. "Change for this game" copies the whole main `video` value, `matchSource` included, and the reset removes the override. The new game-page test checks the copy.

## Verification

- `dotnet build ReplayBuffer.slnx -c Release`: 0 warnings.
- Default suites, 0 failed: App 200 (20 skipped; `GlossaryTests` passed), Core 170, Contracts 137.
- New tests:
  - `ReplayBufferSettingsPageTests.SourceComesFirstSendsMatchSourceKeepingTheSizeAndAFixedSizeTurnsItOff`: Source first; choosing it sends `matchSource` with the size kept; the H.264 line shows for H.264 only; a fixed size turns it off; there is no help line then.
  - `GameProfilesPageTests.AGameTakingItsOwnVideoQualityKeepsSource`: the summary says Source, and the game's override carries `matchSource`. The tests' `SetProfiles` helper now lets a test's own global values win over its defaults; it overwrote `video` before.
  - `SourceResolutionViewTests`: the Clips page shows Source selected and its help line in Fluent and Gaming dark.
- PNGs for the builder, beside the App test binary (`tests/ReplayBuffer.App.Tests/bin/Release/net10.0-windows10.0.26100.0/`): `clips-source-fluent.png` and `clips-source-gaming.png`.
- No Heavy, GPU or bench runs: none apply.

## Follow-ups

- None.
