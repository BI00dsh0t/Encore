---
title: 'IPC server and client with numbered events, snapshot and shared shapes'
type: 'feature'
ticket: '5'
created: '2026-10-25'
status: 'in-progress'
baseline_revision: 'f22cd177374125b3ad4d77d6fd6b0b8b67779726'
route: 'full'
route_source: 'auto'
risk: 'medium'
review: ''
review_source: ''
lenses_ran: []
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/initiative-replay-buffer/architecture-replay-buffer/architecture-replay-buffer.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** The engine and the Main App have no way to talk. Epics 2 and 3 would each invent message shapes, and stories 6–8 have no `seq`/snapshot mechanism to publish state through (AD-3, AD-22).

**Approach:** Contracts gains the AD-3 protocol: pipe name, `{v,id,type,payload}` JSON-lines framing, a typed command/event registry with one message file per epic area, `state.snapshot` with a shared reducer per event, the two shared shapes, and the `EngineClient`. The engine gains the IPC server: an event hub that assigns `seq` and builds snapshots under one lock, a per-connection bounded writer queue with coalescing and drop rules, and a command router.

## Boundaries & Constraints

**Always:**
- Pipe `ReplayBuffer.Engine.v1.<instance>.<user SID>`, server created with `PipeOptions.FirstPipeInstance` (first instance only) and a DACL granting only the current user; client connects with `CurrentUserOnly`.
- UTF-8 JSON lines, source-generated `System.Text.Json` only, camelCase. `v` is 1.
- Every command gets exactly one reply: result `payload`, or `error: { code, message }`. On disconnect the client fails every pending command with `engine-disconnected`.
- Events carry an envelope `seq` that only increases. `state.snapshot { engineInstanceId, pid, seq, settings, capture, warnings }` is the first frame on every connection, built and enqueued under the lock that assigns `seq`; the client drops events with `seq` ≤ the last it holds.
- Each connection has one writer loop, the only code that writes its pipe, fed by a bounded queue. A coalescing type keeps only its latest queued value, moved to the tail. More than 1 000 queued frames, or the oldest unwritten frame older than 5 s, drops the connection. Publishing never blocks on a client.
- Every registered event names the snapshot field it changes and has one `Apply(snapshot, payload)` in Contracts, used by the engine hub and the client mirror alike.
- Message files: `Engine.cs`, `Settings` messages, and `Audio.cs`, `Game.cs`, `Capture.cs`, `Library.cs`, `Recording.cs`, `Feedback.cs`, each owning its area's commands, events and JSON context.
- New codes kebab-case in `ErrorCodes.All`; 0 warnings with the trim/AOT analyzers; AD-2 reference graph unchanged.

**Never:**
- No P/Invoke outside `Platform.*` (managed `System.IO.Pipes` ACL APIs only).
- No reconnect, heartbeat, hung-engine detection or mutex (story 8); no App UI (story 7); no orchestrator (story 6) — nothing in the engine publishes `capture.stateChanged` yet.
- No payloads for epics 2–7 beyond the shared shapes; their files start empty.
- No auto-retry of commands.

**Decisions (user, 2026-10-25):**
- The engine gains `serve`: the store plus the IPC server, running until `engine.shutdown` or Ctrl+C. `--instance ReplayBuffer-Test-<suffix>` (only that prefix) lets tests use their own pipe and data folders. The kill test kills a real engine process.
- AD-3's server image-path check: `EngineClient` takes an optional verify-server callback, run after connecting and before the snapshot is read; story 7 or 8 supplies it from the App side.
- The plan is kept whole at about 2 100 tokens.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Connect | engine running, N events published | first frame `state.snapshot` with `seq` = N; later events N+1, N+2… | — |
| Command | `settings.update` valid | `settings.changed` event delivered, then reply `{ revision }` | — |
| Command refused | stale `expectedRevision` | error reply, no event | `settings-conflict` |
| Unknown command | `type` not registered | error reply | `command-unknown` |
| Bad payload / version | payload doesn't fit args type, or `v` ≠ 1 | error reply | `invalid-arguments` |
| Handler crash | handler throws a non-coded exception | error reply, connection stays | `command-failed` |
| Malformed frame | not JSON, no `type`, or line > 1 MiB | connection dropped, engine keeps serving | — |
| Server gone | engine killed with a command pending | pending task fails; later sends fail at once | `engine-disconnected` |
| Stalled client | client never reads, engine keeps publishing | connection dropped at > 1 000 queued or > 5 s lag; other clients and `Publish` unaffected | — |
| Coalescing | queued `A#1, B#2`, then `A#3` (A coalesces) | written `B#2, A#3` | — |
| Second engine | pipe already exists for the instance | `serve` exits non-zero | `ipc-pipe-in-use` |
| Dismiss | `warning.dismiss { id }` | `warning.dismissed` event, reply `{}` | unknown id `warning-not-found`; not dismissable `warning-not-dismissable` |
| Unknown event | client receives an unregistered event type | `seq` advances, mirror unchanged, still raised raw | — |

</frozen-after-approval>

## Code Map

- `src/ReplayBuffer.Contracts/ErrorCodes.cs` -- add codes to constants and `All`; `ErrorCodesTests` checks both.
- `src/ReplayBuffer.Contracts/ErrorCodeException.cs` -- client throws it for error replies and `engine-disconnected`.
- `src/ReplayBuffer.Contracts/Instance.cs` -- `Name` feeds the pipe name; don't change.
- `src/ReplayBuffer.Contracts/Settings/*` -- reuse `SettingsDocument`, `SettingsUpdate` and `SettingsJsonContext` for the settings messages; don't change the catalog.
- `src/ReplayBuffer.Engine/Settings/SettingsStore.cs` -- `Update` throws `ErrorCodeException`; `Changed` fires under the store lock (store lock → hub lock is the only nesting order).
- `src/ReplayBuffer.Engine/Program.cs`, `CommandLine.cs` -- add `serve`; keep the debug commands as they are.
- `tests/ReplayBuffer.Engine.Tests/EngineProcessTests.cs` -- pattern for running the built engine exe.
- The App references only Contracts (AD-2), so the shared framing and `EngineClient` live in Contracts.

## Tasks & Acceptance

**Execution:**
- [ ] `src/ReplayBuffer.Contracts/ErrorCodes.cs` -- add `engine-disconnected`, `command-unknown`, `command-failed`, `ipc-pipe-in-use`, `warning-not-found`, `warning-not-dismissable`.
- [ ] `src/ReplayBuffer.Contracts/Ipc/` -- `PipeName`, `Envelope` (+ `IpcJsonContext`), `FrameReader`/`FrameWriter` (line limit, `Utf8JsonWriter` with a typed payload), `CommandType<TArgs,TResult>`, `EventType<T>`, `StateSnapshot`, `MessageRegistry` (concatenates the area lists), `EngineClient` (with the optional verify-server callback).
- [ ] `src/ReplayBuffer.Contracts/Messages/` -- `Engine.cs` (`state.snapshot`, `engine.shutdown`, `Empty`), `SettingsMessages.cs` (`settings.update` → `{ revision }`, `settings.changed`), `Capture.cs` (`capture.stateChanged`), `Feedback.cs` (`warning.raised`, `warning.dismissed`, `warning.dismiss`), empty `Audio.cs`, `Game.cs`, `Library.cs`, `Recording.cs`.
- [ ] `src/ReplayBuffer.Engine/Ipc/` -- `EventHub` (lock, `seq`, state, attach/publish), `WriterQueue`, `Connection` (reader loop, writer loop, drop), `CommandRouter`, `IpcServer` (accept loop, lag timer, options for tests), `Warnings` (raise/dismiss).
- [ ] `src/ReplayBuffer.Engine/EngineHost.cs`, `Program.cs`, `CommandLine.cs` -- compose store + hub + router + server; `serve [--instance ReplayBuffer-Test-*]` runs until `engine.shutdown` or Ctrl+C; a `settings-unreadable` load raises a warning.
- [ ] `tests/ReplayBuffer.Contracts.Tests/MessagesTests.cs` -- every event maps to an existing snapshot field and `Apply` changes exactly that field; every snapshot field (bar identity) is covered; names unique and `noun.verb`; shared shapes round-trip to the AD-3 JSON.
- [ ] `tests/ReplayBuffer.Engine.Tests/WriterQueueTests.cs`, `IpcTests.cs` -- the matrix rows, in-process on a unique test instance.
- [ ] `tests/ReplayBuffer.Engine.Tests/EngineServeTests.cs` -- `serve` process: snapshot, `settings.update`, kill → `engine-disconnected`; second `serve` → `ipc-pipe-in-use`; `engine.shutdown` exits 0.

**Acceptance Criteria:**
- Given a client connected to a running engine, when it issues `settings.update`, then it has received the snapshot first and every event after it in increasing `seq`, and the reply after the command's event.
- Given a pending command, when the server process is killed, then the command fails with `engine-disconnected` within 1 s.
- Given one stalled client and one reading client, when the engine publishes 5 000 events, then the stalled one is dropped, the reading one receives all of them in order, and no `Publish` call takes more than 50 ms.
- Given the registry, when the Contracts tests run, then every event maps to a snapshot field.
- Given the solution, when `dotnet build ReplayBuffer.slnx` and `dotnet test --solution ReplayBuffer.slnx` run, then 0 warnings and all tests pass.

## Implementation Notes

## Plan Change Log

## Review Triage Log

## Design Notes

- Frames: client → engine `{v,id,type,payload}`; reply `{v,id,type,payload|error}`; event `{v,type,seq,payload}`. A frame with `id` from the engine is a reply, one without is an event.
- One writer per connection is an async loop on an `Asynchronous` pipe, so a drop cancels its blocked write (`CancelIoEx`); the queue counts the in-flight frame for lag.
- Commands run concurrently per connection; replies correlate by `id`. A handler's events are published before its reply is enqueued, so the reply arrives after them.
- Snapshot defaults: `capture` all `off`, `micMuted: true` (AD-5's missing-state rule), `cooldownUntil: null`; `warnings: []`.
- `warning.raised { id (GUID), code, severity: info|warning|error, source, message, at (UTC), overlay, sound (a `feedbackSounds` name or null), dismissable }`.
- Per-area JSON contexts keep epics 2 and 3 out of each other's files; the snapshot record is the one shared edit (one field each, additive).

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx` -- expected: 0 warnings, 0 errors.
- `dotnet test --solution ReplayBuffer.slnx` -- expected: all pass.
