---
id: 3
type: epic
title: "Game Detection and Game Profiles"
parent: initiative-replay-buffer
covers: [R23, R24, R25, R26]
sources: []
companions: []
after: []
refined: true
risk: medium
status: done
---

# Game Detection and Game Profiles

## Description

This epic decides what is being captured, and where files go. It detects games (fullscreen or borderless, Windows-identified, or added by hand), keeps a game detected until it exits, and picks one Target Game by the 10 s focus rule unless a Recording holds the current one. It emits Target Game appeared and exited events, which epic-replay-buffer-clips uses to start the Replay Buffer and stop it 30 s after exit (R1). It creates Game Profiles automatically, using epic-engine-baseline's override shape, inheriting Global Settings, handles Ignored, and resolves the per-game folder and file name that later epics write to.

## Outcome

Launching any game makes it the Target Game within 5 s, with its own Game Profile and folder, and the capture follows it through alt-tab and window-mode changes (R23, R24, R25, R26).

## Requirements

- R1 (parent R23): A game is detected within 5 s if it runs fullscreen or borderless, if Windows identifies it as a game, or if it was added by hand. It stays detected until its process exits. Processes under the app's install folder are never detected. Game processes are opened only with `PROCESS_QUERY_LIMITED_INFORMATION` (architecture AD-12).
- R2 (parent R24; architecture AD-18, AD-19): With several detected games, the one in focus for 10 consecutive seconds becomes the Target Game. A running game Recording blocks the switch, and the switch is re-evaluated as soon as that Recording stops. When the Target exits, another detected game takes over at once, and on a cold start the Target is chosen at once. Only `target.changed` drives capture: GameMonitor proposes, and the SessionOrchestrator commits.
- R3 (parent R25): A Game Profile is created on first detection, with its id from the one `Canonicalize(path)`. It's named from the executable's product name read on disk, can be renamed, and inherits Global Settings. A profile marked Ignored never becomes the Target Game, and Ignored can be undone. The profile and its attributes are written through the settings op queue.
- R4 (parent R26; architecture AD-21): The OutputPathAllocator is the only producer of output paths, `<Output Folder>\<profile folder>\<Game>_<yyyy-MM-dd_HH-mm-ss>.mp4`, built from the live profile name and the QPC time of the press or start. Characters Windows doesn't allow are replaced, a path is reserved with `CREATE_NEW` (adding `_2`… on a collision), profile folder names are unique, `Monitor Recordings` is reserved, and renaming a profile affects new files only.
- R5 (parent R23, R25 user-facing part): The Main App lists Game Profiles. Each one can be renamed, Ignored, and have Recordings switched on or off; games can also be added by hand (an executable path sent to the engine).

## Constraints

- (parent NFR-3; architecture AD-12) Query-only process access, no hooks of any type, and WinEvents out of context only.
- (architecture AD-4, AD-22) Profile attributes live in story 1.4's `GameProfile`, and hand-added games in `manualGames`. Contracts stay additive, with messages in the `Game.cs` area.
- (architecture AD-19) Target changes are committed only by epic 1's SessionOrchestrator. The "Recording running" flag is a stub until epic-recordings provides it.

## Done when

- A borderless game is detected within 5 s, a hand-added windowed game is detected, and the app's own windows never are. Minimizing or switching to windowed mode doesn't end detection (R23).
- With two games running, 10 s of continuous focus switches the Target Game, but a running Recording blocks the switch. This is verified with a stub Recording flag until epic-recordings provides the real one (R24).
- Target Game appeared and exited events fire on detection and on process exit, and the capture session is pointed at the new Target Game (handoff to epic-replay-buffer-clips for R1).
- The first detection creates a renameable Game Profile, named after the product and inheriting Global Settings. Ignored stops the app from treating it as a game, and can be undone (R25).
- The resolved path is `<Output Folder>\<profile folder>\<Game>_<yyyy-MM-dd_HH-mm-ss>.mp4`, with characters Windows doesn't allow replaced and no overwrites; renaming a profile affects new files only (R26). Verified integrated on the builder's PC.

## Boundaries

- Out: the Replay Buffer's lifecycle, what it stores and how Clips are saved (epic-replay-buffer-clips, R1); the per-profile Track Layout and Output Settings overrides (R17, epic-replay-buffer-clips); Recordings (epic-recordings).
- Handoff: this epic exposes the Target Game, its appeared and exited events, the active Game Profile, and the resolved file path to epics 4, 6 and 7.

## References

- parent — _bmad-output/initiative-replay-buffer/initiative-replay-buffer.md, Requirements R23, R24, R25, R26
- architecture — _bmad-output/initiative-replay-buffer/architecture-replay-buffer/architecture-replay-buffer.md, AD-4, AD-5, AD-12, AD-18, AD-19, AD-21
- prd — _bmad-output/initiative-replay-buffer/prd-replay-buffer/prd-replay-buffer.md, §3 Glossary (Game Detection, Target Game, Game Profile)

## Notes

- Decision: 2026-10-25, the tracer bullet is story 1: a borderless game → `target.changed` → the capture follows it, with the Target Game shown in the Main App. You launch the game.
- Decision: 2026-10-25, the sequencing:
  - The GameMonitor lane runs 1 → 8 → 2 → 3 → 4 → 6.
  - The OutputPathAllocator (5) is pure Core logic, so it runs alongside that lane.
  - A Refactor sweep (7) closes the epic.
- Decision: 2026-10-25, the least-certain item is spike 8, which decides which Windows game list to read. GameConfigStore is undocumented, so it's settled before story 2 builds on it. Confirmed by the user.
- Decision: 2026-10-25, each profile's folder name is assigned once and persisted (an additive `GameProfile` attribute, allowed under AD-22), so adding or renaming a profile never moves another game's files. Confirmed by the user.
- Decision: 2026-10-25, there's no `bmad-ux` pass for the Game Profiles page; the builder lays it out, and you check it by hand. Confirmed by the user.
- Decision: 2026-11-03, spike 8 (unattended): "apps Windows identifies as games" (R1) are read from **GameConfigStore**, the `MatchedExeFullPath` values under `HKCU\System\GameConfigStore\Children` (`Type = 1`). `GameList` is dropped. The full lists and method are in `tickets/spike-which-windows-game-list-to-read-plan.md`; the builder's confirmation is in the Human Check Queue.
  - Probed unelevated and read-only on the builder's PC. GameConfigStore returned 381 children, with 259 `MatchedExeFullPath` paths, 74 of them still on disk. They cover the builder's Steam, Battle.net, EA, Riot, RSI and other games, plus a few non-games (`LeagueClientUx.exe`, `rs2client.exe`, Oculus `Client.exe`, `rpcs3.exe`) that 3.2's exclusions and Ignored handle.
  - `GameList.FindAllAsync()` returned 739 entries. Its 450 `Candidate` entries are every Start-menu app, and `LauncherExecutable` was null on all 739. Its 289 `ConfirmedBySystem` entries are GameConfigStore rows (each carries `GameConfigIds`, and every TitleId matches), with the path only in an undocumented `Properties` bag. Microsoft documents GameList as needing the `gameList` capability and special provisioning, in a Preview namespace. So it adds nothing and is the less supported route.
  - No anti-cheat concern: it's a read of the user's own HKCU hive that Game Bar itself keeps, with no handle to any game process (AD-12 unchanged), and no reports of anti-cheats reacting to it were found.
  - For 3.2, read it with plain Win32 registry calls (NativeAOT-safe, no WinRT), canonicalize each path with the one `Canonicalize`, and refresh it periodically or on `RegNotifyChangeKeyValue`. The key is undocumented, so it is best effort: if it's missing or changes shape, the fullscreen/borderless rule and hand-added games still cover R1, and the engine logs it once.
- Waits on epic-engine-baseline because: it needs the settings store's override shape, and the capture-session API to point at the Target Game.
