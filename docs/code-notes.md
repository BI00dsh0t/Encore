# Code notes

Comments in the code are one line each. This file holds the rules that need more room: which thread owns a class, what
must happen in what order, and the Windows or driver quirks behind a surprising line. There is one heading per class,
grouped by project.

## Encore.Platform.Desktop

### CaptureExclusion

- `ExcludeNewWindows` covers WPF windows, popups and native dialogs alike.
- Its hook is "out of context": events arrive only while the calling thread pumps messages, as the WPF UI thread does.
- A window may show for a moment before it is excluded.

### Monitors

- `OverlayPlanes` asks DXGI (`IDXGIOutput2::SupportsOverlays`) about the monitor holding the game's largest window.
- Why it matters: a topmost window over a borderless flip-model game kept the game in independent flip only when a
  hardware plane was free. Without one, DWM composed the game and about 7.5 % of the frame rate was lost.
- A later measurement showed the always-on dot without planes still left the game in independent flip.

### VideoSurface

- One BGRA texture has two views: Windows' media player copies each frame in through `Direct3DSurface`, and a WPF
  `D3DImage` shows it through `BackBuffer`. That is how the page's controls can draw over the video.
- It owns a D3D11 device and a D3D9Ex device on the App's window, made once. `Resize` remakes only the texture and its
  views, and releases the old views first.
- Every step checks its HRESULT and throws, so a caller sees a GPU failure and not a blank picture.
- `Dispose` releases every COM reference at once, with no wait for a garbage collection. The WinRT wrapper holds its own
  references (one native, and one per interface it was used through) even after `Close`, so each is released by hand.
- Create, use and dispose it on the UI thread.

## Encore.Platform.Windows

### GameConfigStore

- Each `HKCU\System\GameConfigStore\Children\*` key with `Type = 1` names a game by its `MatchedExeFullPath`.
- It is read-only and best effort: a missing key or a failed read keeps the last good set (empty at first) and is logged
  once per engine run.
- It is not thread-safe. Game Detection reads it from its own thread.

### GameMonitor

How a game is detected:

- Each poll classifies every new process that owns a top-level window, once: a limited open, its image path, then the
  canonical path. Nothing else is read from a process: no memory, module list, hook or GPU heuristic.
- A process is detected when its foreground window covers its monitor with no caption (the fullscreen rule), or when its
  path is listed (Windows' game list or the manual games), even when windowed.
- A detected game stays known, with its limited handle held, until its process exits. Minimizing it or closing its window
  does not end it.
- Excluded processes (the engine's and the attached App's) and anything under the install folder are never detected.
- The fullscreen rule alone is weak evidence: a browser or media player showing a video passes it. So the apps in
  `KnownNonGames` are only ever detected by a listing.
- A game whose Game Profile is Ignored is never detected. One that becomes Ignored while detected is forgotten as if it
  had exited, and clearing the flag lets it be detected again.

How a Target is proposed (the monitor only proposes; the orchestrator commits):

- With no Target: the detected game in front, else the best one. The reason is "cold start" on the first poll and
  "detected" after it.
- Another detected game is proposed once it has held the focus for `FocusToSwitch` without a break (reason "focus").
- A game found only by the fullscreen rule never takes the Target from a listed game by focus.
- When the Target exits, the best of the others is proposed, or none (reason "exit").
- "Best" is the most recently focused game, then the most recently started.
- Proposing the committed Target again while a switch is held drops that switch, because the Target is in front.

Constructor arguments worth knowing:

- `isExcluded` is checked on every poll, because the App may attach after its window was first classified.
- `target` gives the committed Target's process, so a Target whose switch is held is still handed over when it exits.
  When it is null, the last proposal counts as committed.
- `detected` is called before any proposal of that game; `exited` is called before the proposal that follows.

### GamePaths

- `Canonicalize` opens only the file's attributes, never its contents, and asks `GetFinalPathNameByHandle` for the
  normalized DOS path. `\\?\` is stripped and `\\?\UNC\` becomes `\\`. It throws `Win32Exception` when the file can't
  be opened.
- `ProductName` reads the version resource from the file on disk without loading it. It falls back to the file name
  without its extension; a file named only `.exe` keeps its extension, so a profile name is never empty.

### HotkeyThread

- One thread owns a message-only window. Every register and unregister runs on that thread, and every binding is
  registered with `MOD_NOREPEAT`.
- QPC is read first thing when `WM_HOTKEY` arrives, so the press time is not delayed by later work.
- `Apply` unregisters the old bindings first, so a combination that moves between actions is free when it is registered
  again. It throws `ObjectDisposedException` once the thread has stopped.

### SuspendResumeNotifications

- It uses `RegisterSuspendResumeNotification` with `DEVICE_NOTIFY_CALLBACK`, which is safe under NativeAOT: a blittable
  struct, an unmanaged callback and a `GCHandle` for context.
- Each change is logged at Information. A failed registration is logged at Warning and gives a no-op subscription.

### RecycleBin

- A volume that is not a fixed drive with a Recycle Bin is refused up front.
- A progress sink aborts any delete the shell could not recycle (the Recycle Bin is off, or the file is over its quota).
  Without that, the shell would delete the file permanently.
- `Recycle` throws an `IOException` whose `HResult` is the shell's own when the operation failed.

### WindowFinder

- `NotCapturable` requires the window to exist, be visible, not be minimized or cloaked, and have a client area of at
  least the given size. A game that is still starting can show a tiny placeholder window, and WGC never delivers a frame
  from one.
- `CaptureWindowOf` prefers the given window; otherwise it takes the process's largest titled, non-tool window, so a
  main window wins over a splash screen.

## Encore.Platform.Audio

### AudioDeviceHub

- At most one client is open per endpoint and flow. The first subscriber opens it and the last closes it.
- Each device's packets go through one `DeviceStream` (silence fill, stamping, the adaptive resampler). Its blocks fan
  out to the subscribers on the device's thread, under the device's dispatch lock.
- Dropout: the hub compares its open devices with the present endpoints on every endpoint change, when a client reports
  its device invalidated, and every `RetryInterval` while a device is out. A device that is gone is dropped: its client
  is closed and a `SilenceHold` keeps its stream continuous.
- Rebind: when a matching endpoint is present again, a new client is opened on the same stream, so every subscriber
  resumes on its old subscription. A match is the same id and flow, or else the one endpoint of that flow with the same
  friendly and parent name that no other open device holds.
- A session's device whose saved id is gone opens by name in the same way. A mixer that restarted with new ids needs
  nothing selected again.
- The rebinding lives only in the hub. Settings are never changed by it.
- The hub is also the engine's endpoint source: the first `EndpointsChanged` handler, or the first subscription, starts
  an `EndpointWatcher` that runs until the hub is disposed.
- `Subscribe` throws `ErrorCodeException` with `AudioDeviceFailed` when the device can't be opened.

### EndpointWatcher

- It registers an `IMMNotificationClient` and reports when an endpoint is added, removed, renamed or changes state, or
  when the default communications render endpoint changes.
- The notification callbacks only set an event. The thread then waits `CoalesceWindow`, so a burst raises one callback.

### FeedbackSoundOutput

- Each play opens the endpoint with a 1 s buffer and no event callback, writes the whole sound, starts, and polls the
  padding every `PollInterval` until it has drained (at most the sound's length plus `DrainSlack`).
- `Play` throws `ErrorCodeException` with `AudioDeviceFailed` when the endpoint can't be opened or fails while playing.

### TonePlayer

- Each burst's QPC is when its first frame plays, taken from `IAudioClock::GetPosition`, and recorded without allocating.
- The first burst is at stream frame `BurstIntervalFrames`, then one every interval.

### WasapiCapture

- WASAPI shared mode, event-driven: loopback for an Output Device and capture for an Input Device. The audio engine
  converts to 48 kHz float stereo.
- It runs on a dedicated MMCSS "Pro Audio" thread. Each packet's stamp goes to an `AudioStampStats` and the packet to a
  `DeviceStream`.
- An idle render endpoint, or a failed device, is filled with silence 50 ms behind now.
- A device invalidated under the client (`AUDCLNT_E_DEVICE_INVALIDATED`) is held silent the same way but is not a
  `Fault`: the client calls its `invalidated` callback once, and the hub drops the device.

## Encore.Platform.Capture

### CompositeCapture

- Each screen has its own capture on the one device: a WGC monitor item, or Desktop Duplication when WGC fails or the
  caller asks for it.
- A `MonitorCompositor` draws each frame into that screen's place in one BGRA canvas, laid out by virtual-desktop
  position.
- The slot clock is a high-resolution timer thread at the output frame rate. It hands the canvas on as one frame per
  slot, so the session's pacer, scaler and encoder see a single source with increasing stamps. A frame that opens a new
  slot hands the canvas on at once, from its source's thread.
- The canvas never changes size. A screen whose mode changes is scaled into its place, and a removed screen's place
  turns black. `Closed` is raised once no screen is left.
- Per-source arrival statistics are logged at `Dispose` and kept in `Sources`.

### D3D11Device

- A GPU driver reset removes the device (`IsLost`). Its calls then fail with `DeviceRemoved` or `DeviceReset`, and only
  a new device works again.
- `RemovedReason` reads as S_OK for a disposed device.
- `SimulateLoss` and `SimulateReset` are fault seams for tests and the developer command; they never cause a real reset.
- `TimeCopies` averages over several copies between two timestamp queries and blocks until the GPU has run them.

### DesktopDuplicationCapture

- Frames come only when the desktop changes. Each is stamped with DXGI's `LastPresentTime`, which is a QPC value.
- Its own thread polls the duplication every half slot of the frame rate. DXGI accumulates updates in between, so the
  latest image is never lost and frames arrive at most twice per slot.
- It never waits inside `AcquireNextFrame`. That wait holds the multithread-protected device's lock, which the encoder
  thread needs for every slot. On a quiet monitor the wait stalled the encoder and left PTS gaps in the Recording.
- `DXGI_ERROR_ACCESS_LOST` (a desktop switch or a mode change) recreates the duplication, retrying every 100 ms.
- The monitor's output leaving the adapter raises `Closed`. Any other failure, such as a removed device, raises `Failed`
  and ends the thread.
- Windows marked `WDA_EXCLUDEFROMCAPTURE` do not appear. The pointer is not drawn. A rotated output's frames are its
  unrotated surface.
- The first frame of a duplication is the whole desktop, delivered even when nothing moves.

### DisplayMonitors

- A display change is `WM_DISPLAYCHANGE`, which Windows broadcasts to top-level windows only. A message-only window
  never gets it, so the listener owns a hidden, never-shown top-level popup.
- One thread owns that window, and only while someone is subscribed.
- A hot-plug sends several messages. The burst is reported once, `Settle` after the last one.
- It only reads the displays and never changes them. It is safe under NativeAOT: blittable structs and an unmanaged
  window procedure.

### FrameScaler

- Content equal to a BGRA output is a plain `CopySubresourceRegion`. Anything else is one blit on the device's D3D11
  VideoProcessor: BGRA to BGRA, or BGRA to NV12 (BT.709, limited range).
- The processor is rebuilt when the content size changes.
- Input views are cached per captured texture, and output views per target texture and slice. Each cached view holds a
  reference on its texture, so a texture's address can't be reused while it is cached.
- A captured texture that can't take an input view is copied into an owned texture first; this is logged once.

### MonitorCanvas

- The canvas is the bounding box of the recorded monitors in virtual-desktop pixels.
- The encoded size is that box scaled down, aspect kept, only to fit the codec's largest side, with even sides. There is
  no pixel budget: the builder chose full width with HEVC or AV1.

### MonitorFinder

- `Describe` takes a friendly name from DisplayConfig, else the monitor's `EnumDisplayDevices` description, else the
  device name.
- Size, position and refresh rate come from the current display mode, whatever the caller's DPI awareness. GDI's
  `rcMonitor` is DPI-virtualized and is never used.
- The monitor id and the HDR state come from DisplayConfig.

### WindowCapture

- It captures through a window item (or a monitor item) only: no hooks and no access to the game's process.
- Frames arrive on a free-threaded pool's worker thread, one at a time.
- In a monitor capture, windows marked `WDA_EXCLUDEFROMCAPTURE` do not appear.

## Encore.Media

### AdaptiveResampler

- `Step` is the input frames consumed per output frame. It is fractional and can change at any time.
- Output frame j is the input signal evaluated at input position `p_j`, where `p_0 = 0` and `p_{j+1} = p_j + Step`.
- The kernel is centred on that position, so input frame I comes out at the output index whose position is I
  (`OutputIndexOf`). The only delay is in time: an output frame needs the `HalfTaps` input frames after its position.
- At a step of exactly 1 the output equals the input, sample for sample.
- Every buffer is allocated up front. Pushing and producing never allocate.
- The table: tap k of a row reads input frame `whole - HalfTaps + 1 + k`. Each row is a Kaiser-windowed sinc with unit
  DC gain.

### AudioStampStats

- It measures the jitter of each stamp against the previous stamp plus its frames, the stamp's age when the packet was
  read, and the long-run drift of the stamps against the frame count.
- It never allocates. The device thread records.

### AudioTimeline

- A session feeds it the hub's blocks, which are already contiguous and QPC-locked, so in practice it only cuts the
  stream at the anchor.
- Gaps are filled with silence and overlaps trimmed once they pass `ToleranceFrames`. Data before the anchor is dropped.
- One device thread drives it.

### DeviceStream

- Output frame k is at `origin + k / 48 000 s`. The origin is the first packet's stamp, or the first `FillTo`.
- A stamp that is zero, flagged bad or not after the previous one is extrapolated from the last packet.
- The first packet, a packet after a discontinuity, and the first packet after silence fill are placed exactly.
- Otherwise one `AdaptiveResampler` is steered by a PI loop on the error between where the packet's first frame comes
  out and where its stamp says it belongs. The ratio is clamped to 1 ± `MaxRatioDeviation`.
- An error past `ToleranceFrames` (an unflagged gap, a stall, an overlap) is a step correction: silence is inserted or
  the packet is trimmed.
- It never allocates after construction. One device thread drives it; other threads may read the counters as
  diagnostics.

### FramePacer

- Slot n is at `anchor + n / fps` with PTS `n × timeBase / fps`, so PTS only ever steps by one slot.
- A new frame takes the slot nearest its capture QPC when that slot is still free. Otherwise it is held for the next
  slot, where a newer frame replaces it.
- The held frame is encoded at its slot's time. A slot with no new frame by its time plus half a slot repeats the last
  frame.
- When the encoder falls more than `MaxSlotsBehind` slots behind, the pacer jumps to the current slot and counts the
  slots it skipped.
- Until it has first caught up, lateness is filled with repeats instead. The anchor frame reaches the encoder thread
  only after the output has opened, so the pacer always starts behind.
- It is pure and never allocates. One thread drives it: call `Poll` until it returns `None`, then `Offer` each new
  frame.

### MicMuteSchedule and MicMuteGate

- `Set` comes from the orchestrator thread and swaps an immutable log. The audio threads' gates read it with one
  `Volatile.Read` per run, without a lock.
- A gate sits between a device's `AudioTimeline` and the Track's mixer input. It counts its frame position from 0 and
  maps each change to the first frame at or after the change's QPC. A change at or before the anchor applies from
  frame 0.
- Muted runs go on as `WriteSilence`. Frames already passed on are never revisited.
- Nothing is allocated or locked per write.

### MonitorCompositor

- Each source's frames are drawn into its rectangle as they arrive, so the canvas always holds every source's latest
  frame.
- The canvas is handed to the pacer at most once per slot of the compositor's own clock, never once per source frame.
  Its stamps therefore always increase and the hand-off never fills, whatever the sources' rates.
- The first frame drawn in a new slot hands the canvas on at once. The clock's `Tick` hands on a slot whose change came
  too late for that.
- A slot in which nothing changed hands nothing on: the pacer repeats the last canvas.
- The hand-off runs outside the drawing lock, so a hand-off that waits on the encoder never holds up the sources.
- A removed source's rectangle turns black, and the canvas never changes size.

### PeakLimiter

- Audio that stays within [-1, 1] passes through bit for bit.
- A frame whose peak would exceed full scale drops the gain at once to bring it to exactly full scale. There is no
  look-ahead, so nothing is delayed.
- The gain then recovers toward 1 with a `ReleaseSeconds` time constant and snaps to exactly 1.
- One gain applies to both channels. Samples that are not finite are written as 0.
- It does no gain control, normalisation or compression below full scale.
- It is not thread-safe: its owner serialises the writes. Nothing is allocated once it is constructed.

### TrackMixer

- Each input is one device's `AudioTimeline` output, whose frame k is k frames after the session anchor. Summing by
  position therefore sums by QPC time.
- Frame k of the output is written once every input has reached it, or once the leading input is more than
  `MaxLagFrames` ahead. In that case the slow input counts as silence there, and its late frames are dropped when they
  arrive.
- With one input there is nothing to sum and its frames pass straight on. With none the Track is silent, and only
  `PadTo` advances it.
- Every emission goes through a `PeakLimiter`.
- Each input is written from its device's thread, under one short lock. Nothing is allocated once it is constructed.

## Encore.Platform.FFmpeg

### AacEncoder

- An encoder chain that starts mid-session passes a later `startSample`. Its priming packets end at or before that
  point and are dropped, just as a sink drops them at 0.
- `StartAt` throws `InvalidOperationException` once a sample has been written.

### ClipWriter

- The MP4 has its index first (`movflags +faststart`).
- It is written to `<final>.rbpart` beside the final path and then renamed over the final path, which is the empty file
  the allocator reserved.
- Each stream is rebased to the Clip's first keyframe in its own time base. Packets go through
  `av_interleaved_write_frame` in time order.
- The calling thread runs in background I/O mode for the write.

### D3D11VAFrames

- The pool holds render-target textures that captured frames are drawn into and NVENC reads from: BGRA, or NV12 written
  by the VideoProcessor.
- The device's immediate context and lock are filled in by `av_hwdevice_ctx_init`.

### DurableOutput

- FFmpeg writes into a 64 KB buffer. The file itself is unbuffered, so a flush reaches the OS at once, and `Flush` then
  calls `FlushFileBuffers`.
- A write or seek that fails returns `AVERROR(EIO)` to FFmpeg and keeps the first exception. `ThrowIfFailed` rethrows
  it as it was.
- `Overwrite` throws `IOException` when the write or the flush fails.
- Every call comes from one thread, the writer's.

### EncoderProbe

- Each candidate is opened on D3D11 frames of the given device and fed one frame. It works when that frame comes out as
  a packet.
- Per codec the candidates go NVENC, AMF, QSV. The first that works is the codec's encoder.
- H.264 falls back to libx264 only when no hardware H.264 encoder works. HEVC and AV1 have no software fallback.

### FragmentedMp4Muxer

- A normal muxer writes an empty moov, then a fragment at every keyframe and at least every second. Encoders hand it
  packets in their own time base; it converts them to stream time.
- A durable muxer (a Recording's) writes through a `DurableOutput`, puts packets straight in with `av_write_frame`,
  cuts a fragment only at `CutFragment`, and flushes the header and every fragment to the disk.
- At `Finish` a durable muxer patches the moov with an `mehd` box holding the file's length (the longest stream's).
  Without it Media Foundation plays only to the last `tfra` entry.
- The patch is two ordered writes, each flushed, with a valid file between them (`Mp4File.PlanFragmentDuration`). A
  layout it doesn't expect, or a failed write, leaves the file as written with a warning.
- HEVC is tagged `hvc1`, not mp4's default `hev1`, because Apple players need it.

### MediaLength

- It calls `avformat_open_input` and `avformat_find_stream_info`, with no decode.
- It reads through a shared handle (`SharedInput`), not FFmpeg's file protocol, so the read never blocks a rename or
  delete. The calling thread runs in background I/O mode.

### NullPacketSink

- Its streams carry their codec headers out of band, as the ring's do, so a Recording's file can open from the tap's
  copies.

### PacedVideoEncoder

- The capture thread only draws a new frame into a pool texture and posts it, with its capture QPC, through a
  fixed-size hand-off that never allocates.
- A frame that finds the hand-off full is not drawn at all and counts in `QueueDrops`.
- The encoder thread holds at most one pending frame (for the next slot) and a reference to the last emitted one (for
  repeats).
- It wakes on a posted frame or on a high-resolution waitable timer at the pacer's next deadline, so repeats go out in
  real time even while nothing is captured.
- PTS come from the slot index, never from the wake time.
- `Complete` does not emit slots that are not yet due, and releases a held frame.

### RecordingTap

- A packet costs an attached Recording one `av_packet_clone` (a native reference, no byte copy) and one enqueue. With no
  writer attached it costs nothing.
- The tap keeps its own copy of each stream's codec parameters, title, default flag and time base, so a Recording can
  open its file without reaching into the ring.
- `Attach` throws `InvalidOperationException` when a writer is already attached or the tap is closed.

### RecordingWriter

Where a Recording starts:

- It starts on the first video keyframe at or after the keyframe forced after the press. When none is served within
  `KeyWait`, it starts on the first keyframe at or after the press. Call that keyframe K.
- Until K is known the packets are held, at most `HoldUs` of them.
- Each stream is rebased to K in its own time base, as `PacketRing.Cut` does. But it keeps only the packets that
  *start* at or after K, not those centred there as a Clip does.
- Why: a Clip's plain MP4 signals an earlier audio start in its edit list. Fragmented MP4 has none, so the muxer would
  shift every stream to the earliest packet and stretch the first video frame off the frame grid. The cost is that an
  audio Track's first sample may start up to one packet late.

Where it ends:

- A stop at E keeps the packets before E, judged the same way.
- The file is finished once every stream has passed E, `StopWait` after the stop at the latest, or as soon as the
  session's output ends.

Fragments:

- The writer cuts the fragments itself: one at each video keyframe and at least every `FragmentUs`.
- A cut at video time V writes every packet before V (audio by its centre) once every audio stream has reached V, or
  once the video is `AudioWaitUs` past V. So no Track's fragment runs ahead of the video.
- Each fragment is then flushed to the disk, so a kill loses at most about one fragment.

Ownership and failure:

- Every packet it was handed is freed on every path.
- It owns its streams' parameters, so the session can free the tap, the ring and the encoders while it still finishes.
- On a full disk it writes no trailer. It cuts the file back to its last complete fragment, or deletes it when there is
  none. Shrinking a file needs no free space.
- It runs on its own thread in background I/O mode.

### ReplayRingSink

- The rings are `RingGenerations`. An encoder `Restart` opens a generation with streams of its own. A Buffer Window
  `Resize` opens one that shares the old generation's streams.
- A Clip is cut from one generation, with that generation's parameters. A restart's first video keyframe is a boundary
  no Clip spans.
- The sink owns the rings. Its reference count is the session's one reference plus one per `Lease`. A save leases it
  until it has cut, so ending the session never frees the rings under a waiting writer.
- `Restart` precondition: the old chain must have stopped writing, and no save may be waiting for its keyframe or its
  streams. `VideoMicros` and `AllStreamsPast` read the current generation, so they would convert the old encoder's PTS
  with the new one's streams, and the video stream is -1 until the new chain adds its streams.
- `Cut` runs outside the sink's lock and picks its range from the first video keyframe at or after its start.

### SharedInput

- FFmpeg's own file protocol opens with `_SH_DENYNO`, which leaves out delete sharing. A read on a starved low-priority
  thread then blocked the user's rename or delete of the same file.
- Every call comes from one thread, the reader's.

### VideoEncoder

- NVENC, AMF and QSV read the pool textures directly. libx264 gets each frame copied back through a staging texture and
  converted from BGRA to yuv420p by swscale, into native frames allocated once.
- There are no B-frames, and the GOP is the frame rate (1 s).
- PTS are in 1/90000. A session's pacer gives each frame its slot's PTS. The variable-rate API (`TryAcquire(long)`, for
  the probe and tests) takes the capture QPC relative to the anchor.
- An IDR is forced once a second of PTS has passed since the last one, and on the first frame captured after a save's
  press (`RequestKeyframe`).
- Keyframe requests: a frame captured at or before the press, still in the pacer's hand-off or held for its slot, does
  not serve the request. A repeat counts as captured at its slot's QPC. Requests outstanding together share one
  keyframe, after the latest of their QPCs, which also restarts the once-a-second count.
- NVENC and QSV return each packet as soon as its frame is encoded.
- BGRA frames go in as they are. NV12 frames are tagged BT.709 limited range, which is what `FrameScaler` writes.
- Latency is QPC as the packet leaves `avcodec_receive_packet` minus the frame's capture QPC. Packets drained by
  `Flush` are counted in `FlushedPackets` instead, because their latency would include shutdown.

### VideoEncoderOptions

- `Preset` is NVENC's, `p1` (fastest) to `p7`. `Tune` is NVENC's (`hq`, `ll`, `ull`), or null for FFmpeg's default.
- `RateControl` is CBR, or VBR peaking at 1.5 times the target.
- The defaults are the first prototype's: h264_nvenc, p5, 60 fps, CBR 20 Mbps.

## Encore.Contracts

Contracts is the wire format between the App and the engine. Fields are only ever added, never renamed or removed. A
field an older peer doesn't send reads as its empty default, and unknown fields are ignored.

### ErrorCodes

More detail on the codes whose one-line summary leaves something out:

- `ring-path-invalid`: the cache folder is `ringPath` or its default, and the Clips folder is `outputFolder` or its
  default. Neither may be inside the other.
- `recording-interrupted`: raised by the engine at start, once per Recording the last instance left open, after
  trimming that file to its last whole fragment. The message names the path.
- `recording-suspended`: the Recording's stop reason is `suspend`. Rewind resumes in a new ring generation.
- `hotkey-duplicate`: "first" means first in `HotkeyAction` order.
- `disk-full`, `access-denied`, `save-failed`: raised by the Clip writer and by the Recording writer.
- `replay-stopped-disk-full`: shown on the Overlay. It stays until the wait for room ends (a recheck finds room for the
  ring, another session starts, or the Target changes or exits) or it is dismissed.
- `clip-saved`, `recording-started`, `recording-stopped`, `recording-restarted`: the engine withdraws each after 10 s.
- `library-corrupt`: both `library.json` and its `.bak` were unreadable. The warning can be dismissed; Cleanup stays off
  (`cleanupBlocked`) until it is.
- `library-item-changed`: the App acts again on the item the snapshot now shows. It is never retried automatically.
- `invalid-file-name`: empty, holding a character Windows forbids, ending with a dot or a space, or a reserved device
  name.
- `cleanup-limit-unreachable`: dismissable, source `library`. Raised once until a pass ends within the limit or Cleanup
  is turned off.
- `feedback-device-captured`: withdrawn when the Track Layout no longer mixes that device.
- `memory-margin`: also raised when the ring holds under 1 s, so a save makes no Clip until memory frees up. Raised once
  per entry into the margin.
- `reset-gap`: Rewind switched to a profile's new encoder chain at a save, and the new chain's first keyframe came more
  than one frame after the old Clip's end (no frame arrived after the press: a static window, say). The message gives
  the gap's length.
- `encoder-budget`: the game session already runs two encoder chains.
- `gpu-reset`: content buffered before the reset stays saveable. A running Recording continues in a new file once the
  session recovers.
- `monitor-removed`: the stop reason is `monitor-removed`. The Recording isn't restarted on another monitor.
- `monitor-added-next-recording`: the canvas never changes inside a file.
- `monitor-recording-scaled`: H.264's largest side is 4096 px.

### Instance

- The instance name is appended to the data folders, so the installed copy and a development copy never share data.
- `--instance` accepts only names that start with `TestPrefix`, so tests never touch a real instance.

### EngineClient

- `ConnectAsync` returns once the snapshot has arrived. `State` then follows every event.
- Each command gets one reply. When the connection ends, every pending command fails with `engine-disconnected`.
- There is no reconnect: a new connection is a new client, with a fresh snapshot.
- `OnEvent` is called after `State` includes the event, and also for event types this build doesn't know. It runs on
  the client's read loop, so a reply that follows the event is not delivered until it returns.
- `VerifyServer` checks that the serving process's image is in the install folder. A failed check is
  `engine-untrusted`.

### Envelope

- A client sends commands: `{ v, id, type, payload }`.
- The engine sends replies `{ v, id, type, payload | error }` and events `{ v, type, seq, payload }`.
- Unknown fields are ignored, so a newer peer can add them.

### FrameReader

- Blank lines are skipped. A partial last line means the peer died mid-write, and reads as the end of the stream.
- After a bad line the stream can't be trusted, which is why the caller must drop the connection.

### StateSnapshot

- An epic adds its field here, with a default, and the event that changes it in its own message file.
- A snapshot from an older engine may lack a newer field. Each such field reads as its empty default: `null`, an empty
  list, all Hotkeys unbound, or unprobed video options. The generated deserializer passes null for a missing field,
  which is why some setters replace null.

### AudioDevice

- Two devices with the same `FriendlyName` are told apart by `ParentName`, the audio device that exposes the endpoint.
- `Available` is false for a device a Track Layout holds that isn't present now.
- `IsDefault` marks the Windows default endpoint of its flow (console role). It is written only when true.
- A device list holds Output Devices first, then sorts by name.

### AudioMessages

- `meters.watch` replies `invalid-arguments` for a Track naming a device key the layout lacks, and keeps the previous
  watch.

### CaptureState

- `ClipsWriting` is how many Clips are being written right now. JSON without it reads as 0.

### VideoOptions

- The engine probes the hardware encoders once at startup. Until then `Probed` is false.
- The resolutions and `MaxFps` are only what is offered: the `video` key itself accepts any even size up to
  `VideoSettings.MaxDimension`.
- `CodecOffer.Encoder` is the FFmpeg encoder name, such as `h264_nvenc`, or `libx264` when no hardware H.264 encoder
  works.

### StorageEstimate

- It is worked out from the Global `video`, `bufferWindow`, `replayStorage`, `ringPath` and the Track count.
- `Bytes` is the ring's size at worst. `FreeBytes` is free RAM for Memory storage and the ring drive's free space for
  Disk storage; it is null when unknown, and `ExceedsFree` is then false.

### Warning

- `Id`: the engine's id for this occurrence. `Code`: one of `ErrorCodes`. `Source`: the engine area that raised it,
  such as `settings` or `audio`. `At`: when it was raised, in UTC.
- `Overlay`: whether the Overlay shows it. `Sound`: the Feedback Sound the engine played for it, or null.
  `Dismissable`: whether `warning.dismiss` may remove it.
- Dismissing is always a command to the engine, never local App state.

### TargetGame and DetectedGame

- `ProfileId` is the executable's canonical path. `Name` is the display name: the executable's file name until a Game
  Profile names it.
- Only `target.changed` sets the Target Game, and only the SessionOrchestrator publishes it.

### LibraryItem

- `Path`: relative to the Output Folder. It is the item's key, compared ignoring case.
- `ProfileId`: the Game Profile it was saved for, or null for a file the start scan found.
- `Size`: bytes; 0 while it is being written.
- `Date`: the write's start, or the file's creation time for a scanned file.
- `BeingWritten`: such an item is shown locked and is never persisted.
- `LengthSeconds`: read by FFmpeg's demuxer; null until read, or when it can't be read.
- `Modified`: the file's last write time in UTC. With the path and size it is the item's identity; null while being
  written. Commands that change an item send this identity, and are refused with `library-item-changed` when it no
  longer matches.
- `Kind`: one of `LibraryItemKinds`, or null. A file the folder scan finds has none, except one in the Monitor
  Recordings folder.

### MeterProbes

- A probe Track holds its one device with `muteAffected` false, so it shows the device as it sounds.
- Probe ids are derived from the endpoint id (ignoring case) and the flow, so they are stable across watches.
- Probes are never saved. They live only in the watched copy of the layout.
- The engine opens each device once whatever the Tracks, so a probe adds a mixer input, not a device.
- In `WithProbes`, a device the layout already holds keeps its key. Another gets `DeviceKey`, and is not a Mic Device.

### DisplayMonitor

- `MonitorId` is DisplayConfig's monitor device path, stable for the same monitor on the same port. It is what
  `monitorRecordingScreens` stores.
- `X` and `Y` are the display mode's position in physical pixels, not GDI's DPI-virtualized rectangle.
- `Hdr` says whether Windows HDR is on. The recording is SDR either way.

### VideoSettings

- With `MatchSource`, the game capture chain takes its size from the game's own captured size at its first frame,
  scaled only to fit the codec's largest side. `Width` and `Height` stay as the size to fall back to.
- It is false by default and left out of the JSON when false, so existing values read and write as before.

### MonitorRecordingScreens

- The value is `{ "mode": "all" }`, or `{ "mode": "selected", "monitors": [ ... ] }` with at least one monitor id.
- A monitor id is DisplayConfig's monitor device path, stable across reboots, or a GDI device name carried over from
  the older `monitorRecordingMonitor` key.
- While the key is null, the older key is read as before.

### OverlayDotMode

- A topmost window over a borderless game keeps the game in independent flip only while the game's monitor has a free
  hardware overlay plane.
- `Auto` therefore shows the dot always where the monitor reports overlay planes, and only for a few seconds after
  each change where it doesn't.

### TrackLayoutPresets

`Build` follows these rules:

- Track 1 keeps the current layout's first Track id, or `Keys.EverythingTrackId` when there is none. The other Tracks
  get new ids.
- A default device the current layout already holds (same endpoint, ignoring case, and same flow) keeps its record and
  key. The default input is marked a Mic Device. Other devices are added with new keys.
- A flow with no available default is left out, and its Tracks stay empty.
- The layout holds only the devices its Tracks use.
- `newId` makes the new keys and ids, so tests can pass a fixed one.

### Supervision

- `CrashLoopGuard`: a restart is allowed while fewer than `CrashLoopRestarts` restarts fall within the last
  `CrashLoopWindow`. The next one is a crash loop.
- `RestartBackoff`: the delays start over once `BackoffReset` passes with no restart.
- `SingleInstance`: the mutex is named `Local\Encore.{process}.v1.{instance}`.

## Encore.Engine

### AudioTimelineTap

- It subscribes before the anchor. Until `Publish` hands it its `AudioTimeline`, it either drops its blocks or, with a
  hold, keeps the newest of them up to `holdFrames` frames (oldest dropped) in buffers allocated up front.
- `Publish` replays the held blocks into the timeline in order, then live blocks follow. None is lost or reordered
  between the two: there is one lock per tap, uncontended except while `Publish` replays.
- The timeline cuts the stream at the anchor. Hub blocks are contiguous and QPC-locked, so the rest is written as it
  is. A held tap's stream therefore starts with real audio, not with fill for the blocks between the anchor and
  `Publish`.
- It is called on the device's thread.

### DeviceDropoutReporter, MemoryMarginReporter

- Each warning has severity warning, is not shown on the Overlay, and can be dismissed. The sources are `audio` and
  `replay`.
- Recovery raises nothing and dismisses nothing: warnings only go by `warning.dismiss`.
- The dropout reporter logs nothing; the hub's own entry is the log line.

### DeviceListPublisher

- It re-lists when the endpoints change and rebuilds when the settings do.
- The first list is built without publishing, for the first snapshot.
- A failed listing is logged and keeps the previous present endpoints.

### FeedbackDeviceCheck

- The Track Layout checked is the one resolved for the Target Game's profile, else Global.
- `Check` runs on each settings, endpoint and target change. Each distinct condition raises once; the warning is
  withdrawn when the condition clears or changes.
- The warning has source `feedback`, is not shown on the Overlay, and can be dismissed.

### TrackMeters

- Each distinct device is subscribed once, cut by an `AudioTimeline` and fanned out to its Tracks' `TrackMixer`s. Mic
  mute gates a Mic Device in a `muteAffected` Track, as in a capture. Each mixer's output is a `PeakMeter`, not an
  encoder.
- A meter thread publishes each Track's peak as `meters.levels`, never twice within `PublishInterval`.
- There is one watch at a time. A new one replaces it: the old subscriptions are closed before the new ones open.
  `Unwatch` closes the subscriptions and publishes no Tracks.
- `Watch` throws `invalid-arguments` when a Track names a device key the layout lacks, and keeps the previous watch.
- Every call is under one lock.

### CaptureStatsReport

Nothing in the collector allocates per frame or per packet. Audio stamps are recorded by the hub, per device.

`EncoderReport`:

- `Preset` and `Tune` are h264_nvenc's. `Tune` is null for FFmpeg's default.
- `EncodeThread`: submit and drain happen on the encoder thread. This is always true when anything is encoded.

`VideoReport`:

- `FramesArrived`: frames WGC delivered after the anchor, warm-up included.
- `FramesEncoded`: video packets written to the file. There is one per slot, new or repeated.
- `Duplicates`: slots the pacer filled by repeating the last frame. `DuplicatePercent` is their share of all slots.
- `QueueDrops`: frames not drawn because the encoder thread's hand-off, or the frame pool, was full.
- `NonIncreasingStamps`: arrived frames whose capture stamp was not after the previous one.
- `MissedSlots`: WGC's cadence, the sum of `round(Δ × 60) − 1` over steps longer than 1.5/60 s.
- `FreshSlots`: slots encoded with a new frame.
- `SkippedSlots`: slots the pacer jumped over because the encoder fell more than 4 slots behind.
- `HeldReplaced`: frames held for the next slot that a newer frame replaced first.

`ResourceReport`: `StartupToAnchorMs` is the time from process start to the first captured frame.

`ResamplerReport`:

- `RatioPpm`: the resampler's ratio at stop, (step − 1) × 10⁶; about minus the drift.
- `MeanRatioPpm`: the mean ratio after the 5 s settle.
- `StepCorrections`: errors past 5 ms without a discontinuity flag, corrected by silence or a trim.
- `SettledPackets`: steered packets after the settle, which the error figures cover.
- `ErrorP99Samples`: |content position − stamp| at the 99th percentile, in samples (the bucket's upper edge).
- `OutputFrames`: frames the device's stream produced since it opened.

### DebugCaptureCommand

- `Codec`, `Size`, `Fps` and `BitrateKbps` make the session's `video` value, with CBR.
- `Encoder` forces an FFmpeg encoder.
- `EncodeThread` (`--encode-thread`) is still accepted and has no effect: the encoder thread is always on.
- `TrackLayout` (`--track-layout`) captures the Tracks of the instance's Global `trackLayout`, not the output and
  input device.
- `Instance` (`--instance`, test names only) overrides the process's own instance.

### ServeCommand

- `Instance` overrides the process's own instance, for tests only.
- `DebugCapture` (`--debug-capture`) runs a capture session that writes one file on the Target Game's window, following
  it. It also turns Game Detection on for a test instance.
- `Rewind` (`--rewind`, test instances only) turns Rewind, and with it Game Detection, on for the test instance, so a
  separate engine process can save Clips.
- `Restarted` (`--restarted crashed|hung`) is how the App relaunches an engine that crashed or hung. The engine raises
  `engine-restarted` before its pipe serves, so the first snapshot carries it.

### EngineHost

What `serve` sets up, roughly in order:

- It claims the instance's pipe and opens the settings store.
- It lists the audio devices before the event hub exists, so the first snapshot holds them. After that it publishes
  `devices.changed` when the endpoints or the Track Layouts change.
- It loads mic mute from `runtime.json` before the pipe serves. The SessionOrchestrator owns mic mute.
- It registers the Hotkeys before the pipe accepts connections.
- Started with `--restarted`, it raises `engine-restarted` before the pipe serves.
- Once the pipe serves, it probes the video encoders off that path and publishes `capture.videoOptions`.

What runs while it serves:

- The heartbeat, and supervision of the App that attaches.
- The SessionOrchestrator, which publishes `capture.stateChanged`. A session starts only on the Target Game.
- Hotkey presses: the orchestrator publishes each as `hotkey.pressed`. A `micMute` press toggles mic mute, a `save`
  press saves a Clip, and a `recordToggle` press starts or stops a Recording beside Rewind.
- Game Detection proposes the Target Game to the orchestrator, which publishes `target.changed`. A Rewind session
  follows the Target, with its audio from the snapshot's Track Layout. With `--debug-capture` a capture session
  writing one file follows the Target instead.
- A save too soon after the last Reset or the session's start is refused as `clip.failed`.
- Each audio device dropout raises a `device-dropout` warning.
- `meters.watch` meters a Track Layout on the hub with its own mixer, nothing captured. The orchestrator's mic mute
  reaches the meters as it does a session.
- The FeedbackRouter turns saved and failed Clips, Recording state changes and a full disk into `warning.raised`
  notices, and dismisses the info ones after 10 s.
- Each notice's Feedback Sound, if switched on, plays on a worker thread on `feedbackSoundDevice` or the default
  communications endpoint. `feedback.preview` plays one even when it is off.
- `dev.simulate` fakes outcomes; the installed instance refuses it.

Other notes:

- The listed games are Windows' game list plus `manualGames` (canonical, or lowercase when not on disk).
- A cancelled encoder probe publishes nothing.

### HotkeyService

- It plans the registrations from the settings when it starts and after each commit, and re-registers only when the
  set to register changes.
- It publishes `hotkey.status` and posts each press to the SessionOrchestrator, which publishes it.
- A document with no `hotkeys` takes the catalog default only on the installed instance.

### CommandRouter

- A handler that throws something without an error code replies `command-failed`.

### Connection

- The writer loop is fed by a `WriterQueue`. The reader loop dispatches each command on its own, so a slow command
  holds up nothing else.
- A full queue, a lagging client, a malformed frame or a broken pipe drops the connection. The engine carries on.

### EventHub

- Because one lock assigns `seq`, applies the event and hands on the frame, a snapshot built under that lock is
  followed by exactly the events after it.
- Publishing never waits on a client.

### IpcServer

- The first pipe instance is created with `FILE_FLAG_FIRST_PIPE_INSTANCE` and a DACL for the current user only, so a
  second engine, or a squatter, can't serve the same name.
- A listening instance always exists while the server runs. Each connected client gets its own `Connection`.
- A client that connects and closes before the wait takes it gives `ERROR_NO_DATA`: an App whose connect attempt timed
  out on a loaded machine. Only that pipe instance is spent, and its replacement is created first so the name is never
  free.
- `LastDisconnected` stops the meter watch, so a crashed App leaves no device open.

### WriterQueue

- A frame with a coalescing key replaces the queued frame with the same key and moves to the tail, so frames stay in
  `seq` order.
- Past `MaxQueued`, enqueuing fails and closes the queue.
- `Lag` is the age of the oldest frame not yet written, the in-flight one included.

### TextLoggerProvider

- The file is `<prefix>-yyyyMMdd.log`. Warnings and above also go to stderr.
- One background thread writes the lines, so callers, capture threads included, never touch the file.
- It was chosen over Serilog, whose destructuring raises trim warning IL2072.

### CaptureSessionOptions

- `Replay`: the ring is sized from the snapshot and lives in memory or on disk as `replayStorage` says. No path is
  allocated, and the session's outcome has none.
- `FollowWindow`: with no capture, the pacer repeats the last frame. The orchestrator retargets the session to the
  process's next window, keeping its Rewind, or ends it. When the option is not set, the session stops.
- `StarveFrames` imitates the secure desktop (lock screen, UAC, Ctrl+Alt+Del), where the pacer repeats the last frame.
- `WgcFault`: a Monitor session then falls back to Desktop Duplication; a game session fails.
- `CompositeMonitors` takes GDI device names; an empty list means every attached monitor. It is kept for cost
  measurements. A real Monitor session composites its resolved screens instead.
- `Warn` is used for `monitor-recording-scaled`.

### CaptureSession

The pipeline:

- Video: WGC capture, then the scaler, then the paced video encoder. Audio: the session's Tracks, each an AAC stream
  summed from its devices' hub streams. A session without Rewind writes them all into one fragmented MP4.
- Each device is subscribed once and cut at the anchor by one `AudioTimeline`, which feeds every Track that holds the
  device (`AudioFanOut`). Each Track's `TrackMixer` sums its devices by frame position, through a limiter.
- A Track with no devices is silence, advanced from QPC by a timer, off the capture thread.
- At the end every Track is padded with silence to one common end frame: the longest device's or the last video
  frame's, whichever is later.
- The first frame is the anchor: PTS 0 for every stream.
- The snapshot's `video` sets the encoder (the probe's for its codec, else H.264), the output size, the frame rate, the
  bitrate and the rate control.
- The capture thread only scales each frame into the chain's output, letterboxed, and posts it. The encoder thread
  emits exactly one frame per slot, repeating the last one when nothing new arrived.
- A retarget swaps only the WGC capture, so the file keeps one unbroken set of streams.
- Mic mute is one `MicMuteSchedule`, set from the spec before anything opens and then by `SetMicMuted`. Each Mic
  Device's input to each `muteAffected` Track passes through a `MicMuteGate`, so it is silenced there, before the mix,
  from the press's frame, and stays audible in every other Track.
- All of that, per spec, is one `EncoderChain`.

Locks:

- `_chainLock` is only ever taken after `_gate`, never before it.
- The current chain, the armed chain (with its press) and the replaced chain that is still fed are written under both
  locks, so the capture thread can read them under `_gate` alone.
- The rest is under `_chainLock` only: the prepared chain, the spec asked for, a running build, the background threads,
  and whether `End` has closed them.
- Device-loss state is under `_chainLock`: whether the device is lost, whether a recovery attempt runs, whether the
  lost device and chains are torn down yet, the lost chain's Recording tap (kept open until the recovery is posted),
  the spec to rebuild, and the attempt's thread, which `End` joins.

Rewind (Replay mode):

- The streams go into the chain's `ReplayRingSink`, not a file. `Save` writes a Clip from it on its own thread.
- The ring is sized from the snapshot's Buffer Window at the anchor. A new window from `SetBufferWindow` resizes it at
  the next Reset, into a new ring generation seeded from the Reset keyframe, so Clips stay gapless.
- The encoders write into the ring through a `RecordingTap`. `Record` attaches a `RecordingWriter` to it, so a
  Recording comes from the same encoder chain.

`Save`:

- A press less than 1 s after the anchor is refused with `clip-too-short`, before any keyframe is asked for.
- The write is bound to the current chain and uses that chain's sink and encoder throughout.
- It asks the encoder for a keyframe on the first frame captured after the press. That is where the Clip ends and,
  once the orchestrator Resets, where the next one starts. No keyframe is asked for unless the write starts.
- Its own thread waits up to `SaveWait` for that keyframe (otherwise the Clip ends at the press), then up to as long
  again for every stream to pass it by an AAC frame, because encoding and the AAC FIFO lag behind.
- It cuts `[max(key − bufferWindow, last Reset, earlier write's claim), key)` from the first keyframe in that range.
- The Buffer Window is the one in effect at the press. The cut comes from one ring generation: less than 1 s past a
  generation's boundary, the previous generation is cut up to the boundary, where the Clip then ends.
- A chain started mid-session cuts from its first keyframe when that is under a frame before the start
  (`EncoderChain.SnapStart`).
- Once drained (`Drain`), the encoders are flushed and gone: the write asks for no keyframe, and the Clip runs to the
  end of the ring. After `Release` there is nothing to save.

`ClipWrite`:

- It leases the ring on the orchestrator thread, so ending the session can't free it, and registers on the chain, so a
  switch can't tear the chain down before the cut.
- It then waits, cuts (pinning the slabs and releasing the lease) and writes on its own background thread, which never
  blocks the ring, and ends its registration once.
- When a switch is armed, the keyframe is asked for after the arm. Frames captured between the press and the arm reach
  only the old chain and stay in its Clip.

Changing chains at a save (`PrepareChain`):

- A profile's new Tracks or video get a chain of their own, built in advance on a background thread and left idle.
- Passing null, or the spec of the chain that is or will be current, drops a prepared chain that is not yet armed.
- A build waits for the anchor, for a replaced chain to be torn down and for an armed one to take over. A build that a
  newer spec superseded is torn down. A failed build is logged and forgotten, so the next call retries it.
- The next save press whose write starts arms the prepared chain, unless a Recording is attached. Its audio starts at
  the press, and the first frame captured after the press makes it the current chain.
- The old chain is fed until every save bound to it has cut. It is then torn down off the capture and orchestrator
  threads. So at most two chains are live at once.
- A new chain whose first keyframe comes more than a frame after the old Clip's end raises `reset-gap`.
- Nothing happens in file mode, or once the session is drained or stopped.

`RestartChain` (a forced restart, used by tests):

- On its own thread, the current chain is taken out of the capture thread's feed, the saves bound to it are waited for
  (bounded), and it is torn down. The same spec is then rebuilt into a new generation of the same ring. The frames
  captured meanwhile are dropped.
- The old chain is gone before the new one is built, and no other build starts meanwhile.
- The new chain becomes current, then the orchestrator is told (`ChainRestarted`), and only then is the old chain's
  Recording ended, so its post-back always follows.
- A failed rebuild frees the ring and fails the session with `encode-failed`.
- It is refused unless the session is in Replay mode, anchored and live, with no switch armed or retiring and no
  restart running.

`SwitchChain` (a switch at a Reset, while a Monitor Recording holds the third chain):

- It uses the restart's machinery, but the new chain is the new spec's, in a fresh ring. The old ring goes with its
  chain; a save still writing keeps its lease.
- No `ChainRestarted` is posted. The gap from the Reset to the first keyframe is raised as `reset-gap`, because the
  frames captured in between are in no Clip.
- It is refused on the same terms as a restart, and also while a chain is prepared, built or torn down, or a
  Recording is attached. It runs on the orchestrator thread and never blocks on the build.

Matching the game's size (`matchSource`):

- A game's output size is the game's own captured size at the first frame, with even sides, scaled only to fit the
  codec. A game under 640×360 keeps the snapshot's size.
- A new size that holds for 3 s moves Rewind to a new chain. No move starts while a Recording is attached.
- `StartMove`: normally the current spec's chain is built ahead at the new size in the prepared slot, and armed
  without a save once built. While a Monitor Recording holds the other chain, the current chain is torn down first and
  rebuilt at the new size into a fresh ring. Either way a gap over a frame raises `reset-gap`.
- A move is refused, to be found again after another 3 s, while any switch, build, teardown, restart or recovery
  runs, a chain is prepared, or a Recording is attached.
- `ArmMove`: the old chain, bound to no save, retires at once.
- A Record press reserves the current chain until its writer attaches (at most `RecordReserve`), and cancels a pending
  move: the prepared chain is torn down, a build in flight is torn down when it ends, and a queued arm finds nothing.
  A profile's chain waiting for a save is left alone.

Monitor sessions:

- A Monitor session captures the monitor its snapshot names (else the primary) on a device of that monitor's adapter.
  It holds no Rewind and writes no file of its own: its chain feeds only the Recording that `Record` attaches.
- Its anchor is posted (`Anchored`) so the orchestrator can start it.
- When WGC can't create or start the monitor item, the same monitor is captured through Desktop Duplication, into the
  same chain, and `CaptureFallback` is raised once. A game session never falls back.
- `PlanCanvas`, for several screens: the canvas of the screens replaces the snapshot's video size. Over H.264's
  4096 px it uses HEVC, else AV1, at full size. A canvas over 4096 px that has to stay H.264 is fitted to 4096 with
  `monitor-recording-scaled`. With fewer than two screens still attached, the one left is captured directly. With
  none, the start fails with `monitor-recording-no-screens`.

A GPU driver reset:

- A loss is a failure that carries `DeviceRemoved` or `DeviceReset`, or a device that reports itself lost. The device
  is checked every `DeviceWatchInterval` from the anchor on.
- A loss stops feeding the chains and posts `DeviceLost` once. Later failures are swallowed.
- Nothing is built, armed, switched, resized or recorded while the device is lost. A save cuts the ring as it is, with
  no keyframe asked for.
- Each `RecoverDevice` is one attempt, on its own thread. A running forced restart and the last attempt finish first.
- The first attempt tears down, in this order: WGC, then every chain (builds and teardowns on the lost device finish
  first; the current chain keeps its ring and its Recording tap stays open), then the device.
- Then: a new device on the Target's adapter (the monitor's for a Monitor session), the spec rebuilt into a new
  generation of the same ring, and a new capture. The spec rebuilt is an armed chain's when the orchestrator already
  took its switch, else the current one's.
- On success it posts `ChainRestarted`, and only then ends the lost chain's Recording. On failure it frees what the
  attempt built (the ring is kept) and posts `DeviceLost` again. A stop posts nothing.
- Only sessions with a Rewind, and the Monitor session, recover. A file-mode session fails.

Sleep (`Suspend`):

- The session takes the same lost state without a loss: the same teardown on its own thread, with the Recording ended
  at its last fragment. The ring is kept, so a save still cuts the old generation.
- No frames arrive until `RecoverDevice` succeeds on resume.
- If the device is already lost, a recovery attempt still running installs nothing, and the teardown runs once it has
  finished. If already suspended, it returns true and does nothing more.
- It returns false when it can't: not anchored, neither a Rewind nor a Monitor session, or stopped, finished,
  draining or closed.

### ClipClaim

- The claim is the highest end of a write that succeeded or is still in flight. It is ring time, not a Reset: a Reset
  moves only once the orchestrator takes a write's result.
- So a write that starts while an earlier one is still writing cuts from that earlier write's end, not back over it.
- A failed write only drops its own claim. Failures in any order leave the start at the last succeeded end, or at a
  write still in flight.
- A succeeded write may end earlier than it claimed, when its cut ended at a ring generation's boundary.

### CaptureWaitWarnings

- The window to capture is the one given while it can be captured (visible, titled, not minimized or cloaked, at least
  `MinWidth` × `MinHeight`), else the process's largest that can.
- Each run of waits is logged once and shown as a `capture-failed` warning saying capture is waiting. It is withdrawn
  when the run ends.

### EncoderChain

- A chain holds: the device taps, each cut by one `AudioTimeline` and fanned out to every Track that holds the device;
  each Track's `TrackMixer` and `AacEncoder`; the frame pool, scaler and paced video encoder at the spec's output size;
  and the output.
- The output is Rewind's `ReplayRingSink` behind a `RecordingTap`, or the file's muxer, or, for the Monitor session, a
  tap with a `NullPacketSink` behind it.
- Every PTS is relative to the one session anchor, so Clips of two chains meet on one timeline.
- The session's first chain starts at the anchor, video and audio alike. Its pacer gives its first frame slot 0.
- A later chain is built in advance and stays idle. Its audio starts at the save press that switches the session to
  it, and its frames at the first one captured after that press, in the slot nearest it.
- A Track with no devices is silence, advanced from QPC every `SilenceInterval` by a timer, off the capture thread.
- A chain rebuilt by a forced restart takes over its predecessor's ring in a new generation.
- `StartAudio`: each device's held blocks are replayed first. Each Track's first sample has its frames since the
  session anchor as its PTS.
- `Draw`: a full hand-off drops the frame.
- `Finish`: the common end frame is the longest device's or the video's end, whichever is later. The video ends where
  its pacer's next slot starts, which repeats can take past the last captured frame.
- `SnapStart`: a chain that started mid-session may give its first frame the slot before the old chain's last key.
  Cutting from that key would skip to the chain's next keyframe.
- `Dispose`: a save still writing holds its own lease and pins, and a Recording still finishing owns its own copies of
  the streams.

### DevSimulator

- It can fake: a stub Recording (its state, and its started and stopped notices), a full disk, a save failure, a saved
  notice, an engine hang, and a GPU driver reset.
- The stub Recording state drives the solid dot only. The saved notice publishes no `clip.saved`, as no file exists.
- The hang stops the heartbeat and freezes the event hub while the process lives.

### EstimatePublisher

- The estimate is the ring's bytes against the free RAM (Memory storage) or the ring drive's free space (Disk storage).
- The first estimate is made without publishing, for the first snapshot.
- `estimate.changed` is published only when the storage, the bytes or the over-free flag change, or the free bytes
  move by 10 % or more.
- The ring's own use counts back as free, so a running buffer that fits doesn't warn. In Disk mode that is the
  `ring-<n>.bin` files; in Memory mode it is this process's working set, an approximation.
- A settings change is re-estimated on the thread pool. An estimate from an older settings revision is dropped, and a
  failed re-estimate is skipped.

### GpuResetWarnings

- Each loss raises `gpu-reset` (a warning, not on the Overlay). A recovery withdraws the oldest one still shown.
- A give-up withdraws it too and raises `gpu-reset-failed` (an error).

### ReplayRings

- A Disk-mode ring is a new `RingFile` in the folder, of at most `diskBytes`, at low I/O priority.
- Either kind has its native-memory slabs checked against the memory margin, if one is set.

### VideoEncoderCatalog

- The probe for a codec runs on first use. A result is cached; a throw is not, so a transient failure is retried.
- Each codec is probed on its own D3D11 device on adapter 0. Choosing the adapter per game is not done.
- `ProbeAll` throws `OperationCanceledException` when cancelled between two codecs.

### GameProfiles

- `EnsureProfile` writes one `name`, taken from the executable's version resource, else the file name without its
  extension.
- With `captureNewGames` off, the same update sets `ignored`, unless the game is in `manualGames`. The caller is told
  the game's name once the write commits.
- Everything else is the store's default: Recordings off, no overrides, a folder assigned.
- An existing profile is never touched. A conflict with another write is retried while the profile is still missing.
  Any other failure is logged.

### RuntimeStateStore

- The file is `{ "micMuted": bool }`, plus `openRecordings` while a Recording is being written, so the next start can
  tell which ones a dead engine left open.
- It is never in a settings snapshot. A missing or unreadable file means muted.
- Writes are atomic: a flushed temp file is moved over the old one. A failed write is logged and leaves the old file.
- Both values are kept in memory and written together on every change.

### SettingsStore

- `Current` is an immutable document that is swapped only after the file holds it.
- `Update` validates every op, writes the file, swaps `Current` and raises `Changed`. A refused update throws an
  `ErrorCodeException` whose code says why.
- An exception from a `Changed` handler propagates out of `Update` after the update has committed.
- `Open` deletes a stale temp file left by a killed write. An unreadable file is reported as `settings-unreadable`.
- `SettingsRetry.Update` tries up to `Tries` times, and throws `settings-conflict` on the last.

### AppSupervisor

- `app.attach` names the App to watch. When that App exits while no `engine.shutdown` has been received, the engine
  starts it again with backoff and raises `app-restarted`.
- It relaunches only an App that attached, or one it started itself that then failed (a non-zero exit before
  attaching).
- After `CrashLoopRestarts` relaunches within `CrashLoopWindow`, the next exit is a crash loop: it logs, raises
  `app-crash-loop` and stops watching until an App attaches again.

### TracerCapture

- It drives the session only through orchestrator messages: Start, wait (`--seconds` from the anchor, Ctrl+C, or the
  session ending itself), End.
- The session starts with the instance's saved mic mute (muted when there is none). With no Hotkeys it never changes,
  so a muted run silences each Mic Device in the `muteAffected` Tracks.

## Encore.Core

### AudioBlock and IAudioPcmSubscriber

- Frame k of a device's stream is at `origin + k / 48 000 s` in QPC. `StartQpc` is the QPC of the block's first frame.
- Blocks are contiguous: each starts where the previous one ended.
- A subscriber that throws is cut off; its subscription keeps the exception as `SubscriberFault`. The other subscribers
  keep receiving.

### IAudioEndpointSource

- `EndpointsChanged` fires when an endpoint is added, removed, enabled, disabled or renamed, when a flow's default
  (console role) changes, or when the default communications render endpoint changes.
- It carries nothing. Call `ListPresent` again to get the new list.
- When `ListPresent` throws, callers log it and keep their last list.

### AudioDeviceListBuilder

- A Track Layout's device that isn't present with its flow is listed as unavailable, named as the layout names it.
  Global's layout and every profile's are included.
- Devices match by endpoint id (ignoring case) and flow.
- The sort is: flow (Output first), friendly name, parent name (ignoring case), then endpoint id.

### FeedbackRouter

- Recording notices come from the real `recording.started` and `recording.stopped` events. The stop's message names
  its reason.
- Info notices, and the restart's, are withdrawn after `InfoLifetime`.
- A Clip not saved stays until the next saved Clip. A full disk stays until the next Recording starts. A Rewind stopped
  by a full disk stays until it restarts. The user can dismiss any of them sooner.
- With a `FeedbackSoundPlayer`, each notice's sound is queued as it is raised. A sound that is switched off isn't
  played, and its notice carries no `Sound`.
- An encoder restart's replacement Recording (a start after a stop that left the state Running) raises and withdraws
  nothing. Its `recording-restarted` notice follows and plays the only sound.
- A Monitor Recording's notices never touch the game Recording's restart handover.
- `FeedbackNotice`: `Code` is one of `ErrorCodes`; `Source` is the engine area it is about, such as `clips` or
  `recordings`.

### FeedbackSoundPlayer

- The `feedbackSounds` flag is read at each enqueue from the current settings.
- The endpoint is resolved at each play, so it follows a change of the Windows default.
- A failing play is reported to the `failed` callback and skipped.

### FeedbackSoundSynth

- Each sound has a short fade in and out and a peak of exactly `PeakDbfs`.
- Nothing ships as a file, and the samples are identical on every run.

### HotkeyPlan

- With no valid `hotkeys` in the document, the catalog default applies only on the installed instance. Otherwise
  nothing is bound.
- An unbound action is `hotkey-unbound`, whatever the mode. A bound one the mode doesn't use is `hotkey-inactive`. One
  whose combination an earlier action already takes is `hotkey-duplicate`.
- Toggle mode uses `recordToggle` and `monitorRecordToggle`. Separate mode uses `recordStart`, `recordStop`,
  `monitorRecordStart` and `monitorRecordStop`.

### CleanupPlanner

- The total counts every item, Favourites and files being written included.
- A candidate isn't being written, isn't a Favourite, isn't in the skip list, and is an `.mp4` under the folder.
- Candidates are taken by date, then path, while the total is over the size limit or the candidate is older than the
  age limit. The first candidate that is neither ends the plan.
- A candidate is passed over when its file may really be outside the folder (a junction or link).
- `Remaining` is the Library's total size once the planned items are deleted.

### LibraryActor

Threading:

- Every index change, every disk check and every `library.json` write runs on the actor's own thread, which reads a
  single-reader channel. The public members, the timers and the `FileSystemWatcher` only post to it.
- Each item's length is read by a low-priority worker beside the actor. The worker only reads the file and posts the
  result back; the actor applies it, publishes it and caches it in `library.json` by size and modified time.

The index:

- An item's key is its path relative to the Output Folder, compared ignoring case.
- A write publishes `library.itemAdded` (locked) at its start, then `library.itemChanged` with its size, or
  `library.itemRemoved` when it failed. Items being written are never persisted.
- Changes made outside the app are reconciled against the disk, so only what differs from the index is published.
- Reconciling one path: a file that is gone, isn't an `.mp4` or is empty is removed. One indexed at the same size is
  left alone. Any other is listed only when it is a complete MP4. A path reached through a junction or link is
  removed. A busy file, or an incomplete one written in the last 2 s, is checked again at the next settle.

Commands from the App:

- Rename, delete and set-Favourite identify the item by path, size and modified time. A stale identity faults with
  `library-item-changed`; a file that changed on disk is refreshed and published first, so the App's next command
  matches.
- Rename and delete never act on a file being written (`file-being-written`). Each path they touch is registered
  first, so the watcher's echo isn't taken for an outside change.
- Rename keeps the file's own `.mp4` extension, with its case, and publishes exactly `library.itemRemoved` and
  `library.itemAdded`. It can fault with `invalid-file-name`, `file-name-taken` or `command-failed`.
- Delete only ever moves the file to the Recycle Bin, through the injected port. It can fault with
  `recycle-bin-unavailable` (the file is untouched) or `command-failed`.
- After the actor is disposed, commands fault with `engine-disconnected`.

Cleanup:

- Cleanup is the only permanent delete.
- A pass starts on a written Clip or Recording, a change of the `cleanup` limits, the end of a rescan, an acknowledged
  rebuild, 60 s after start, and every 10 minutes.
- It runs only once 60 s have passed since start, while no rescan is pending and Cleanup isn't blocked.
- It logs the planner's plan, then deletes one item per message. It plans again and re-checks the item against the
  index and the disk right before each delete, so a command queued meanwhile (a Favourite) is honoured.
- A file that can't be deleted is skipped for the rest of the pass.
- It never deletes a Favourite, a file being written, a path outside the Output Folder, or a file that isn't `.mp4`.
- `cleanup.preview` runs the same planner over the index without deleting anything.

`Load`, at start:

- It deletes a crash's leftovers: each `*.rbpart`, and an empty `.mp4` beside one.
- It reads `library.json`, else its `.bak`. A `library.json` that doesn't parse is moved to `library.json.corrupt`.
  Entries saved for a different Output Folder are ignored.
- It lists each non-empty `*.mp4` with its saved profile and date when its size still matches its entry. Any other
  file is listed only when it is a complete MP4, with the profile of its top-level folder and its creation time.

Constructor ports:

- `items`: what `Load` found. They are already in the first snapshot, so they are not published.
- `profileForFolder`: the Game Profile whose folder is a top-level folder's name, for files found on disk.
- `cleanupBlocked`: the load was corrupt, so Cleanup stays off until `AcknowledgeCorrupt`.
- `time`: the clock a write's date is read from, and the source of the settle timers.
- `failed`: reports a disk operation that failed. The index is unaffected.
- `createMissingFolder`: whether a missing Output Folder is created so it can be watched.
- `readLength`: reads a file's length in seconds, on the low-priority worker. Without it no length is read.
- `recycle`: moves a file to the Recycle Bin, on the actor thread. It returns false when the volume has none, and
  throws an `IOException` carrying the HRESULT when it fails. Without it every delete is refused.
- `cleanup`: the current limits, read on the actor thread. Without it Cleanup is off.
- `limitUnreachable`: raises `cleanup-limit-unreachable`, once until a pass ends within the limit or Cleanup is off.
- `finalPath`: a path's final target, every junction and link followed. A delete, rename or Cleanup is refused unless
  the item's final path is under the Output Folder's. Without it only the path's own folders are checked.

The saved file:

- `LibraryFile.OutputFolder` is null in an old file, which is then read for any folder.
- `LibraryFileEntry.Size` is the size when saved (null in an old file); it tells a file changed outside the app.
- The entry's length and the modified time it was read at are both null when no length was read. The length is reused
  only while the size and time still match.
- The Favourite flag is kept through a size change that happened while the engine was off.

### ILibraryWrites

- Clips and Recordings use it. The LibraryActor implements it, and `NoLibraryWrites` is the no-op for a host or test
  with no Library.
- Neither call blocks or throws, and any thread may call them.
- At `BeginWrite` the reserved empty file may already exist.

### Mp4File

- A box is a 32-bit big-endian size and a four-letter type. A size of 1 means a 64-bit size follows, and 0 means the
  box runs to the end of the file.
- A file is a complete MP4 when its top-level boxes start with `ftyp`, include a `moov`, and end exactly at the file's
  length. A copy that stopped, or a write cut off mid-box, fails that walk.
- The length patch: Media Foundation otherwise stops at the last `tfra` entry. The patch turns the first 20 bytes of
  the `udta` right after `mvex` into a version-1 `mehd` holding the duration in the `mvhd` timescale, rounded up, and
  the rest of the `udta` into a `free` box. It then grows `mvex` by 20 to take the `mehd` in as its last child. No
  byte after the `moov` moves.
- The patch is planned only for exactly this layout: a `moov` with an `mvhd`, and an `mvex` with no `mehd` directly
  followed by a `udta` of 20 bytes or at least 28, both with 32-bit sizes.
- `FragmentDurationPatch`: the first write, at the `udta`'s start, is the `mehd` (20 bytes) and then the `free` box's
  header, unless the `udta` was exactly 20 bytes. The second write is `mvex`'s new size.

### MonitorCanvasRule

- The canvas is the bounding box of the screens at their physical positions and sizes.
- A canvas wider or taller than H.264's largest side, when H.264 is the Output codec, is encoded with HEVC, else AV1,
  whichever this PC can encode, at full size. H.264 stays for a single screen or a canvas that fits.
- The canvas is scaled down, aspect kept, only to fit the codec's largest side. There is no pixel budget. Both sides
  are rounded down to even.
- With no HEVC or AV1 encoder at all, a canvas over 4096 px stays H.264 and is fitted to 4096.
- A screen without a position (from an older engine) is placed right of the previous one, top-aligned.
- `CanvasPlan.Scaled` is true when the encoded size is below the native one.

### MonitorScreens

- `All`: every attached monitor.
- `Selected`: each id is matched to an attached monitor by `MonitorId`, else by `DeviceName` (a value carried over
  from the older key). The unmatched ids are returned as `Missing`. With none attached there are no screens.
- Null: the older key's monitor, else the primary one when that key is null or names no attached monitor.

### OutputPathAllocator

- A path is `<Output Folder>\<profile folder>\<Game>_<yyyy-MM-dd_HH-mm-ss>.mp4`.
- A Monitor Recording's path is `<Output Folder>\Monitor Recordings\Monitor_<yyyy-MM-dd_HH-mm-ss>.mp4`.
- Nothing is cached: each path is reserved on disk as an empty file before it is returned.

### PathNames

- `Sanitize`: invalid and control characters become `_`, surrounding spaces and trailing dots go, and a name whose
  part before the first `.` is a reserved device name gets a `_` prefix. A name with nothing left becomes the
  fallback.

### SourceOutputSize and SourceSizeWatch

- The codec limits are `MonitorCanvasRule`'s constants: 4096 px for H.264, 8192 px for HEVC and AV1. The codec is
  never switched for a game.
- A game under 640×360 (a launcher or a minimised window) never sets the size. In the watch, such a frame neither
  starts nor resets a candidate.
- A new size must hold for `Hold` of frame time before the watch fires.
- While a Recording is attached nothing fires, because a Recording is never split or resized. A candidate still held
  fires at the first frame after the Recording ends.
- A fire clears the candidate. The caller stops observing while its move runs, and a move that didn't happen needs the
  size to hold again.

### MemoryMargin

- A slab is refused when it would leave less than `MarginBytes` available. Once inside the margin, the ring leaves it
  only when a slab would still leave the margin plus `LeaveSlackBytes`.
- There is one per engine, shared by every ring under its own lock.
- `Raised` is invoked outside that lock: `Entered` once per entry, and `ShortRing` at most once per entry.

### PacketRing

Storage:

- Packets of every stream are kept newest last, trimmed to the Buffer Window plus `SlackUs` of GOP slack.
- The entry array is preallocated and circular (it doubles only when full), and the bytes go into the current slab,
  so an append allocates nothing managed.
- A keyframe index beside it holds the sequence numbers of the video keyframe entries, so a cut finds its keyframe
  without scanning every entry.
- A slab's reference count is the ring's one reference (while the slab is current or holds live entries) plus one per
  cut that reads it. At 0 its memory is freed or its block returned. So trimming or disposing the ring never frees
  bytes a writer is reading.
- `Append` and `Cut` take one short lock. A cut's bytes are read with no lock, since they are never rewritten.

Memory and disk:

- In Memory mode a slab is native memory. In Disk mode it is one block of a `RingFile` the ring owns. Only a packet
  bigger than a block, or a slab wanted with no block free, is native memory.
- The file is closed, and so deleted, once the ring is disposed and its last slab freed.
- Before each new native-memory slab the ring asks its `MemoryMargin`. When refused it stops growing: it drops its
  oldest entries until slabs it alone held free the new slab's size, never the current slab's, and then allocates.
  Slabs a cut pins leave the ring but free nothing, so the trim goes on past them.
- A Disk-mode ring tries a block again after that trim before it spills to memory.
- When no trim could free anything (every older slab is pinned), nothing is dropped.

`Cut`:

- K is the first video keyframe at or after the start. The cut holds every video packet from K that starts before the
  end, and every other packet whose centre lies between K and the end, in time order.
- Two cuts that share a boundary therefore split the packets exactly, none in both and none missing, and each audio
  end is the AAC boundary nearest the video's.
- Under the lock it only pins: K from the keyframe index, one contiguous copy of the entries from just before K to
  the newest, and the slabs that range touches.

`CopyTailTo` (a Buffer Window resize):

- It copies from where a cut from the given time would start: its keyframe less the audio look-back, or, with no
  keyframe, the first packet at or after the time less the look-back.
- A target with no streams first gets this ring's streams, in the same order.
- The tail can span the whole time a long Clip took to write, so it is copied in batches of `CopyBatch` entries, each
  under this ring's lock and then the target's only. `CopyFrom` then copies what was appended meanwhile.
- The look-back exists because audio that leads the video is appended before K though it overlaps it. Lagging audio
  is appended after K and is caught anyway.

### ReplayStorageEstimate

- The 3 s is `PacketRing.SlackUs` plus one GOP. The two slabs are headroom.
- The Disk-mode ring file is sized from this estimate.

### RingFile

- The file is `ring-<n>.bin`. `<n>` counts across the process, so two rings never share a file.
- Bytes go in and out by positional `RandomAccess` calls on one handle, never a memory map, so writeback takes the
  handle's I/O priority hint.
- The file is never grown. The handle is opened with `DeleteOnClose`, so the file goes when it closes, even when the
  process dies with it open.
- `Create` makes at least 2 blocks and never exceeds the size it was given. It throws `IOException` or
  `UnauthorizedAccessException` when the folder or file can't be made.

### RingGenerations

- A `Restart` (a forced encoder restart) opens an empty generation whose first video keyframe is its start. That
  boundary counts as a Reset.
- A Clip holds one generation. A cut ending less than `MinClipUs` past a boundary comes from the previous generation,
  ending at the boundary, if that holds at least `MinClipUs`.
- A `Resize` (a Buffer Window change at a Reset) opens a generation with the same streams, seeded with the old ring's
  packets from the Reset keyframe on, and retires the old one at once, so the timeline stays continuous.
- A retired ring is disposed once no cut is reading it. Slabs a cut pins outlive it.
- Routing a packet is a field read under one short lock. A cut's filter and sort run outside that lock.
- `Restart` precondition: no save is waiting for its keyframe or its streams, since `ToMicros` and `AllStreamsPast`
  read the new generation from then on, and it has no streams until the new chain adds them.
- A restart while the current generation is itself a restart's with no keyframe yet (a failed recovery attempt, say)
  replaces that empty generation. Its predecessor stays the new one's, so content before the first restart can still
  be cut.
- `Resize` is called from one thread, never alongside `Restart` or another resize. Most of the tail is copied without
  the lock, so the encoders keep appending.

### ICaptureSession

- `Retarget`: a throw leaves the session without capture; the orchestrator then ends it.
- `SetMicMuted`: each Mic Device in each `muteAffected` Track is silenced, or heard again, from the first audio frame
  at or after the time.
- `PrepareChain`: the first save press whose write starts once the chain is built switches the session to it, so the
  Clip after that one has the new Tracks and video, with no gap. A session with no Rewind ignores it.
- `Save`: the session asks its encoder for a keyframe on the next frame, where the Clip ends.
- `SwitchChain`: Rewind starts empty on the new chain, and the gap between the chains is raised as `reset-gap`. The
  caller retries a refused switch at the next Reset.
- `RecoverDevice` posts `ChainRestarted` when it succeeds, or `DeviceLost` again when it fails.
- `IRecordingWrite.Start`: the write runs from the first video keyframe after the press until `Stop`, or until the
  session's output ends. A failed write whose file holds data keeps it; one that wrote nothing leaves no file behind.
- `IClipWrite.Write`: a failed write leaves no file behind, the reserved one included.

### SessionOrchestrator

The basics:

- It runs on one dedicated thread that reads a single-reader channel. Callers and media threads only post to it.
- It is the only component that starts, retargets and ends sessions and that publishes `CaptureState`.
- It owns mic mute: loaded from the store before any session can start (muted when nothing is saved), toggled by each
  Mic Mute press, saved, published, put in each session's spec at Start and handed to the running session with the
  press's QPC.
- A session's settings snapshot is resolved once at Start. A retarget never touches it.

Saving a Clip (with `ReplayClips`):

- The Save Cooldown comes first: a press less than the Global `saveCooldown` after the last accepted press is ignored.
- Any other press is accepted. It starts the next cooldown, publishes `CooldownUntil` and `clip.saving`, and an
  outcome always follows.
- With `MaxWriters` Clips already being written, the press is refused with `save-busy`.
- A press less than 1 s after the session's last Reset is refused with `clip-too-short`, before the session is asked
  for anything: no keyframe, no Clip, no Reset.
- Otherwise the path is reserved in the Global Output Folder (else the default one), the Library is told the write
  begins, and the session writes the Clip. `ClipsWriting` follows each write as it starts and ends.
- The writer posts its result back. Only then is the Clip published as saved and Rewind Reset at its end. A failed
  write Resets nothing, so the next Clip still starts at the last Reset.

The exit drain:

- A game session whose Target exits, or whose window closes, is drained, not ended. It keeps its Rewind, `Draining`
  is published, and save presses still cut from it.
- The drain ends when `ExitDrain` passes, a new session starts, or the orchestrator stops.
- A switch to another game, or to Ignored, ends the session at once.

Recordings (with `Recordings`):

- The Record actions depend on `recordHotkeyMode`, read live from the current document. `recordToggle` stops the
  running Recording, else starts one. `recordStart` starts one only while none runs, and `recordStop` stops only a
  running one. A press while a Recording is stopping is ignored.
- A start needs the profile's `recordingsEnabled`, read live. A stop isn't gated by it.
- The path is reserved beside the game's Clips. The writer posts its result back before `Recording` goes off.
- A session that ends mid-Recording finishes it with everything encoded. The stop reason is what ended it: the
  Target's exit or its window closing (`game-exit`), an Ignored handover (`ignored`), the orchestrator stopping
  (`shutdown`), else `session-ended`.
- While a Recording runs, a switch of the Target to another game is held, and committed the moment the writer posts
  back. An exit or Ignored handover is never held.
- On `Dispose`, a running Recording's writer is waited for (up to `ShutdownWait`). One not done by then is left open.
- A forced encoder restart ends the running Recording with `encoder-restart` and starts a new one at once on the new
  chain, in a new file, without reading `recordingsEnabled` again. Nothing is published until the old writer posts
  back; then the old one is published as stopped, the new one as started, and `Restarted` names both. `Recording`
  stays on throughout.
- A Recording whose disk fills is stopped with `disk-full`. When the session's ring is on that disk, the session ends
  too, and a timer rechecks every `RingRecheck` until the ring fits again, when Rewind restarts on the Target.

Settings changes for the game session:

- The settings are resolved again for the profile on each settings update and after each Reset.
- When they need another encoder chain (`ChainKey.SameChain`), the session is asked to prepare it. A later change back
  drops it.
- The save press whose write switches the session updates the session's spec: its snapshot and audio. Its Target is
  kept.
- While a Recording runs on the session, it keeps one chain: a chain prepared before the Recording starts is dropped,
  and a change is not prepared but logged once. When the Recording's writer posts back, the settings are resolved
  again, so the first save after the stop switches.
- A Track Layout that can't be resolved is skipped. A failure of the session is swallowed.

The Monitor session:

- A Monitor Record press starts the Monitor session in a second slot beside the game session, on the Global snapshot
  and Global Track Layout.
- At its anchor a Recording starts on it in `<Output Folder>\Monitor Recordings`. A later press stops it, and once
  its writer posts back the Monitor session ends. `MonitorRecording` is on from the press until then.
- A stop before the session anchored ends it at once, with no Recording.
- It never touches the Target and never holds a Target switch.
- Encoder chains are counted. A chain the game session prepared is dropped first; when the game session still runs
  `MaxChains`, the press is refused with `encoder-budget`.
- While the Monitor session runs, a chain change of the game session is not prepared in advance. It is switched at the
  next Reset, tearing the old chain down first. A refused switch is retried at the next Reset.
- Screens: each chosen screen that isn't attached raises `monitor-missing` and is skipped. With none left the start is
  refused with `monitor-recording-no-screens`. A monitor whose mode reads 0×0 counts as not attached.

A lost GPU device (`DeviceRecovery`):

- The first loss calls `Reset` and asks the session to rebuild at once.
- Each failed attempt is retried after `RetryEvery`, until `GiveUpAfter` has passed since the loss. Then the session
  is ended and `Failed` is called.
- A given-up game session stays off until the Target's process changes. A new window of the same process doesn't
  restart it.
- A recovery posts `ChainRestarted`, which calls `Recovered` and hands a running Recording over as any forced restart
  does. A Monitor Recording is handed over the same way.

Sleep and resume:

- A sleep stops a running Recording with `suspend`, ends the Monitor session, raises `recording-suspended` once when
  either ran, and suspends the game session, keeping its Rewind saveable.
- The resume drives the same recovery as a GPU reset, with its retries and give-up, but without the `Reset` and
  `Recovered` callbacks. No Recording starts again.

Display changes:

- A display change ends a Monitor session whose monitor is gone. Its Recording stops at its last frame with
  `monitor-removed`, and the warning of the same name is raised.
- A Monitor session that closes or fails while its monitor is gone ends the same way.
- Game capture is of a window, so a display change never touches the game session.

Following the Target's process (`TargetCapture.Wait`):

- The game session follows the Target's process, not one window. It starts only on a window of the process that can
  be captured, polled for until there is one.
- A failure, or a window that closes before the first frame, starts it again after a bounded backoff.
- After the first frame, a window that closes or can no longer be captured is replaced by a retarget to the process's
  next window, keeping Rewind. With none, the session waits, its last frame repeated.
- A session whose GPU device is lost is left to its recovery.
- Only the Target's exit or change, Ignored, or a GPU give-up ends it.

Committing a Target:

- Another process, or none, becomes the Target and is published. A new window of the Target's own process only moves
  the capture to it.
- While a Recording runs, each later held switch replaces the earlier one. A proposal of the Target itself drops it.

Constructor arguments:

- `currentDocument` is read once per Start, for the snapshot.
- `publish` receives each changed `CaptureState` once per change. A loaded mute that differs from the initial state is
  published from the constructor, before the thread starts, so it is in place before anyone can connect.
- `micMute` null keeps mute in memory only, starting muted.
- `targetCapture`, `clips`, `recordings`, `power` and `displays` each turn a feature on when given.

### CaptureWait

- A game still starting can show a placeholder window (156×27 for Rocket League) that WGC never delivers a frame from,
  then swap it for a splash and its real window. So no game session starts until `Find` names a window that can be
  captured.
- `Find` returns the given window when it can be captured, else the process's largest window that can.
- The backoff for a failure starts at `FirstRetry` and doubles up to `MaxRetry`.
- `Retries` says which failures are retried; null means `capture-failed` only.
- A run of waits is reported once by `Waiting`, and its end once by `Ended` (true once the Target is captured again).

### ReplayClips, Recordings and DeviceRecovery

- Every callback is called on the orchestrator thread, and a null callback is ignored.
- `ReplayClips.Failed` is for logging a write that could not be made; it is not called for a refusal. `NotSaved` is
  called for every accepted save that made no Clip, with its reason.
- `Recordings.Info` says why a press made no Recording, or what a started one shares that differs from the profile's
  settings.
- `Recordings.Open` keeps each Recording's path from just after the Library is told it begins until just after it is
  told how it ended.
- `Recordings.ReplayDiskFull` is called with true when a full disk stops a Disk-mode Rewind, and with false when that
  wait ends.
- `Recordings.MonitorStarted` and `MonitorStopped` fall back to `Started` and `Stopped` when null.

### SessionAudio.FromLayout

- Tracks keep the layout's order. Devices are the distinct ones some Track references, in first-reference order.
- A device is one endpoint and flow, however many layout keys name it. A device no Track uses is left out.
- A Track with no devices is a silent Track.
- A device is a Mic Device when any layout key naming it is one. That is the safe side for a privacy control.

### SessionSpec

- `Target` changes on a retarget. A game session's `Snapshot` and `Audio` change at a save that switches its chain.
- `MicMuted` is the mute at Start. Later toggles reach the session through `SetMicMuted`, and the spec keeps the Start
  value.

### ChainKey

- Two specs share a chain when their keys are equal and their mic mute inputs match: each a Track name and a device
  endpoint (ignoring case) and flow, in order.
- So a change of `isMic` or `muteAffected` alone needs another chain too.

## Encore.App

The App never opens media files itself. It gives the Windows shell a path, and renders only what the engine's state
snapshot holds.

### EngineConnection

- The class is the session the views use (state, send, Quit). `EngineConnector` makes one attempt with the server
  check and launches the engine; `EngineSupervisor` runs the supervise loop; `EngineConnectionState` holds the
  status, the problem and the live link for all three.
- `ConnectAsync` connects, launching the engine when none answers. It doesn't supervise.
- `StartSupervising` keeps the engine running: it attaches with `app.attach`, kills an engine that is hung, and
  relaunches one that crashed, with backoff and `serve --restarted crashed|hung`.
- It raises `EngineEnded` when the engine ends cleanly on its own (an `engine.shutdown` from elsewhere, or Ctrl+C).
- A crash or hang after `CrashLoopRestarts` restarts within `CrashLoopWindow` is a crash loop: no relaunch, status
  `CrashLoop`, until `RestartAfterCrashLoop`. That restart passes no reason, as the user asked for it.
- `ShutdownAsync` stops supervising, then shuts the engine down for Quit. The App exits even when the engine didn't
  exit in time.
- The server check returns the engine's process with its handle open. That is the one handle the App may kill it
  through.

### EngineWatchdog

- The engine is hung after no event for `SilenceLimit` (3 missed heartbeats; any event counts), or a command with no
  reply for `ReplyLimit`.
- It only keeps time. The connection acts on `Check`.

### LibraryViewModel

- The class builds its parts and gives each snapshot to them: `LibraryList` (rows, grouping, filters, sort, search),
  `LibrarySelection` (selected and focused rows), `LibraryFileActions` (the commands' work and the error line),
  `LibraryPlayback` (the player page and the clip creator) and `LibraryBanner`. The notes below say what each does.
- It only reads the snapshot. Items are shown newest first, each with its name, game, date and size. An item still
  being written is shown locked.
- While Cleanup is blocked by a rebuilt index a banner says so, and Acknowledge dismisses the `library-corrupt`
  warning.
- Each row shows its length and the shell's thumbnail, asked by absolute path once the item is written, never while it
  is.
- A row's star sends `library.setFavourite` and changes only when the snapshot does.
- The list is grouped by game or by day and filtered by game, date and Favourite. The choice is remembered per
  instance.
- The sort orders rows within each group, or one flat list under Group by None. Search matches file names. The sort is
  remembered; the search isn't. Both only change what is shown, so the selection keeps a searched-out item.

Selection:

- The selection is the page's own set of item paths, with an anchor and a focused path. It is never the list
  control's.
- A filter hides selected items but keeps them. A snapshot drops the ones that left the Library. The App's own rename
  carries them to the new path.
- Clicks follow Explorer: plain selects only the row, Ctrl toggles it, Shift selects the range from the anchor in
  shown order (headers skipped), and Ctrl+Shift adds that range. Plain and Ctrl make the row the anchor; every click
  makes it the focus.
- Keys: plain selects only the target, Shift selects the range from the anchor, Ctrl moves only the focus.

Actions:

- Every action acts on the shown selected rows, in shown order.
- Favourite, Delete and Rename go through the engine's per-item commands, sent one at a time. Delete asks one
  confirmation and leaves out Favourites and files being written; with nothing deletable, nothing is asked or sent.
- Open, Copy (one file drop), Copy to folder and Show in Explorer go through the shell by absolute path, after
  checking only that each file exists.
- Rename is inline and disabled while the row is locked. A refusal shows its short message.

Other details:

- An item's game is its profile's live name; else the profile whose folder is the item's top-level folder (scanned
  files carry no profile id); else that folder's name; else "Other" at the Output Folder's root.
- The search binding is delayed in the view, so it applies at most every 150 ms while typing.
- `Sync` is linear: unwanted items are dropped, and when what is left is still in the wanted order the new items are
  merged in.
- Row order: Newest and Oldest first go by date, then path. Name is natural order, ignoring case. Largest and Longest
  first put an unknown length last. Game goes by game name.

### LibraryViewState

- A file from before the sort was added loads with Newest first. A file from before the player's volume was added
  loads at full volume, unmuted.
- The search text isn't kept.

### LibraryOverflowPanel

- Children marked `Overflows` are hidden when the row doesn't fit, and the child marked `IsMore` (the "More ⋯" menu
  that holds them) shows.
- Hidden children are still measured, so the panel can tell when they fit again. They are arranged at zero width.

### LibraryThumbnails

- Thumbnails are never cached on disk. A file with no thumbnail isn't cached, so it is asked again only when its row
  asks again.
- `Request` runs on the UI thread.

### PlayerViewModel

- It opens the file read-only, shared for read, write and delete, and hands the stream to the player.
- It opens paused on the first frame with the first Track. Windows would otherwise pick the last.
- The transport: play and pause, a seek bar over the file's length with elapsed and total time, keys that seek 5 s and
  change the volume 5 %, and one volume with mute, kept by the Library.
- A file with no known length (a Recording cut short by a crash) shows the elapsed time only, and can't be sought.
- The Track list shows the file's Tracks by name, in file order. One plays at a time. Choosing another switches
  mid-play, with no restart and no jump; Windows leaves a gap of about 0.1 s.
- At the end it stops on the last frame, and Play starts again from the beginning.
- When Windows can't play the file the page says why in plain words: the missing HEVC or AV1 Video Extension by name,
  with its Microsoft Store page; or a file it can't open or that shows no picture. "Open in default player" is offered
  either way. A Track Windows can't decode shows as unavailable.
- Disposing it stops the player and releases the file.
- The player's own events are brought back to the UI thread.

Volume:

- A set always raises PropertyChanged, so a slider at 0.333 snaps to the 0.33 that is kept.
- Any request while muted unmutes, by the slider or the keys, even one that leaves the value as it is (Up at 1.0,
  Down at 0).
- `VolumeChanged` is raised once, when the volume or the mute changed.

### PlayerTrack

- A Track's name is the stream's `handler_name`, which the writers set to the Track's name. A Track with no name is
  "track n", counted from 1.

### PlayerView

- The player's frame events are coalesced into one present on the UI thread. It copies the frame into the
  `VideoSurface` and marks the `D3DImage` dirty.
- The surface's two devices are made once per page. Its texture is sized to the picture's pixels, with the video's
  aspect and never above the video's own size.
- The texture is remade only after a resize settles. Until then the old one is drawn scaled, so a paused picture never
  blanks.
- A GPU failure building the surface, or no picture within 5 s of opening, puts the page in its Failed state. A
  failing HRESULT can arrive as `COMException`, as `OutOfMemoryException` (out of video memory) or as
  `ArgumentException` (a size the GPU won't take). A frame that can't be copied is skipped.
- After a lost front buffer (a lock screen, a display change) the surface is remade on new devices and the current
  frame redrawn. While the front buffer is gone the no-picture timer pauses, and it restarts in full when it is back.
- Leaving the page drops its frames before the back buffer goes.
- The position is read for the seek bar about ten times a second while playing.
- Keys bubble to the page (KeyDown, not PreviewKeyDown), so a focused control's own keys win.
- The gear's menu is the theme's ContextMenu: Esc, a click outside and a choice close it. Its heading is not an item.
- The no-picture wait is `NoPictureWatch`, the texture size rule is `PictureFit`, and one present's work and the
  page's keys are `PlayerViewModel.DrawFrame` and `PlayerViewModel.HandleKey`.

### WindowsMediaPlayer

- It is WinRT's `Windows.Media.Playback.MediaPlayer` (Media Foundation) in frame-server mode. Each frame is copied
  into the page's `VideoSurface`.
- Quirk: a Track switch while paused makes Media Foundation's next Play resume from the start, though a paused seek
  after it shows the right frame.
- So the first Track is chosen as the Tracks are first listed, before the file opens, with no switch. And after a
  switch while paused, Play moves back to the paused position.

### MainViewModel

- A settings page that needs more than its settings takes the snapshot through its own method before `Render`. The
  calls stay in one fixed order, because that order decides the order of the meter watch and unwatch commands.
- Everything shown comes from the engine session's state.
- Audio Tracks and a game's page meter only while on screen, so a hidden window doesn't keep the engine metering.

### OverlayViewModel

- `OverlayPlaneQuery` asks about the monitor's overlay planes and `OverlayMessageQueue` queues the messages; the view
  model keeps the dot, the icons and the order they show in.
- A snapshot read at reconnect is treated as any event: the dot and the saving icon follow it.
- Messages: the snapshot's warnings with `overlay: true` queue up. Each shows for `MessageDuration`, oldest first, and
  an unseen repeat of a waiting one takes its place.
- Which warnings were shown is the Overlay's own fact, kept by id, so a reconnect never replays one.
- The dot and the mic-muted icon show always, or only for `DotOnChangeDuration` after each change of either, as the
  `overlayDot` setting says.
- The default, `auto`, shows them always where the Target Game's monitor reports hardware overlay planes. A borderless
  game stays in independent flip under a topmost window only with a free plane.
- The plane query runs in the background and posts its answer back. Until it answers, and when it can't say, the
  monitor is taken to have no planes, so the dot shows on change. An unknown answer is asked again after
  `UnknownPlanesRetry`.

### OverlayWindow

- The dot sits in the top-right corner of the Target Game's monitor. To its left, right to left: the mic-muted icon,
  the green saving icon, and a red X while the disk is full. The queued message shows below them on the theme's card,
  in its success colour for a notice and its danger colour for an error.
- The glyphs keep fixed colours in both themes.
- It is a topmost, click-through window that is never activated, and it permits screenshots and recording.
- The window is one fixed-width strip of fixed slots, so the dot stays put as the icons come and go. While a message
  shows it grows left and down.
- It is hidden while there is no dot, icon, X or message. Nothing is drawn in or hooked onto the game.

### PromptDialog

- It is an owned, modal `ShellWindow` that follows the theme live. Yes is the default button and has focus first.
- The window keeps no resources of its own: a window-scoped ThemeMode, which the tests use, skips a window whose
  Resources were set first.

### AudioTracksDraft

- A device is one record across Tracks. Adding a source reuses the record of that endpoint and flow, so "Treat as a
  microphone" applies on every Track that has it.
- A recording device added through the picker is a Mic Device; a playback device isn't.
- `ToLayout` drops a device that is on no Track. The devices keep their first-use order.

### DrillIn

- It closes on Back, the parent link, Esc, Alt+Left, the mouse's Back button or the parent's nav item. The shell
  routes those to it.
- The parent keeps each item's view model, so a draft survives going back.

### FolderPicker

- A pick sends the key's Global `set` at once. Use default sends `remove`. A cancelled dialog sends nothing.
- It also shows the drive's free space, and a danger line when the last choice was refused.
- The file variant (`IsFile`) opens a file dialog and has Browse only.

### GameViewModel and GameProfilesViewModel

- A game's page holds: inline rename, Capture (`ignored`, inverted), then Clips, Video quality and Audio tracks as
  inherited settings over override set and remove ops (Track ids are kept), Recordings, the read-only Files, and
  "Stop detecting it" for a hand-added game.
- An inherited setting shows the main value as text with "Change for this game" and "Edit the main setting". A changed
  one shows the editor with "Reset to the main setting", which asks first.
- Add a game picks a program with the file picker and sends `game.add`, which writes `manualGames`.
- Each game page is kept for the run, keyed by its profile id, so a draft survives going back.

### HotkeysViewModel

- Each change sends one Global op at once.
- A combination another action has is refused before sending, by the engine's rule: same key and same modifiers,
  ignoring NoRepeat, whatever the mode.
- The rows are grouped into Clips, Recordings, Monitor Recording (with the screen picker) and Microphone.

### LibrarySettingsViewModel

- Cleanup deletes permanently. So turning a limit on, or lowering one, first asks the engine what it would delete
  (`cleanup.preview`) and sends the change only after the user confirms. Raising a limit or turning it off sends at
  once.
- An invalid entry is not sent: the page shows the rendered value again with a message.

### MonitorPicker

- All screens, or Chosen screens with one row per screen, at least one kept ticked.
- A layout preview shows a tile per screen at its position. It is hidden when the engine reports no positions. Each
  tile toggles the same selection.
- The output line comes from the canvas rule. Chosen screens that aren't attached are shown greyed.
- Each choice is one Global `monitorRecordingScreens` op, sent through the hosting page. The App never writes the older
  `monitorRecordingMonitor` key, which is only read while the new key is null.
- The list comes from `monitors.list` once per engine run and follows `monitors.changed`.

### RewindSettingsViewModel

- The video quality editor is `VideoQualityViewModel` (`Video`), shared by the Clips page and a game's page. The
  estimate's wording is in `StorageEstimateText`.
- It edits `bufferWindow`, `replayStorage`, `ringPath`, `saveCooldown` and `video`, each sent at once as one Global
  op.
- It shows the engine's storage estimate and warns, blocking nothing, when it is over the free bytes.
- With a profile id it edits that game's `bufferWindow` and `video` overrides: each key either uses Global, shown and
  not editable, or has the game's own value.
- The cache folder's picker is enabled only under Disk storage.

### SettingsViewModel and SettingsSender

- `SetAndSend`, `ShowGlobal` and `Refuse` are the three steps most settings repeat: keep and send a value, show a
  rendered value, and show a refused entry's old value with the reason.
- A page renders the settings document it is given and changes it only through `SendAsync`, against the revision it
  rendered.
- On success the `settings.changed` event renders the result. On a conflict the page reloads the current document. On
  a refusal it shows its document again.
- The sender never retries: a conflict comes back as a reload.

### TrackLayoutViewModel

- `SoundPicker` (`Picker`) is the sound picker, and `MeterWatch` keeps the engine's meter watch in step with the
  page.
- Save changes sends one `settings.update`.
- While the page is open it meters the draft through `meters.watch`, with one probe Track per source and, while the
  sound picker is open, one per listed device.
- A game without its own tracks shows a read-only summary until "Use the main audio tracks" is unticked.

### ShellApp

- `AppArguments` reads the switches, `TrayMenu` builds the tray menu, `AppTray` owns the icon and `AppLog` writes the
  log file.
- The App starts in the tray and runs until Quit, or until the engine ends cleanly on its own.
- `--tray` starts it with no window, as Start with Windows and the engine's relaunch do.
- `--instance Encore-Test-*` runs it on a test instance. `--rewind` (test instances only) passes the engine's own
  switch on, so a test instance's engine buffers and saves.
- `--player-unsupported=hevc` or `=av1` makes the in-app player report that codec as undecodable. It is ignored on the
  installed instance.
- An engine crash loop puts the tray in its error state, with a Restart item, and is written to `logs\app.log` in the
  instance's local data folder.
- The tray menu's status item and Restart item have fixed automation names (`TrayMenu.StatusItemName`,
  `TrayMenu.RestartItemName`). Dev and test instances also get a Control gallery item.

### StartupRegistration

- A dev or test instance saves the setting and leaves the registry key alone.
- Start with Windows launches the App with `--tray`, and the App starts its engine.
- The first `Sync` always reconciles, so a key that is missing, stale or points elsewhere is fixed at start.
- A failed registry write leaves the key as it is and is retried on the next committed revision, not on every state
  change that carries the same document.

### ThemeService

- Fluent is WPF's `ThemeMode = System`, which follows the Windows mode and accent live. Gaming dark is
  `ThemeMode = Dark` with `GamingDarkTheme` merged after Fluent's dictionary.
- A switch swaps the merged dictionary in place, so every window, popup and menu restyles with nothing recreated.
- `DisableFluentBackdropSwitch`: WPF's Fluent theme puts Mica behind every window by itself. The App wants it on the
  Main App only, and never under Gaming dark, so the switch turns that off and `Decorate` sets the backdrop itself.
- WPF caches that switch the first time any window shows. So the App's runtimeconfig (and the test host's) sets it
  from process start; setting it in code would be too late whenever a window showed first.
- `Decorate` leaves transparent windows (the Overlay) alone. A window with no handle yet is set when it gets one.
- The area dictionaries (settings layout, Library, Games, Audio Tracks) serve both themes. Their keys are their own and
  they name the theme's only through DynamicResource, so where they sit in the merged list doesn't matter.

### TrayCapture

- The tray shows the capture state whatever `overlayDot` says, so the state can be read when the Overlay's dot is
  hidden. The tooltip names the state.

## Encore.Bench

Bench is the measurement tool. It never compiles against the App; where it needs the App it runs the real executable
on a throwaway test instance.

### Calibrator and GpuLoadWindow (Encore.Bench.GpuLoad)

- The load is a borderless, unowned popup over the primary monitor (or `--size`) with a D3D11 flip-model swap chain
  (FLIP_DISCARD; ALLOW_TEARING when supported; sync interval 0) that draws one fullscreen triangle.
- Its pixel shader runs a loop of N trig iterations animated by time, so every frame is new to WGC and the GPU does the
  work a game would.
- Calibration: each window's measured rate scales N by (measured / target), held to ×0.25–×4 a step so a frame never
  grows long enough to trip the driver's timeout, and damped once within ×1.5 of the target.
- `--mode fullscreen` takes the flip-model swap chain DXGI fullscreen, which Windows runs with fullscreen
  optimizations, as most games' "exclusive fullscreen" is today. `--mode exclusive` uses a blt-model (DISCARD) swap
  chain, as an older exclusive-fullscreen game would, and Bench starts it with fullscreen optimizations off. Both
  present with sync interval 0 and no tearing flag, which exclusive fullscreen refuses.
- The default target of 80 fps is close to what Cyberpunk ran at in the first measurement, at 2560×1440.

### Overlay host (Encore.Bench.Overlay)

- It prints `ready 0x<hwnd> excluded=<bool> exstyle=0x<hex>` with the dot hidden, then reads `show` and `hide` lines on
  stdin and answers each with `shown` or `hidden`. It exits at `quit` or when stdin closes. `--pid <game>` picks the
  monitor.
- It uses the same WPF window properties and the same `CaptureExclusion` and `OverlayWindowStyle` calls as the App's
  Overlay, without the engine session behind it.

### AppOverlayHost

- "On" is the stub Recording (the solid dot), then a saved and a failed notice. "Off" stops the Recording.
- The load's Game Profile is set Ignored first, so Game Detection starts no capture.

### BorderProbe

- The band checked is `Margin` pixels each side of the marker window's edge.
- A border of 2 px round a 640×360 window is about 8 % of the band, so 2 % is a safe threshold.
- The images are kept beside the run's other files for a person to look at.

### ConformanceDrift (`bench drift --app`)

- The real App and engine (the local publish) run on a throwaway test instance with everything on. Bench's marker
  window is the Target Game, listed with `game.add` and with Recordings on, so Rewind buffers it and the Overlay shows.
- The Library and Cleanup run on the instance's Output Folder. Save is pressed every `--save-seconds`. A real Recording
  runs for `--minutes` (30 by default) from a Record Hotkey press.
- The Track Layout is the builder's devices: `--layout` reads one from a settings file, read-only; by default it is
  four Tracks over `--device-a`, `--device-b` and `--input-device`.
- Two tone players burst into A and B half a second apart, on the marker's flash grid.
- The analysis: per Track and toned device, A/V drift; between every two series, Track-to-Track drift; and every
  Track's length against the video's, which also bounds an input-only Track that hears no burst. Each is held to one
  frame.
- It holds the `.bench-running` lock for the whole capture.
- `AnchorQpc` in its log is the QPC at the Recording's file time 0: the Record press plus the engine's logged key
  offset, or a 50 ms guess when that is missing.

### DriftMath

- Per burst k: `error_k = (onset − flash frame time) in the file − (burst QPC − flash QPC) at source`. Each burst is
  paired with the logged flash nearest it, within ±0.5 s.
- Logged markers are found in the file through the anchor, refined per stream by the median residual. That takes the
  stream's constant pipeline offset out of the mapping, so it only identifies markers and never enters an error.
- `AbsoluteOffsetMs` is the first bucket's median: the constant pipeline offset that the drift leaves out.
- Audio-to-audio is |output error| + |input error|. It has no acoustic coupling, so it trusts the stamps.
- `AvInput`: `AnchorQpc` is an estimate of the QPC at file time 0, good to well under 0.5 s. `FrameMs` is one video
  frame at the output fps, which is the target. `BucketSeconds` is a minute by default.

### DriftBenchmark

- A track's length is compared with the video's span from its packet timestamps, not its frame count over the fps: a
  slot the pacer skips leaves a gap in the timestamps and no frame.
- `TrackCheck.Samples` is decoded samples for the burst track, and AAC packets × 1024 for the others.

### FlashDetector and OnsetDetector

- Each frame is reduced to its mean luma (0–255, from a 64×36 gray decode). A flash starts at a frame above the
  threshold whose previous frame was not.
- The threshold is halfway between the median frame (dark: the marker is black 95 % of the time) and the brightest, so
  it adapts to letterboxing, the capture border and the encoder's range. With too little contrast no flash is found.
- The onset detector is a 3 kHz quadrature detector with a 2 ms window (one cycle is 16 samples at 48 kHz). An onset is
  the first sample where the envelope crosses `On`. It re-arms after 200 ms below `Off`.
- Its thresholds scale with the bursts' peak level: −30 dBFS gives 0.004 and 0.0015.

### FpsBenchmark

- `tools/measure-path-a.ps1` runs baseline against capture in balanced passes, at the output size and rate.
- Without `--game` it measures Bench's own synthetic GPU load, which the script finds and PresentMon traces by its
  process name, like a game. Bench stops the load on every path.
- In the Overlay switch, a failure from the App's engine on the timer's thread would end Bench, which is why it is
  reported and fails the run.

### HotkeyBenchmark, KeyboardHook and KeyInjector

- A `WH_KEYBOARD_LL` hook in Bench stamps each key-down. The engine stamps its `WM_HOTKEY` and publishes
  `hotkey.pressed`. Press-to-stamp is the difference of the two QPCs.
- A low-level hook runs before the system's Hotkey handling, so its stamp comes before the engine's for the same
  press.
- The injector sends the modifiers down, the key down and up, and the modifiers up as one batch. The hook sees each
  key-down flagged as injected.
- When `SendInput` inserts nothing (a window of higher integrity has the focus, or the desktop is locked) the injector
  stops, `Fault` names why, and `Faulted` fires so a waiting reader stops at once.

### LiveApp and LiveCapture

- The App comes from `--app-exe`, the local publish (`publish/Encore`) or the Release build. It is never the installed
  App or a real instance.
- Feedback Sounds are off, because they play on the default communications endpoint, which may be a Track's device. A
  sound there would read as a burst.
- `LiveCapture`: "off" (the baseline runs) has the load's profile Ignored, so nothing captures. "On" clears the flag,
  waits for Rewind to buffer the load, and presses Record. Off presses Record again and sets Ignored.

### MarkerWindow

- It is a black 640×360 top-level window that turns white at each whole second from its origin, for 3 frames at the
  output fps (and at least 3 DWM compositions).
- Each flash is painted with GDI from a thread of its own. `DwmFlush` then waits for the composition that shows it, and
  the QPC read right after is the flash's stamp.
- The window lives on its own STA thread, so the caller's thread stays free.

### OverlayBenchmark

- For each `--modes` entry it starts the synthetic GPU load in that mode and the Overlay window beside it. It records
  the load's presents with PresentMon with the dot hidden, shown, and hidden again, and each phase's
  `SHQueryUserNotificationState`.
- It writes `overlay.json` and `overlay.md` into the run folder.
- `--host app` drives the real App's Overlay through its engine, with a saved and a failed notice in the shown phase.

### PlaybackCheck and PlayerScripts

- `bench playback` makes a short marked capture (both tracks, H.264), then asks a checklist per player. HEVC and AV1
  are not checked: they are labelled as possibly not playing everywhere.
- With `--auto` nobody answers: Chrome and Windows Media Player are scripted, screen grabs answer the capture-border
  question, and every other player is `not-checked`.
- A scripted check's `Plays` is true when playback advanced at least half as fast as the wall clock, and false when the
  player refused the file.
- Script result fields: `playedSeconds`, `wallSeconds`, `width`, `height`, `durationSeconds`, `audioTracks`,
  `trackLabels`, `framesDecoded`, `framesDropped`, `error` and `errorKind`.
- Chrome runs headless with a fresh profile, from a loopback HTTP server that serves the page and the clip with byte
  ranges. VLC runs with dummy audio and video outputs, on 127.0.0.1 with a free port and a random password.

### SoakBounds

- The warm-up value is the first sample at or after the warm-up time.
- Private bytes must end within max(5 %, 20 MB) of it, and their least-squares slope from the warm-up on must be under
  2 MB/h. Handles, GDI and USER objects must end within 50 of it.
- "Within" means growth: a counter that falls after warm-up meets its bound.
- The slope is judged only over at least `MinSlopeSpan` of samples. Over a shorter span the ±3 MB noise of a flat
  process outweighs 2 MB/h, so a short run reports the slope as information only.

### SoakClipCheck

- The video must be [window − one GOP (1 s) − one frame, window + one frame] long, since a Clip starts at the first
  keyframe inside the window.
- Each audio Track must be within one frame of the video, or one AAC packet when that is longer, as audio ends on a
  whole packet.
- The whole file must decode: `ffmpeg -v error -i clip -map 0 -f null -` exits 0 with nothing on stderr.

### SoakRun (`bench soak`)

- The engine (`serve --instance … --rewind`) and the App (`--tray --instance …`) run as separate processes on a
  throwaway test instance, with the synthetic GPU load as the Target Game.
- The Save Hotkey is injected every `--press-seconds`. Both processes are sampled every `--sample-seconds`. Every Clip
  is checked with ffprobe and ffmpeg.
- It writes `soak-samples.csv`, `soak-clips.csv`, `soak.json` and `soak.md`, and holds the `.bench-running` lock.
- It exits 0 only when every Clip passes, at least one was made, and every bound is met.
- `--kill-engine`, `--kill-app` and `--hang-engine` each do that once, that many minutes into the loop, and check the
  recovery. Sampling follows the new processes; the bounds use each process's first pid only.
- Kills go only to this instance's processes: the engine serving its pipe and the App its snapshot names, each checked
  against the expected image.
- `SoakSchedule`: a sample or press that runs late moves its next one to the following slot, so they never bunch up.

### TrackAnalysis and TracksCheck (`bench tracks`)

- The capture uses a four-Track layout: Everything = A + B + Mic, Game = A, Chat = B, Mic = Mic. The file is then
  checked as a player sees it: ffprobe, the decoded audio, and VLC's HTTP interface. DaVinci Resolve stays a person's
  check.
- The checks: the Track names and order, Track 1 as the only default, each Track's length against the video's, Game and
  Chat in sync with Everything, each single-device Track at its source's level, Everything never over full scale, and
  VLC's listing of the same names.
- `TrackFigures`: `ExpectedOnsets` is at least the stream's whole seconds less one; for Everything, Game's and Chat's
  summed. `BurstLevelDbfs` is the median RMS of the 5 to 45 ms after each onset, × √2, so a sine reads its peak. `Peak`
  is information only, since AAC rings past full scale at the bursts' edges. `P999` is what the full-scale check
  judges.
- The percentile comes from a fixed histogram, so a stream is never held whole.
- The mp4 muxer stores a stream's title in the track's `name` box, which FFmpeg reads back as the `name` tag; `title`
  is the fallback. `handler_name` is never used: the engine writes the name there too, and a file whose title tag is
  missing must fail the titles check.
- The second tone player gets three tries at landing its bursts about half a second after the first's, with a warning
  when the last misses by more than 0.15 s.

### Report

- It merges `fps.json`, `drift.json`, `playback.json`, `hotkey.json` and `tracks.json`. Each measurement present gets a
  section with every figure beside its target and a pass or miss mark; the rest say "not run". A malformed file is
  named and skipped.
