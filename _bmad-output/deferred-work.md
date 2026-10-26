- source_plan: `_bmad-output/initiative-replay-buffer/tickets/epic-engine-baseline/tickets/spike-nativeaot-proof-for-the-engine-plan.md`
  summary: The engine publish folder carries ffmpeg.exe, ffplay.exe and every project's PDB; story 1.13's install layout should ship only the exe, the FFmpeg DLLs and what it needs.
  evidence: Directory.Build.targets copies $(FFmpegNativeDir)*.exe beside the output, and tools/publish-engine.ps1 publishes everything, so publish/engine holds the GPL ffmpeg/ffplay CLIs and the PDBs. This predates spike 1.3, and the layout is story 1.13's.
