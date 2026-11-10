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

Status: recorded 2026-11-10; waits for the builder's go to implement.

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
