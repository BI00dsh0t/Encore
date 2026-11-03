- source_plan: `_bmad-output/initiative-replay-buffer/tickets/epic-engine-baseline/tickets/spike-nativeaot-proof-for-the-engine-plan.md`
  summary: The engine publish folder carries ffmpeg.exe, ffplay.exe and every project's PDB; story 1.13's install layout should ship only the exe, the FFmpeg DLLs and what it needs.
  evidence: Directory.Build.targets copies $(FFmpegNativeDir)*.exe beside the output, and tools/publish-engine.ps1 publishes everything, so publish/engine holds the GPL ffmpeg/ffplay CLIs and the PDBs. This predates spike 1.3, and the layout is story 1.13's.
  outcome: Closed by story 1.13 (recorded in story 1.15). `tools/publish.ps1` composes `publish/ReplayBuffer/` with only the two exes and the FFmpeg DLLs, and fails on any other exe or a PDB; `publish/engine/` keeps the CLIs for the tests.
- source_plan: `_bmad-output/initiative-replay-buffer/tickets/epic-engine-baseline/tickets/story-sessionorchestrator-and-the-capture-session-api-plan.md`
  summary: A session's output path in serve comes from `OutputNaming.Allocate` with a `File.Exists` probe, not AD-21's Core `OutputPathAllocator` with `CREATE_NEW`.
  evidence: The behaviour is inherited from the tracer (`CaptureSession.Open`). It's latent, because nothing starts a session in serve until epic 3; the first story that writes files from serve should switch to the allocator.
  outcome: Recorded, open (story 1.15). Owned by story 3.5 (`OutputPathAllocator`), the first story that writes files from serve. Story 3.7 moved it to epics 4 and 7 (entry below).
- source_plan: `_bmad-output/initiative-replay-buffer/tickets/epic-engine-baseline/tickets/story-output-settings-and-encoder-probing-plan.md`
  summary: The encoder probe feeds each candidate BGRA D3D11 frames, while its test oracle (`ffmpeg.exe` on a system-memory nullsrc) lets FFmpeg convert to the encoder's format, so an encoder that takes NV12 but not BGRA D3D11 input (h264_qsv is the likely one) may be offered by the oracle and refused by the probe.
  evidence: Unverified (maybe-false, medium if true): the builder's PC has only NVENC, which accepts both. Settle it by running `EncoderProbeTests` on an Intel-iGPU or AMD PC; if they disagree, probe QSV/AMF with NV12 frames (the X2 colour path) or convert before the encoder.
  outcome: Recorded, open (story 1.15). Needs an Intel or AMD GPU, which the builder's PC lacks; epic 8's hardening is the place for it.

- source_plan: `_bmad-output/initiative-replay-buffer/tickets/epic-engine-baseline/tickets/story-audiodevicehub-port-with-qpc-stamping-and-adaptive-resamplin-plan.md`
  summary: A session's audio tracks drop hub blocks after the anchor until StartOutput has built the encoders, so each track opens with ~50 ms of silence fill.
  evidence: SessionAudioTrack drops blocks until Publish, which runs after the muxer, frame pool, scaler and encoders are created; the hub has delivered post-anchor audio since Open. Buffering those blocks (preallocated) and replaying them at Publish would keep that audio. The tracer had the same loss.
  outcome: Recorded, open (story 1.15). Owned by epic 4's exact save cut (story 4.2); it changes `SessionAudioTrack`, which story 2.1 is reworking.

- source_plan: `_bmad-output/initiative-replay-buffer/tickets/epic-multi-track-audio/tickets/story-refactor-sweep-plan.md`
  summary: Epic 2 (2.1–2.9, 2.11) deferred no items; every one of its plans has an empty `deferred:`.
  evidence: Story 2.10 closed the duplicates epic 2 left instead: the device tap (`AudioTimelineTap`), the device-to-Track fan-out with mic-mute gating (`TrackWiring.DeviceSink`), the endpoint-and-flow match (`AudioEndpointMatch.Same`), the Mic-in-affected-Track walk (`SessionAudio.GatedMicInputs`, with `SessionAudio.FirstTrackOf`), and the unused `RuntimeStateStore.FilePath`.
  outcome: Closed by story 2.10.

- source_plan: `_bmad-output/initiative-replay-buffer/tickets/epic-game-detection-profiles/tickets/story-outputpathallocator-plan.md`
  summary: `SettingsStore.Open` throws a NullReferenceException for a Game Profile with no `overrides` property instead of treating the file as unreadable.
  evidence: `SettingsMaps.WithOrdinalKeys` (src/ReplayBuffer.Contracts/Settings/SettingsDocument.cs) dereferences `GameProfile.Overrides`, which deserializes as null when the property is absent. Pre-existing; the engine always writes `overrides`, so only a hand-edited file hits it. Fixing it changes behaviour, so story 3.7's sweep left it.
  outcome: Recorded, open (story 3.7). Owned by epic 8's hardening.

- source_plan: `_bmad-output/initiative-replay-buffer/tickets/epic-engine-baseline/tickets/story-sessionorchestrator-and-the-capture-session-api-plan.md`
  summary: Serve still names a session's output with `OutputNaming.Allocate` (a `File.Exists` probe), not AD-21's `OutputPathAllocator`; the item named story 3.5 as its owner.
  evidence: Story 3.5 added `OutputPathAllocator` and made `OutputNaming.Sanitize` delegate to `PathNames.Sanitize`, but left the rewiring of serve's output path to the epics that write recordings from serve.
  outcome: Recorded, open (story 3.7). Owner changed from story 3.5 to epics 4 (Replay Buffer saves) and 7 (recordings), which retire `OutputNaming`.

- source_plan: `_bmad-output/initiative-replay-buffer/tickets/epic-game-detection-profiles/tickets/story-refactor-sweep-plan.md`
  summary: Epic 3 (3.1–3.6, 3.8) deferred nothing beyond the two items above; of its plans only 3.5's `deferred:` is non-empty.
  evidence: Story 3.7 closed the duplicates epic 3 left instead: the `Canonicalize` failure filter and AD-18's listed-path match (`GamePaths.CanonicalizeFailed`, `GamePaths.CanonicalOrLowercase`), GameMonitor's exit and Ignored hand-over (`GameMonitor.HandOver`), the settings conflict-retry loop (`SettingsRetry.Update`), the allocator's QPC-to-local time (`OutputPathAllocator.LocalTimeOf`) and `"Game"` fallback (`PathNames.GameFallback`), and the Game Profiles page's flag setters (`GameProfileItem.SendFlag`).
  outcome: Closed by story 3.7.
