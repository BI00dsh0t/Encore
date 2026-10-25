---
lens: adversary
target: _bmad-output/initiative-replay-buffer/architecture-replay-buffer/architecture-replay-buffer.md
against: tickets/tickets.toml, tickets/epic-*/epic-*.md (8 epics), initiative-replay-buffer.md (R1–R36)
date: '2026-10-25'
verdict: 'Not buildable as a spine: eight epics can each obey all 18 ADs to the letter and still ship an engine where the capture session has two owners, settings updates silently overwrite each other, and a crash can either unmute the mic or get Favourites deleted.'
counts: { critical: 5, high: 13, medium: 15, low: 7 }
---

# Adversarial review: Replay Buffer architecture spine

## Method

For each finding I build two (sometimes three) epics, each written by a careful team that reads every AD literally and breaks none of them, and show that the two still don't fit together. Every finding is a hole in the spine, not a bug in an epic. A pair that only conflicts because an epic ignored an AD doesn't count. Each finding ends with the exact rule that would have made the clash impossible, as a new AD or a tightened existing one.

The spine is strong on *placement*: which process, which project, which API. It is almost silent on *mutation*: who may change shared state, in what order, on which thread, and what the payload looks like. The ADs say "the engine owns X" and stop there. The engine is about six concurrent subsystems on at least five threads (hotkey message loop, GameMonitor, WASAPI MMCSS threads, WGC frame callbacks, IPC server, file watcher). "The engine owns it" doesn't name an owner. Most of the holes below come from that.

Severity scale:
- **critical**: the epics integrate and ship but lose data, leak audio, or capture the wrong thing.
- **high**: the epics won't integrate without one of them being rewritten.
- **medium**: integration works, but with a user-visible defect or an epic-boundary rewrite.
- **low**: rough edges.

---

## Critical

### C1. Two owners of the capture session's target: epic 3 vs epic 4 (and 6)

- **Epic 3, obeying AD-18:** GameMonitor decides the Target Game and publishes `target.changed`. Its own Done-when says "the capture session is pointed at the new Target Game." Epic 1 hands over a capture-session API, so epic 3 calls `session.SetSource(newWindow)` on the running session. That's legal: R20 explicitly requires window changes with no break, so the API supports retargeting in place.
- **Epic 4, obeying AD-5:** a session reads one immutable `EffectiveSettings` snapshot when it starts. Epic 4 starts the Replay Buffer on the "appeared" event and stops it 30 s after "exited". R24 says a switch Resets the buffer.
- **Clash:** epic 3 has already moved the running session to game B. Epic 4 never sees a session start, because the session never ended. The session now captures game B under game A's snapshot: A's Track Layout, A's Output Settings, A's folder. Alternatively both epics react to `target.changed`, and you get two sessions, two rings and two encoder chains, both "obeying" AD-7. Neither AD-5 nor AD-18 says *who starts, stops or retargets a session*.
- **Proposed AD-19 (Session orchestration has one owner):**
  > `ReplayBuffer.Core.Sessions.SessionOrchestrator` is the only component that creates, Resets, retargets or ends a capture session (Replay Buffer, Recording, Monitor Recording). GameMonitor, Hotkeys, the settings store and the file writers only post messages to it. Changing the Target Game is always end-session plus new-session, with a new snapshot. A capture source may be retargeted in place only when the window *of the same process* changes (R20 resolution or window-mode changes). Epic 3 delivers events only, and the orchestrator that acts on them is epic 4's.

### C2. Target Game switch racing Recording start: epic 3 vs epic 6

- **Epic 3, obeying AD-18:** "GameMonitor *alone* decides … the Target Game." To honour R24's "unless a Recording is running", it reads a `bool IsRecording` flag (stubbed now, real in epic 6) on its own focus-timer thread.
- **Epic 6:** the Record Hotkey arrives on AD-11's hotkey thread. Epic 6 checks `recordingsEnabled`, builds a Recording session on the current Target Game, and sets the flag.
- **Clash:** game B's 10 s focus timer fires on the GameMonitor thread in the same millisecond as the Record press. GameMonitor reads `IsRecording == false`, switches to B and Resets. Epic 6 has already resolved the Recording's source as A's window and A's folder. Result: a Recording of game A that the R24 rule should have pinned, but the Target is B, so A's exit won't stop it (R10) and B's exit will. Or the Recording starts on B with A's profile. AD-18's "alone decides" actually *forces* the cross-thread read that causes this.
- Two further gaps the epics will fill differently:
  - When the Recording stops, does B (focused for the last 40 s) become Target immediately, or only after a fresh 10 s?
  - Does a Monitor Recording block the switch?
- **Proposed tightening of AD-18, plus AD-19:**
  > GameMonitor *proposes* (`TargetCandidate{profileId, focusedSinceQpc}`). The SessionOrchestrator, a single-threaded actor (one queue, one thread), *commits* the switch and emits `target.changed`. It handles hotkey presses, focus proposals, `game.exited`, settings revisions and writer completions strictly in arrival order. "A Recording is running" means a game Recording on the current Target. A Monitor Recording never blocks a switch. When a blocking Recording stops, the orchestrator re-evaluates immediately: if another detected game has `focusedSinceQpc` at least 10 s old, it switches with no new 10 s wait.

### C3. Lost settings updates: epics 2 and 4 (App writes) vs epic 3 (engine writes)

- **Epic 2 / epic 4 Settings UI, obeying AD-4:** the App never writes settings and sends `settings.update`. AD-4 doesn't define the payload, so the natural MVVM choice is to send the edited `global` object, or the whole document the App got from `state.snapshot` / `settings.changed`.
- **Epic 3, obeying AD-4:** the engine is the sole writer, so on first detection it auto-creates `profiles["c:\\games\\x\\x.exe"]` inside the engine and broadcasts `settings.changed`.
- **Clash:** the user has had the Settings page open for 2 minutes. A game launches and its profile is auto-created. The user clicks Save, and the App's whole-document (or whole-`profiles`) update wipes the new profile, or reverts a rename made in another view. Every step obeyed AD-4.
- If epic 4 instead sends a JSON merge patch (RFC 7396), it violates AD-4's "Track Layout … never merged Track by Track" without noticing: merge patch merges the `trackLayout` object field by field and replaces its arrays.
- **Proposed tightening of AD-4 (Update shape and concurrency):**
  > The settings document carries a monotonically increasing `revision`.
  >
  > `settings.update` payload: `{ expectedRevision, ops: [ { scope: "global" | "profile", profileId?, key, op: "set" | "remove", value? } ] }`.
  > - `key` is a member of the closed key catalog in `ReplayBuffer.Contracts.Settings.Keys`, and `value` replaces that key whole. There is no deeper patching.
  > - Profile attributes (`name`, `ignored`, `recordingsEnabled`) are keys with `scope: "profile"`.
  > - A mismatched `expectedRevision` is refused with `settings-conflict`, and the App re-reads.
  >
  > Writes made by the engine itself (profile auto-creation, see C4 for mute) go through the same single-writer queue and bump `revision`.
  >
  > `settings.changed` carries `{ revision, document }`.
  >
  > Commit order: validate → write temp → replace → swap the in-memory document → broadcast. A failed disk write returns an error and leaves memory unchanged.

### C4. Mic mute: a setting or runtime state? Epic 2 vs epic 4 vs epic 8

- **Epic 2:** R35 says "settings … survive reboots", and AD-4 makes the engine the sole writer of settings. So epic 2 stores the mute toggle as `global.micMuted` through the settings store, which gives `settings.changed` and persistence for free.
- **Epic 4, obeying AD-5:** each session snapshots `EffectiveSettings` at start. `micMuted` is in the settings document, so it's in the snapshot. Epic 4 reads AD-5's "mic mute is the only state that changes" as "mute is special-cased *somewhere*", but its snapshot type is generated from the DTO and includes the field. Pressing Mute changes the file, and the running session ignores it until the next Reset. The mic stays hot in the Clip.
- **Or epic 2 goes the other way** and keeps mute as an in-memory bit. Then epic 8, obeying AD-16, kills the engine and restarts it: "the Replay Buffer resumes"… unmuted. The user muted the mic, the engine crashed silently, and their voice is in every Clip from then on.
- **And with two concurrent sessions** (Replay Buffer on profile layout P, Monitor Recording on the Global layout per R21), is there one mute bit or one per session? Mixers in separate encoder chains (AD-7) also apply a toggle at whatever buffer each happens to be processing, so the Clip and the Recording disagree on where the mute begins.
- **Proposed tightening of AD-5 and AD-13:**
  > Mute is runtime state, not a setting. It is one engine-wide bit, owned by `MuteController` in Core and never present in `settings.json` or `EffectiveSettings`.
  > - It is persisted to `%LOCALAPPDATA%\ReplayBuffer\runtime.json` (written atomically on every change), restored before any session starts, and included in `state.snapshot`. The default after a corrupt or missing file is **muted**: fail closed.
  > - A toggle is stamped with the hotkey's QPC tick (see H3b). Every mixer in every chain applies it to the first sample with QPC ≥ that tick.
  > - Each session applies the bit to *its own snapshot's* Mic Devices and mute Tracks.
  > - Event: `mic.muteChanged { muted, atQpc }`.

### C5. Favourites lost on a crash, then deleted by Cleanup: epic 7 vs epic 8

- **Epic 7, obeying AD-17:** the engine owns `library.json` (Favourites keyed by relative path), and Cleanup "never deletes Favourites" by checking that file. AD-17, unlike AD-4, says nothing about atomic writes, so epic 7 does `File.WriteAllText` on every change.
- **Epic 8, obeying AD-16:** kills the engine at random points (T-01 to T-25), including mid-write of `library.json`. The restarted engine finds a truncated or empty `library.json`, treats it as "no Favourites", and runs Cleanup over the size limit. R29 makes that deletion **permanent**. The user's Favourites are gone, and every AD was obeyed.
- **Proposed tightening of AD-17:**
  > `library.json` is written atomically (temp file plus replace) with a rotated `library.json.bak`.
  > - If both fail to parse at startup, the engine raises `warning.raised{code:"library-corrupt"}` and **Cleanup stays disabled until the user acknowledges it**. Cleanup fails closed.
  > - Cleanup never runs in the first 60 s after engine start, or before the first full Output Folder scan is reconciled with the index.

---

## High

### H1. Two owners of each audio device: epic 2 meters vs epic 4/6 sessions

- **Epic 2:** R30 needs meters "with nothing being captured", for "the layout being viewed", which may be an unsaved draft in the Settings page. So epic 2 opens WASAPI clients for those devices outside any session. It obeys AD-13 (event-driven, MMCSS, rebinding through `IMMNotificationClient`).
- **Epic 4:** a session's audio Source, built under AD-7 as Source → Processor, opens its own WASAPI clients for the snapshot's devices, also obeying AD-13.
- **Clash:** each device ends up with two capture clients, two sets of rebinding logic and two silence-gap fillers, so dropout handling (R19) runs twice, possibly with different outcomes. The meter shows a level the Clip doesn't contain. A Monitor Recording (R21) adds a third set.
- AD-7's "filters communicate only through the bus" names the *encoded*-packet bus, so there's no sanctioned way for a meter to tap raw PCM from the session graph.
- **Proposed AD-20 (One capture client per Audio Device):**
  > `AudioDeviceHub` (Platform.Audio, behind a Core port) holds at most one WASAPI client per endpoint for the whole engine. It does the binding, rebinding, gap filling, 48 kHz float conversion and QPC stamping. It publishes raw buffers on a *PCM fan-out*, the only sanctioned pre-encoder tap. Session mixers and meter mixers subscribe to it and never open devices.
  >
  > Meters for a draft layout: `meters.watch { layout }` builds a meter-only mixer, and `meters.unwatch` releases it. The engine keeps a device open only while it has at least one subscriber.

### H2. Device references and Mic Devices drift apart on rebind: epic 2 vs epic 4 vs epic 8

AD-4 bundles "Tracks, Mic Devices, mute Tracks" into one key. AD-13 binds by endpoint ID with a friendly-name fallback. Neither defines what a device reference *is*.

- **Epic 2:** `tracks[].devices[]` holds `{endpointId, friendlyName}` objects.
- **Epic 4:** per-profile `micDevices: ["{endpointId}"]` holds bare strings.
- **Epic 8:** the BEACN restart re-enumerates the virtual devices with **new endpoint IDs**. Track sources rebind by friendly name, as AD-13 says. The `micDevices` strings match nothing, so mute silences nothing, and the mic is live in what the user believes is a muted recording.
- Side question: does rebinding write the new ID back to settings? If it does, that's an engine settings write mid-session.
- **Proposed tightening of AD-4 (Track Layout shape):**
  > `trackLayout = { devices: [ { key: <guid>, endpointId, friendlyName, parentName, flow: "output" | "input", isMic } ], tracks: [ { id: <guid>, name, deviceKeys: [<guid>], muteAffected: bool } ] }`.
  > - Tracks and the mic flag refer to devices only through the layout-local `key`.
  > - Rebinding resolves `key` → live endpoint at runtime. The resolution is not written back to settings and never triggers `settings.changed`.
  > - `isMic` and `muteAffected` replace the separate "Mic Devices" and "mute Tracks" lists.

### H3a. Hotkey bindings: three schemas and two apply rules (epic 2 vs epic 4 vs epic 6)

AD-11 says where Hotkeys are registered, and nothing about how a binding is stored or when a change takes effect.

- **Epic 2:** writes `global.micMuteHotkey: "Ctrl+Alt+M"`.
- **Epic 4:** for R32 rebinding, writes `global.hotkeys.save: { vk: 0x53, mods: ["ctrl","alt"] }`.
- **Epic 6:** adds `recordMode: "toggle" | "split"` with a third shape.
- **Apply rule:** Hotkeys sit in the settings document, and AD-5 says changes apply "at the next Reset". So epic 4 re-registers at Reset, while epic 2 re-registers on `settings.changed`.
- **Duplicates:** R32's duplicate check needs every binding in one place. Instead it's spread across three keys in two shapes.
- **Per-profile overrides:** AD-4 doesn't forbid a per-profile `hotkeys` override, which would make RegisterHotKey churn on every target switch.
- **Proposed tightening of AD-11:**
  > `global.hotkeys: { [HotkeyAction]: { vk, modifiers: ["ctrl" | "alt" | "shift" | "win"] } | null }`, plus `global.recordHotkeyMode`.
  > - `HotkeyAction` is a closed enum in Contracts: `save`, `micMute`, `recordToggle`, `recordStart`, `recordStop`, `monitorRecordToggle`.
  > - Hotkeys are global-only, never in `EffectiveSettings`, and applied as soon as `settings.update` commits.
  > - Duplicates are refused at validation with `hotkey-duplicate`.
  > - The registration outcome is published as `hotkey.status { action, registered, error? }` and kept in `state.snapshot`.

### H3b. The press instant isn't defined: epic 1 vs epic 4

- **Epic 1:** its Hotkey API raises `HotkeyPressed(action)` through a .NET event or channel to subscribers.
- **Epic 4, obeying AD-10:** "marks the press frame" as the frame current when its handler runs. That's after a thread hop, a queue, and possibly a GC, typically 5 to 40 ms later.
- **Clash:** R4's "within one frame of the press" needs ≤8 ms at 120 fps. Under AD-6, everything is QPC except the one event that defines the cut.
- **Proposed tightening of AD-6 and AD-10:**
  > The hotkey thread stamps `QueryPerformanceCounter` as its first action on receiving `WM_HOTKEY`. The port signature is `HotkeyPressed(HotkeyAction, long qpc)`. The Clip's last video frame is the last frame with capture QPC ≤ `qpc`, and the audio cut is at `qpc`. The Save Cooldown is measured from `qpc`.

### H4. Clock stamping and drift correction are unassigned: epic 1 vs epic 2

AD-6 says "stamped in QPC ticks when captured", but it doesn't say *which* stamp.

- **Epic 1's benchmark** (two devices on independent clocks) reads QPC in the WASAPI callback.
- **Epic 2** uses `IAudioCaptureClient::GetBuffer`'s `pu64QPCPosition`, which differs by the buffer period plus jitter.
- **Video:** one epic uses WGC's `SystemRelativeTime` and another QPC at frame arrival.

Neither AD says anything about **correcting device-clock drift against QPC**: a 48 kHz device running at 48 003 Hz drifts by a frame in a few minutes. AD-13 says "everything is processed internally as 48 kHz float", which says nothing about rate correction. So epic 1's benchmark validates one method, epic 2 ships another, and epic 8's 60-minute conformance run is the first time anyone finds out.

- **Proposed tightening of AD-6 and AD-13:**
  > - The audio stamp is WASAPI's `pu64QPCPosition` (100 ns units, converted to QPC ticks) of the packet's first frame.
  > - The video stamp is `Direct3D11CaptureFrame.SystemRelativeTime` converted to QPC ticks.
  > - Each device stream passes through exactly one adaptive resampler in `ReplayBuffer.Media` (owned by epic 1, reused by epic 2), steered so its sample count tracks elapsed QPC. Mixers align sources by QPC, not by sample count.

### H5. "File being written" has three incompatible meanings: epic 4 vs epic 6 vs epic 7

AD-17 says the engine holds "the in-memory set of files being written".

- **Epic 4:** R7 requires that a failed save leave "no file that only looks playable", so it writes `X.mp4.tmp` and renames it at the end. It registers the *final* path in the set when the write finishes, which is too late. Or it registers at the start under the final name while the bytes are in `.tmp`.
- **Epic 6:** R11 needs crash-safe fMP4, so it writes in place to `X.mp4` and registers that.
- **Epic 7's watcher (AD-17):**
  - It sees `X.mp4.tmp` appear. That path isn't in the set, so the watcher indexes it as a Library item and Cleanup may delete it as "oldest non-Favourite" when it's an old orphan.
  - It sees the rename to `X.mp4` *before* epic 4 removes the entry, so the item shows "locked" forever. Or it sees it *after*, and the item flickers unlocked, then locked.
- **Proposed tightening of AD-17 (Writer protocol):**
  > Every writer calls `Library.BeginWrite(finalRelPath, tempRelPath?)` **before creating any file**, and `EndWrite(finalRelPath, outcome)` after the final rename or flush.
  > - Temporary files are `<final>.rbpart` in the same folder. The Library and Cleanup ignore `*.rbpart` always.
  > - At engine start, `.rbpart` files are deleted. Crash-surviving fMP4 Recordings are not temporary and get indexed normally.
  > - Events: `file.writing { path }`, then `library.itemAdded` / `library.itemChanged`, then `file.written { path, outcome }`, in that order, on the same event sequence (see H11).

### H6. Lifecycle events don't define the transitions: epic 3 vs epic 4

AD-18 names three events, while epic 3 promises "Target Game appeared and exited" events and epic 4 consumes those. Neither defines the sequences:

- Game A is the Target and game B is detected. A exits. Is `target.changed(B)` emitted immediately, or after B's 10 s of focus?
- Epic 4 runs A's 30 s post-exit drain (R1). If B becomes Target at second 3 of that drain, are there two Replay Buffer sessions and two rings? Which one does the Save Hotkey save from?
- Is `game.exited` for a *non-Target* game a lifecycle signal at all? Epic 4 listening to `game.exited` stops the buffer 30 s after an unrelated game closes.
- **Proposed tightening of AD-18:**
  > Epic 4 consumes only `target.changed { from: profileId | null, to: profileId | null, reason: "focus" | "exit" | "ignored" | "detected" }`. `game.detected` and `game.exited` are informational.
  > - When the Target exits with other games detected, `to` = the most recently focused detected game, chosen immediately, with no 10 s wait.
  > - The 30 s drain exists only when `to` is null. A new `target.changed` during a drain ends the drain at once, and the drained ring is released after any pinned save completes.
  > - There is at most one Replay Buffer session.

### H7. Feedback Sounds recorded into every Clip: epic 5 vs epic 2

- **Epic 5, obeying AD-15:** Feedback Sounds are played by the engine on the default render endpoint.
- **Epic 2, obeying AD-13:** Output Devices are captured by *endpoint loopback*, and the "Everything" Track includes the default output.
- **Clash:** the "Clip saved" chime plays about 0.5 to 2 s after the press, after the write finishes. That's inside the next Clip, which started at the press frame (AD-10). Every consecutive Clip opens with the previous save's chime, and the save-failed and disk-full sounds land in Recordings too. Both epics obey their ADs exactly.
- **Proposed tightening of AD-15:**
  > Feedback Sounds render to `global.feedbackSoundDevice`, which defaults to the default communications endpoint. Settings validation warns (`feedback-device-captured`) when that endpoint is loopback-captured by any Track in Global or any profile. The engine never plays sounds through a stream that a Track captures without that warning having been shown.

### H8. A Clip spanning an encoder generation: epic 4 vs epic 6

- **Epic 6:** handles forced encoder restarts (R12). Per AD-8, the restart opens a new ring generation with new SPS/PPS, possibly a new resolution.
- **Epic 4:** saves "the Buffer Window, or back to the last Reset" (R4). The window crosses the generation boundary, so it writes one faststart MP4 (AD-9) with two `avcC` sample descriptions.
- **Clash:** Discord and Chrome reject that, or play only one half, which breaks R33. AD-8 creates generations and no AD says what a save does at one.
- **Proposed tightening of AD-8:**
  > A generation boundary is a Reset for Clip purposes. A Clip contains packets from exactly one generation, the newest one that intersects the window, so it starts at that generation's first keyframe. A save requested within 1 s of a boundary uses the previous generation, if it holds ≥1 s, so the save doesn't produce "no Clip".

### H9. Profile flags: live or snapshot? Epic 3 vs epic 4 vs epic 6

AD-4's schema puts `name`, `ignored` and `recordingsEnabled` beside `overrides`. AD-5 says a session reads "one immutable EffectiveSettings snapshot".

- **Epic 3 marks the Target as Ignored mid-session.** Epic 3 treats it as no longer a game straight away. Epic 4's session snapshot still says not-ignored, so the Replay Buffer keeps capturing an Ignored game until the next Reset.
- **The user switches `recordingsEnabled` off mid-Recording.** Epic 6 might stop the Recording (it's "off"), or keep it (AD-5).
- **The profile is renamed mid-session.** R26 says "affects new files only". Is a Clip saved after the rename a new file (current name) or bound by the snapshot (old name)?
- **Proposed tightening of AD-5:**
  > `EffectiveSettings` holds only overridable media keys (see H10). Profile attributes are read live:
  > - Setting `ignored` on the current Target is a `target.changed{reason:"ignored", to:null}` **with no drain**. Nothing more is captured from it, and a pending save still completes.
  > - `recordingsEnabled` gates only Record *start*, so a running Recording continues.
  > - `name` is read when a file name is allocated (see M2), so a Clip saved after a rename uses the new name.

### H10. No catalog of which keys are overridable: epic 1 vs epic 4

AD-4 says overrides hold "the keys the user changed" but never lists the keys or which ones a profile may override.

- **Epic 1** builds the store generically: any `global` key can be overridden.
- **Epic 4** builds a profile Settings page offering Track Layout and Output Settings (R17). A generic store, though, also accepts profile overrides of `bufferWindow`, `ringMode` (Memory/Disk), `outputFolder`, `saveCooldown` and Hotkeys.
- **Clash:**
  - A profile overriding `ringMode` makes a target switch reallocate the ring, so a 60 GB disk ring is recreated on alt-tab.
  - A profile overriding `outputFolder` breaks AD-17's "path relative to the Output Folder".
  - Epic 7 assumes one Output Folder, so Cleanup misses the per-profile ones.
- **Proposed tightening of AD-4:**
  > `ReplayBuffer.Contracts.Settings.Keys` is the closed catalog. Each key declares `{ type, default, overridable, appliesAt: "immediate" | "nextSession" }`.
  > - Overridable in v1: `trackLayout`, `video` (codec, resolution, fps, bitrate, rate control), `bufferWindow`.
  > - Global-only: `ringMode`, `ringPath`, `outputFolder`, `saveCooldown`, `hotkeys`, `recordHotkeyMode`, `cleanup`, `startWithWindows`, `feedbackSounds`, `feedbackSoundDevice`, `monitorRecording`.
  > - The store refuses an override of a non-overridable key with `key-not-overridable`.

### H11. `state.snapshot` is unordered against events and incomplete: epic 5 vs epic 8

AD-3: "On connect, the engine first sends a `state.snapshot` event."

- **Epic 5's Overlay** keeps the red X "until dismissed" (R13), the mute icon, the solid dot and the cooldown icon from events.
- **Epic 8** restarts the App or the engine. On reconnect the snapshot holds whatever each epic remembered to add, and epic 6 forgot the disk-full flag, so the red X disappears.
- **Race:** the snapshot is assembled on the IPC thread while `clip.saved` is raised on a writer thread. That event is either missing from both the snapshot and the stream, or applied twice, and the Overlay queues a duplicate "Clip saved".
- **Dismissing the red X:** the App does it locally, so the next reconnect brings it back.
- **Proposed tightening of AD-3:**
  > Every event carries `seq`, a per-engine-instance monotonically increasing number. `state.snapshot` carries `{ engineInstanceId, seq, … }` and is built under the same lock that assigns `seq`. The App drops events with `seq ≤ snapshot.seq`.
  >
  > **Every piece of engine state that any event changes must be a field of `state.snapshot`.** Contracts tests enforce this: each event type maps to a snapshot field.
  >
  > User dismissals are commands (`warning.dismiss { id }`), not App-local state.

### H12. Track Layout inline vs a named-layout library: epic 2 vs epic 4

- **Epic 2** reads "Global Track Layout**s**" (tickets.toml) and R30's "the layout being viewed", and builds `global.trackLayouts: [{id, name, tracks}]` with `activeLayoutId`.
- **Epic 4**, obeying AD-4 ("a Track Layout is one key, overridden as a whole"), overrides `trackLayout` with an inline layout.
- **Clash:** the resolver has to reconcile an id reference with an inline value. "Overridden as a whole" has no clear meaning when Global holds a list.
- **Proposed tightening of AD-4:**
  > In v1 a Track Layout is a single inline value at key `trackLayout`, in Global and in each profile's overrides. There is no named-layout library. "Layout being viewed" (R30) means Global's or the selected profile's effective `trackLayout`, or the unsaved draft via `meters.watch` (see H1). Editing a profile's layout first copies Global's, **keeping Track ids**.

### H13. Epics 2 and 3 in parallel each extend Contracts and the session API

Both epics start after epic 1, at the same time.

- **Epic 2** adds `trackLayout` to the settings DTO, audio Sources to the session spec, and `meters.*` and `audio.*` messages. A schema change seems to call for it, so it bumps `schemaVersion` to 2 with a migration.
- **Epic 3** adds `profiles[].name`, `ignored` and `recordingsEnabled`, auto-creation, `game.*` and `target.changed`, and also bumps `schemaVersion` to 2 with a different migration.
- **Clashes at merge:**
  - Two different "v2" settings schemas.
  - Two edits to the one source-generated `JsonSerializerContext` and the `[JsonDerivedType]` list of the message union.
  - Two incompatible changes to the capture-session constructor: epic 2 adds `AudioSources`, while epic 3 replaces `WindowHandle` with `TargetGame`.
- Does an added message count as a "breaking change" under AD-3, bumping `v` and the pipe name? Neither epic can know.
- **Proposed AD-22 (Contracts are additive inside v1; epic 1 seeds the skeleton):**
  > - Epic 1 ships the complete key catalog of H10, with types and defaults (values may be stubs), the session spec type `SessionSpec { kind, target, snapshot, audio }`, and an empty event catalog with a registration pattern of one file per epic area: `Contracts/Messages/Audio.cs`, `Game.cs`, …
  > - Until v1 ships, `schemaVersion` stays 1 and `v` stays 1. Changes are additive only: new keys get defaults, and new messages and fields are optional.
  > - A rename or removal needs a spine update first.
  > - Migrations live in one ordered registry, `SettingsMigrations`, and only a spine change adds one.

---

## Medium

### M1. A slow App can stall the engine through the pipe: epic 2 meters vs epic 4/1 hotkeys

Epic 2 streams meter events (several Tracks at 30 Hz). Epic 5 renders on the WPF UI thread. If the App's UI thread hangs for 3 s (a GC pause, or the Library loading thumbnails), the pipe's buffer fills. Epic 1's IPC server writes synchronously from whichever thread raised the event, which can be the hotkey thread (`clip.saving`) or the orchestrator. That blocks capture and Hotkeys, which is exactly what AD-1 was meant to prevent. AD-7's no-per-buffer-allocation rule also clashes with serializing a meter event per audio buffer.
- **Proposed tightening of AD-3:**
  > Each connection has one writer thread fed by a bounded outbound queue. No engine thread except that writer ever touches the pipe.
  > - Coalescing event types (`meters.levels`, `estimate.changed`) keep only their latest value per key.
  > - If the queue holds >1 000 non-coalescing events or is 5 s behind, the engine drops the connection, and the App reconnects and gets a fresh snapshot.
  > - `meters.levels` is sent at ≤20 Hz from peaks accumulated in preallocated slots on the audio thread.

### M2. When and by whom a file name is allocated: epic 3 vs epic 4 vs epic 6

- **Epic 3** "resolves the per-game folder and file name … for later epics" and exposes `ResolvedPath` for the active profile, computed when it becomes Target.
- **Epic 4** saves two Clips, either into the same path or each calling `Resolve(now)`. With R7's two concurrent writes and a cooldown below 1 s, both get `X_2026-10-25_21-14-03.mp4`, both check that it doesn't exist, and both create it. That breaks "no file is ever overwritten" (R26).
- **Undefined timestamp:** is it the press time, the Clip's start, or the write time? Recordings (epic 6) presumably use their start time.
- **Proposed AD-21 (Output path allocator):**
  > `OutputPathAllocator` in Core is the only producer of output paths. It allocates when a writer is created, using the current profile name (see H9) and the local time of the press QPC (Clip) or start QPC (Recording). It reserves the path atomically through `CREATE_NEW` on the `.rbpart` or final file, adding `_2`, `_3`… on a collision. Epic 3 owns the sanitising and folder rules inside it. Nobody caches a resolved path.

### M3. Library grouping by folder vs by profile: epic 3 rename vs epic 7

Epic 7 groups "by game" using the folder name, the only thing on disk. Epic 3's rename (R26) sends new files to a new folder, so one game splits into two Library groups. If epic 7 instead looks up the profile by folder name, the lookup misses after a rename.
- **Proposed tightening of AD-17:**
  > Each `library.json` item stores the `profileId` recorded by the writer at `BeginWrite`. Items are grouped by `profileId` and shown under the current profile name. Files without a `profileId` (external or pre-index) are grouped by folder name.

### M4. Library mutations race each other: epic 7 Cleanup vs epic 7 actions vs epic 4/6 writers

AD-17 gives the engine the index, the watcher, Cleanup and the being-written set, but not one thread.
- **Favourite vs Cleanup:** Cleanup picks its victims on a timer thread while `library.favourite` arrives on the IPC thread. The file is deleted right after the user Favourites it.
- **Rename vs watcher:** a Library rename (epic 7) re-keys the item, then the watcher's own rename event re-keys it again, or registers delete-plus-create and drops the Favourite. A rename made *outside* the app (R27) loses the Favourite outright.
- **Proposed tightening of AD-17:**
  > `LibraryActor` is a single-threaded queue that handles watcher events, IPC commands, writer `BeginWrite`/`EndWrite` and each Cleanup step. Cleanup re-checks Favourite and being-written status inside the actor, immediately before each delete.
  > - The engine's own renames and deletes are registered as expected operations, so their watcher echoes are swallowed.
  > - A watcher rename event moves the item's metadata (Favourite included) to the new key.

### M5. More than one resolver, and torn snapshots: epic 1 vs epic 3 vs epic 4

Epic 1's store resolves overrides. Epic 3 exposes "the active Game Profile". Epic 4 builds `EffectiveSettings` by reading `Global` and then `profiles[id]` as two property reads from a store that a concurrent `settings.update` is swapping. The result is a torn snapshot: new Global with the old profile, or the reverse. Epic 6 writes its own resolver for Recordings, with "present but null" handled differently.
- **Proposed tightening of AD-4 and AD-5:**
  > There is exactly one function, `EffectiveSettings Resolve(SettingsDocument doc, ProfileId? id)`, in Core. It is called on a single immutable `SettingsDocument` reference taken with one volatile read. `EffectiveSettings` records `settingsRevision`, and `session.started { kind, profileId, settingsRevision }` lets the App show "pending until next Reset".

### M6. Chain sharing and the chain switch at Reset: epic 4 vs epic 6

AD-7 says sinks share a chain "while their settings snapshots are equal".
- Epic 4 compares whole `EffectiveSettings` records, including `profileId` and `settingsRevision`. Epic 6 compares only the video fields. One builds a second NVENC chain that the other would have shared, doubling the FPS cost against R36.
- When a Reset changes the media settings while a Recording holds the old chain, the Replay Buffer needs a new chain. A hardware encoder takes 100 to 300 ms to initialise, so "no gap" (R5) fails at exactly the Resets that change settings, and no AD says whether that's allowed.
- **Proposed tightening of AD-7:**
  > Sharing is decided by `ChainKey = hash(source identity, video keys, trackLayout device and Track structure)` and nothing else. When `settings.changed` changes the pending `ChainKey` of the Replay Buffer, the orchestrator pre-builds the next chain in the background. The switch happens at the press frame, and the new chain's first frame is the frame after the press. If the pre-build hasn't finished, the gap is reported as `warning.raised{code:"reset-gap"}` rather than silently breaking R5.

### M7. A hung engine defeats supervision: epic 1 vs epic 8

AD-16: the App restarts a *crashed* engine, and a named mutex keeps one instance. If the engine *hangs* (stuck in a driver, deadlocked), the pipe stops answering but the process holds the mutex. The App's relaunched engine sees the mutex and exits. The App backs off and loops for ever, and Hotkeys are dead.
- **Proposed tightening of AD-16:**
  > The engine emits `engine.heartbeat` every 2 s. Missing 3 heartbeats, or a command with no reply after 10 s, means hung: the App kills the engine by the PID from `state.snapshot.engine.pid` and relaunches it. The engine treats `WAIT_ABANDONED` on the mutex as acquired.

### M8. Tray Quit vs "the engine survives the App exiting": epic 1 vs epic 8

R31 says the tray icon "quits". AD-16 says the engine survives the App exiting, with no kill-on-close. Epic 1 implements Quit as the App exiting, which obeys AD-16. The engine then keeps capturing and keeps Hotkeys registered with no visible UI. Epic 8 tests that "Hotkeys survive the Main App being killed" and passes.
- **Proposed tightening of AD-16:**
  > Tray **Quit** sends `engine.shutdown` and waits for its reply (≤5 s; a pending save finishes first), then the App exits. Closing the Main App window only hides it. Only an App crash, kill or update leaves the engine running.

### M9. A Recording auto-resumes after an engine restart: epic 8 vs epic 6

Epic 8 makes the restarted engine resume "the Replay Buffer". A generic "restore the previous session state" also restores the Recording that was running, which breaks R9: "nothing starts a Recording automatically".
- **Proposed tightening of AD-16:**
  > After a restart only the Replay Buffer resumes. Recordings and Monitor Recordings never auto-resume. The restarted engine emits `warning.raised{code:"recording-interrupted"}` if `runtime.json` (see C4) shows one was running.

### M10. Library thumbnails and file actions vs AD-1: epic 7 vs AD-1 and epic 6

AD-1: the App "never … touches media files directly". Epic 7's Library is App UI and needs thumbnails, length, open, show in Explorer, and copy to clipboard (`CF_HDROP`).
- If epic 7 obeys AD-1 literally, the engine must decode thumbnails with FFmpeg during gameplay. That means CPU and GPU contention against R36, and a decode path that no AD sanctions.
- If epic 7 uses the Windows shell thumbnail provider, the shell (dllhost) holds handles on files, including a Recording being written. That can block epic 6's finalise or epic 7's rename and delete.
- **Proposed tightening of AD-1:**
  > The App may hand *paths* to the shell: `ShellExecute` open, `SHOpenFolderAndSelectItems`, `CF_HDROP`, and shell thumbnails via `IShellItemImageFactory` with `SIIGBF_THUMBNAILONLY`. It never opens file contents itself. It never requests a shell thumbnail for an item flagged being-written. Writers open files with `FILE_SHARE_READ | FILE_SHARE_DELETE`.

### M11. The Output Folder changes mid-session: epic 7 vs epic 6

AD-17 keys the index "by path relative to the Output Folder". Epic 6 has a Recording writing to the old folder, which is fine since the writer holds an absolute path. Epic 7 re-keys everything against the new folder: the old items vanish, the being-written entry's relative path now points nowhere, and Cleanup's scope moves under the user.
- **Proposed tightening of AD-17:**
  > The being-written set stores absolute paths. Changing `outputFolder` (global-only, see H10) rebuilds the index from the new folder only. Items in the old folder leave the Library and are never cleaned. Writers in progress finish in the old folder, and a `library.outputFolderChanged` event tells the App.

### M12. Cooldown and capture state computed twice: epic 4 vs epic 5

R6: the green icon "stays through the Save Cooldown". Epic 5 starts a local timer from `settings.saveCooldown` when it receives `clip.saving`. That's wrong once the cooldown setting changes, and wrong after a reconnect. The dot's states also don't cover R1's 30 s drain: hollow or off? There's no shared state enum, so epic 4 emits `capture.started` / `capture.stopped` while epic 5 expects a state field.
- **Proposed rule (Contracts):**
  > `capture.stateChanged { replay: "off" | "buffering" | "draining" | "stopped", recording: "off" | "running", monitorRecording: "off" | "running", cooldownUntil: iso8601 | null }`. It is the single source the Overlay renders, and it lives in `state.snapshot` (see H11). Epic 4 defines it, and epic 6 extends it additively.

### M13. Warnings and error codes invented per epic: epics 2, 4, 6, 8 vs epic 5

Epic 5's status line must show device dropouts (epic 2), save failures (epic 4), disk full and encoder restarts (epic 6), and engine restarts (epic 8). Each epic invents its own event (`audio.dropout`, `clip.failed`, `disk.full`, `engine.restarted`) and its own kebab-case codes. Epic 5 hard-codes four event types and misses the fifth.
- **Proposed tightening of the Errors convention:**
  > Every user-facing warning is `warning.raised { id, code, severity: "info" | "warn" | "error", source, message, at, overlay: bool, sound: SoundId | null, dismissable }`. Codes live in one `Contracts.ErrorCodes` registry. Epic 5 renders only `warning.raised` and `capture.stateChanged`.

### M14. AD-3's exactly-one reply vs an engine crash with a command in flight: epic 1 vs epic 8

The App awaits a reply to `library.delete` or `settings.update`. The engine dies. The reply never comes, so the await either hangs or the App retries and the delete runs twice: the second attempt fails, or worse, deletes a re-created file with the same name.
- **Proposed tightening of AD-3:**
  > On disconnect, every pending command fails locally with `engine-disconnected`. The App never auto-retries a mutating command. After reconnect, it re-reads the state and lets the user act again. Mutating commands carry their target's identity (path plus size and mtime, or `expectedRevision`), so a stale retry is refused.

### M15. Profile id canonicalization isn't one function: epic 3 detection vs epic 3 hand-add vs epic 8 reboot

AD-18: "the full executable path, normalized to lowercase".
- Process monitoring gets `\Device\HarddiskVolume4\games\x.exe` from `QueryFullProcessImageName(PROCESS_NAME_NATIVE)`, or `C:\GAMES\X.EXE`.
- The App's add-by-hand picker gives `D:\SteamLibrary\…` through a junction or a subst drive.
- 8.3 short names also exist.

Each lowercases its own string, and one game gets two profiles with two folders.
- **Proposed tightening of AD-18:**
  > There is one function, `ProfileId Canonicalize(path)`, in Platform.Windows:
  > 1. Open the file and call `GetFinalPathNameByHandle(FILE_NAME_NORMALIZED | VOLUME_NAME_DOS)`.
  > 2. Strip `\\?\`.
  > 3. Apply `ToLowerInvariant`.
  >
  > The App sends raw paths, and only the engine canonicalizes them.

---

## Low

- **L1. Folder-name collisions (epic 3 vs epic 6).** Two profiles with the same product name ("Launcher") share a folder. A profile named "Monitor Recordings" merges with R21's folder. *Rule (AD-21):* the allocator reserves `Monitor Recordings`, and gives each profile a folder name unique among profiles by appending ` (2)`.
- **L2. Ring inside the Output Folder (epic 4 vs epic 7).** If `ringPath` can be configured, a user can point it at the Output Folder, and Cleanup counts the preallocated ring file, or the watcher indexes it. *Rule:* validation refuses a `ringPath` inside `outputFolder` (`ring-path-invalid`), and Cleanup considers only `*.mp4` items present in the index.
- **L3. Pinned slabs vs the RAM margin (epic 4 vs epic 6).** Two concurrent saves pin up to twice the window, and AD-8's "drops the oldest slabs first" can't drop pinned ones. *Rule (AD-8):* pinned bytes count toward the margin. If the unpinned ring has to shrink below 1 s, `warning.raised{code:"memory-margin"}` is raised. The writer still never blocks.
- **L4. Ring reallocation at Reset (epic 4).** Changing `bufferWindow` while a save reads pinned slabs from the old ring file means the file can't be truncated or recreated in place. *Rule (AD-8):* resizing allocates a new ring (`ring-<generation>.bin`), and the old one is released when its last pin drops.
- **L5. Pipe identity (epic 1).** `ReplayBuffer.Engine.v1` isn't per-user, despite AD-3 calling it "per-user", and any process can create it first. *Rule (AD-3):* name it `ReplayBuffer.Engine.v1.<user SID>`, created with `FILE_FLAG_FIRST_PIPE_INSTANCE` and an ACL for the current user only. The App checks that the server PID's image path is in its own install folder.
- **L6. Target after an engine restart (epic 8 vs epic 3).** With GameMonitor's state lost and two games running, the 10 s rule restarts from zero and the resumed buffer waits up to 10 s, or follows whichever game enumerates first. *Rule (AD-18):* on a cold start, the Target is the foreground detected game, otherwise the most recently started detected game, chosen immediately.
- **L7. Detecting the app's own windows (epic 3 vs epic 5).** R23's "own windows never detected" is done by App PID, which changes when the App restarts, or by window class, which epic 5's toasts don't share. *Rule (AD-18):* exclude any process whose canonical image path is under the install folder.

---

## Proposed AD changes at a glance

| AD | Change | Closes |
| --- | --- | --- |
| AD-1 | App may hand paths to the shell; never opens contents; share flags for writers | M10 |
| AD-3 | `seq` + complete snapshot; outbound queue and backpressure; disconnect semantics; per-SID pipe | H11, M1, M14, L5 |
| AD-4 | Revisioned key-level ops; closed key catalog with `overridable` and `appliesAt`; Track Layout shape; one resolver | C3, H2, H10, H12, M5 |
| AD-5 | Mute is runtime state (persisted, fail-closed); profile flags read live; snapshot carries a revision | C4, H9, M5 |
| AD-6/10 | Hotkey QPC stamp; WASAPI and WGC stamp sources; one drift-correcting resampler | H3b, H4 |
| AD-7 | `ChainKey`; chain pre-build and switch at the press frame | M6 |
| AD-8 | A generation boundary is a Reset for Clips; pinned bytes and margin; ring resize | H8, L3, L4 |
| AD-11 | Hotkey schema, action catalog, global-only, immediate apply, `hotkey.status` | H3a |
| AD-15 | Feedback Sound device and loopback warning | H7 |
| AD-16 | Heartbeat and hang kill; Quit = shutdown; Recordings never auto-resume | M7, M8, M9 |
| AD-17 | Atomic index and fail-closed Cleanup; `BeginWrite`/`EndWrite` and `.rbpart`; `LibraryActor`; `profileId` grouping; Output Folder change | C5, H5, M3, M4, M11 |
| AD-18 | GameMonitor proposes, orchestrator commits; `target.changed` semantics and drain; canonical path; cold-start Target; exclusion of own processes | C2, H6, M15, L6, L7 |
| **AD-19 (new)** | Single-threaded `SessionOrchestrator` owns every session's lifecycle and target | C1, C2 |
| **AD-20 (new)** | `AudioDeviceHub`: one WASAPI client per device, PCM fan-out, `meters.watch` | H1 |
| **AD-21 (new)** | `OutputPathAllocator`: allocation time, atomic reservation, reserved names | M2, L1 |
| **AD-22 (new)** | Contracts are additive inside v1; epic 1 seeds the key catalog, session spec and message files | H13 |
| Conventions | `warning.raised` + `ErrorCodes` registry; `capture.stateChanged` | M12, M13 |

## Parallel epics 2 and 3: what has to be true before they start

Epic 1's Done-when must also include:
1. The H10 key catalog, with `trackLayout`, the profile attributes, `hotkeys` and `feedbackSoundDevice` declared.
2. `SessionSpec` with both an `audio` slot and a `target` slot (H13).
3. The `SessionOrchestrator` actor shell with its message types (AD-19).
4. The `AudioDeviceHub` port signature (AD-20).
5. The `seq`/snapshot mechanism in the IPC server (H11).

Without these, epics 2 and 3 each build half of them in conflicting shapes, and epic 4, which depends on both, is where the rework lands.
