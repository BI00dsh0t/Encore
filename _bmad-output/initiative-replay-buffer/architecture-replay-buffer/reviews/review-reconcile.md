---
type: review
lens: reconcile
target: _bmad-output/initiative-replay-buffer/architecture-replay-buffer/architecture-replay-buffer.md
inputs:
  - _bmad-output/initiative-replay-buffer/initiative-replay-buffer.md (R1–R36, Constraints, Boundaries, Notes)
  - _bmad-output/initiative-replay-buffer/tickets/epic-*/epic-*.md (8 epics, handoffs)
  - PLAN.md (§0, §1, §2, §3, §6, §7)
  - _bmad-output/initiative-replay-buffer/prd-replay-buffer/addendum.md (Precedence, Inputs for architecture)
  - (spot check) prd-replay-buffer.md §5 NFR-2/NFR-3/NFR-5/NFR-6
date: 2026-10-25
---

# Reconcile review: architecture spine vs. its inputs

## Verdict

The spine lands most of the platform decisions faithfully: process split, IPC envelope, settings ownership, ring, containers, Hotkeys, capture APIs and the interop stack. PRD-over-PLAN precedence is respected throughout. But three of the four handoffs the epics name have no real governing AD: the capture-session API, the status-line warnings and the file-being-written protocol. The Target Game events have only a partial one. One question the addendum explicitly routed to architecture is left open (a Clip that spans an encoder restart). Two quiet constraints only partly land: NFR-2's bound on GPU use, and NFR-3 beyond injection and keyboard hooks. The supervision design also leaves a gap in R34/NFR-5 when the App process itself dies.

## Findings

### F1 [high] Epic handoffs: the capture-session API has no governing AD

- **Input:** initiative Notes ("Epic 1 owns ... the capture-session API"). Epic 1 Description and Boundaries. Epic 2 Notes ("Tracks attach to its capture-session API"). Epic 3 ("the capture session is pointed at the new Target Game"). Epic 4 (lifecycle: start on appeared, stop 30 s after exited). Epic 6 ("Recording running" flag read by epic 3's rule). R1, R10, R21, R24.
- **What didn't land:** AD-5 defines only what a session *reads* (one immutable `EffectiveSettings` snapshot). Nothing defines:
  - what a capture session is, or who owns its lifecycle (Core "session orchestration" is named in the layer table but has no rule);
  - how Tracks attach to a session, and how a session is retargeted;
  - the lifecycle rules several epics must agree on: start on Target Game appeared; keep running 30 s after exit (R1); Reset on a Target Game switch (R24); a game Recording stops on game exit (R10), but a Monitor Recording doesn't (R21); the "Recording running" flag blocks a Target switch (R24).
- **Related mismatch in AD-18:** AD-18 publishes `game.detected`, `target.changed` and `game.exited`, but epics 3 and 4 build on "Target Game appeared / exited" events. `game.exited` (any detected game) is not the same thing as "the Target Game exited". AD-18 also frames these as IPC-style `noun.verb` events, but the epic 3 → epic 4 handoff happens inside the engine. AD-3 governs only the pipe, and no AD covers in-engine domain events. AD-18 says GameMonitor "alone decides" the Target Game, but R24 needs a Recording-running input that epic 6 owns, and the spine has no rule for that dependency.
- **Suggested fix:** Add an AD "Capture sessions":
  - A single `SessionOrchestrator` in Core owns every session: the Replay Buffer, game Recordings and Monitor Recordings.
  - Sessions are created from a Target and an `EffectiveSettings` snapshot. Tracks attach through a port.
  - State the lifecycle table: appeared → start; exited → 30 s hold, then stop; switch → Reset; game exit stops game Recordings only.
  - The orchestrator exposes `IsRecording(target)` to GameMonitor.
  - In-engine domain events (`TargetAppeared`, `TargetExited`, `TargetChanged`) are distinct from their IPC projections.
  - Align AD-18's event names with these.

### F2 [high] Addendum Precedence: the "Clip spanning an encoder restart" question is left open

- **Input:** the addendum Precedence section, "Encoder restarts": "[Open for architecture: whether a Clip can stay one file.]". PLAN.md §0 decision 3 and §3 (`clip_part1.mp4`, `clip_part2.mp4`). R7 and R12.
- **What didn't land:** AD-8 says only "A forced encoder restart opens a new generation". The spine never answers the routed question: does a Clip that spans generations become one file or one file per generation? That answer affects three epics:
  - epic 4, which writes Clips;
  - epic 3, which owns the R26 naming `<Game>_<yyyy-MM-dd_HH-mm-ss>.mp4` and has no part-suffix rule;
  - epic 7, which lists the files and has no idea how parts group.

  For R12, whether a Recording continues the same file or ends and restarts is also unpicked. The naming and Library consequences of "starts a new Recording at once" are cross-unit for the same reason.
- **Suggested fix:** Decide it in AD-8 or AD-9. Example: a Clip spanning generations is written as N files, `<Game>_<ts>_part<N>.mp4`, and reported through the R7 failure/notice path. Extend the file-name convention to match. For R12, state whether the default is to end cleanly and start a new file (same naming rule) or to continue.

### F3 [high] R34, R35, NFR-5, R31: supervision fails when the App process itself dies

- **Input:** R34 ("Hotkeys survive the Main App closing or crashing. A crashed engine restarts by itself"), NFR-5, R35 (hands-off) and R31 (the tray quits).
- **What didn't land:** AD-16 makes the App the engine's only supervisor, and the engine never supervises the App. If the App crashes, nothing restarts it. A later engine crash then goes unsupervised, which breaks R34 and hands-off operation.
- **Second problem:** AD-15's "Prevents: feedback disappearing when the UI crashes" is only half true. The Overlay lives in the App process and disappears with it. Only the sounds survive.
- **Third problem:** AD-16 says "the engine survives the App exiting", but R31 says the tray quits. The spine never defines whether Quit stops the engine, or whether "Main App closed" means the window closing or the App process exiting. PRD NFR-5 says "Main App window is closed".
- **Suggested fix:**
  - Closing the Main App window leaves the App process in the tray.
  - Tray Quit sends an `engine.shutdown` command and exits both processes.
  - Make supervision mutual: the engine relaunches the App, and with it the Overlay, if the App dies unexpectedly. Alternatively, add a minimal watchdog.
  - Correct AD-15's Prevents line.

### F4 [medium] NFR-2 and one GPU (C1): there is no bound on GPU, encoder or I/O load

- **Input:** initiative Constraints (NFR-2: "bounds its memory and GPU use"). The PRD's NFR-2: "GPU scheduling choices are made in the architecture". PLAN.md C1, §0 decision 5 ("low I/O priority"), §1 (engine priority) and §7.2.
- **What didn't land:** AD-7 builds "a second chain" whenever two Sinks' snapshots differ. Under R17 (a Recording keeps its settings while the Replay Buffer Resets onto new profile settings) and R21 (a Monitor Recording on Global Settings, alongside both), that means an unbounded number of concurrent hardware encoder sessions, scalers and color passes on the game's GPU. Consumer GPUs cap NVENC sessions.
- **Other gaps:**
  - The capability map credits NFR-2 to AD-1, AD-2, AD-11 and AD-12, none of which bounds a resource.
  - GPU priority is deferred as something "no other unit sees", yet every encoder chain that epics 4 and 6 build inherits it.
  - "All devices on the game's adapter" (C1) is not stated.
  - PLAN's low I/O priority for the Disk ring and Recording writes is dropped.
- **Suggested fix:** Add a resource-budget AD:
  - a maximum number of concurrent encoder chains (for example, one game chain plus one monitor chain), with a rule for what happens when a Recording's frozen snapshot differs;
  - one D3D11 device on the adapter that owns the game's output;
  - the epic 1 priority decision applies to every chain;
  - Disk ring and Recording I/O run at low I/O priority.

### F5 [medium] NFR-3 (anti-cheat): only injection and LL keyboard hooks are ruled out

- **Input:** initiative Constraints (NFR-3: "no injection, no hooking ... nothing drawn inside the game process"). PLAN.md §9.
- **What didn't land:**
  - AD-11 forbids `WH_KEYBOARD_LL`, and AD-12 forbids loading code into another process.
  - Nothing governs how GameMonitor (epic 3) and Platform.Windows touch the game process. Anti-cheat flags many access patterns:
    - `OpenProcess` with VM or read rights;
    - `ReadProcessMemory`;
    - module enumeration;
    - other `SetWindowsHookEx` types (global hooks inject DLLs);
    - `SetWinEventHook` with `WINEVENT_INCONTEXT`.
  - The Structural Seed says "Game process — never touched", but no rule enforces it.
- **Suggested fix:** Extend AD-12 or AD-18 with:
  - process access limited to `PROCESS_QUERY_LIMITED_INFORMATION`;
  - no memory reads and no module enumeration;
  - no `SetWindowsHookEx` of any type;
  - WinEvent hooks out-of-context only;
  - the product name (R25) read from the executable file on disk, not from the process.

### F6 [medium] Epic handoffs: status-line warnings and notices have no shared shape

- **Input:** initiative Notes ("Epics 2 and 8 emit warnings that epic 5's status line displays"). Epics 2, 5 and 8. R3 ("the status line says so"), R8 ("recent warnings"), R12 and R13 ("tells the user", "notice", a red X until it is dismissed) and R19 (the Overlay stays silent on a dropout).
- **What didn't land:** The Errors and Logging conventions only say failures are "emitted as events". There is no common event type or payload, and no routing rule saying which surface (Overlay, status line, sound) each notice targets; R19 needs exactly that. Three more gaps:
  - Where "recent warnings" and sticky state such as the disk-full red X live, so they survive an App restart or reconnect through `state.snapshot`.
  - How these notices are dismissed.
  - Who emits the engine-restart warning. When the engine has crashed, only the App supervisor can, and AD-3 only covers engine → App events.
- **Suggested fix:** Add a convention: one `notice.raised` event `{ code, severity, source, at, surfaces: [overlay|statusLine|sound], sticky }`. The engine keeps a bounded recent-notice list in `state.snapshot`, `notice.dismiss` clears sticky ones, and the supervisor produces a local `engine-restarted` notice in the same shape.

### F7 [medium] Epic handoffs: the "file being written" signal and the Clip write protocol

- **Input:** initiative Notes ("Epic 6's Recordings raise the 'file being written' signal for epic 7"). Epics 4, 6 and 7. R7 ("no file that only looks playable"), R27 (new Clips appear within 2 s), R28 and R29.
- **What didn't land:**
  - AD-17 gives the engine "the in-memory set of files being written", but no rule says how writers register: before the first byte, released after the final close or rename.
  - Nothing says how a faststart Clip (AD-9) avoids leaving a half-written `.mp4` that the watcher lists, or what happens to temp files and partial Recordings after an engine crash and restart.
  - Epic 4 expects "the encoder and fragmented MP4 writer" from epic 1, but AD-9 makes Clips complete faststart MP4. That is a second muxer path, and no epic owns it.
- **Suggested fix:**
  - In AD-17: writers register with `LibraryIndex.BeginWrite(path)` before creating the file and call `EndWrite` after it closes.
  - Clips are written to `<name>.mp4.part` and atomically renamed into place. The watcher ignores `.part` files.
  - On engine start, leftover `.part` files are deleted and unfinished fMP4 Recordings are kept.
  - In AD-9, name the faststart Clip muxer's owner (epic 4) or update epic 4's dependency note.

### F8 [medium] PLAN.md §2 and the addendum: audio drift correction and one source per endpoint are dropped

- **Input:** PLAN.md §2 pipeline step 5 ("Adaptive drift correction against QPC"), the addendum's Inputs for architecture (which list drift correction), R15 (a device can be in several Tracks, and all stay in sync), R30 (meters work with nothing captured), R33 and R1 (no capture after the 30 s hold).
- **What didn't land:**
  - AD-6 and AD-13 keep QPC stamping, silence fill and 48 kHz, but drop drift correction.
  - They don't say where correction happens relative to the Track mix. Several independent-clock devices summed in one Track must be corrected first.
  - They don't say whether each endpoint is opened once and fanned out to every Track, session (Replay Buffer, Recording, Monitor Recording) and meter, or opened per consumer. That determines whether Tracks stay in sync, and how epic 2's meters coexist with epic 4 and 6 sessions.
- **Suggested fix:** Add to AD-13:
  - one WASAPI client per endpoint, drift-corrected to QPC by resampler compensation before fan-out to Track mixers;
  - meters tap the same sources, which are open only while the meter view is open, and never feed a Sink (consistent with R1).

## Lower-severity notes (not in the top 8)

- **AD-4 doesn't limit which keys a profile can override:** R17 limits overrides to the Track Layout and Output Settings, and R18 adds the Mic Devices and mute Tracks. Suggest listing the overridable keys, and marking Hotkeys, the Output Folder and Buffer mode as Global-only.
- **AD-4 bundles the Mic Devices and mute Tracks into the Track Layout key:** this fits R18, since mute Tracks reference Track GUIDs. The consequence is unstated: a profile that overrides only its mic setup freezes a copy of the Global layout. Suggest stating it.
- **No rule for adding Hotkey actions:** AD-11 says where Hotkeys are registered, but not how epics 2, 4 and 6 add their actions. Suggest a Contracts action enum and a single `hotkeys` settings key.
- **The frame pacer isn't stated as an invariant:** PLAN.md §0 decision 6 calls for a constant frame rate. It's named in the Media layer but not in any AD. This is mostly epic 1's business, but it bears on R33 drift for every Sink.
- **PLAN.md §7.1 leftovers:** pausing on the secure desktop or lock screen, and the T-04 behaviour, are not mentioned. These are "consider" items, acceptable as epic detail.

## Checked and consistent (no action)

- **AD-9 Clips as faststart MP4 vs PLAN.md's fMP4-everywhere:** a mechanism change made for the R33 behaviour, flagged as an ASSUMPTION. This is acceptable under PRD precedence; only its ownership is open (F7).
- **Precedence overrides respected:** one Save Hotkey and no Post-roll (AD-10). No master mix or gain; Track 1 is marked default (AD-9, AD-13). Mute affects only the chosen Tracks (AD-13). Desktop Duplication is used for monitors only (AD-12). Game Detection replaces the allow-list (AD-18).
- **Covered:** RegisterHotKey in the engine with no LL hook (AD-11). Engine ownership of settings, with the override shape (AD-4). Endpoint-ID binding and IMMNotificationClient for R35 mixer restarts (AD-13). `WDA_EXCLUDEFROMCAPTURE` on every window (AD-15, PLAN.md §7.1). Interop Path A with Path C as the fallback behind ports (AD-14, PLAN.md §6). The Memory-mode RAM margin (AD-8, R3). Fragment flushing at 1 s or less (AD-9, R11). Personal, GPL FFmpeg and no installer (Stack, Deployment). Multiple GPUs, HDR and an installer deferred.
