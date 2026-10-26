- source_plan: `_bmad-output/initiative-replay-buffer/tickets/epic-engine-baseline/tickets/spike-nativeaot-proof-for-the-engine-plan.md`
  summary: The engine publish folder carries ffmpeg.exe, ffplay.exe and every project's PDB; story 1.13's install layout should ship only the exe, the FFmpeg DLLs and what it needs.
  evidence: Directory.Build.targets copies $(FFmpegNativeDir)*.exe beside the output, and tools/publish-engine.ps1 publishes everything, so publish/engine holds the GPL ffmpeg/ffplay CLIs and the PDBs. This predates spike 1.3, and the layout is story 1.13's.
- source_plan: `_bmad-output/initiative-replay-buffer/tickets/epic-engine-baseline/tickets/story-sessionorchestrator-and-the-capture-session-api-plan.md`
  summary: A session's output path in serve comes from `OutputNaming.Allocate` with a `File.Exists` probe, not AD-21's Core `OutputPathAllocator` with `CREATE_NEW`.
  evidence: The behaviour is inherited from the tracer (`CaptureSession.Open`). It's latent, because nothing starts a session in serve until epic 3; the first story that writes files from serve should switch to the allocator.
