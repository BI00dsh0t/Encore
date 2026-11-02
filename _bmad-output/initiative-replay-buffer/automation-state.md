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
| 1 | 1.14 amendment: synthetic GPU load, unattended `fps` and `drift`, injected-key `hotkey`, scripted playback; the runs; the epic Notes Decision; merge | story-1-14 | done |
| 2 | 1.15 Refactor sweep | story-1-15 | in progress |
| 3 | Release epic 1 (`v0.1.0`) | | pending |
| 4 | 2.1 Tracer: Global Track Layout to named Track streams | 2-1-tracer-track-layout | done |
| 5 | 2.2 Track mixer | story-2-2 | in progress |
| 6 | 2.3 Live device list over IPC | story-2-3 | in progress |
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
| 26 | Slice epic 5 (Overlay and Feedback Sounds) | slice-epic-5 | done |
| 26.1 | 5.1 Tracer: capture state to the Overlay dot | | pending |
| 26.2 | 5.2 Spike Q-4: the Overlay over exclusive fullscreen and its borderless FPS cost | | pending |
| 26.3 | 5.3 Saving icon through the Save Cooldown | | pending |
| 26.4 | 5.4 Feedback notices in the engine, with a dev simulator | | pending |
| 26.5 | 5.5 Overlay message queue | | pending |
| 26.6 | 5.6 Feedback Sounds in the engine | | pending |
| 26.7 | 5.7 Mic-muted icon and the disk-full red X | | pending |
| 26.8 | 5.8 Overlay over exclusive fullscreen and the presentation path | | pending |
| 26.9 | 5.9 Capture exclusion for every App window | | pending |
| 26.10 | 5.10 Main App status line | | pending |
| 26.11 | 5.11 Feedback Sounds settings page | | pending |
| 26.12 | 5.12 Benchmark with the Overlay on | | pending |
| 26.13 | 5.13 Refactor sweep | | pending |
| 26.14 | 5.14 Overlay and Feedback Sounds end to end on the builder's PC | | pending |
| 26.15 | Release epic 5 (`v0.5.0`) | | pending |
| 27 | Slice epic 6 (Recordings) | slice-epic-6 | done |
| 27.1 | 6.1 Tracer: Record Hotkey to a fragmented MP4 Recording beside the Replay Buffer | | pending |
| 27.2 | 6.2 Crash-safe Recordings and recording-interrupted | | pending |
| 27.3 | 6.3 Record Hotkey modes and the Recordings switch | | pending |
| 27.4 | 6.4 Recording lifecycle: game exit, Target hold, Ignored and Quit | | pending |
| 27.5 | 6.5 Clips during a Recording on one encoder chain | | pending |
| 27.6 | 6.6 One file however long, and forced encoder restarts | | pending |
| 27.7 | 6.7 Disk full during a Recording | | pending |
| 27.8 | 6.8 Recording notices and sounds | | pending |
| 27.9 | 6.9 Monitor Recording by its own Hotkey | | pending |
| 27.10 | 6.10 Desktop Duplication fallback for Monitor Recording | | pending |
| 27.11 | 6.11 Recordings settings in the Main App | | pending |
| 27.12 | 6.12 Refactor sweep | | pending |
| 27.13 | 6.13 Recordings end to end on the builder's PC | | pending |
| 27.14 | Release epic 6 (`v0.6.0`) | | pending |
| 28 | Slice epic 7 (Library and Cleanup) | slice-epic-7 | done |
| 28.1 | 7.1 Tracer: a saved Clip appears in the Library within 2 s | | pending |
| 28.2 | 7.2 Changes outside the app, reconciliation and a corrupt index | | pending |
| 28.3 | 7.3 Length and thumbnails | | pending |
| 28.4 | 7.4 Favourite | | pending |
| 28.5 | 7.5 Group and filter by game, date and Favourite | | pending |
| 28.6 | 7.6 Open, show in Explorer and copy for Discord | | pending |
| 28.7 | 7.7 Rename and delete, locked while being written | | pending |
| 28.8 | 7.8 Cleanup | | pending |
| 28.9 | 7.9 Library and Cleanup settings page | | pending |
| 28.10 | 7.10 Refactor sweep | | pending |
| 28.11 | 7.11 Library and Cleanup end to end on the builder's PC | | pending |
| 28.12 | Release epic 7 (`v0.7.0`) | | pending |
| 29 | Slice epic 8 (Hardening) | slice-epic-8 | done |
| 29.1 | 8.1 Tracer: a soak harness and a 20-minute soak report | | pending |
| 29.2 | 8.2 Engine crash recovery and the restart warnings | | pending |
| 29.3 | 8.3 GPU driver reset recovery | | pending |
| 29.4 | 8.4 Sleep, resume and the secure desktop | | pending |
| 29.5 | 8.5 Monitor changes | | pending |
| 29.6 | 8.6 Nothing to re-select after a reboot, a restart or a mixer restart | | pending |
| 29.7 | 8.7 Final conformance run | | pending |
| 29.8 | 8.8 8-hour soaks in Memory and Disk mode | | pending |
| 29.9 | 8.9 T-01 to T-25 conformance record | | pending |
| 29.10 | 8.10 Refactor sweep | | pending |
| 29.11 | 8.11 Hands-off operation on the builder's PC | | pending |
| 29.12 | Release epic 8 (`v0.8.0`) | | pending |

Epic 2 (row 4 on) may start before rows 1–3 finish: the builder said so on 2026-11-01, and 2.1 no longer waits on 1.14 or 1.15. The order follows `tickets.py next`; a session that finds the board disagrees follows the board and fixes this table.

## Human Check Queue

Checks only the builder can do. Each row: ticket, what to check, how, and what the automated proxy showed.

| Ticket | Check | How | Automated proxy |
|--------|-------|-----|-----------------|
| 1.14 | `bench fps` in a real GPU-bound game (Cyberpunk, RT Overdrive, Frame Generation off, overlay off) to confirm the synthetic-load figure | `bench fps --game Cyberpunk2077` while in gameplay | the synthetic-load run |
| 1.14 | Real key presses for the Hotkey latency | `bench hotkey --presses 20` with a game focused | injected presses |
| 1.14 | Playback in Discord and DaVinci Resolve (and any player the session couldn't script) | `bench playback --out <run folder>` | the players the session scripted |
| 1.14 | A/V sync of a saved clip in a player | Play `playback.mp4` from `measurements/bench-1.14` and watch the flash against the burst | Bench's file analysis: A/V drift 0.54 ms over 60 min, offset −1.6 ms |
| 1.14 | No WGC capture border | Compare `playback-border-before.png` and `playback-border-during.png` in `measurements/bench-1.14` | the screen grab: 726 yellow pixels before and during, from desktop icons |
| 2.1 | Each Track plays only its own devices' audio, in sync with the video, in VLC (R2, R7) | Put the BEACN Game, Chat and Mic devices from `ReplayBuffer.Engine debug-list` into the Dev instance's `settings.json` as three Tracks (the engine stopped). Then run `ReplayBuffer.Engine debug-capture --window <game> --track-layout --seconds 30` with game audio, chat and speech going, and switch audio tracks in VLC | 30 s BEACN capture: three AAC streams Game, Chat, Mic in order, each within 0.07 s of the video, silent because nothing was playing. Tone proxy: a tone on System appeared in System and Both at −54 dB, and Game stayed silent (−91 dB) |
| 2.1 | The Track names show in VLC's and DaVinci Resolve's audio-track lists | Open the capture above in both | ffprobe reads the names as each stream's `name` tag (the mp4 udta `name` box), not `title`; the pre-2.1 per-device streams behave the same |

## Decisions Awaiting Review

Choices sessions made without the builder. Each row: date, ticket, decision, why.

| Date | Ticket | Decision | Why |
|------|--------|----------|-----|
| 2026-11-01 | 1.14 | A synthetic Direct3D GPU load stands in for the game in `bench fps`; R4's "GPU-bound game" counts as met by it | The builder chose it, so FPS runs unattended |
| 2026-11-02 | 1.14 | PLAN.md §6.3's "< 3 % of one modern CPU" is read as the whole processor: the engine uses 0.17 % of the machine, 4.2 % of one core | Spike 1.2 reported it that way; read per core, it misses |
| 2026-11-02 | 1.14 | Path A stays the final stack although capture→encoded latency p99 under the synthetic load is 29.3 ms, against one frame | The FPS loss passes (1.47 %), and Path C changes the hot path's language, not its GPU work |
| 2026-11-02 | 1.14 | Three real-time timing tests are accepted as known flakes under whole-suite parallelism: `ThePacedEncoderEncodesEverySlotWithAFastPreset`, `CaptureEndTests.AStatsRunWritesAReportThatMatchesTheFile`, `OutputResolutionTests.AStatsRunOnAFastWindowMeetsTheLatencyGcAndAllocationBounds` | Each passes alone, and `7229d26` fails the first the same way; making them robust is a bugfix task, not yet queued |
| 2026-11-01 | 4 | 14 stories kept in one epic, past the usual 8 to 12 | R1 to R7 are the first usable version and deliver together; a split would change the initiative's epic set |
| 2026-11-01 | 4.8 | Memory mode's safe margin: the ring stops growing below the larger of 4 GB and 15% of installed RAM available; a constant, not a setting | The PRD says only "a safe margin of the free RAM" |
| 2026-11-01 | 4.2 | A press with under 1 s buffered is reported as a save failure `clip-too-short`, with no Reset | FR-4 says no Clip is made but not whether the user is told; this way the Overlay and sound say why |
| 2026-11-01 | 4.4 | A press after the cooldown while two Clips are still writing fails as a retryable `save-busy`, with no Reset | FR-7 caps writers at two but doesn't say what a third press does |
| 2026-11-01 | 4.10 | The VBR worst-case estimate uses 1.5× the target bitrate (story 1.10's peak rate) plus each Track's AAC bitrate | FR-3 asks for a worst case without defining it |
| 2026-11-01 | 4.10 | The Global Output Settings page lands in epic 4, beside the Buffer Window and its estimate | Epic 1 delivered the keys and probing (R22) but no page, and no other epic owns one |
| 2026-11-01 | 4.10, 4.11, 4.12 | No `bmad-ux` pass for the Replay Buffer, Game Profile override and Hotkeys pages; the builder checks them by hand | Same as epics 2 and 3 |
| 2026-11-01 | 4.14 | A closing end-to-end story on the builder's PC (hitl) after the sweep | The epic's Done when ends "Verified integrated on the builder's PC" |
| 2026-11-02 | 5 | 14 entries kept in one epic, past the usual 8 to 12 | The Overlay, sounds and status line report the same events and deliver together |
| 2026-11-02 | 5.1 | The Overlay sits top-right on the Target Game's monitor (else the primary), 24 px in; a constant, not a setting | The PRD doesn't place it |
| 2026-11-02 | 5.2, 5.8 | Over exclusive fullscreen the Overlay detects `QUNS_RUNNING_D3D_FULL_SCREEN` and stays hidden; sounds carry the feedback. If an always-shown dot breaks independent flip in borderless, the Overlay shows only while it has something to show (dot for 3 s per state change), a deviation from FR-6. The spike confirms or replaces both | Q-4 is open until measured, and the builder isn't here to choose |
| 2026-11-02 | 5.3 | The saving icon is driven by snapshot state: `capture.stateChanged` gains an additive `clipsWriting` count beside `cooldownUntil` | AD-3 requires every rendered state to be in the snapshot; FR-6 wants the icon through the cooldown and while writing |
| 2026-11-02 | 5.4, 5.5 | Overlay messages are `warning.raised` notices from an engine FeedbackRouter: `clip-saved` (info, auto-dismissed after 10 s), `clip-failed` (until dismissed or the next save); each shows 4 s, oldest first | AD-15 lets the Overlay render only `capture.stateChanged` and `warning.raised`; FR-6 gives no timings |
| 2026-11-02 | 5.4 | A dev-instance-only `dev.simulate` command stubs Recording state, disk full and save failures | The epic verifies the solid dot, red X and Recording sounds before epic 6 exists |
| 2026-11-02 | 5.6 | Five built-in synthesized sounds (<600 ms, -12 dBFS peak), no custom files or volume setting; saved a rising chime, failed a low descending buzz | FR-39 doesn't specify the sounds |
| 2026-11-02 | 5.6, 5.11 | A `feedback.preview` command and a Play button per sound on the Feedback Sounds page | Lets the builder hear each sound on the chosen device |
| 2026-11-02 | 5.10, 5.11 | No `bmad-ux` pass for the Overlay, status line or Feedback Sounds page; the builder checks them by hand | Same as epics 2 to 4 |
| 2026-11-02 | 5.14 | A closing end-to-end story on the builder's PC (hitl) after the sweep | The epic's Done when ends "Verified integrated on the builder's PC" |
| 2026-11-02 | 6 | 13 entries kept in one epic, past the usual 8 to 12 | Recordings and the Monitor Recording share the writer, crash safety and disk-full path, and deliver together |
| 2026-11-02 | 6.1 | A Recording starts at a keyframe forced on the first frame after the press, with audio from that frame's QPC; it doesn't reach back into the ring | The PRD doesn't say where a Recording starts |
| 2026-11-02 | 6.2 | Each 1 s fragment is followed by `FlushFileBuffers`; a real power cut is only an optional check for the builder | FR-12 covers power loss, which can't be automated |
| 2026-11-02 | 6.5, 6.9 | While a game Recording runs, ChainKey-changing settings wait until it stops, and a Recording started with one pending shares the Replay Buffer's current chain; during a Monitor Recording a Replay Buffer chain change tears down before rebuilding (`reset-gap`), and a third chain is refused with `encoder-budget` | AD-7 caps chains at two but doesn't say how a Recording, a Reset and a Monitor Recording share them |
| 2026-11-02 | 6.6 | A forced encoder restart always ends the Recording and starts a new one at once, raising `recording-restarted`; continuing the same file isn't attempted | FR-13 allows either; a new chain's codec headers can differ |
| 2026-11-02 | 6.7 | On disk full the file is truncated to its last complete fragment; a Disk-mode Replay Buffer stopped by it raises `replay-stopped-disk-full` and restarts by itself once there is room for the ring, checked every 30 s | FR-14 says a Disk buffer stops but not when it comes back |
| 2026-11-02 | 6.9 | New `monitorRecordStart` and `monitorRecordStop` Hotkey actions (unbound by default) follow `recordHotkeyMode`, added additively; AD-11's action list should gain them | FR-23 asks for separate Start and Stop keys, which AD-11's enum lacks |
| 2026-11-02 | 6.11 | The monitor picker goes on a new Recordings page (via a new `monitors.list` command) and the Monitor Record keys on the Hotkeys page; no `bmad-ux` pass, the builder checks it by hand | Same as epics 2 to 5 |
| 2026-11-02 | 6.13 | A closing end-to-end story on the builder's PC (hitl) after the sweep | The epic's Done when ends "Verified integrated on the builder's PC" |
| 2026-11-02 | 7 | Output Folder picker on a new Library and Cleanup settings page (7.9) | No story in epics 1 to 6 owns one; changing it rebuilds the index (AD-17) |
| 2026-11-02 | 7.3 | The engine reads length with FFmpeg's demuxer, cached in `library.json` by size and mtime; a being-written item shows locked with no thumbnail or length until `EndWrite` | FR-30 asks for length; AD-1 keeps the App out of file contents |
| 2026-11-02 | 7.3 | Thumbnails only from the Windows shell with an in-memory cache and a generic icon fallback; no disk cache | AD-1 names the shell API; the spine deferred caching to epic 7 |
| 2026-11-02 | 7.2, 7.5 | An item's game is its `BeginWrite` profileId, else the profile whose folder holds it, else its top-level folder (Other at the root); Monitor Recordings are a group; its date is the `BeginWrite` time, else the file's creation time | FR-30 groups by game and date but files found outside the app have no profile |
| 2026-11-02 | 7.5 | Group by game or by day; date filter Today, Last 7 days, Last 30 days, All; Favourites-only filter | FR-30 doesn't define the date groups |
| 2026-11-02 | 7.7 | Rename changes only the base name; codes `file-being-written`, `invalid-file-name`, `file-name-taken`, `library-item-changed`; delete refuses with `recycle-bin-unavailable` rather than delete permanently | FR-31 doesn't cover these cases |
| 2026-11-02 | 7.8 | Cleanup runs after each `EndWrite`, on a `cleanup` change, after reconciliation and every 10 min; all indexed items count toward the size; Favourites alone over the limit raise `cleanup-limit-unreachable` once; a locked file is skipped until the next pass | FR-32 and AD-17 don't say when it runs or what happens when it can't reach the limit |
| 2026-11-02 | 7.8, 7.9 | Turning a limit on or lowering it shows a confirmation with a `cleanup.preview` count and size; only then is the update sent | FR-32 asks for a confirmation without saying what it shows |
| 2026-11-02 | 7.2 | `library-corrupt` is acknowledged by dismissing its warning, from a banner on the Library page, so epic 7 doesn't wait on epic 5 | AD-17 requires an acknowledgement but names no UI |
| 2026-11-02 | 7 | Actions act on one item at a time (no multi-select in v1); no `bmad-ux` pass for the Library or its settings page, the builder checks them by hand | Same as epics 2 to 6 |
| 2026-11-02 | 7 | Epic 7 doesn't wait on epic 6: locking is verified with Clips and a fake writer growing a sample fragmented MP4; epic 8's soak covers live Recordings | The epic's Handoff says so; keeps epic 7 buildable beside epic 6 |
| 2026-11-02 | 7.11 | A closing end-to-end story on the builder's PC (hitl) after the sweep | The epic's Done when ends "Verified integrated on the builder's PC" |
| 2026-11-02 | 8 | Recovery from T-01 to T-04 (driver reset, sleep, monitor changes, the secure desktop) is built in epic 8 | No earlier epic owns it, and it is robustness, not a new feature |
| 2026-11-02 | 8.2 | A restarted engine raises `engine-restarted` (the App relaunches it with `--restarted crashed\|hung`); the engine raises `app-restarted`; after 3 restarts in 10 min the supervisor stops (`engine-crash-loop`, `app-crash-loop`) and the tray offers Restart; hang simulated by dev-only `dev.simulate engine-hang` | AD-16 and NFR-5 require the restart and its report but don't say who raises it or what a crash loop does |
| 2026-11-02 | 8.2 | After a kill the Replay Buffer buffers again within 10 s; after a hang within 20 s | NFR-5 gives no recovery time |
| 2026-11-02 | 8.3 | A GPU driver reset rebuilds the device and chains in the SessionOrchestrator (every 2 s for up to 60 s, then `gpu-reset-failed`), opens a new ring generation, raises `gpu-reset`; tests use a dev-only fault seam, a real `dxcap -forcetdr` is a Human Check | T-01 says what, not how; a real reset needs elevation |
| 2026-11-02 | 8.4 | Sleep closes the ring generation (still saveable), ends a Recording (`recording-suspended`, not restarted); resume opens a new generation; the secure desktop is a static screen with no generation break; power events and frame starvation simulated, real sleep, lock and UAC are Human Checks | T-02 and T-04 don't say what happens to a Recording or the ring; sessions mustn't sleep or lock the PC |
| 2026-11-02 | 8.5 | A display change re-places the Overlay; a Monitor Recording of a removed monitor ends with `monitor-removed`, file kept; tests use a fake display port, real monitor changes are Human Checks | T-03 asks only for a clear error and no crash |
| 2026-11-02 | 8.1, 8.8 | Soak bounds after a 30 min warm-up: private bytes within max(5%, 20 MB) with slope < 2 MB/h; handles, GDI and USER within 50; every Clip passes ffprobe (all Tracks, length within a frame) | NFR-5 says no memory growth without a bound |
| 2026-11-02 | 8.8 | Two unattended 8-hour soaks (Memory, then Disk) on Bench's synthetic game without GPU load, real Audio Devices, a press every 5 min, a 10-min Recording each hour, a crossed Cleanup limit, the App killed in the last hour; a real-game soak is a Human Check | T-16 asks for both modes; the synthetic window keeps it unattended |
| 2026-11-02 | 8.9 | T-01 to T-25 recorded in `conformance-t01-t25.md` beside the epic; T-09's HDR half and T-12 (Post-roll) out of v1; T-11 met by 4.4's `save-busy`; failures needing new behaviour become bugfixes against the owning story | PLAN.md §8 predates the PRD's overrides |
| 2026-11-02 | 8.11 | A closing end-to-end story on the builder's PC (hitl) after the sweep, ending with ShadowPlay uninstalled | The epic's Done when ends with checks only the builder can run |

## Log

- 2026-11-01: Automation set up. 1.14 checkpointed at `7229d26` on `feature/1.14-benchmark` (Release build 0 warnings, Bench tests 71/71). Coordinator starts the 1.14 amendment session.
- 2026-11-01: Epic 4 sliced into stories 4.1–4.14 (merged `4a42b04`); the board validates with no problems. Queue row 25 expanded; eight defaults added to Decisions Awaiting Review.
- 2026-11-01: The builder said epic 2 can begin while 1.14's benchmarks finish. Story 2.1 now waits only on 1.4, 1.5, 1.6, 1.9, 1.10 and 1.12, and `tickets.py next` lists it as ready.
- 2026-11-01: QA round 1 on 1.14 (`d596f0f`): QA FAIL with 7 findings, sent to the 1.14 session. The stage-split diagnostic was rerun with the display awake (done 12:08).
- 2026-11-01 12:18: **PAUSED by the builder**, who needs the PC. The coordinator stopped 1.14's FPS rerun (started 12:08; it must be redone from scratch) and paused 2.1. `.bench-running` in the main folder marks the pause, and sessions start no builds, tests or benchmarks while it exists. **To resume:** the builder says so, the coordinator deletes `.bench-running`, tells 1.14 to rerun `bench fps` and finish its QA fixes, and tells 2.1 to continue. Running stories at the time: 1.14 (QA fixes), 2.1 (implementation).
- 2026-11-02 02:45: **Resumed by the builder.** 1.14 restarted `bench fps` at 02:40 and holds `.bench-running` until the run ends. 2.1 continues from `420ee54` but waits for the lock before it builds or tests. The board shows nothing else ready (the rest of epic 2 waits on 2.1; epics 3 and 4 wait on epic 1), so slicing epic 5 runs in parallel (planning only, no GPU).
- 2026-11-02: Epic 5 sliced into stories 5.1–5.14 (merged `910486b`); the board validates with no problems. Epic 5 now also declares needs on epics 1 (5.2, 5.12 use 1.14's bench) and 2 (2.3, 2.4, 2.5). Queue row 26 expanded; ten defaults added to Decisions Awaiting Review.
- 2026-11-02: 1.14 reported READY FOR QA at `7c21dc2`. All 7 round-1 findings are handled, and the display-on FPS rerun shows a 1.47 % loss. The coordinator removed the stale `.bench-running` and sent QA round 2 to qa-1-14. 2.1 is now building and testing. Epic 5 is sliced (`910486b`), and slicing epic 6 has started (slice-epic-6).
- 2026-11-02: Epic 6 sliced into stories 6.1–6.13 (merged `cbb1255`); the board validates with no problems. Epic 6 now also declares needs on epics 3 (3.3, 3.4, 3.6) and 2 (2.4). Queue row 27 expanded; nine defaults added to Decisions Awaiting Review.
- 2026-11-02: QA round 2 on 1.14 (`7c21dc2`): **QA PASS**, 0 findings. 1.14 was told to merge. 2.1 reported READY FOR QA at `d7d2485`, and QA round 1 started (qa-2-1). Epic 6 is sliced into 6.1–6.13 (`cbb1255`), and slicing epic 7 has started (slice-epic-7).
- 2026-11-02: 1.14 merged into `develop` (`3db2541`) and marked done (`cc95c7b`). Final figures on the synthetic load: FPS loss 1.47 % (pass), latency p99 29.3 ms (miss), A/V drift 0.54 ms over 60 min, audio-to-audio 160 samples. Path A stays the final stack. Two Human Check rows and three Decisions Awaiting Review rows added. Follow-ups not queued: a bugfix to make the three timing tests robust, and `measure-path-a.ps1` cleaning up the `ReplayBuffer-PathA-<pid>` ETW session a killed run leaves behind.
- 2026-11-02: Epic 7 sliced into stories 7.1–7.11 (merged `3c4f93f`); the board validates with no problems. Epic 7 now also declares a need on epic 3 (7.1 waits on 3.5) and doesn't wait on epic 6. Queue row 28 expanded; twelve defaults added to Decisions Awaiting Review.
- 2026-11-02: QA round 1 on 2.1 (`d7d2485`): **QA PASS**, 0 findings. 2.1 was told to merge. 1.14 is merged (`3db2541`), so 1.15 (story-1-15) has started. Epic 7 is sliced into 7.1–7.11 (`3c4f93f`), and slicing epic 8 has started (slice-epic-8). Once 2.1 merges, 2.2 and 2.3 are next; both wait only on 2.1.
- 2026-11-02: 2.1 merged into `develop` (`8a501fc`) after QA round 1 passed with 0 findings, and marked done. Before the merge, `develop` (with 1.14) was merged into the branch: Release build 0 warnings, 694 of 696 tests passed and the two timing tests passed on their one rerun, publish 0 warnings. Two Human Check rows added (VLC listening and sync, and the Track names in VLC and Resolve). Each fresh QA worktree needs `native/ffmpeg` copied in or `tools/fetch-ffmpeg.ps1`, or the capture tests are skipped. 2.2 and 2.3 are now ready.
- 2026-11-02: 2.1 is merged (`8a501fc`). 2.2 (story-2-2) and 2.3 (story-2-3) started in parallel; both waited only on 2.1. 1.15 and epic 8's slicing are still running.
- 2026-11-02: Epic 8 sliced into stories 8.1–8.11 (merged `8e77c75`); the board validates with no problems. Epic 8 now also declares needs on epics 1 to 5. Queue row 29 expanded; ten defaults added to Decisions Awaiting Review. Every epic is now sliced.
- 2026-11-02 07:41: The usage limit stopped 1.15, 2.2 and 2.3 mid-work. Their state: 1.15's plan was committed and its timing-test edits were uncommitted, 2.2's plan was committed, and 2.3 had no commits. After the limit reset, the coordinator told all three to continue. Epic 8 is sliced into 8.1–8.11 (`8e77c75`), so all eight epics are now sliced.
