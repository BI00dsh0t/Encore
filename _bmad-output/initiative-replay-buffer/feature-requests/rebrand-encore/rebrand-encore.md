---
type: epic
title: "Rebrand the app to Encore, and the Replay Buffer feature to Rewind"
parent: initiative-replay-buffer
covers: []
sources:
  - _bmad-output/initiative-replay-buffer/feature-requests/rebrand-encore/request.md
after: []
risk: high
---

# Rebrand the app to Encore, and the Replay Buffer feature to Rewind

Status: phases 1–3 implemented 2026-11-10 at the builder's "Do all phases". Feature `d979da3`, no-ff develop merge `6f41cea`. Release build 0 warnings; no tests run (the no-tests rule), so they're in `test-backlog.md`. Phase 4, the repo folder, waits for the builder.

**As built:** one bulk rename plus hand fixes.
- The visible name, Rewind and the code all changed in one pass.
- `LegacyData.CopyIfFirstStart` (Contracts) runs at App and engine start, before anything reads the data folders. It copies the old `ReplayBuffer` / `ReplayBuffer-Dev` roaming and local folders once, without `ring`, staged then renamed into place, and leaves the old folders alone.
- `RunKey` drops the old `ReplayBuffer` Run value whenever the startup value is written or deleted. The App syncs it from the first settings snapshot.
- `GameMonitor` skips `replaybuffer.app.exe` and `replaybuffer.engine.exe` as well as the Encore exes.
- `GlossaryTests` now bans "Replay Buffer" and "ReplayBuffer".
- The meter-probe hash salt keeps its old text, so probe ids don't change.

**A change from R3:** a Clips folder left at its default now becomes `Videos\Encore`. Old Clips stay where they are and stay in the Library through their saved paths. A Clips folder the user chose is kept.

**Note for the builder:** the app's screens already avoided "Replay Buffer" (epic 10's glossary uses "Clips" and "recent gameplay"), so "Rewind" shows only in a few messages. Making Rewind a visible label, such as the Clips page heading or a status line, is a small follow-up if wanted.

## Requirements

- **R1, what users see:** every place that shows the app's name says **Encore**:
  - window titles, the tray tooltip and menu, the brand label, About text, dialogs and notices;
  - the installer's display name, the Start menu shortcut, and the Startup (Run key) entry;
  - the exe's file description and product name;
  - the default Clips folder under Videos (`Videos\Encore`).
- **R2, the feature:** "Replay Buffer" as a feature name becomes **"Rewind"** everywhere a user reads it (labels, status line, help, notices, tray, glossary, EXPERIENCE.md). `GlossaryTests` bans the old term in user-facing strings. Settings keys, error codes and IPC message names don't contain the brand and stay unchanged (AD-22), so stored settings keep loading.
- **R3, folders and data, with migration:**
  - The instance name becomes `Encore`, `Encore-Dev` or `Encore-Test-…`. That changes the `%APPDATA%` and `%LOCALAPPDATA%` data folders, the install folder (`%LOCALAPPDATA%\Programs\Encore`), named pipes, mutexes and the Run key value.
  - **On first start, Encore copies an existing `ReplayBuffer` settings and Library folder to the new place, once,** and leaves the old one in place. If the old Clips folder setting points at `Videos\ReplayBuffer`, it keeps pointing there; existing files never move.
  - The installer removes the old Startup entry and the old Start menu shortcut. It doesn't touch the old install folder's user data.
  - A running old ReplayBuffer engine is not killed; the user quits it.
- **R4, code:**
  - `ReplayBuffer.slnx` becomes `Encore.slnx`, and every `ReplayBuffer.*` project, folder and namespace becomes `Encore.*` (src, tests, bench, tools).
  - Assembly names and exes become `Encore.App.exe` and `Encore.Engine.exe` (the App can ship as `Encore.exe`).
  - Scripts in `tools/`, `CLAUDE.md`, the build props and the publish and install scripts follow.
  - Theme keys prefixed `Rb…` may stay; they're internal.
- **R5, the repo folder:** renaming `%USERPROFILE%\Documents\repos\ReplayBuffer` to `…\Encore` is done **last, by the builder**, with no agent running, because moving it breaks open worktrees and sessions. The BMad initiative folder name `initiative-replay-buffer`, past logs and history are left as they are.

## Phases (each on its own branch from develop, build-only under the current no-tests rule, then merged)

1. **Words:** R1's visible names and R2's "Rewind", with no path or identity change.
2. **Identity and migration:** R3, including installer changes and the one-time data copy. Its tests are written and added to `test-backlog.md`.
3. **Code rename:** R4, done when no other branch is open. `feature/5.8-overlay-perf-research` and `feature/8.8-soaks` are unmerged and will need the rename applied when they come back.
4. **Repo folder:** R5, by the builder. The handoff and memory paths are updated afterwards.

## Decisions (recorded defaults)

- Settings keys and error codes keep their names (none contain the brand); only `Instance` names change.
- Old data is copied, never moved or deleted.
- The Trello board and the initiative's planning files keep their names; only new text uses Encore.
