# Rubric review: architecture-replay-buffer (initiative altitude)

Reviewer: rubric walker (good-spine checklist, `references/reviewer-gate.md`)
Spine: `_bmad-output/initiative-replay-buffer/architecture-replay-buffer/architecture-replay-buffer.md` (status: draft, Fast path)
Inputs read: the initiative ticket (R1 to R36, Constraints, Notes), all 8 epic tickets, the spine memlog, the PRD addendum, and PLAN.md.
Stakes: greenfield, hobby, single user. Findings are scaled to that.

## Verdict

The spine is solid and close to ready. It names a clear paradigm, the dependency graph is enforceable, and every requirement R1 to R36 is bound by at least one AD. But it fixes the App-to-engine seam much more tightly than the seams **inside** the engine, and that is where most of the 8 epics meet. Two high gaps need fixing before epics 2 to 8 are incepted: there is no in-engine coordination and state-mutation model, and there is no shared event shape for notices and capture state that epic 5 can display. Five medium gaps are also worth fixing: the operational envelope (environments) is silent, and one Deferred item is wrong.

## Checklist walk

| Checklist item | Result |
| --- | --- |
| Fixes the real divergence points for the level below, and misses none | **Partial.** The App/engine split, IPC, settings, clock, ring, containers, Hotkeys, capture, audio and supervision are all fixed. Missed: the in-engine coordination model (F1), the shared notice and state event shapes (F2), the in-progress file naming (F7), settings schema evolution (F6), and AOT safety (F9). |
| Every AD Rule is enforceable and prevents its stated divergence | **Mostly.** AD-2, AD-3, AD-11, AD-12 and AD-14 are crisp. AD-7's "communicate only through the bus" contradicts its own pipeline, and its "settings snapshots are equal" test is too coarse (F8). AD-1's "touches media files directly" is ambiguous for Library actions (F12). AD-16 has a supervision hole (F5). |
| Nothing under Deferred could let two units diverge | **No.** The GPU scheduling priority item says "no other unit sees the choice", which is false if elevation is chosen (F3). "Full IPC message catalog: each epic adds its own" lets producers and the epic 5 consumer diverge (F2). |
| Named tech is verified-current | **Yes, with one nit.** .NET 10.0.13 matches the October 2026 servicing release. FFmpeg.AutoGen 9.0.x exists, but the exact package id and version for 9.0.1.1 should be confirmed (F14). |
| Ratifies a brownfield codebase | N/A (greenfield). |
| Covers every requirement in the driving ticket | **Yes.** The union of the AD Binds covers R1 to R36, and the Capability Map routes all 36. Some coverage depends on the missing in-engine mechanism (R1, R24, R8 and R29 handoffs; see F1). |
| Parent spine inherited without weakening | N/A (no parent). |
| Every dimension the altitude owns is decided, deferred or an open question | **Partial.** Deployment gets one sentence. Environments (a dev/test build next to the daily-driver copy), install location, updating a running engine, FFmpeg DLL acquisition and crash diagnostics are silent (F4). The process privilege level is silent (F3). |

## Findings

### F1 [high] Invariants & Rules: no in-engine coordination or state-mutation model

The spine fixes how media flows (AD-7) and how the App talks to the engine (AD-3). It doesn't fix how the engine's domain components talk to each other, or which thread may mutate domain state. Yet most epic handoffs are in-engine:
- epic 3's Target Game appeared and exited events go to epic 4's Replay Buffer lifecycle (R1);
- epic 6's "Recording running" flag is read by epic 3's Target Game rule (R24);
- the "file being written" signal goes from epics 4 and 6 to epic 7's Library and Cleanup (R28, R29);
- save, failure and disk-full outcomes go from epics 4 and 6 to epic 5's engine-side Feedback Sounds (R8);
- device dropout warnings come from epic 2 (R19).

Inputs arrive on at least five threads: the Hotkey message-only window, the WASAPI MMCSS threads, the WGC frame thread, the IPC server and the GameMonitor timer. Each epic will otherwise pick its own mechanism (C# events, polling a property, Rx, direct calls) and its own locking. That produces races such as a save on the Hotkey thread racing a Target Game switch Reset. The skill names "how state is mutated" as a core spine invariant, and none is stated here.

**Fix:** add an AD for engine orchestration:
- All domain state (sessions, Target Game, Recording running, the being-written set, mute) is mutated only on one serialized engine loop in `Core`, for example a single-reader `Channel<EngineCommand>`.
- Hotkeys, IPC commands, GameMonitor, writers and device notifications post messages to that loop and never mutate state directly.
- In-engine signals are published on one in-process event hub defined in `Core`, and IPC events are projected from that hub.
- Hot-path threads (audio, frames) never block on the loop.

### F2 [high] AD-3 and Deferred "full IPC catalog": shared notice and state shapes left to each epic

Epic 5 builds the status line, the Overlay message queue and the Feedback Sounds. It must display warnings from epic 2 (device dropouts) and epic 8 (engine restarts), and Recording state, red X and disk-full from epic 6. But epics 6 and 8 are built **after** epic 5. Under "each epic adds its own messages under AD-3", every producer invents its own event type and payload, and epic 5 can't build a generic consumer. The ticket explicitly flags this seam ("Epics 2 and 8 emit warnings that epic 5's status line displays"). The Errors convention says user-facing failures "go out as events", but gives no shape. Separately, AD-3's `state.snapshot` doesn't require each epic to add its rendered state to the snapshot. Without that, an App reconnect after an App crash shows a wrong dot, mute icon or red X (R6, R13, R18).

**Fix:** in AD-3, fix two shared event shapes in `Contracts` and move them out of Deferred:
- `capture.state`: buffer state, recording state, monitor recording state, mic muted, disk-full flag.
- `notice.raised`: `{ code, severity, message, at, source, sticky, overlay: bool, sound: <soundId|null> }`.

Every producer emits these rather than its own types. Add a rule: every engine state the App renders is part of `state.snapshot`, and events are deltas to it.

### F3 [medium] Deferred "GPU scheduling priority": the claim "no other unit sees the choice" is false

PLAN.md §7.2 notes that `D3DKMTSetProcessSchedulingPriorityClass` needs elevation. If epic 1 chooses elevation, three things break:
- AD-16's HKCU `Run` startup (an elevated process can't start silently at logon, which breaks R31 and R35);
- the named pipe between an elevated engine and an unelevated App (AD-3, pipe DACL and integrity levels);
- possibly App-to-engine supervision (AD-16; the App can't restart an elevated engine without UAC).

The process privilege level is otherwise silent in the spine.

**Fix:** decide it in AD-16. Both processes run unelevated at medium integrity, and GPU priority uses only unprivileged APIs (`IDXGIDevice::SetGPUThreadPriority`). Alternatively, rewrite the Deferred line to say an elevation choice reopens AD-3 and AD-16 and needs a spine update.

### F4 [medium] Structural Seed "Deployment": environments and operations are silent

One sentence covers deployment. The operational envelope the altitude owns is missing:
- **Environments:** the builder daily-drives a build (the month of use, initiative Done when) while developing and running integration tests. A dev or test engine collides with the daily copy on the pipe name `ReplayBuffer.Engine.v1`, the named mutexes, `RegisterHotKey` combinations (registration fails, which trips R32), and the `%APPDATA%` and `%LOCALAPPDATA%` data.
- **Install location and update procedure:** HKCU `Run` needs a stable exe path. Under AD-16 the engine outlives the App, so it holds file locks during a redeploy.
- **FFmpeg DLL acquisition:** `native/ffmpeg/` is "not committed" and there's no fetch step or pinned source.
- **Crash diagnostics** for a NativeAOT engine: dumps or a crash log location.

**Fix:** add an environments rule. An instance name (for example `REPLAYBUFFER_INSTANCE`, default empty) suffixes the pipe name, the mutex names and the data folders. Tests run under an isolated instance and don't register Hotkeys, or they use test combinations. The daily copy lives at one fixed path (for example `%LOCALAPPDATA%\Programs\ReplayBuffer`). An update is: tray Quit (stops both processes), copy, relaunch. FFmpeg DLLs are fetched by a script pinned to the Stack version. Or list each of these explicitly under Deferred with an owner (epic 1 or epic 8).

### F5 [medium] AD-16: tray Quit semantics and App-crash supervision

AD-16 says the engine survives the App exiting, with no kill-on-close. So:
- R31's tray **quit** must explicitly stop the engine, or every Quit orphans an engine. This is exactly the divergence AD-16 says it prevents, and no `engine.shutdown` command is named.
- If the App process crashes, no one supervises the engine and no one relaunches the App (Overlay, tray). A later engine crash then isn't restarted, contrary to R34's "A crashed engine restarts by itself". Epic 1 (R31) and epic 8 (R34) could each patch this differently.

**Fix:** extend AD-16:
- Tray Quit sends `engine.shutdown`, then the App exits.
- Closing the Main App window only hides it; the App process stays in the tray.
- The engine relaunches the App if the App process dies unexpectedly, guarded by the App's mutex. This gives mutual supervision.

### F6 [medium] AD-4: no settings schema-evolution rule, and override key granularity is unstated for Output Settings

Every epic adds settings keys: Track Layouts (2), profile fields (3), the buffer and Hotkeys (4), sound toggles (5), Recordings (6), Cleanup (7). The spine has `schemaVersion` but no rule for missing or new keys. A new required property, or a deserializer that rejects an older file, breaks R35 ("settings survive app restarts"). AD-4 also says "overrides holds only the keys the user changed" and fixes Track Layout as one whole key. It doesn't say whether Output Settings is one key or per field (resolution, fps, bitrate, codec). Epic 1 builds the shape and epic 4 builds the R17 UI, so they could pick different granularity.

**Fix:** add these rules to AD-4:
- Every key has a code default, and a missing key takes it.
- New keys never bump `schemaVersion`. Only a rename or removal does, with a migration step in the settings store.
- Override keys are the leaf fields of `global` (dotted paths such as `output.fps`), except `trackLayout`, which is whole.

### F7 [medium] Conventions: in-progress file naming between epics 4, 6 and 7, and the open Clip-spanning-generations question

R7 says a failed save leaves "no file that only looks playable". AD-9's faststart Clips need a finishing pass. Epic 7's watcher (R27, new Clips within 2 s) and Cleanup (R29) see whatever lands in the Output Folder. Nothing says whether Clips are written under a temp name and renamed, or under the final `.mp4` name and tracked only in the being-written set. Epic 4 might write in place, while epic 7 lists or thumbnails a half-written file. Separately, the addendum's Precedence leaves an architecture question open: whether a Clip that spans an encoder restart can stay one file, or becomes per-generation parts. That affects epic 3's path resolution, epic 4's writer and epic 7's listing. The spine neither decides nor defers it.

**Fix:**
- Add a convention: Clips are written as `<name>.mp4.partial` and renamed on success, or deleted on failure. Recordings and Monitor Recordings use the final name and are in the being-written set. The Library and Cleanup consider only `*.mp4` files that aren't in that set.
- Decide the spanning-Clip case (for example `<Game>_<ts>_part2.mp4`) or name it under Deferred with epic 4 as owner.

### F8 [medium] AD-7: Rule wording doesn't match its intent

"Filters communicate only through the bus" contradicts the stated flow: Source to Processor to Encoder happens before any encoded-packet bus exists. "Sinks share one encoder chain only while their settings snapshots are equal" compares the whole `EffectiveSettings`. That includes keys that don't affect encoding (Buffer Window, storage mode, Cleanup). A Recording would then spawn a needless second hardware encode, which costs FPS (R36) and NVENC sessions, as soon as any unrelated setting changes at a Reset.

**Fix:**
- Reword the first part: Sinks consume only encoded packets from the bus; Sources, Processors and Encoders are wired only by the pipeline builder.
- Define equality on the encoding-relevant subset: capture source, Output Settings, Track Layout.

### F9 [medium] Missing rule: AOT and trim safety for engine-side code

NativeAOT is the target (Stack, Deployment), and the memlog flags it as unproven. Later epics could pull in reflection-heavy or non-AOT libraries for the engine, for example WMI through `System.Management` for process detection in epic 3, or reflection-based config binding. They would only find out at publish time, possibly after the tracer has proven AOT.

**Fix:** add to AD-2 or the Conventions: engine-side projects (`Contracts`, `Core`, `Media`, `Platform.*`, `Engine`) set `IsAotCompatible=true`, and AOT and trim warnings are errors. This holds even if the engine falls back to framework-dependent, so the fallback stays reversible.

### F10 [medium] Distillation: `[ASSUMPTION]` tags dropped

The memlog tags AD-3, AD-4, AD-15, AD-16, AD-17 and AD-18 as `[ASSUMPTION]`. The spine keeps the tag only on AD-9, AD-10 and AD-14. On the Fast path the tags are how the user knows what still needs correcting. Without them, untested calls read as settled.

**Fix:** restore `[ASSUMPTION]` on AD-3, AD-4 and AD-15 to AD-18, or record the user's confirmation in the memlog and then mark them `[ADOPTED]`.

### F11 [low] Deferred: installer and CI attributed to the initiative Boundaries

The Boundaries name "distribution", not an installer or CI. The deferral is fine, but cite it as a spine call (or "Constraint: never distributed"), not as the Boundaries.

### F12 [low] AD-1 / AD-17: "touches media files directly" is ambiguous for Library actions

R28 needs open in the Media Player, show in Explorer, copy to the clipboard, and thumbnails (R27). Read literally, AD-1 forces all of these through the engine. Epic 7 could read it either way.

**Fix:** the App may hand engine-supplied paths to the Shell (open, select in Explorer, the clipboard `CF_HDROP`, and Shell thumbnails). Every mutation (rename, delete, Favourite) goes through IPC.

### F13 [low] AD-3: pipe access control

"Per-user" isn't a DACL. The default named-pipe security grants read access beyond the owner. Even at hobby stakes it's a one-line rule: create the pipe with a DACL for the current user SID only, and reject remote clients.

### F14 [low] Stack: FFmpeg.AutoGen package id

Search results show `FFmpeg.AutoGen` 9.0.1 (with a newer version noted) and `FFmpeg.AutoGen.Bindings.DynamicallyLoaded` 9.0.1.1. Confirm which package ids are pinned. The split Abstractions plus DynamicallyLoaded packages are the AOT-friendly choice. Pin the exact pair.

### F15 [low] Deferred: items that epics will hit

These are single-epic calls but currently unnamed. Add them under Deferred with owners:
- R12's continue-the-file versus start-a-new-Recording on a forced encoder restart (epic 6);
- PRD Q-4's possible effect on AD-15, such as showing the Overlay only while it has something to show (epic 5);
- the IPC meter event rate for R30 (epic 2);
- session-lock and secure-desktop behaviour (PLAN.md §7.1 and T-04, epic 8).

## Requirement coverage (R1 to R36)

All 36 requirements appear in at least one AD's Binds and in the Capability Map. There are no orphans. Coverage that relies on the missing in-engine mechanism (F1): R1 (the Target Game event drives the buffer lifecycle), R24 (the Recording-running flag), R28 and R29 (the being-written set), R8 (engine-side sounds triggered by other epics), and R34 (resume after restart, F5).

## Divergence-point coverage per epic seam

| Seam (from the tickets) | Fixed by | Status |
| --- | --- | --- |
| Settings store and override shape (1 → 2, 3, 4) | AD-4, AD-5 | Fixed; evolution and granularity gaps (F6) |
| Capture-session API (1 → 2, 3, 4, 6) | AD-5, AD-7 | Partly; the session ownership and threading model is missing (F1) |
| Hotkey registration (1 → 2, 4, 6) | AD-11 | Fixed; dispatch into the domain is unspecified (F1) |
| Target Game events (3 → 4) and the Recording-running flag (6 → 3) | AD-18 | Owner fixed; mechanism missing (F1) |
| "File being written" signal (4, 6 → 7) | AD-17 | Owner fixed; registration mechanism (F1) and file naming (F7) missing |
| Warnings and state to the status line and Overlay (2, 4, 6, 8 → 5) | AD-3, AD-15 | Not fixed (F2) |
| Encoder and muxer reuse (1 → 4, 6) | AD-7, AD-9 | Fixed; equality test too coarse (F8) |
| Benchmark harness (1 → 5, 8) | Conventions (Bench) | Fixed |
| Supervision and restart (1 → 8) | AD-16 | Partly (F5, F3) |

Sources for the version checks: [.NET October 2026 servicing updates](https://devblogs.microsoft.com/dotnet/dotnet-and-dotnet-framework-october-2026-servicing-updates/), [FFmpeg.AutoGen on NuGet](https://www.nuget.org/packages/FFmpeg.AutoGen/9.0.1), [FFmpeg.AutoGen.Bindings.DynamicallyLoaded](https://www.nuget.org/packages/FFmpeg.AutoGen.Bindings.DynamicallyLoaded).
