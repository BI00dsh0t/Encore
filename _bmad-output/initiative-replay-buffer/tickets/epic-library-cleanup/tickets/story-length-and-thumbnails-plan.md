---
title: 'Length and thumbnails'
type: 'feature'
ticket: '3'
created: '2026-11-03'
status: 'ready-for-dev'
baseline_revision: ''
route: 'full'
route_source: 'auto'
risk: 'medium'
review: ''
review_source: ''
lenses_ran: []
review_loop_iteration: 0
followup_review_recommended: false
context: []
warnings: ['oversized']
deferred: []
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
- [ ] `src/ReplayBuffer.Contracts/Messages/Library.cs`, `Messages/Engine.cs` -- the fields above, with doc comments -- R3 contract
- [ ] `src/ReplayBuffer.Platform.FFmpeg/MediaLength.cs` (new) -- `public static double? Read(string path, ILogger logger)`: `FFmpegLibrary.Initialize`, then on the calling thread between `THREAD_MODE_BACKGROUND_BEGIN`/`END`: `avformat_open_input`, `avformat_find_stream_info`, length = `ic->duration / AV_TIME_BASE` when set and > 0, else the largest stream `duration * time_base`; `avformat_close_input` always. Any failure (negative result, `ErrorCodeException`) logs a warning naming the file and returns null -- the reader
- [ ] `src/ReplayBuffer.Core/Library/LibraryActor.cs` -- (a) `Load` sets `LengthSeconds` from an entry whose size and `Modified` match the file. (b) Ctor `readLength`: when non-null, a background worker thread (`LibraryLength`, `ThreadPriority.Lowest`) reads its own unbounded channel of jobs (folder, key, absolute path, size), calls `readLength` (an exception → `_failed`, null), and posts `LengthRead` to the actor channel. (c) Actor-side `QueueLength(key)`; a `Dictionary<string, DateTime> _modified` (key → mtime the length belongs to) filled when a length is applied and, in `OnStart`, by stat-ing each item loaded with a length. Queue at the points in Boundaries; `OnStart` queues every loaded non-written item without a length. (d) `OnLengthRead`: ignore if the folder changed, the item is gone or being written, its size differs, or the result is null; else set `LengthSeconds`, record the mtime, `_changed`, `_dirty`. (e) `MoveItem` moves the `_modified` entry; `Remove`/folder change drop it; `Reconcile`'s size change clears `LengthSeconds` and re-queues. (f) `Save` writes `LengthSeconds` and `Modified`. (g) `OnFolderChanged`'s reset carries `Folder: _folder`. (h) `Dispose` completes the job channel and joins the worker after the actor -- R3 length
- [ ] `src/ReplayBuffer.Engine/EngineHost.cs`, `Ipc/EventHub.cs` -- `readLength: path => MediaLength.Read(path, loggers.CreateLogger("FFmpeg"))`; seed `LibraryFolder` with `Path.GetFullPath(OutputFolder())` -- wiring
- [ ] `src/ReplayBuffer.Platform.Desktop/ShellThumbnail.cs` (new) + `NativeMethods.txt` -- `public static ShellImage? TryGet(string path, int size)`: `SHCreateItemFromParsingName` → `IShellItemImageFactory::GetImage(size×size, SIIGBF_THUMBNAILONLY)`; copy the HBITMAP's 32-bpp pixels (`GetObject` → `DIBSECTION`, normalise bottom-up to top-down) into `ShellImage(int Width, int Height, byte[] Bgra)`; `DeleteObject` always; any failing HRESULT → null. Callers run it on an STA thread -- AD-1 shell access
- [ ] `src/ReplayBuffer.App/Library/LibraryThumbnails.cs` (new) -- `LibraryThumbnails(Func<string, ImageSource?> source, Action<Action> background, Action<Action> post)` with an in-memory cache keyed by absolute path + size. `Request(LibraryRow row, string absolutePath, long size)`: cached → set at once; else run `source` via `background` and set `row.Thumbnail` via `post`. `Shell(Action<Action> post)` factory: a single dedicated STA worker thread (background, a blocking queue), `source` = `ShellThumbnail.TryGet(path, 192)` → frozen `BitmapSource.Create(..., PixelFormats.Bgra32, ...)` -- R3 thumbnails
- [ ] `src/ReplayBuffer.App/Library/LibraryPage.cs`, `LibraryView.xaml`, `MainViewModel.cs` -- `LibraryPage(IEngineSession engine, LibraryThumbnails? thumbnails = null)`; `MainViewModel` passes `LibraryThumbnails.Shell(post)`. `Render` requests a thumbnail for a row only when its item isn't being written, `state.LibraryFolder` is set, and the row hasn't already requested it for that size (so a row asks again after its `EndWrite`, never while locked). `LibraryRow` gains `Length` (`LengthText`: `m:ss`, `h:mm:ss` from 1 h; empty when null or being written), `Thumbnail` (`ImageSource?`) and `HasThumbnail`. `SizeText` becomes MB below 1 GB, else GB (1024-based, one decimal). The view: a 96×54 thumbnail column at the left (an `Image`, else a generic grey video glyph), and the length beside the size -- R3 rows
- [ ] `tests/ReplayBuffer.Platform.FFmpeg.Tests/MediaLengthTests.cs` -- generate with `ffmpeg.exe` (`lavfi testsrc` 320×240 + `sine`, libx264 `ultrafast`, aac) into a temp folder: (1) a 30 s Clip: `Read` within 0.1 s of ffprobe's `format=duration`; (2) a fragmented MP4 (`-movflags frag_keyframe+empty_moov+default_base_moof -frag_duration 1000000`, 10 s) cut to half its bytes: length within 0.1 s of ffprobe's on the cut file and < 10 s; (3) an `.mp4` of random bytes: null, no throw -- reader ACs
- [ ] `tests/ReplayBuffer.Core.Tests/LibraryActorTests.cs` -- with a fake counting `readLength`: (1) a `Written` `EndWrite` publishes an `itemChanged` with the fake's length, and the saved `library.json` holds it; (2) `Load` of that json then a new actor: the item has its length and the fake isn't called; after the file's size changes, it is re-read -- cache AC
- [ ] `tests/ReplayBuffer.App.Tests/LibraryPageTests.cs` -- a fake source recording paths, inline `background`/`post`: a being-written item isn't requested; after a snapshot with it written, it is requested once with `Path.Combine(LibraryFolder, item.Path)`; the row shows a length `0:30` and `1:02:03`, and size "1.5 MB"/"2.0 GB". Update 7.1's expected size strings for the MB/GB format -- App AC
- [ ] `tests/ReplayBuffer.Contracts.Tests/MessagesTests.cs` -- update/extend samples so `lengthSeconds` and the reset's `folder` round-trip -- contract

**Acceptance Criteria:**
- Given a 30 s test Clip, when the reader reads it, then its length is within 0.1 s of ffprobe's.
- Given a fragmented MP4 truncated mid-fragment, when read, then its length is that of its last complete fragment (ffprobe's on the cut file).
- Given an unreadable `.mp4`, when read, then there is no length, a warning is logged and nothing throws.
- Given an item with a cached length whose size and mtime are unchanged, when the engine restarts, then the item has its length and the reader isn't called.
- Given a Clip being written, when the Library page renders, then no thumbnail is requested; when its `EndWrite` arrives, then one is requested by its absolute path.
- Given an item with a length of 3723 s and a size of 2 GiB, when shown, then the row reads `1:02:03` and `2.0 GB`.

## Implementation Notes

## Plan Change Log

## Review Triage Log

## Design Notes

Why the snapshot gets `LibraryFolder`: the App needs absolute paths for the shell (here, and in 7.5's open/Explorer/copy), and only the engine knows the resolved Output Folder (the `MyVideos\<instance>` default). Seeding it in the snapshot and carrying it on `library.reset` keeps it right across a folder change without the App duplicating the default.

The truncated-fragment case needs no special code: FFmpeg's mov demuxer reads the fragments it finds and reports the end of the last complete one (checked by hand with ffprobe: a 10 s file cut in half reads 5.0 s).

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings, 0 errors
- `dotnet test --project tests/ReplayBuffer.Contracts.Tests -c Release --no-build`, then `ReplayBuffer.Core.Tests`, `ReplayBuffer.Platform.FFmpeg.Tests`, `ReplayBuffer.Engine.Tests`, `ReplayBuffer.App.Tests`, `ReplayBuffer.Platform.Desktop.Tests` -- expected: all pass
- `pwsh tools/publish-engine.ps1` -- expected: 0 IL/AOT warnings
