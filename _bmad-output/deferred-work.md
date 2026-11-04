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
  outcome: Recorded, open (story 3.7). Owner changed from story 3.5 to epics 4 (Replay Buffer saves) and 7 (recordings), which retire `OutputNaming`. Story 7.10 moved it to epic 6's sweep (entry below).

- source_plan: `_bmad-output/initiative-replay-buffer/tickets/epic-game-detection-profiles/tickets/story-refactor-sweep-plan.md`
  summary: Epic 3 (3.1–3.6, 3.8) deferred nothing beyond the two items above; of its plans only 3.5's `deferred:` is non-empty.
  evidence: Story 3.7 closed the duplicates epic 3 left instead: the `Canonicalize` failure filter and AD-18's listed-path match (`GamePaths.CanonicalizeFailed`, `GamePaths.CanonicalOrLowercase`), GameMonitor's exit and Ignored hand-over (`GameMonitor.HandOver`), the settings conflict-retry loop (`SettingsRetry.Update`), the allocator's QPC-to-local time (`OutputPathAllocator.LocalTimeOf`) and `"Game"` fallback (`PathNames.GameFallback`), and the Game Profiles page's flag setters (`GameProfileItem.SendFlag`).
  outcome: Closed by story 3.7.

- source_plan: `_bmad-output/initiative-replay-buffer/tickets/epic-replay-buffer-clips/tickets/story-failed-saves-retry-and-the-file-being-written-signal-plan.md`
  summary: A Clip still writing when the engine shuts down is not published, and its write gets `BeginWrite` but never `EndWrite`.
  evidence: Deferred from 4.1 (the writer posts ClipWritten to an orchestrator that may already be disposed; nothing joins it) and 4.5 (Dispose completes the channel, so OnClipWritten, which calls EndWrite, never runs). Shutdown only; AD-17 has `.rbpart` ignored and deleted at start. The fix needs new dispose handling: end in-flight writes as Failed, or rebuild being-written state at start.
  outcome: Recorded, open (story 4.13). Owned by epic 7's LibraryActor. Story 7.10 moved it to epic 8's hardening (entry below).
- source_plan: `_bmad-output/initiative-replay-buffer/tickets/epic-replay-buffer-clips/tickets/story-disk-mode-ring-plan.md`
  summary: In Disk mode, while a long Clip writes, its pins hold trimmed ring blocks, so new slabs spill to native memory, silently.
  evidence: 4.7's review rejected it as the planned fallback (the disk stays within the estimate; RAM grows only during the write) and left pin headroom to stories 4.8 and 4.10, which did not add it. Nothing tells the user RAM is in use in Disk mode.
  outcome: Recorded, open (story 4.13). Owned by epic 8's hardening.
- source_plan: `_bmad-output/initiative-replay-buffer/tickets/epic-replay-buffer-clips/tickets/story-memory-mode-ram-margin-plan.md`
  summary: The `memory-margin` warnings aren't withdrawn when the margin is left; they go only by `warning.dismiss`, like `device-dropout`.
  evidence: 4.8's Implementation Notes. Withdrawing them changes the warnings' lifecycle, which belongs with how the status line shows them.
  outcome: Recorded, open (story 4.13). Owned by epic 5's status line.
- source_plan: `_bmad-output/initiative-replay-buffer/tickets/epic-replay-buffer-clips/tickets/story-hotkeys-page-in-the-main-app-plan.md`
  summary: Capturing a combination an active Hotkey holds on the Hotkeys page fires that Hotkey's action instead of being captured.
  evidence: 4.12's Implementation Notes: a combination the engine (or another app) holds reaches the engine, not the page. A fix needs the engine to pause its Hotkeys during capture, an engine change.
  outcome: Recorded, open (story 4.13). Owned by epic 8's hardening.
- source_plan: `_bmad-output/initiative-replay-buffer/tickets/epic-replay-buffer-clips/tickets/story-per-game-profile-settings-at-the-next-reset-plan.md`
  summary: At a chain switch the audio seam between the two Clips (up to about 10.7 ms of gap or overlap) is unmeasured, and a Record press while a switch is armed starts no Recording.
  evidence: 4.9's residuals: the new chain's AAC grid starts at the press QPC and the old one's at the anchor, and the Heavy test checks only the video seam; `Record` returns null while armed, which with a static window lasts until the next captured frame.
  outcome: Recorded, open (story 4.13). The Record press is owned by epic 6 (Recordings); the audio seam by epic 8's hardening.
- source_plan: `_bmad-output/initiative-replay-buffer/tickets/epic-replay-buffer-clips/tickets/story-refactor-sweep-plan.md`
  summary: Epic 4 (4.1–4.12) left three items for its sweep: the unused encoder-restart path, the stale ring-file match and a GPU test in the default suite.
  evidence: 4.6's `RingGenerations.Restart`, `ReplayRingSink.Restart` and the previous-generation fallback in `Cut` had no caller after 4.9 moved to a ring sink per chain; `RingFile.RemoveStale` matched every `ring-*.bin` (4.7 QA); `DiskRingClipTests` ran an NVENC encode by default (4.7 QA).
  outcome: Closed by story 4.13: the restart path is removed, `RemoveStale` deletes only `ring-<digits>.bin`, and `DiskRingClipTests` is Heavy/Explicit.

- source_plan: `_bmad-output/initiative-replay-buffer/tickets/epic-replay-buffer-clips/tickets/story-memory-mode-ram-margin-plan.md`
  summary: The `memory-margin` warnings still aren't withdrawn when the margin is left; the item named epic 5's status line as its owner.
  evidence: Story 5.10's status line shows the warnings as the snapshot carries them, and dismisses them by `warning.dismiss`; withdrawing them changes the warnings' lifecycle in the engine, which a behaviour-preserving sweep can't do.
  outcome: Recorded, open (story 5.13). Owner changed from epic 5's status line to epic 8's hardening.
- source_plan: `_bmad-output/initiative-replay-buffer/tickets/epic-overlay-feedback/tickets/story-refactor-sweep-plan.md`
  summary: FeedbackRouter repeats its disposed guard, names sounds as strings and the `"replay"` source inline; the feedback tests carry two fake notice sinks, and the serve tests no shared harness.
  evidence: Found by story 5.13's sweep. `FeedbackRouter.cs`, `FeedbackRouterTests.cs`, `FeedbackSoundsTests.cs`, `DevSimulateServeTests.cs` and `FeedbackSoundServeTests.cs` are being changed by story 6.8, so the sweep left them.
  outcome: Recorded, open (story 5.13). Owned by epic 6's sweep, after 6.8.
- source_plan: `_bmad-output/initiative-replay-buffer/tickets/epic-overlay-feedback/tickets/story-refactor-sweep-plan.md`
  summary: `FeedbackSounds`' per-sound `IsOn` and `With` are written twice, in the App's `FeedbackSoundsPage` and Core's `FeedbackSoundPlayer`.
  evidence: Found by story 5.13's sweep. One shared copy belongs on `FeedbackSounds` in Contracts, which a sweep doesn't change (AD-22).
  outcome: Recorded, open (story 5.13). Owned by epic 6's sweep.
- source_plan: `_bmad-output/initiative-replay-buffer/tickets/epic-overlay-feedback/tickets/story-refactor-sweep-plan.md`
  summary: Bench repeats the pipe-server P/Invoke, the Overlay's style mask, the dot-mode names and the load-option parsing.
  evidence: Found by story 5.13's sweep. They sit in `AppOverlayHost.cs`, `BenchOptions.cs` and `FpsBenchmark.cs`, which story 8.1 and the capture-cost investigation are changing, and the copies' timeout rules differ, so merging them isn't behaviour-preserving.
  outcome: Recorded, open (story 5.13). Owned by epic 8's sweep.
- source_plan: `_bmad-output/initiative-replay-buffer/tickets/epic-overlay-feedback/tickets/story-refactor-sweep-plan.md`
  summary: `ManualTime` is copied across test projects, and the Explicit screen-copy helpers are duplicated.
  evidence: Found by story 5.13's sweep. Sharing them across projects needs a shared test assembly, for little gain.
  outcome: Recorded, open (story 5.13). Low value. Owned by epic 8's sweep.
- source_plan: `_bmad-output/initiative-replay-buffer/tickets/epic-overlay-feedback/tickets/story-refactor-sweep-plan.md`
  summary: Epic 5 (5.1–5.12, 5.15) deferred no review findings, but left small duplicates and stale docs for its sweep.
  evidence: Bench re-declared `GetForegroundWindow`; Platform.Desktop's `NativeMethods.txt` listed an unused `DXGI_ERROR_NOT_FOUND`; the App wrote the "connected or nothing" rule four times and `Monitors` its monitor lookup twice; the General page hard-coded the 3 s dot window; the Overlay's and `Monitors`' docs predated 5.12 and 5.15; `OverlayWindow`'s layout constants were public and `Place` re-picked `Update`'s size; App tests copied `Pump`; and the status-line tests sat in the serial WPF collection for one Explicit test.
  outcome: Closed by story 5.13: Bench uses `OverlayWindowStyle.ForegroundWindow`, `OverlayHostProcess` folds `ShowDot`/`HideDot` into `On`/`Off`, `EngineSessions.ConnectedState` holds the rule, `Monitors.MonitorOf` the lookup, the OnChange label is built from `OverlayViewModel.DotOnChangeDuration`, the docs credit 5.8's and 5.12's measurements, `OverlayWindow.ExtentDip` picks the size, `Sta.Pump` is shared, and the PNG test moved to `StatusLineRenderTests`.

- source_plan: `_bmad-output/initiative-replay-buffer/tickets/epic-replay-buffer-clips/tickets/story-failed-saves-retry-and-the-file-being-written-signal-plan.md`
  summary: A Clip or Recording still writing when the engine shuts down gets `BeginWrite` but never `EndWrite`; the item named epic 7's LibraryActor as its owner.
  evidence: The LibraryActor never persists an item being written and deletes the `.rbpart` at the next start, so nothing stale survives a restart. Ending in-flight writes changes the orchestrator's and the actor's dispose behaviour, which a behaviour-preserving sweep can't do.
  outcome: Recorded, open (story 7.10). Owner changed from epic 7 to epic 8's hardening (crash recovery).
- source_plan: `_bmad-output/initiative-replay-buffer/tickets/epic-engine-baseline/tickets/story-sessionorchestrator-and-the-capture-session-api-plan.md`
  summary: Serve still names a session's output with `OutputNaming.Allocate`; story 3.7 named epics 4 and 7 as its owners.
  evidence: Epic 7 writes no capture files: it indexes what the writers produce. Recordings are epic 6's, so retiring `OutputNaming` belongs there.
  outcome: Recorded, open (story 7.10). Owner changed from epic 7 to epic 6's sweep.
- source_plan: `_bmad-output/initiative-replay-buffer/tickets/epic-library-cleanup/tickets/story-length-and-thumbnails-plan.md`
  summary: The length worker's read of a file may hold it open while the App renames or deletes it (story 7.3's deferred review finding).
  evidence: Mitigated by story 7.7: `LibraryActor.Retried` tries a sharing or lock violation again up to 10 times 50 ms apart (about 500 ms), which covers a short read. A read held longer than that (a slow or network drive) is unverified.
  outcome: Recorded, open (story 7.10). Unverified. Owned by epic 8's hardening.
- source_plan: `_bmad-output/initiative-replay-buffer/tickets/epic-library-cleanup/tickets/story-length-and-thumbnails-plan.md`
  summary: The App's thumbnail cache is never pruned, and a shell "no thumbnail" is not retried until the item's path or size changes (story 7.3 QA).
  evidence: `LibraryThumbnails` caches in memory by absolute path and size (about 150 KB each), so a moved or deleted item's entry stays, and an outside rewrite at the same size keeps a stale thumbnail. Low cost for a personal Library.
  outcome: Recorded, open (story 7.10). Low. Owned by epic 8's sweep.
- source_plan: `_bmad-output/initiative-replay-buffer/tickets/epic-library-cleanup/tickets/story-cleanup-plan.md`
  summary: OneDrive placeholders (cloud-only files) aren't listed in the Library (story 7.8 QA note 2).
  evidence: The scan skips every reparse point (`AttributesToSkip = ReparsePoint`) so Cleanup never reaches through a junction or link, and a cloud placeholder is one, so an Output Folder inside OneDrive may show fewer items. Listing them safely needs a reparse-tag check.
  outcome: Recorded, open (story 7.10). Checked in story 7.11.
- source_plan: `_bmad-output/initiative-replay-buffer/tickets/epic-library-cleanup/tickets/story-cleanup-plan.md`
  summary: `CleanupPlanner` doesn't count an item that isn't inside the Output Folder as skipped (story 7.8 QA note 3).
  evidence: The planner passes over such an item without counting it, so in that stale case (a folder swapped for a junction after the scan) `cleanup-limit-unreachable`'s message could blame Favourites. Counting it changes the warning, which a behaviour-preserving sweep can't do.
  outcome: Recorded, open (story 7.10). Changes the warning, so owned by epic 8's hardening.
- source_plan: `_bmad-output/initiative-replay-buffer/tickets/epic-library-cleanup/tickets/story-library-and-cleanup-settings-page-plan.md`
  summary: The Output Folder picker doesn't refuse a junction or symbolic link (story 7.9 QA note 1).
  evidence: The engine's guards cover it: the Output Folder itself may be a junction, `ThroughReparsePoint` checks every folder below it, and the `finalPath` port refuses a delete, rename or Cleanup whose final path leaves the folder's own final path.
  outcome: Recorded, open (story 7.10). Owned by epic 8's hardening.
- source_plan: `_bmad-output/initiative-replay-buffer/tickets/epic-library-cleanup/tickets/story-refactor-sweep-plan.md`
  summary: The `Mp4`/`Box` test helpers are copied across the Core and Engine test projects, and the binding-error trace block across the App test classes.
  evidence: Found by story 7.10's sweep. Sharing them across projects needs a shared test assembly, for little gain.
  outcome: Recorded, open (story 7.10). Low. Owned by epic 8's sweep.
- source_plan: `_bmad-output/initiative-replay-buffer/tickets/epic-library-cleanup/tickets/story-refactor-sweep-plan.md`
  summary: Epic 7 (7.1–7.9) left small duplicates, dead members, over-public members and stale docs, and no test of the final-path guard on rename, delete and Cleanup (story 7.8 QA note 1).
  evidence: Story 7.10's sweep. Behaviour, IPC, events, `library.json`, log and UI text are unchanged.
  outcome: Closed by story 7.10: `LibraryActor` shares its request posting (`Post<T>`), its registration-timer disposal, its rescan-and-clean, its Cleanup timer callback, its changed-on-disk log line and one limits read in `EndCleanup`; the unused `ThreadId` and the test-only `Move` path are removed and `ValidName` is private; the class, `Reconcile`, `ILibraryWrites`/`NoLibraryWrites` and `LibraryItem` docs are current. The App shares its rename/delete send (`LibraryPage.SendActionAsync`), its Yes/No prompt (`Prompts.YesNo`), the day format and the folder-change note, and narrows `LibraryPage`, `LibraryThumbnails.Size` and `LibraryViewState.Default`. The tests drop the test-only Move test, share `Modified` and `Box`, and add `AFileWhoseFinalPathIsOutsideIsNeitherPreviewedCleanedRenamedNorDeleted`, which closes 7.8 QA note 1.

- source_plan: `_bmad-output/initiative-replay-buffer/tickets/epic-engine-baseline/tickets/story-sessionorchestrator-and-the-capture-session-api-plan.md`
  summary: Serve still names a session's output with `OutputNaming.Allocate`; story 7.10 named epic 6's sweep as its owner.
  evidence: Story 6.12's sweep. Every Clip and Recording now gets its path from `OutputPathAllocator` (AD-21). `CaptureSession.Open` uses `OutputNaming` only for a session without a Replay Buffer (`serve --debug-capture` and test instances), and the tracer's `debug-capture` command uses it for its debug files. Moving those to the allocator would rename debug files, which a behaviour-preserving sweep can't do, and nothing user-facing writes through it.
  outcome: Closed by story 6.12: kept as the debug-capture namer, and its doc now says where it is used.
- source_plan: `_bmad-output/initiative-replay-buffer/tickets/epic-overlay-feedback/tickets/story-refactor-sweep-plan.md`
  summary: FeedbackRouter repeats its disposed guard and names sounds and the `"replay"` source inline; the feedback tests carry two fake notice sinks (story 5.13).
  evidence: Story 6.12's sweep, after 6.8 and 6.9 finished changing the router.
  outcome: Closed by story 6.12: one `UnlessDisposed` guard, `FeedbackSoundNames` holds the sound names (shared with `FeedbackSoundPlayer.Parse`), a `Replay` source constant, one shared `FakeFeedbackNotices`, and the router tests use the shared `ManualTime`.
- source_plan: `_bmad-output/initiative-replay-buffer/tickets/epic-overlay-feedback/tickets/story-refactor-sweep-plan.md`
  summary: `FeedbackSounds`' per-sound `IsOn` and `With` were written twice, in the App's `FeedbackSoundsPage` and Core's `FeedbackSoundPlayer` (story 5.13).
  evidence: Story 6.12's sweep. Adding methods to the Contracts record is additive under AD-22: the JSON shape is unchanged.
  outcome: Closed by story 6.12: `FeedbackSounds.IsOn` and `FeedbackSounds.With` in Contracts; the page and the player call them.
- source_plan: `_bmad-output/initiative-replay-buffer/tickets/epic-overlay-feedback/tickets/story-refactor-sweep-plan.md`
  summary: The engine's serve tests have no shared harness: each class repeats its temp folder, test `Instance` and disposal list (story 5.13).
  evidence: Story 6.12's sweep. About a dozen serve test classes share the pattern, most outside epic 6 (Library, Hotkeys, Meter, game detection), and a shared base touches all of them for little gain.
  outcome: Recorded, open (story 6.12). Low. Owned by epic 8's sweep.
- source_plan: `_bmad-output/initiative-replay-buffer/tickets/epic-replay-buffer-clips/tickets/story-per-game-profile-settings-at-the-next-reset-plan.md`
  summary: A Record press while a chain switch is armed starts no Recording (story 4.9's residual, owned by epic 6).
  evidence: `SessionOrchestrator.BeginRecording` gets null from `Session.Record` while a switch is armed and reports "can't record yet"; with a static window that lasts until the next captured frame. Starting the Recording on the new chain changes the press's behaviour, which a sweep can't do.
  outcome: Recorded, open (story 6.12). Owner changed from epic 6 to epic 8's hardening.
- source_plan: `_bmad-output/initiative-replay-buffer/tickets/epic-recordings/tickets/story-monitor-recording-by-its-own-hotkey-plan.md`
  summary: The Monitor session opens its own D3D11 device on its monitor's adapter, where AD-7 says the engine uses one device (6.9's deferred review finding).
  evidence: `CaptureSession.Open` calls `D3D11Device.CreateForMonitor` for a Monitor session; sharing the game session's device is a larger change, chosen in 6.9's Design Notes pending the builder's review.
  outcome: Recorded, open (story 6.12). Awaiting the builder's review; an AD-7 spine update or a shared device belongs to epic 8.
- source_plan: `_bmad-output/initiative-replay-buffer/tickets/epic-recordings/tickets/story-desktop-duplication-fallback-for-monitor-recording-plan.md`
  summary: 6.10's QA notes: `DesktopDuplicationCapture.Failed`'s doc said capture continues though its thread ends; a start while the secure desktop is up fails the session instead of retrying.
  evidence: The constructor makes one `DuplicateOutput` call. Retrying it changes the fallback's behaviour; the secure desktop is story 8.4's.
  outcome: Doc closed by story 6.12 (`Failed` now names both cases). The start-on-the-secure-desktop retry is recorded, open, owned by story 8.4.
- source_plan: `_bmad-output/initiative-replay-buffer/tickets/epic-recordings/tickets/story-refactor-sweep-plan.md`
  summary: Epic 6 (6.1–6.11) left duplicated Recording write handling in the orchestrator and stale docs.
  evidence: Story 6.12's sweep. Behaviour, IPC, events, log and UI text are unchanged. 6.7's Disk-mode restart margin is in the Decisions Awaiting Review.
  outcome: Closed by story 6.12: the game and Monitor Recordings share `SessionOrchestrator.OpenWrite` (begin, start, undo on a failed start) and `CloseWrite` (end the write, report, pick the reason); `MonitorFinder`'s and `OutputNaming`'s docs cover 6.11 and the debug path.
- source_plan: `_bmad-output/initiative-replay-buffer/tickets/epic-modern-ui/tickets/story-refactor-sweep-plan.md`
  summary: Epic 9 (9.1–9.7) left a duplicated area-dictionary placement helper, an unused `ThemeService.Shared`, Gaming's `WindowBackground` and `WindowForeground` repeating palette hex, a hard-coded `Transparent` on the nav list, and stale "tab" and `MessageBox` names.
  evidence: Story 9.8's sweep. No look or behaviour change: every App render the tests write is byte-identical to develop's except the shell's status clock.
  outcome: Closed by story 9.8.
- source_plan: `_bmad-output/initiative-replay-buffer/tickets/epic-modern-ui/tickets/story-refactor-sweep-plan.md`
  summary: The committed `ux-modern-ui/renders/shell-fluent.png` and `shell-gaming-dark.png` (9.3) show the General page before 9.5 restyled it; the tests now render the carded page.
  evidence: Story 9.8's comparison. Develop's tip and the sweep render the same shell (104 px differ, the status clock's seconds only).
  outcome: Closed at 9.8's merge: both shell renders were replaced with the tests' current output.
- source_plan: `_bmad-output/initiative-replay-buffer/tickets/epic-modern-ui/tickets/story-refactor-sweep-plan.md`
  summary: Library and Cleanup's blocked message still says "acknowledged on the Library tab"; the Library is a nav page since 9.3.
  evidence: `LibrarySettingsPage.cs:69`. The epic's Boundaries keep every page's wording, so a sweep doesn't change it.
  outcome: Recorded, open (story 9.8). Low. Awaiting the builder's review (a one-word wording change).
