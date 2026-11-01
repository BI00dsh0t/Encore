# Automation State

The single place to resume the unattended build of initiative-replay-buffer after a usage limit or a restart. Every session reads this first and updates it at each checkpoint. It lives on `develop` in the main folder: `%USERPROFILE%\Documents\repos\ReplayBuffer\_bmad-output\initiative-replay-buffer\automation-state.md`.

**To resume:** open the coordinator session (or any new session in this repo), say "continue the automation", and point it at this file. It reads the Queue, checks `git -C <main> log develop` and the board (`python _bmad/method/scripts/tickets.py status _bmad-output/initiative-replay-buffer`), and starts the session for the first row that isn't `done`.

## Standing decisions (builder, 2026-11-01)

- Build everything to the end of the initiative, unattended. The builder is away from the desktop.
- **One fresh session per task** (story, spike, epic slicing, release), started by the coordinator one at a time, so context stays small. Sessions run sequentially: they share `develop` in the main folder.
- Each story runs `bmad-build-auto` on its ticket (unattended plans, implementation and review). No plan-approval halts: the builder pre-approved plans that keep to the ticket and epic.
- **QA gate (builder, 2026-11-01):** after each change, the coordinator starts a separate QA session on the story's branch before anything merges. QA sends its findings straight to the story's session, which fixes them; QA re-checks until it passes.
- **Merging is approved:** when a story is green (0-warning build, all tests, NativeAOT publish) and QA has passed, its session merges it into `develop` from the main folder with `--no-ff` and `<type>(<scope>): merge <ref>-<slug>`, then commits `tickets.py mark <epic> <ref> done` on `develop`. **Never push.**
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
5. Commit, then end the turn with a report that begins `READY FOR QA`: the branch, its tip commit, the verification results, and anything queued for the builder. Don't merge yet.
6. Fix each QA finding that arrives as a cross-session message, commit, and report `READY FOR QA` again.
7. When the coordinator says QA passed: merge into `develop` from the main folder (`git -C %USERPROFILE%/Documents/repos/ReplayBuffer merge --no-ff ...`), mark the ticket done, commit, and update this file's Queue, Log and queues. Don't push.

## QA procedure

The coordinator starts a fresh QA session in its own worktree, cut from the story's branch, once that session reports `READY FOR QA`. QA:

1. Reads this file, `CLAUDE.md`, the ticket (`tickets.py find <ref>`), the epic file, and the story's plan, its ACs above all.
2. Reviews the diff against `develop` for unmet ACs, broken `CLAUDE.md` rules, bugs and gaps in the tests.
3. Runs the full verification itself (0-warning Release build, `dotnet test --solution ReplayBuffer.slnx`, NativeAOT publish), plus each AC that can run without a person. For a HITL story it also runs the automated proxy the story describes.
4. Changes no code. It sends its findings as one cross-session message (`SendMessage`, `to` = the story session's id) listing each problem's location, what goes wrong, and the evidence, then ends with a report that begins `QA PASS` or `QA FAIL` and gives the count.
5. On a re-check, verifies the fixes and anything they touched.

The coordinator logs each QA round in the Log below.

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
| 25 | Slice epic 4 (Replay Buffer and Clips) | | done |
| 25.1 | 4.1 Tracer: Save Hotkey to a multi-Track Clip from a Memory ring | | pending |
| 25.2 | 4.2 Exact save cut and gapless Reset | | pending |
| 25.3 | 4.3 Replay Buffer lifecycle: Target switch, exit drain and Ignored | | pending |
| 25.4 | 4.4 Save Cooldown, save events and two concurrent writers | | pending |
| 25.5 | 4.5 Failed saves, retry and the file-being-written signal | | pending |
| 25.6 | 4.6 Ring generations and Buffer Window resizing | | pending |
| 25.7 | 4.7 Disk-mode ring | | pending |
| 25.8 | 4.8 Memory-mode RAM margin | | pending |
| 25.9 | 4.9 Per-Game-Profile settings at the next Reset | | pending |
| 25.10 | 4.10 Replay Buffer and Output Settings page with the storage estimate | | pending |
| 25.11 | 4.11 Game Profile overrides in the Main App | | pending |
| 25.12 | 4.12 Hotkeys page in the Main App | | pending |
| 25.13 | 4.13 Refactor sweep | | pending |
| 25.14 | 4.14 Clips end to end on the builder's PC | | pending |
| 25.15 | Release epic 4 (`v0.4.0`) | | pending |
| 26 | Slice epic 5 (Overlay and Feedback Sounds), then its stories and release | | pending |
| 27 | Slice epic 6 (Recordings), then its stories and release | | pending |
| 28 | Slice epic 7 (Library and Cleanup), then its stories and release | | pending |
| 29 | Slice epic 8 (Hardening), then its stories and release | | pending |

Epic 2 (row 4 on) may start before rows 1–3 finish: the builder said so on 2026-11-01, and 2.1 no longer waits on 1.14 or 1.15. The order follows `tickets.py next`; a session that finds the board disagrees follows the board and fixes this table.

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
| 2026-11-01 | 4 | 14 stories kept in one epic, past the usual 8 to 12 | R1 to R7 are the first usable version and deliver together; a split would change the initiative's epic set |
| 2026-11-01 | 4.8 | Memory mode's safe margin: the ring stops growing below the larger of 4 GB and 15% of installed RAM available; a constant, not a setting | The PRD says only "a safe margin of the free RAM" |
| 2026-11-01 | 4.2 | A press with under 1 s buffered is reported as a save failure `clip-too-short`, with no Reset | FR-4 says no Clip is made but not whether the user is told; this way the Overlay and sound say why |
| 2026-11-01 | 4.4 | A press after the cooldown while two Clips are still writing fails as a retryable `save-busy`, with no Reset | FR-7 caps writers at two but doesn't say what a third press does |
| 2026-11-01 | 4.10 | The VBR worst-case estimate uses 1.5× the target bitrate (story 1.10's peak rate) plus each Track's AAC bitrate | FR-3 asks for a worst case without defining it |
| 2026-11-01 | 4.10 | The Global Output Settings page lands in epic 4, beside the Buffer Window and its estimate | Epic 1 delivered the keys and probing (R22) but no page, and no other epic owns one |
| 2026-11-01 | 4.10, 4.11, 4.12 | No `bmad-ux` pass for the Replay Buffer, Game Profile override and Hotkeys pages; the builder checks them by hand | Same as epics 2 and 3 |
| 2026-11-01 | 4.14 | A closing end-to-end story on the builder's PC (hitl) after the sweep | The epic's Done when ends "Verified integrated on the builder's PC" |

## Log

- 2026-11-01: Automation set up. 1.14 checkpointed at `7229d26` on `feature/1.14-benchmark` (Release build 0 warnings, Bench tests 71/71). Coordinator starts the 1.14 amendment session.
- 2026-11-01: Epic 4 sliced into stories 4.1–4.14 (merged `4a42b04`); the board validates with no problems. Queue row 25 expanded; eight defaults added to Decisions Awaiting Review.
- 2026-11-01: The builder said epic 2 can begin while 1.14's benchmarks finish. Story 2.1 now waits only on 1.4, 1.5, 1.6, 1.9, 1.10 and 1.12, and `tickets.py next` lists it as ready.
