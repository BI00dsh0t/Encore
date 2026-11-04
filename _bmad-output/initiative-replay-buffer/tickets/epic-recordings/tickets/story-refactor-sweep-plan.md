---
title: 'Refactor sweep'
type: 'refactor'
ticket: '12'
created: '2026-11-04'
status: done
baseline_revision: 'f02e8844'
route: 'full'
route_source: 'auto'
risk: 'low'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
followup_review_recommended: false
context: ['{project-root}/CLAUDE.md']
deferred: []
---

<intent-contract>

## Intent

**Problem:** Epic 6 (6.1–6.11) left its sweep:
- the game and Monitor Recordings' write handling, written twice in `SessionOrchestrator`;
- stale docs (`DesktopDuplicationCapture.Failed`, 6.10's QA note; `MonitorFinder`; `OutputNaming`);
- items in `_bmad-output/deferred-work.md` that name epic 6's sweep as owner:
  - `OutputNaming`;
  - `FeedbackRouter`'s duplicates and the feedback tests' two fake notice sinks;
  - `FeedbackSounds`' `IsOn`/`With` written twice.

**Approach:** Fix them in place with no behaviour change. Record every open item in `deferred-work.md` with an owner.

## Boundaries & Constraints

**Always:**
- Behaviour-preserving: same IPC, events, settings JSON, log text, UI text and notice text.
- Before removing or narrowing a member, grep `_bmad-output/initiative-replay-buffer/tickets` for its name (forward hooks). Keep `CaptureSession.RestartChain` and the ring restart path, which a later story wires.
- Contracts changes are additive only (AD-22).

**Never:**
- Change the capture, encode or mux path's behaviour.
- Run Heavy or GPU tests (GPU tests paused by the builder).

</intent-contract>

## Changes

- `SessionOrchestrator`: `OpenWrite` (begin the Library write and the open record, start the writer, undo on a failed start) and `CloseWrite` (end the write, report a failure, pick the stop's reason), used by both the game and the Monitor Recording.
- `FeedbackRouter`: one `UnlessDisposed` guard instead of twelve copies; `FeedbackSoundNames` constants, shared with `FeedbackSoundPlayer.Parse`; a `Replay` source constant.
- `FeedbackSounds` (Contracts, additive): `IsOn(FeedbackSound)` and `With(FeedbackSound, bool)`. `FeedbackSoundsPage` and `FeedbackSoundPlayer` call them; the player's static `IsOn` is gone (no other caller, no ticket names it).
- Tests: a shared `FakeFeedbackNotices` replaces the router's and the sounds tests' private fakes; the router tests use the shared `ManualTime`.
- Docs: `DesktopDuplicationCapture.Failed` names both cases; `MonitorFinder` covers `monitors.list`; `OutputNaming` says it names only debug captures.

## Not changed

- `OutputNaming` stays: only `--debug-capture`, test instances and the tracer use it, and moving them to the allocator would rename debug files.
- The serve tests' repeated harness: about a dozen classes, mostly outside epic 6; owned by epic 8's sweep.
- Behaviour items: a Record press while a chain switch is armed (epic 8), the Monitor session's own D3D11 device (builder review), the Desktop Duplication start on the secure desktop (8.4).

## Verification

- `dotnet build ReplayBuffer.slnx -c Release`: 0 warnings.
- Default tests: Contracts, Core, App and Engine (Core's direct dependent).
- `pwsh tools/publish-engine.ps1`: engine code changed (a doc comment).
- Not run (GPU tests paused by the builder): the Heavy Recording, Clip-during-Recording and Monitor Recording capture tests that would show they still write playable files.
