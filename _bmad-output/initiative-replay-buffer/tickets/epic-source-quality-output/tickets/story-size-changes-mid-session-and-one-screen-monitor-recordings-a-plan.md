---
title: 'Size changes mid-session, and one-screen Monitor Recordings at source size'
type: 'feature'
ticket: '2'
created: '2026-11-05'
status: built
baseline_revision: 'fea3ac6'
route: 'full'
risk: 'high'
---

# Story 12.2: size changes mid-session, and one-screen Monitor Recordings at source size

The plan checkpoint: no product code until the coordinator approves this plan.

## Intent

With `video.matchSource` on (12.1), AD-26's held size change moves the Replay Buffer to a chain at the game's new size, never during a game Recording, and a one-screen Monitor Recording records at its screen's physical mode size through `MonitorCanvasRule.Plan`. With `matchSource` off, nothing changes.

## Where things are today (12.1, 11.3)

- `CaptureSession.StartOutput` records the first frame's content size (`_sourceSize`), and `SizedToSource` sizes every chain the session builds from it.
- A chain switch at a Reset (AD-7, 4.9):
  - `PrepareChain(spec)` builds a chain ahead into `_prepared`, only when the `ChainKey` differs;
  - a save press (`ClipWrite`) calls `TryArm`, which arms it at the press;
  - the first frame captured after the press makes it current (`SwitchTo`);
  - the old chain retires once no save is bound to it (`Retire`), and `CheckGap` raises `reset-gap` when the new chain's first keyframe is more than a frame after the old chain's `SwitchEndUs`.
- `FrameScaler.Draw` already letterboxes content that differs from the output size (R20).
- A one-screen Monitor session captures through today's single path at the snapshot's size; only several screens go through `PlanCanvas` (11.3).

## Design

### 1. Detecting a held size change (pure, Core)

- **A new `Core.Output.SourceSizeWatch`**, pure and QPC-based, so the default tests drive it with synthetic stamps.
  - `Observe(qpc, contentWidth, contentHeight, chainOutput, codec, recording)` returns the size to move to, or null.
  - It computes the target with 12.1's `SourceOutputSize.For`.
- **The candidate:**
  - A frame whose target is null (content under 640×360: a minimised or launcher window) is ignored. It neither starts nor resets a candidate, so a brief minimise and restore at the same size changes nothing.
  - A target equal to the chain's output size clears any candidate.
  - A target different from the chain's starts a candidate, with its first QPC, or keeps the candidate when it is the same target. A different target restarts it.
- **Firing:** the candidate fires once it has held **3 s** of frame time (from its first frame's QPC to the current frame's).
  - While `recording` is true it doesn't fire. It is kept, so it fires at the first frame after the Recording ends, if still held.
  - After firing, the watch waits until the chain's output size is the target, and doesn't fire again for the same target meanwhile.

### 2. The engine-initiated move (Engine, AD-7's build-ahead switch without a `ChainKey` change)

- **Where it runs:** in `CaptureSession.OnFrame`, under the gate, for a game session with a Replay Buffer (`HasRing`) and `matchSource` on. The watch is fed each frame with the current chain's output size, the codec, and whether a Recording is attached (`chain.Tap.HasWriter`).
- **On a fire, `MoveToSourceSize(target)`:**
  - **Build ahead:** under the chain lock, `_movingTo = target`, and the build starts through the existing `StartBuildLocked` slot. Its builder sizes the new chain with `SizedToSource` using `_movingTo`; the spec, and so the `ChainKey`, is the current one.
  - **Refused, waiting for the next fire,** when a switch is already armed or retiring, a restart or recovery runs, a build is in flight, or the device is lost. A `PrepareChain` request for a new spec drops a pending move: the profile's chain is built at the current target size instead.
  - **Arm without a save:** when the build is ready, a new internal `ArmMove` does what `TryArm` does at a press, but for no save.
    - It arms at `now` under the gate, and its audio starts at the arm QPC.
    - It asks the old chain's encoder for a keyframe at the arm, so the old ring ends on a clean boundary, and sets the old chain's `SwitchEndUs` to its last frame there.
    - The first frame after that makes the new chain current (`SwitchTo`, unchanged). The old chain retires at once, since no save is bound to it, and `CheckGap` raises `reset-gap` when the gap is over a frame (AD-26).
  - **The next Clip starts at the move:** Clips cut from one chain's ring (AD-8, AD-10), so a save after the move holds only post-move content. A save during the build cuts from the old chain as today.
  - `_sourceSize` becomes the target once the switch happens.
- **Recording hold (R12):**
  - While a game Recording is attached, the watch doesn't fire, and the content is letterboxed into the chain's fixed size by `FrameScaler` (today's behaviour). One file, one size.
  - The move fires at the first frame after the Recording ends, if the new size still holds; the 3 s have usually already passed.
  - The engine never arms a move while a Recording is attached: `ArmMove` re-checks `Tap.HasWriter` under the lock, as `TryArm` does.
  - Known edge, shared with today's save-press switch: a Record press inside the ≤ 1-frame armed window is refused by `Record`'s existing "switch armed" rule. The plan keeps that rule; it isn't new.
- **Budget (AD-7):** the move uses the same prepared slot as a Reset switch, so at most two chains live at once, the old one retiring.
- **The Monitor Recording beside a game:** when a Monitor session holds the third-chain slot, a Reset switch tears the old chain down first (`SwitchChain`). The move goes the same way, through `SwitchChain`'s path with the current spec and the new size, and raises `reset-gap` as that path does.
- **GPU reset (8.3):** the recovery rebuilds through `SizedToSource` with `_sourceSize`, which is the current chain's source size, so it rebuilds at the current size. A held candidate survives the reset; the watch then sees the new chain's size and fires if the content still differs.
- **File mode** (no Replay Buffer: `debug-capture`) never moves. The file letterboxes: AD-26, a file never changes size.

### 3. One-screen Monitor Recording at source size (Engine, AD-25 / AD-26)

- `CaptureSession.PlanCanvas` (the internal static one from 11.3) gains the one-screen case: with `matchSource` on, one placement is planned through `MonitorCanvasRule.Plan`, so a screen over 4096 px under H.264 switches to HEVC, else AV1.
  - The snapshot's video codec and size are replaced as for several screens, and the capture stays on the single-screen path (no composite).
  - With `matchSource` off, one screen is unchanged.
- **`Open` plans the one screen** for both sources:
  - the resolved one (`Screens` with one entry);
  - the legacy `monitorRecordingMonitor` (`Screens` null): the monitor from `MonitorFinder.Find`, placed by `MonitorCanvas.Place([name])` (its physical mode).
- Several screens are unchanged (AD-25's canvas is already full size). The size is fixed per file.

## Tests

- **Default (lean, fakes, no GPU):**
  - `Core.Tests/SourceSizeWatchTests`:
    - a change held 3 s fires with the new size;
    - a change held under 3 s, a change to under 640×360, and a minimise and restore at the old size don't fire;
    - with `recording` true it doesn't fire, and it fires at the first frame after the Recording ends.
  - `Engine.Tests/CanvasPlanTests` (11.3's class): one placement of a fake 3840×2160 mode with `matchSource` on plans 3840×2160 on H.264 (it fits 4096); a fake 5120×1440 one plans HEVC at full size; with `matchSource` off, one screen keeps the snapshot.
  - The move's arming (`ArmMove`) needs a built encoder chain, so its default coverage is the watch's decision. The switch itself is the Heavy test's (below), as the 4.9 switch is.
- **Heavy, explicit, at night, one project, each taking `.bench-running` itself:**
  - `ARealWindowResizedTo2560By1440MovesTheReplayBufferAndTheNextClipHasIt`: Replay mode with `matchSource` on; a borderless window at 1280×720, resized by its script to 2560×1440. After 4 s, a save gives a 2560×1440 Clip that decodes cleanly. One move is logged, and `reset-gap` is raised at most once.
  - `AResizeDuringAGameRecordingKeepsItsSizeAndMovesAfterItStops`: a Recording started at 1280×720 and resized mid-way stays one 1280×720 file. After the stop and 4 s, a save gives a 2560×1440 Clip.
  - `AOneScreenMonitorRecordingOfThe3840By2160ScreenRecordsAtThatSize`: `monitorRecordingScreens` selects DISPLAY2 with `matchSource` on, giving a 3840×2160 H.264 file that decodes cleanly.
- **Cost (night, under `.bench-running`, 2 passes × 45 s):**
  - `bench fps --conditions baseline,capture,capture-ms`, where a new `-ms` suffix of `measure-path-a.ps1` adds `--match-source` to the game engine's `debug-capture`.
  - On the 2560×1440 synthetic load, that is the Replay Buffer at 2560×1440 source size against the 1080p `capture` baseline and NFR-1.
  - The result goes into epic 12's Notes.

## Out of scope

- The page and labels (R5, a later story).
- A size move for the Monitor session: its size is fixed per file (AD-25).
- A codec switch for games (AD-26).

## Implementation notes (approved 2026-11-05; built)

- **`SourceSizeWatch` is simpler than planned.** A fire clears the candidate, and the session doesn't observe while a move is in flight (`_movingTo`). So a move that fails (a build failure, a lost device, a prepared chain dropped) just needs the size to hold another 3 s, and there's no "wait for the chain" state to get stuck.
- **The budget, settled in code:** a new `ICaptureSession.SetBuildAhead(bool)` (a default method). The orchestrator turns it off while a Monitor Recording runs: when the Monitor session starts, and for a game session started beside one. It turns it back on when the Monitor session ends.
  - With it off, the move goes through `Restart(old, …, old.Spec)`, `SwitchChain`'s tear-down-first machinery, at the new size. With it on, it builds ahead into the prepared slot and `ArmMove` arms it.
- **`PrepareChain` and a move:**
  - the same settings leave a move alone;
  - null (a Monitor Recording taking the other chain) drops it;
  - a profile's new spec is built at the move's size and switches at a save, as today.
- A move is refused while a profile's chain is already prepared; it then happens after that chain's save switch.
- **The move's default test:** a fake chain doesn't make it cheap. `StartMove` → `Build` → `ArmMove` needs a built encoder chain (a D3D11 device and FFmpeg), so the switch is covered by the Heavy tests, and the default tests cover the decision (`SourceSizeWatchTests`). The coordinator's note 1 allowed this.

## Verification

- `dotnet build ReplayBuffer.slnx -c Release`: 0 warnings.
- Default suites, 0 failed: Contracts 137, Core 173, Media 102, Platform.Capture 8, Engine 202, App 197.
- `pwsh tools/publish-engine.ps1`: clean, 12.1 MB.
- New default tests:
  - `Core.Tests/SourceSizeWatchTests` (3): held 3 s; a short change, a tiny window, and a minimise and restore; a Recording holding the move;
  - `Engine.Tests/CanvasPlanTests.UnderMatchSourceOneScreenRecordsAtItsModeSizeSwitchingCodecOnlyOver4096`.

## Night runs (pending; one at a time, quiet PC)

1. **Heavy** (each takes `.bench-running` itself):

   `dotnet test --project tests/ReplayBuffer.Engine.Tests -c Release --no-build --explicit only --filter-method "*ARealWindowResizedTo2560By1440MovesTheReplayBufferAndTheNextClipHasIt" --filter-method "*AResizeDuringAGameRecordingKeepsItsSizeAndMovesAfterItStops" --filter-method "*AOneScreenMonitorRecordingOfThe3840By2160ScreenRecordsAtThatSize"`

2. **Cost**, under a hand-made `.bench-running` (bench fps doesn't take it), on the 2560×1440 synthetic load, with an overlap log:

   `bench\ReplayBuffer.Bench\bin\Release\net10.0-windows10.0.26100.0\ReplayBuffer.Bench.exe fps --repo <worktree> --presentmon %USERPROFILE%\Documents\repos\ReplayBuffer\native\presentmon\PresentMon-2.6.0-x64.exe --seconds 45 --passes 2 --settle 10 --conditions baseline,capture,capture-ms`

   Then record the FPS loss of `capture` (1080p) and `capture-ms` (2560×1440 source) against NFR-1 in epic 12's Notes.
