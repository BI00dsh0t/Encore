- source_plan: `_bmad-output/initiative-replay-buffer/tickets/epic-engine-baseline/tickets/spike-nativeaot-proof-for-the-engine-plan.md`
  summary: The engine publish folder carries ffmpeg.exe, ffplay.exe and every project's PDB; story 1.13's install layout should ship only the exe, the FFmpeg DLLs and what it needs.
  evidence: Directory.Build.targets copies $(FFmpegNativeDir)*.exe beside the output, and tools/publish-engine.ps1 publishes everything, so publish/engine holds the GPL ffmpeg/ffplay CLIs and the PDBs. This predates spike 1.3, and the layout is story 1.13's.
- source_plan: `_bmad-output/initiative-replay-buffer/tickets/epic-engine-baseline/tickets/story-sessionorchestrator-and-the-capture-session-api-plan.md`
  summary: A session's output path in serve comes from `OutputNaming.Allocate` with a `File.Exists` probe, not AD-21's Core `OutputPathAllocator` with `CREATE_NEW`.
  evidence: The behaviour is inherited from the tracer (`CaptureSession.Open`). It's latent, because nothing starts a session in serve until epic 3; the first story that writes files from serve should switch to the allocator.
- source_plan: `_bmad-output/initiative-replay-buffer/tickets/epic-engine-baseline/tickets/story-output-settings-and-encoder-probing-plan.md`
  summary: The encoder probe feeds each candidate BGRA D3D11 frames, while its test oracle (`ffmpeg.exe` on a system-memory nullsrc) lets FFmpeg convert to the encoder's format, so an encoder that takes NV12 but not BGRA D3D11 input (h264_qsv is the likely one) may be offered by the oracle and refused by the probe.
  evidence: Unverified (maybe-false, medium if true): the builder's PC has only NVENC, which accepts both. Settle it by running `EncoderProbeTests` on an Intel-iGPU or AMD PC; if they disagree, probe QSV/AMF with NV12 frames (the X2 colour path) or convert before the encoder.

- source_plan: `_bmad-output/initiative-replay-buffer/tickets/epic-engine-baseline/tickets/story-audiodevicehub-port-with-qpc-stamping-and-adaptive-resamplin-plan.md`
  summary: A session's audio tracks drop hub blocks after the anchor until StartOutput has built the encoders, so each track opens with ~50 ms of silence fill.
  evidence: SessionAudioTrack drops blocks until Publish, which runs after the muxer, frame pool, scaler and encoders are created; the hub has delivered post-anchor audio since Open. Buffering those blocks (preallocated) and replaying them at Publish would keep that audio. The tracer had the same loss.
