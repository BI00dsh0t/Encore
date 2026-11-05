---
id: 13
type: epic
title: "New games captured by default, or not"
parent: initiative-replay-buffer
covers: [R41]
sources: []
companions: []
after: []
refined: true
risk: low
---

# New games captured by default, or not

## Description

A Global setting on the General page, "Capture new games", decides whether a game found for the first time is captured. It is on by default, so nothing changes for anyone who leaves it alone. With it off, a newly detected game's profile is created with Capture this game off (`ignored` true). The game then shows in Games under Ignored games with its "Not captured" pill, and its page's Capture switch turns it on. A game added by hand is always captured, and existing games are never changed.

## Outcome

The builder can stop launchers, tools and odd programs that Windows calls games from being captured the first time they run, and turn on only the games they want (R41).

## Requirements

- R1 (parent R41, R25; architecture AD-4, AD-22): A Global-only `captureNewGames` key (bool, default true, applied at once) is added to the key catalog additively. The settings document and every existing profile are unchanged by it.
- R2 (parent R41, R23, R24, R25; architecture AD-5, AD-18): When the engine creates a Game Profile for a game it detected itself, it creates it with `ignored` set to the opposite of `captureNewGames`, in the same settings update as the name, so the game never becomes the Target Game for a moment first. A game in `manualGames` is always created captured. A profile that already exists is never changed, whatever the setting.
- R3 (parent R41, R6, R8): The first time a game is found and not captured because of the setting, the user is told once: the status line and an Overlay message say "{Game} was found but isn't captured. Turn it on in Games." It isn't repeated on later launches.
- R4 (parent R41, R38; architecture AD-23, AD-24): The General page gets a "Games" section with the "Capture new games" switch and its help text: "When off, a game ReplayBuffer finds for the first time is added to Games as Not captured. Games you add by hand are always captured." It follows epic 10's glossary and both themes. Games' Ignored group, "Not captured" pill and Capture this game switch (story 10.10) are how a game is turned on, unchanged.

## Constraints

- (architecture AD-22) Additive only: a new key, and a new optional warning code `new-game-not-captured`. Nothing renamed or removed.
- (initiative R25) Ignored can always be undone, and Ignored keeps its meaning: a game that isn't captured never becomes the Target Game.
- (CLAUDE.md) Lean tests: one or two per AC. No GPU work.

## Done when

- With "Capture new games" off, a game run for the first time appears in Games as Not captured, isn't captured, and the user is told once; turning its switch on captures it from its next session (R1 to R3).
- With it on (the default), nothing changes from today; a hand-added game is captured either way; existing games keep their switch (R1, R2).
- The General page shows the switch in both themes (R4). Verified integrated on the builder's PC.

## Boundaries

- Out: changing existing games in bulk (turn all off, say); Games' own controls do that one at a time.
- Out: per-source rules (only Windows-identified games, only fullscreen games); the setting covers every game the engine finds.
- Out: Recordings' own default (`recordingsEnabled` stays off for new games, R9).
- Touch point: Games' list and a game's page (story 10.10), consumed unchanged; owner: epic-ux-pass-2.

## References

- parent — _bmad-output/initiative-replay-buffer/initiative-replay-buffer.md, Requirements R41 (and R6, R8, R23, R24, R25)
- architecture — _bmad-output/initiative-replay-buffer/architecture-replay-buffer/architecture-replay-buffer.md, AD-4, AD-5, AD-18, AD-22, AD-23, AD-24
- design — _bmad-output/initiative-replay-buffer/tickets/epic-ux-pass-2/ux-pass-2/EXPERIENCE.md (General, Games, the glossary: `ignored` is "Capture this game", off)
- code — `src/ReplayBuffer.Engine/Settings/GameProfiles.cs` (`EnsureProfile`), `src/ReplayBuffer.Engine/EngineHost.cs` (the detection callback), `src/ReplayBuffer.Engine/Settings/ManualGames.cs`, `src/ReplayBuffer.Contracts/Settings/Keys.cs`, `src/ReplayBuffer.App/Settings/GeneralSettingsPage.cs` and its view

## Notes

- Decision: 2026-11-05, the builder's words: "add a general setting for allowing the user to specify if they want new games to be captured by default. So if a new game is detected, its Capture this game setting will be on or off by default."
- Decision: 2026-11-05, its own small epic, not stories in epic 3 or 10, which are done. It shares no code with epic 12 beyond adding a key to `Keys.cs`, so it doesn't wait on it. It has no prerequisite left, and 8.11's hands-off run waits on 13.1.
- Decision: 2026-11-05, one story: the key, the engine change and the switch are a session's work together. No Refactor sweep (one entry) and no separate end-to-end story; the builder's check is a Human Check row on 13.1.
- Decision for the builder (unattended default): 2026-11-05, default on, which is today's behaviour.
- Decision for the builder (unattended default): 2026-11-05, a game added by hand is always captured: adding it is the request to capture it.
- Decision for the builder (unattended default): 2026-11-05, existing games keep their switch; changing the setting affects only games found afterwards.
- Decision for the builder (unattended default): 2026-11-05, a game that isn't captured because of the setting is announced once, on the status line and in the Overlay, so a Save Hotkey press in it isn't a silent surprise.
- Decision for the builder (unattended default): 2026-11-05, the switch sits on the General page in a new "Games" section, as asked, rather than on the Games page.
- Open question (changes scope): should the Overlay message be dropped, leaving only the status line? Default: keep both, once per game.
