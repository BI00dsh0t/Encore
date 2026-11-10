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
