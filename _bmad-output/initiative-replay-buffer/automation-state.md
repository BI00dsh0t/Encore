# Automation State

The single place to resume the unattended build of initiative-replay-buffer after a usage limit or a restart. Every session reads this first and updates it at each checkpoint. It lives on `develop` in the main folder: `%USERPROFILE%\Documents\repos\ReplayBuffer\_bmad-output\initiative-replay-buffer\automation-state.md`.

**To resume:** open the coordinator session (or any new session in this repo), say "continue the automation", and point it at this file. It reads the Queue, checks `git -C <main> log develop` and the board (`python _bmad/method/scripts/tickets.py status _bmad-output/initiative-replay-buffer`), and starts the session for the first row that isn't `done`.

## Standing decisions (builder, 2026-11-01)

- Build everything to the end of the initiative, unattended. The builder is away from the desktop.
- **One fresh session per task** (story, spike, epic slicing, release), started by the coordinator one at a time, so context stays small. Sessions run sequentially: they share `develop` in the main folder.
- Each story runs `bmad-build-auto` on its ticket (unattended plans, implementation and review). No plan-approval halts: the builder pre-approved plans that keep to the ticket and epic.
- **Merging is approved:** when a story is green (0-warning build, all tests, NativeAOT publish), its session merges it into `develop` from the main folder with `--no-ff` and `<type>(<scope>): merge <ref>-<slug>`, then commits `tickets.py mark <epic> <ref> done` on `develop`. **Never push.**
- **Releases:** at each epic's end (after its refactor sweep), a release session runs `git flow release start 0.<epic>.0` / `finish` in the main folder, tagging `v0.<epic>.0`, and leaves `develop` checked out there.
- **HITL checks:** a session automates what it can (synthetic input, injected keys, scripted players, logs, screenshots of its own windows) and records the rest in the Human Check Queue below instead of waiting. The story can still be marked done; the queue is the builder's to work through on return.
- **Unsliced epics (4–8):** a slicing session runs Sofia (`bmad-agent-po`) to break the epic into stories, choosing defaults where the PRD and architecture leave a gap, and lists each such choice in the Decisions Awaiting Review below.
- The PC is kept awake and unlocked while sessions work (the app's keep-awake hold plus a display-required holder). Never change Windows settings.
- Commit at every checkpoint and keep each plan's log current; the usage limit can land at any time.

## Per-session procedure (stories)

1. Read this file, `CLAUDE.md`, the ticket via `tickets.py find <ref>`, and the epic file.
2. `git flow feature start <ref>-<slug>` in the session's own worktree (cut from `develop`). For 1.14 only: switch to the existing `feature/1.14-benchmark`.
3. Run `bmad-build-auto` with the ticket ref.
4. Verify: `dotnet build ReplayBuffer.slnx -c Release` (0 warnings), `dotnet test --solution ReplayBuffer.slnx`, `pwsh tools/publish-engine.ps1` (0 IL warnings). Real-time timing tests that fail only under GPU load get one rerun.
5. Merge into `develop` from the main folder (`git -C %USERPROFILE%/Documents/repos/ReplayBuffer merge --no-ff ...`), mark the ticket done, commit, and update this file's Queue, Log and queues. Don't push.
6. End with a one-paragraph report: commits, verification results, anything queued for the builder.

## Queue

| # | Task | Session | Status |
|---|------|---------|--------|
| 1 | 1.14 amendment: synthetic GPU load, unattended `fps` and `drift`, injected-key `hotkey`, scripted playback; the runs; the epic Notes Decision; merge | | in progress |
| 2 | 1.15 Refactor sweep | | pending |
| 3 | Release epic 1 (`v0.1.0`) | | pending |
| 4 | 2.1 Tracer: Global Track Layout to named Track streams | | pending |
| 5 | 2.2 Track mixer | | pending |
| 6 | 2.3 Live device list over IPC | | pending |
| 7 | 2.5 Device dropout and rebind | | pending |
| 8 | 2.4 Mic Devices and mic mute | | pending |
| 9 | 2.6 Track meters in the engine | | pending |
| 10 | 2.7 Track Layout editor in the Main App | | pending |
| 11 | 2.11 Mic Devices and double-count warning in the editor | | pending |
| 12 | 2.8 Live meters in the Track Layout editor | | pending |
| 13 | 2.9 Named Tracks in players and editors | | pending |
| 14 | 2.10 Refactor sweep | | pending |
| 15 | Release epic 2 (`v0.2.0`) | | pending |
| 16 | 3.1 Tracer: detect a borderless game | | pending |
| 17 | 3.8 Spike: which Windows game list to read | | pending |
| 18 | 3.2 Detection sources and exclusions | | pending |
| 19 | 3.3 Target Game rule | | pending |
| 20 | 3.4 Game Profile auto-creation and Ignored | | pending |
| 21 | 3.5 OutputPathAllocator | | pending |
| 22 | 3.6 Game Profiles page in the Main App | | pending |
| 23 | 3.7 Refactor sweep | | pending |
| 24 | Release epic 3 (`v0.3.0`) | | pending |
| 25 | Slice epic 4 (Replay Buffer and Clips), then its stories and release | | pending |
| 26 | Slice epic 5 (Overlay and Feedback Sounds), then its stories and release | | pending |
| 27 | Slice epic 6 (Recordings), then its stories and release | | pending |
| 28 | Slice epic 7 (Library and Cleanup), then its stories and release | | pending |
| 29 | Slice epic 8 (Hardening), then its stories and release | | pending |

The order follows `tickets.py next`; a session that finds the board disagrees follows the board and fixes this table.

## Human Check Queue

Checks only the builder can do. Each row: ticket, what to check, how, and what the automated proxy showed.

| Ticket | Check | How | Automated proxy |
|--------|-------|-----|-----------------|
| 1.14 | `bench fps` in a real GPU-bound game (Cyberpunk, RT Overdrive, Frame Generation off, overlay off) to confirm the synthetic-load figure | `bench fps --game Cyberpunk2077` while in gameplay | the synthetic-load run |
| 1.14 | Real key presses for the Hotkey latency | `bench hotkey --presses 20` with a game focused | injected presses |
| 1.14 | Playback in Discord and DaVinci Resolve (and any player the session couldn't script) | `bench playback --out <run folder>` | the players the session scripted |

## Decisions Awaiting Review

Choices sessions made without the builder. Each row: date, ticket, decision, why.

| Date | Ticket | Decision | Why |
|------|--------|----------|-----|
| 2026-11-01 | 1.14 | A synthetic Direct3D GPU load stands in for the game in `bench fps`; R4's "GPU-bound game" counts as met by it | The builder chose it, so FPS runs unattended |

## Log

- 2026-11-01: Automation set up. 1.14 checkpointed at `7229d26` on `feature/1.14-benchmark` (Release build 0 warnings, Bench tests 71/71). Coordinator starts the 1.14 amendment session.
