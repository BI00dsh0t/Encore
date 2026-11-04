---
title: 'Length and thumbnails'
type: 'feature'
ticket: '3'
created: '2026-11-03'
status: done
baseline_revision: '2e0187b0e74f970ceab36432bcb293fd06eb37c9'
route: 'full'
route_source: 'auto'
risk: 'medium'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
followup_review_recommended: false
context: []
warnings: ['oversized']
deferred:
  - summary: >-
      A length read holds the file open without FILE_SHARE_DELETE, so a rename or delete during it fails.
    evidence: |-
      FFmpeg's Windows file protocol opens with _wsopen(..., SH_DENYNO), which doesn't grant delete sharing.
      The window is open + find_stream_info (milliseconds), but OnStart and OnFolderChanged queue every
      item without a length. 7.6's engine rename/delete (OnMove) would get a sharing violation and report
      `failed`; Explorer would show "file in use". Fix: a custom AVIOContext over a FileStream opened with
      FileShare.ReadWrite | FileShare.Delete, or retry in 7.6. Not reproduced.
    location: >-
      src/ReplayBuffer.Platform.FFmpeg/MediaLength.cs
    severity: medium (unverified)
---

<intent-contract>

## Intent

**Problem:** Library rows show name, game, date and size, but no length or thumbnail (R3, FR-30).

**Approach:** The engine reads each item's length with FFmpeg's demuxer (open + find_stream_info, no decode) on a low-priority worker beside the LibraryActor, caches it in `library.json` by size and mtime, and publishes it as `library.itemChanged`. The App asks the Windows shell for a thumbnail by absolute path on a background thread, caches it in memory, shows a generic icon when there is none, and never asks for an item being written. Rows show length as m:ss (h:mm:ss past an hour) and size in MB or GB.

## Boundaries & Constraints

**Always:**
- AD-17: the actor thread owns the index. The length worker only reads a file and posts the result back; the actor applies it only if the item still exists, isn't being written and has the size the job was queued at.
- Length is queued after a `Written` `EndWrite`, for items `Load`/rescan/folder change find without a cached length, and when `Reconcile` sees an item's size change (its length is cleared first). Moves keep the length.
- A cached length is reused only when the saved entry's size and mtime (`LastWriteTimeUtc`) both match the file.
- A file the reader can't read gets no length, is logged, and nothing crashes.
- AD-1: the App passes only a path to the shell (`IShellItemImageFactory::GetImage` with `SIIGBF_THUMBNAILONLY`); it never opens the file.
- AD-22: Contracts change additively (trailing optional record parameters, new snapshot property).
- Lean tests (CLAUDE.md "Tests"): one or two happy-path tests per AC.

**Never:**
- No decode, no thumbnail cache on disk, no engine-side thumbnails.
- No grouping/filter, actions, Cleanup (7.4–7.8).
- Don't change `Mp4File`, the reconciliation rules or `ILibraryWrites`.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Clip saved | `EndWrite(Written)` | `itemChanged` (size), then `itemChanged` with `lengthSeconds` | — |
| Truncated fragmented MP4 | frag MP4 cut mid-fragment | reader returns the end of the last complete fragment | — |
| Unreadable | `.mp4` that FFmpeg can't open | no length; a log line | reader returns null |
| Restart, unchanged | entry size + mtime match | length from `library.json`; reader not called | — |
| Changed size | file rewritten outside the app | length cleared, re-read | — |
| Being written | `BeginWrite` item | no thumbnail request, "Saving…" | — |
| Write ended | item turns `BeingWritten: false` | thumbnail requested once | shell has none: generic icon |

</intent-contract>

## Code Map

- `src/ReplayBuffer.Contracts/Messages/Library.cs` -- `LibraryItem` gains trailing `double? LengthSeconds = null`. `LibraryReset` gains trailing `string? Folder = null`; its apply also sets `LibraryFolder = reset.Folder ?? snapshot.LibraryFolder`.
- `src/ReplayBuffer.Contracts/Messages/Engine.cs:112-127` -- `StateSnapshot`: add `string? LibraryFolder { get; init; }` (the actor's absolute Output Folder) beside `CleanupBlocked`; mirror how `CleanupBlocked` is declared/defaulted.
- `src/ReplayBuffer.Core/Library/LibraryActor.cs` -- the 7.2 actor (read it whole before editing). Hook points: `Load` (:176, `known` check :211), ctor (:102; add trailing optional `Func<string, double?>? readLength = null`), `OnStart` (:540), `OnEnd` (:566), `OnFolderChanged` (:695, the reset at :726 now passes `_folder`), `Reconcile` (:790, the add at :820 and the size change at :828), `MoveItem` (:908), `Remove` (:918), `Save` (:1030), `Dispose` (:274), `LibraryFileEntry` (:1119; add trailing `double? LengthSeconds = null, DateTime? Modified = null`). Core is BCL + Contracts only.
- `src/ReplayBuffer.Engine/EngineHost.cs:225-265` -- Load/actor wiring; pass `readLength`. `src/ReplayBuffer.Engine/Ipc/EventHub.cs:28-34` -- seed `LibraryFolder` (new optional ctor parameter) from the resolved `OutputFolder()` (full path).
- `src/ReplayBuffer.Platform.FFmpeg/FFmpegLibrary.cs` -- `Initialize(ILogger)` (throws `ErrorCodeException` `FFmpegMissing`). `ClipWriter.cs:61-122` shows the unsafe FFmpeg.AutoGen call style. `NativeMethods.txt` already has `SetThreadPriority`/`GetCurrentThread` (CsWin32, `THREAD_PRIORITY.THREAD_MODE_BACKGROUND_BEGIN/END` for low I/O priority).
- `src/ReplayBuffer.Platform.Desktop/` -- CsWin32 Win32 helpers (`NativeMethods.txt`, `NativeMethods.json`; check `allowMarshaling`). The engine references it too, so keep it AOT-safe.
- `src/ReplayBuffer.App/Library/LibraryPage.cs`, `LibraryView.xaml` -- rows (`LibraryRow.Show`), `SizeText`. `MainViewModel.cs:35` constructs `new LibraryPage(engine)` and has `post` (UI-thread runner).
- Tests: `tests/ReplayBuffer.Core.Tests/LibraryActorTests.cs` (7.2's helpers: minimal MP4 bytes, temp folders, actor factory), `tests/ReplayBuffer.Platform.FFmpeg.Tests` (`ffmpeg.exe`/`ffprobe.exe` sit in `AppContext.BaseDirectory`; skip with `Assert.SkipUnless(File.Exists(...ffprobe.exe))` as `FFmpegLibraryTests` does; process pattern in `EncoderProbeTests.cs:164`), `tests/ReplayBuffer.App.Tests/LibraryPageTests.cs` (`FakeEngineSession`, `Sta.Run`, the binding-error render test), `tests/ReplayBuffer.Contracts.Tests/MessagesTests.cs` `Samples`.

## Tasks & Acceptance

**Execution:**
- [x] `src/ReplayBuffer.Contracts/Messages/Library.cs`, `Messages/Engine.cs` -- the fields above, with doc comments -- R3 contract
- [x] `src/ReplayBuffer.Platform.FFmpeg/MediaLength.cs` (new) -- `public static double? Read(string path, ILogger logger)`: `FFmpegLibrary.Initialize`, then on the calling thread between `THREAD_MODE_BACKGROUND_BEGIN`/`END`: `avformat_open_input`, `avformat_find_stream_info`, length = `ic->duration / AV_TIME_BASE` when set and > 0, else the largest stream `duration * time_base`; `avformat_close_input` always. Any failure (negative result, `ErrorCodeException`) logs a warning naming the file and returns null -- the reader
- [x] `src/ReplayBuffer.Core/Library/LibraryActor.cs` -- (a) `Load` sets `LengthSeconds` from an entry whose size and `Modified` match the file. (b) Ctor `readLength`: when non-null, a background worker thread (`LibraryLength`, `ThreadPriority.Lowest`) reads its own unbounded channel of jobs (folder, key, absolute path, size), calls `readLength` (an exception → `_failed`, null), and posts `LengthRead` to the actor channel. (c) Actor-side `QueueLength(key)`; a `Dictionary<string, DateTime> _modified` (key → mtime the length belongs to) filled when a length is applied and, in `OnStart`, by stat-ing each item loaded with a length. Queue at the points in Boundaries; `OnStart` queues every loaded non-written item without a length. (d) `OnLengthRead`: ignore if the folder changed, the item is gone or being written, its size differs, or the result is null; else set `LengthSeconds`, record the mtime, `_changed`, `_dirty`. (e) `MoveItem` moves the `_modified` entry; `Remove`/folder change drop it; `Reconcile`'s size change clears `LengthSeconds` and re-queues. (f) `Save` writes `LengthSeconds` and `Modified`. (g) `OnFolderChanged`'s reset carries `Folder: _folder`. (h) `Dispose` completes the job channel and joins the worker after the actor -- R3 length
- [x] `src/ReplayBuffer.Engine/EngineHost.cs`, `Ipc/EventHub.cs` -- `readLength: path => MediaLength.Read(path, loggers.CreateLogger("FFmpeg"))`; seed `LibraryFolder` with `Path.GetFullPath(OutputFolder())` -- wiring
- [x] `src/ReplayBuffer.Platform.Desktop/ShellThumbnail.cs` (new) + `NativeMethods.txt` -- `public static ShellImage? TryGet(string path, int size)`: `SHCreateItemFromParsingName` → `IShellItemImageFactory::GetImage(size×size, SIIGBF_THUMBNAILONLY)`; copy the HBITMAP's 32-bpp pixels (`GetObject` → `DIBSECTION`, normalise bottom-up to top-down) into `ShellImage(int Width, int Height, byte[] Bgra)`; `DeleteObject` always; any failing HRESULT → null. Callers run it on an STA thread -- AD-1 shell access
- [x] `src/ReplayBuffer.App/Library/LibraryThumbnails.cs` (new) -- `LibraryThumbnails(Func<string, ImageSource?> source, Action<Action> background, Action<Action> post)` with an in-memory cache keyed by absolute path + size. `Request(LibraryRow row, string absolutePath, long size)`: cached → set at once; else run `source` via `background` and set `row.Thumbnail` via `post`. `Shell(Action<Action> post)` factory: a single dedicated STA worker thread (background, a blocking queue), `source` = `ShellThumbnail.TryGet(path, 192)` → frozen `BitmapSource.Create(..., PixelFormats.Bgra32, ...)` -- R3 thumbnails
- [x] `src/ReplayBuffer.App/Library/LibraryPage.cs`, `LibraryView.xaml`, `MainViewModel.cs` -- `LibraryPage(IEngineSession engine, LibraryThumbnails? thumbnails = null)`; `MainViewModel` passes `LibraryThumbnails.Shell(post)`. `Render` requests a thumbnail for a row only when its item isn't being written, `state.LibraryFolder` is set, and the row hasn't already requested it for that size (so a row asks again after its `EndWrite`, never while locked). `LibraryRow` gains `Length` (`LengthText`: `m:ss`, `h:mm:ss` from 1 h; empty when null or being written), `Thumbnail` (`ImageSource?`) and `HasThumbnail`. `SizeText` becomes MB below 1 GB, else GB (1024-based, one decimal). The view: a 96×54 thumbnail column at the left (an `Image`, else a generic grey video glyph), and the length beside the size -- R3 rows
- [x] `tests/ReplayBuffer.Platform.FFmpeg.Tests/MediaLengthTests.cs` -- generate with `ffmpeg.exe` (`lavfi testsrc` 320×240 + `sine`, libx264 `ultrafast`, aac) into a temp folder: (1) a 30 s Clip: `Read` within 0.1 s of ffprobe's `format=duration`; (2) a fragmented MP4 (`-movflags frag_keyframe+empty_moov+default_base_moof -frag_duration 1000000`, 10 s) cut to half its bytes: length within 0.1 s of ffprobe's on the cut file and < 10 s; (3) an `.mp4` of random bytes: null, no throw -- reader ACs
- [x] `tests/ReplayBuffer.Core.Tests/LibraryActorTests.cs` -- with a fake counting `readLength`: (1) a `Written` `EndWrite` publishes an `itemChanged` with the fake's length, and the saved `library.json` holds it; (2) `Load` of that json then a new actor: the item has its length and the fake isn't called; after the file's size changes, it is re-read -- cache AC
- [x] `tests/ReplayBuffer.App.Tests/LibraryPageTests.cs` -- a fake source recording paths, inline `background`/`post`: a being-written item isn't requested; after a snapshot with it written, it is requested once with `Path.Combine(LibraryFolder, item.Path)`; the row shows a length `0:30` and `1:02:03`, and size "1.5 MB"/"2.0 GB". Update 7.1's expected size strings for the MB/GB format -- App AC
- [x] `tests/ReplayBuffer.Contracts.Tests/MessagesTests.cs` -- update/extend samples so `lengthSeconds` and the reset's `folder` round-trip -- contract

**Acceptance Criteria:**
- Given a 30 s test Clip, when the reader reads it, then its length is within 0.1 s of ffprobe's.
- Given a fragmented MP4 truncated mid-fragment, when read, then its length is that of its last complete fragment (ffprobe's on the cut file).
- Given an unreadable `.mp4`, when read, then there is no length, a warning is logged and nothing throws.
- Given an item with a cached length whose size and mtime are unchanged, when the engine restarts, then the item has its length and the reader isn't called.
- Given a Clip being written, when the Library page renders, then no thumbnail is requested; when its `EndWrite` arrives, then one is requested by its absolute path.
- Given an item with a length of 3723 s and a size of 2 GiB, when shown, then the row reads `1:02:03` and `2.0 GB`.

## Implementation Notes

- The actor dedupes length jobs by key and size (`_queued`), so `OnStart`'s queue of loaded items and `Reconcile`'s adds don't read a file twice. A move of an item not yet read re-queues it under the new key. `OnBegin` drops the key's `_modified` (a write over an existing file).
- The length worker skips jobs once the actor is disposed, so shutdown doesn't wait on queued reads.
- `ShellThumbnail` calls `IShellItemImageFactory::GetImage` through the vtable (CsWin32's wrapper throws on a failing HRESULT), and makes a bitmap whose alpha is all zero opaque, as the shell returns some thumbnails.
- `LibraryThumbnails` caches only found thumbnails; a request whose row has asked for another path or size since is dropped.
- `SizeText` shows bytes below 1 MB as `0.x MB` (7.1's `512 B` row now reads `0.0 MB`).
- The Core test syncs on `Watching` before writing, so the start rescan can't see the file first.

## Plan Change Log

- **`library.folderChanged` instead of `LibraryReset.Folder`.** `MessagesTests` holds two invariants: each event changes exactly its one snapshot field, and every snapshot field has an event that changes it. A reset that also set `LibraryFolder` would break both. So `LibraryReset` is unchanged, and a new additive event `library.folderChanged` (`LibraryFolderChanged(string Folder)`, field `libraryFolder`) is published just before the reset of a new Output Folder. The actor takes it as a trailing optional `folderChanged` callback. `LibraryServeTests` now expects it before `library.reset`, and checks the snapshot's `LibraryFolder`.

## Review Triage Log

### 2026-11-03 — Review pass
- verdicts: 7 findings — high 0, medium 1, low 5, false 0, maybe-false 1
- findings:
  - `low` `reject` The App's thumbnail cache is never pruned — entries are ~150 KB each and only stale ones (moved, rewritten, other folder) are wasted; pruning adds a sweep in `Render`, not a direct correction, and a personal Library won't churn enough to matter.
  - `low` `patch` `BitmapSource.Create` used `Bgra32` though the shell's DIB is premultiplied — now `Pbgra32`; `ShellImage`'s doc says premultiplied.
  - `maybe-false` `defer` A length read may block a rename or delete of the file (FFmpeg opens without `FILE_SHARE_DELETE`) — would be medium if true; settle by renaming a file while `MediaLength.Read` holds it open. Deferred to 7.6, which adds engine renames and deletes.
  - `medium` `patch` `OnBegin` kept `_queued[key]`, so a write over an indexed file ending at the same size skipped its new length read and could take the old file's — `OnBegin` now also removes `_queued[key]`.
  - `low` `patch` `SizeText` showed "1024.0 MB" just under 1 GiB — the MB/GB switch now compares the rounded MB value.
  - `low` `patch` The Contracts round-trip test sat in `FrameTests` — moved into `MessagesTests`.
  - `low` `reject` The plan's Code Map, task (g) and Design Notes still describe `LibraryReset.Folder` — the fix is editing this build's plan; the Plan Change Log records the `library.folderChanged` replacement.

## Design Notes

Why the snapshot gets `LibraryFolder`: the App needs absolute paths for the shell (here, and in 7.5's open/Explorer/copy), and only the engine knows the resolved Output Folder (the `MyVideos\<instance>` default). Seeding it in the snapshot and carrying it on `library.reset` keeps it right across a folder change without the App duplicating the default.

The truncated-fragment case needs no special code: FFmpeg's mov demuxer reads the fragments it finds and reports the end of the last complete one (checked by hand with ffprobe: a 10 s file cut in half reads 5.0 s).

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings, 0 errors
- `dotnet test --project tests/ReplayBuffer.Contracts.Tests -c Release --no-build`, then `ReplayBuffer.Core.Tests`, `ReplayBuffer.Platform.FFmpeg.Tests`, `ReplayBuffer.Engine.Tests`, `ReplayBuffer.App.Tests`, `ReplayBuffer.Platform.Desktop.Tests` -- expected: all pass
- `pwsh tools/publish-engine.ps1` -- expected: 0 IL/AOT warnings

## Auto Run Result

**Summary:** Each Library item now has a length and a thumbnail (R3, FR-30). The engine reads lengths with FFmpeg's demuxer (`MediaLength.Read`: open + find_stream_info, no decode, background I/O mode) on a low-priority `LibraryLength` worker beside the `LibraryActor`, which applies a result only when the item is unchanged, publishes it as `library.itemChanged` and caches it in `library.json` by size and mtime. A truncated fragmented MP4 reads as its last complete fragment; an unreadable file gets no length and a warning. The snapshot gains `libraryFolder` (seeded at start, then `library.folderChanged` before a folder's reset), so the App can build absolute paths. The App asks the shell (`IShellItemImageFactory`, `SIIGBF_THUMBNAILONLY`) on one STA thread, caches found thumbnails in memory, shows a grey play glyph otherwise, never asks for an item being written and asks again after its `EndWrite`. Rows show `m:ss`/`h:mm:ss` and MB/GB.

**Files changed:**
- `src/ReplayBuffer.Contracts/Messages/Library.cs`, `Messages/Engine.cs` -- `LibraryItem.LengthSeconds`, `StateSnapshot.LibraryFolder`, `library.folderChanged`
- `src/ReplayBuffer.Platform.FFmpeg/MediaLength.cs` -- the length reader
- `src/ReplayBuffer.Core/Library/LibraryActor.cs` -- length worker, queueing, cache in `library.json`, folder event
- `src/ReplayBuffer.Engine/EngineHost.cs`, `Ipc/EventHub.cs` -- wiring and the `LibraryFolder` seed
- `src/ReplayBuffer.Platform.Desktop/ShellThumbnail.cs`, `NativeMethods.txt` -- shell thumbnails
- `src/ReplayBuffer.App/Library/LibraryThumbnails.cs`, `LibraryPage.cs`, `LibraryView.xaml`, `MainViewModel.cs` -- thumbnails, length, MB/GB
- Tests: `Platform.FFmpeg.Tests/MediaLengthTests.cs` (3), `Core.Tests/LibraryActorTests.cs` (+1), `App.Tests/LibraryPageTests.cs`, `Contracts.Tests/MessagesTests.cs`, `Engine.Tests/LibraryServeTests.cs`

**Review:** Quick lens, 7 findings: 4 patches applied (medium 1, low 3; patched in the main session), 1 deferred (a length read blocking rename/delete, unverified, for 7.6), 2 rejected (the unpruned thumbnail cache; plan text describing the superseded `LibraryReset.Folder`). Follow-up review: not recommended (one medium patch, a one-line guard).

**Verification:** `dotnet build ReplayBuffer.slnx -c Release`: 0 warnings. Default runs after the patches: Contracts 116, Core 97, Platform.Desktop 11, App 78 (7 explicit skipped) passed; before them, Platform.FFmpeg 36 (1 skipped, as before) and Engine 161 (19 explicit skipped) passed. `pwsh tools/publish-engine.ps1`: no IL/AOT warnings. HITL proxy: `ShellThumbnail.TryGet` on a 1280×720 H.264 test clip returned a correctly oriented 192×108 image; on random bytes, null. The heavy suite wasn't run: capture, encode, mux and audio are unchanged.

**Residual risks:** How the thumbnails and rows look is unchecked by eye (Human Check Queue). The deferred sharing issue. The thumbnail cache is keyed case-sensitively and never pruned.

