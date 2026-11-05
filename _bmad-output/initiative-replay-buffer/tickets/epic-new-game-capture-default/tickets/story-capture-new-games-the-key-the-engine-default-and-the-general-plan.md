---
title: 'Capture new games: the key, the engine default and the General switch'
type: 'feature'
ticket: '1'
created: '2026-11-05'
status: built
baseline_revision: '40bf280'
route: 'quick'
risk: 'low'
---

# Story 13.1: Capture new games

The builder's defaults (2026-11-05, "Yes, start 13.1"): on by default; hand-added games are always captured; existing games are unchanged; a one-time notice on the status line and in the Overlay; the switch in a Games section on General.

## What changed

| Layer | Change |
|---|---|
| Contracts | `Keys.CaptureNewGames` (`captureNewGames`, bool, default true, Global-only, applied at once), last in the catalog (AD-22, additive). `ErrorCodes.NewGameNotCaptured` (`new-game-not-captured`). `EffectiveSettings.CaptureNewGames`, optional, defaulting to true. |
| Core | `SettingsResolver` resolves the key. |
| Engine | `GameProfiles.EnsureProfile` decides inside its retried update: with `captureNewGames` off and the game not in `manualGames` (compared by `GamePaths.CanonicalOrLowercase`, as `ListedGames` does), the same `SettingsUpdate` sets `name` and `ignored: true`. An existing profile is never touched. Once the write commits, its new `notCaptured` callback gets the name. `EngineHost` raises `warning.raised new-game-not-captured` (Info, source `games`, Overlay on): "{Game} was found but isn't captured. Turn it on in Games." Because the profile exists from then on, it is raised once per game. |
| Platform.Windows | `GameMonitor.Detect` checks `Ignored` again after the detected callback. Before, the callback could create the profile Ignored, yet `Decide` in the same poll could still propose the game, and `ForgetIgnored` would only drop it on the next poll, so capture would start for a moment. Now the game is forgotten at once, logged and reported as exited, so the App sees `game.detected` then `game.exited`, the same pair as ignoring a running game. |
| App | General gets a last section, **Games**, with a **Capture new games** check box (the same control as Start with Windows) and the help line from the ticket. `GeneralSettingsPage.CaptureNewGames` sends the Global set and renders it from the document. Games' Ignored group, Not captured pill and Capture this game switch (10.10) are unchanged. |

## Verification

- `dotnet build ReplayBuffer.slnx -c Release`: 0 warnings.
- Default suites, 0 failed: Contracts 136, Core 169, Engine 199 (47 explicit skipped), App 197 (20 skipped; `GlossaryTests` included), Platform.Windows 32.
- `pwsh tools/publish-engine.ps1`: clean, 12.1 MB.
- New tests:
  - `KeysTests.CaptureNewGamesIsGlobalOnlyAndOnByDefault`; the catalog test lists the key.
  - `GameDetectionServeTests`:
    - `WithCaptureNewGamesOffANewGameIsIgnoredNeverProposedAndAnnouncedOnce`: the profile is Ignored, the one notice carries the Overlay flag and text, no target, and nothing more over three polls;
    - `WithCaptureNewGamesOffAHandAddedGameIsStillCaptured`;
    - `TurningCaptureNewGamesOffLeavesAnExistingGameCaptured`.

    The default case (on, captured) is the existing `ADetectedGameBecomesTheTargetAndItsExitClearsIt`. The settings.update round trip is covered by the engine tests' update and the App test.
  - `CaptureNewGamesTests`: the switch starts on and sends `captureNewGames: false`; the General page shows it in Fluent and Gaming dark.
  - `SettingsLayoutTests`' General section list gains Games.
- PNGs for the builder, beside the App test binary (`tests/ReplayBuffer.App.Tests/bin/Release/net10.0-windows10.0.26100.0/`): `general-games-fluent.png` and `general-games-gaming.png`.
- No Heavy, GPU or bench runs: none apply.

## Human Check

- The builder turns Capture new games off, runs a game for the first time, sees the notice on the status line and in the Overlay, finds it under Ignored games as Not captured, turns Capture this game on, and saves a Clip in its next session.

## Follow-ups

- None.
