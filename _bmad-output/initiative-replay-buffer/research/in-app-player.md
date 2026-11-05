# In-app player with audio Track switching

Research for a new feature request, 2026-11-05. The builder asked: "add a new feature request. first research easiest approach for an in app media player that also allows us to switch audio tracks."

**Status:** research only. No tickets, no product code. Throwaway proofs ran in a session scratchpad against generated test files. Nothing in the Library, the installed App or the real settings was touched, and nothing was installed system-wide. Every number marked *measured* comes from those proofs on this PC (RTX 5090, Windows 11 26300, Edge WebView2 Runtime 156.0.4314.58, HEVC and AV1 Video Extensions already installed). Everything else is desk research and is labelled as such.

## 1. Answer

- **Recommendation: WinRT `Windows.Media.Playback.MediaPlayer`, rendered through its frame server into a WPF `D3DImage`.**
  - It ships with Windows, and the App's TFM (`net10.0-windows10.0.26100.0`) already projects it, so there's no new package.
  - It switched the audible Track 30 times out of 30 (*measured*). The gap was about 0.1 s.
  - It plays all three codecs, fragmented and faststart MP4, and 6400×2160 HEVC. Seeks completed in 7–144 ms.
  - Because the picture is a WPF `D3DImage`, there's no airspace problem. Theme brushes and transport controls can sit over the video.
- **Runner-up: LibVLCSharp.WPF (LibVLC 3).** It has its own decoders, so it needs no codec extensions, and its Track API is good. But it adds about 90 MB of native code (LGPL) and is a separate window under the hood (airspace). It's the fallback if the Media Foundation gotchas in §5 can't be handled.
- **Not recommended:**
  - **WPF `MediaElement`.** It has no Track API, it plays the *last* Track (*measured*), it can't open AV1 (*measured*), and it drops the last fragment of a Recording.
  - **WebView2 `<video>`.** `audioTracks` exists only behind an experimental Chromium flag, and with the flag 6–7 of 13 switches never took effect (*measured*).
  - **FFME.** Dormant since 2024, and built for FFmpeg 7.0, not our FFmpeg 9.0.
  - **Our own FFmpeg decode and mix.** It's the only route that mixes several Tracks natively, but it costs two to three times the stories.

## 2. What we play

| | Clip | Recording |
|---|---|---|
| Writer | `ClipWriter` (AD-9) | `FragmentedMp4Muxer` (AD-9) |
| Layout | Ordinary MP4, index first (`+faststart`) | Fragmented MP4: an empty `moov`, a `moof`/`mdat` pair at every keyframe (and at least every 1 s, or at each `CutFragment` when durable), and an `mfra` index written by the trailer |
| Video | H.264, HEVC or AV1, up to 6400×2160 for a Monitor Recording | same |
| Audio | Several AAC Tracks, often 4. Each has a `title` and a `handler_name`; the first is the default and is the "Everything" Track in a new layout (epic-multi-track-audio R2) | same |

## 3. How it was tested (throwaway)

- **Fixtures**, made with our bundled FFmpeg 9.0.2. Each has a test-pattern video and four AAC Tracks, each a sine at a different pitch: Track 0 "Game" 300 Hz, Track 1 "Mic" 600 Hz, Track 2 "Discord" 900 Hz, Track 3 "Desktop" 1200 Hz. The Tracks carry `title` and `handler_name` as our muxer writes them, and only Track 0 is default.
  - `h264-frag`, `hevc-frag` and `av1-frag` are 1280×720 at 60 fps, 10 s long, using our Recording muxer flags (`frag_keyframe+empty_moov+default_base_moof`, 1 s fragments, `mfra` at the end).
  - `h264-clip` is the same content written with `+faststart`, as a Clip is.
  - `hevc-6400-frag` is 6400×2160 at 60 fps, 4 s long, with two Tracks.
- **Proving which Track is audible**, not just which one the API reports, without playing audio aloud:
  - **MediaPlayer and WPF:** the player ran at volume 0.001 (WPF at 0.01, its lowest step), about −80 dBFS. The proof captured its *own process's* audio with Windows process-loopback capture (`ActivateAudioInterfaceAsync("VAD\\Process_Loopback")`). A Goertzel filter on each 20 ms window then named the Track that was playing.
  - **WebView2:** the `<video>` element fed a WebAudio `AnalyserNode` through `createMediaElementSource`, connected to nothing else, so it made no sound. The loudest FFT bin named the Track.
- **Rendering check:** `MediaPlayer.CopyFrameToVideoSurface` into a 1600-wide shared D3D11 BGRA texture, the surface a `D3DImage` would show, then a readback to check that real picture content arrived.
- **What didn't run:** the full WPF `D3DImage` wiring, LibVLC, FFME, and anything GPU-heavy or long. LibVLC and FFME are desk research only.

## 4. Measured results

### 4.1 WinRT `MediaPlayer` (Media Foundation)

| File | Opens | Video decoder status | Track names | Audible switches correct | Switch gap | Seek | Plays to the end |
|---|---|---|---|---|---|---|---|
| h264-frag | yes | FullySupported | Game, Mic, Discord, Desktop | 10/10 | ~120–140 ms to new Track, ~100 ms of it silent; clock holds ~110 ms | 9 ms | **no: stops at 9.03 of 10.02 s** |
| h264-clip | yes | FullySupported | same | 10/10 | same | 10 ms | yes, 10.00 s |
| hevc-frag | yes | FullySupported | same | 10/10 | same | 12 ms | yes, 10.02 s (see §5.2) |
| av1-frag | yes | FullySupported | same | (API level: yes) | ~125 ms clock hold | 7 ms | **no: stops at 9.03 s** |
| hevc-6400-frag | yes, 6400×2160 | FullySupported | (unnamed, as generated) | (API level: yes) | ~125 ms clock hold | 144 ms | yes |

- **Switching:** setting `MediaPlaybackItem.AudioTracks.SelectedIndex` takes under 2 ms. The pipeline then swaps streams in about 0.1 s. Playback stays in `Playing` the whole time, with no `Buffering` state and no position jump. A switch to the Track already selected costs nothing.
- **Frame server:** the 6400×2160 HEVC file was scaled to 1600×540, and each of the H.264 and AV1 files to 1600×900. Each did about 58 copies a second, one per new video frame. The copy call averaged 1.7–2.2 ms (worst 9.7 ms). Readback confirmed every centre-row pixel had real picture content.
- **Track names:** `AudioTrack.Name` returns our `handler_name` ("Game", "Mic"…), and `Label` is empty. So the picker can show the Track names the user gave in Settings with no extra work.

### 4.2 WebView2 `<video>` (Chromium 156)

| | Default | With `--enable-blink-features=AudioVideoTracks` |
|---|---|---|
| `audioTracks` API | absent | present, labels = `handler_name` |
| Track playing at start | Track 0 (300 Hz) | Track 0 |
| All five files open (H.264, HEVC, AV1, 6400×2160) | yes | yes |
| Duration and play to the end on fMP4 | correct (10.02 s), ends | same |
| Seek | 7–12 ms | same |
| Switching | n/a | **unreliable.** A switch back to Track 0 never applies live. In three switching strategies (enable only the target; enable then seek in place, with and without a 150 ms delay; disable all then enable), 6–7 of 13 switches left the old Track playing while the API reported the new one. Switches that did work had a 0–11 ms freeze. |

### 4.3 WPF `MediaPlayer` / `MediaElement`

| File | Result |
|---|---|
| h264-frag | opens; plays **Track 3 "Desktop"** (1200 Hz), not the default; stops at 9.03 s |
| hevc-frag | opens; plays **Track 3**; plays to 10.02 s |
| av1-frag | **fails**, `0xC00D11B1` |
| hevc-6400-frag | opens at 6400×2160 (its audible Track wasn't captured, since the run was at volume 0.001, which WPF rounds to silence) |

## 5. Media Foundation gotchas (they apply to both MediaPlayer and MediaElement)

### 5.1 It picks the last Track, not the default one

Media Foundation ignores the MP4 default flag. At open, `SelectedIndex` was 3 (the last Track) on every four-Track file, and 2 on the faststart Clip. WPF `MediaElement` audibly played the last Track. **Fix (WinRT only):** set `AudioTracks.SelectedIndex = 0` when the item opens, before `Play()`. It's free, since no switch has happened yet. WPF `MediaElement` can't do this at all.

### 5.2 It drops the last fragment of a fragmented MP4

- **The problem:** for an fMP4, Media Foundation takes the time of the last random-access entry in `mfra/tfra` as the file's duration, then ends playback there.
  - The H.264 and AV1 fixtures have their last `tfra` entry at 9.02 s, so playback ended at 9.03 s and the final ~1 s was never shown.
  - The HEVC fixture happened to have a keyframe right at the end, so its last entry was at 10.02 s and it played in full.
  - A Recording can lose up to one fragment, which is up to the keyframe interval.
  - Clips aren't affected, since they aren't fragmented.
- **What doesn't fix it:**
  - A file without `mfra` has no duration at all (`TimeSpan.MaxValue`-ish).
  - A file without `empty_moov` reports only the first fragment (1.02 s).
  - `global_sidx` changes nothing.
- **What does fix it (*measured*):** patching a 16-byte `mehd` box (the fragment duration) into the `moov/mvex` of `h264-frag`, with its `tfra` offsets shifted to match. Media Foundation then reports 10.02 s and plays to the end. Our muxer's `moov` has `mvex` directly followed by a 97-byte `udta`, so the engine could probably patch this **in place at finalize, with no shift**: grow `mvex` over the first 16 bytes of `udta` as an `mehd`, and turn the rest into a `free` box. That's unverified, and it's an engine change. Chromium and LibVLC read fMP4 duration correctly.
- **App-only alternatives:**
  - Let it end early. This isn't acceptable for a replay tool.
  - Serve the file through a custom `IRandomAccessStream` that injects the `mehd`.
  - Feed Media Foundation through a `MediaStreamSource`, which would need our own demux, and that means FFmpeg in the App.

### 5.3 HEVC and AV1 depend on Store codec extensions

- MediaPlayer, `MediaElement` and Chromium's HEVC path all decode through Media Foundation. So they need **HEVC Video Extensions** (a paid Store item, unless the OEM preinstalled it) and **AV1 Video Extension** (free).
- This PC has both. On a PC without them, `VideoTracks[0].SupportInfo.DecoderStatus` reports it, and the player should say so and offer "Open in default player".
- The engine's NVENC encode doesn't need them. LibVLC doesn't need them either, since it carries its own decoders.

### 5.4 Smaller notes

- With the frame server on and no copies made, `VideoFrameAvailable` fired at about the display refresh rate (240 Hz on the primary monitor). With copies, it fired once per video frame. Either way, copy on the event and mark the `D3DImage` dirty only when a frame was copied.
- Without any video output, such as frame server off in a console, the AV1 file stalled in `Paused`. With a video output it played. This was a probe artefact, but it means a player must always enable its video output before playing.
- `AV1 "Error parsing OBU data"` lines appeared on stderr from the AV1 decoder on SVT-AV1's fastest preset. They did no harm.

## 6. Candidates in detail

### 6.1 WinRT `Windows.Media.Playback.MediaPlayer` (recommended)

- **Track switching:** `MediaPlaybackItem.AudioTracks.SelectedIndex`. It switches mid-play with a ~0.1 s audio gap and a ~0.1 s video hold (*measured*). Plays one Track at a time.
- **fMP4, HEVC and AV1:** all play.
  - fMP4 loses its last fragment (§5.2).
  - HEVC and AV1 need the Store extensions (§5.3).
- **Large resolution:** 6400×2160 HEVC opens. The frame server scales it to the view size in one GPU copy (*measured*: ~2 ms for each call).
- **Seeking and scrubbing:**
  - `PlaybackSession.Position` seeks took 7–12 ms at 720p and 144 ms on 6400×2160 (*measured*).
  - `StepForwardOneFrame` and `StepBackwardOneFrame` exist for frame stepping. `PlaybackRate` covers slow motion.
  - Scrubbing while dragging works by throttling `Position` writes, for example to one per rendered frame. That isn't measured.
- **Hosting in WPF:** there are three ways.
  1. **Frame server into `D3DImage` (recommended).** `IsVideoFrameServerEnabled = true`. On each `VideoFrameAvailable`, call `CopyFrameToVideoSurface` into a shared D3D11 BGRA texture. Open that texture in a D3D9Ex device (`OpenSharedResource`), and give its surface to `D3DImage.SetBackBuffer`. Lock, `AddDirtyRect` and unlock on the UI thread.
     - This is a known pattern (the same one as Microsoft's old `D3D11Image` interop).
     - There's no airspace: the video is an ordinary WPF image, so Fluent or Gaming dark theme brushes, rounded corners and transport controls can all sit over it.
     - The Overlay is a separate top-level window, so it's unaffected either way.
     - The D3D11/D3D9 interop needs about 150 lines. CsWin32 (already used by Platform.Desktop) or Vortice (MIT) can generate the bindings; the proof used Vortice.
  2. **`MediaPlayer.GetSurface(Compositor)`** with Windows.UI.Composition in an `HwndHost`. That's less code, but it's a child HWND, so WPF content can't overlap it (airspace).
  3. **`MediaPlayerElement` through XAML Islands.** The old UWP Community Toolkit WPF wrapper is from 2020 and is effectively retired. WinUI 3 islands in WPF need the Windows App SDK runtime. That's heavy, it's airspace-bound, and full screen isn't supported. Not worth it.
- **Packaging:** none. It's part of Windows, and the TFM already gives the projection. Add Vortice (about 1 MB managed) only if the interop isn't hand-written with CsWin32.
- **Licence:** Windows component; no new licence.
- **AOT and trimming:** only the engine side is AOT (`IsAotCompatible` is set for everything except `ReplayBuffer.App`, Bench and tests in `Directory.Build.props`). The App is a normal JIT WPF app, so CsWinRT projections and COM interop are fine. Hosting this in the engine would need AOT care, but nothing suggests it should be.
- **Architecture:** it fits AD-2. The player is App-side UI with no engine or FFmpeg reference. Only the §5.2 `mehd` fix touches the engine (Platform.FFmpeg muxer).
- **Maintenance:** OS component, actively serviced. The frame-server API has been stable since Windows 10 1703.
- **Effort:** 4–5 stories (§9).

### 6.2 LibVLCSharp.WPF with LibVLC 3 (runner-up)

Desk research only.

- **Track switching:**
  - `MediaPlayer.AudioTrackDescription` lists the Tracks, and `SetAudioTrack(id)` switches mid-play. VLC's elementary-stream switch is quick, but its gap here isn't measured.
  - One audio Track at a time.
  - Names come from the stream's title or language metadata.
- **fMP4, HEVC and AV1:**
  - VLC's own MP4 demuxer handles fragmented files.
  - Its bundled libavcodec decodes H.264 and HEVC, and dav1d decodes AV1, with D3D11VA hardware decoding. **No Store codec extensions needed.**
- **Large resolution:** fine with D3D11VA. Hardware HEVC limits are 8192 px wide on NVIDIA.
- **Seeking:** `Time` and `Position` setters, with fast keyframe seeks and `NextFrame()` for frame stepping. Scrubbing is good in VLC generally.
- **Hosting in WPF:**
  - The 3.x `VideoView` is an HWND (WinForms host) plus a transparent top-level overlay window that carries WPF `Content`. It's the "airspace hack": overlaid controls work, but they're a separate window. Rounded corners, opacity and theme effects over the video are limited, and the overlay has quirks with focus, z-order and moving.
  - LibVLC 4's output callbacks remove airspace, but `LibVLC4Sharp.WPF` is a nightly prerelease only, and LibVLC 4 isn't released.
- **Packaging:** `VideoLAN.LibVLC.Windows` 3.0.24 (3.0.21's .nupkg is 90 MB, with x86 and x64). The x64 libvlc folder is roughly 140 MB unpacked with all plugins. It can be trimmed to a plugin allow-list (MP4 demux, avcodec, dav1d, d3d11, mmdevice), probably to a few tens of MB. Either way it's a second copy of libavcodec beside our FFmpeg 9.
- **Licence:**
  - libVLC and LibVLCSharp are LGPL-2.1+. Dynamic linking is fine for a personal app, and we already ship GPL FFmpeg builds.
  - A `.GPL` variant of the package exists. It isn't needed.
- **AOT:** App only; not an issue.
- **Maintenance:** active. `LibVLCSharp.WPF` 3.10.2 was published 2026-10-13, and LibVLC 3.0.24 is the latest 3.x.
- **Effort:** 3–4 stories, plus a packaging story (the plugin allow-list and install size) and the airspace caveats in the UX.

### 6.3 WPF `MediaElement`

- **Track switching:** none; there's no API. It plays the *last* Track (*measured*, §5.1).
- **Codecs:** H.264 and HEVC play; **AV1 fails** (*measured*). It also drops the fMP4 end (§5.2).
- Hosting would otherwise be ideal: a native WPF element, no airspace, zero packages.
- **Verdict:** disqualified by the Track requirement alone.

### 6.4 WebView2 with HTML5 `<video>`

- **Track switching:**
  - The `audioTracks` API exists in Chromium only behind `--enable-blink-features=AudioVideoTracks`, which WebView2 can pass through `CoreWebView2EnvironmentOptions.AdditionalBrowserArguments`. The feature has been "experimental" since about 2016.
  - Measured on 156, it's unreliable (§4.2). Switching back to Track 0 never applies live. A seek in place sometimes forces the change, but not consistently.
  - The only reliable way left would be to reload the source at the current time for every switch, which is slower than Media Foundation's switch. Or we'd serve a remuxed single-Track stream through `WebResourceRequested`, which needs FFmpeg in the App.
- **Codecs and fMP4:**
  - Everything played, including 6400×2160 HEVC with 0 dropped frames.
  - fMP4 duration and the end of the file were correct, since Chromium uses its FFmpeg demuxer.
  - HEVC still goes through Media Foundation, so it needs the extension.
- **Hosting:** the WPF `WebView2` control is an HWND, so airspace applies. Theming the player means styling HTML and CSS to match Fluent and Gaming dark, a second UI stack. The newer `WebView2CompositionControl` removes airspace at some performance cost.
- **Packaging:** the runtime is part of Windows 11 (Evergreen). The SDK package is small.
- **Licence:** fine.
- **Effort:** 3 stories, if switching worked. It doesn't, reliably.
- **Verdict:** no.

### 6.5 FFME (Unosquare.FFME.Windows)

Desk research only.

- **Maintenance:** last release 7.0.361-beta.1 on 2024-06-26, built for **FFmpeg 7.0** (avcodec 61). Before that, 4.4.350 (FFmpeg 4.x) in 2021. Effectively dormant.
- **FFmpeg compatibility:** it can't load our FFmpeg 9.0 (avcodec-63, avformat-63, avutil-61). We'd have to ship a second, GPL FFmpeg 7.0 shared build (about 100+ MB) beside ours, or fork FFME onto FFmpeg.AutoGen 9.
- **Track switching:** change `MediaOptions` stream selection and call `ChangeMedia()`, which reopens the media (a short stall).
- **Rendering:** FFME renders into a `WriteableBitmap`. It's a CPU path: no airspace, but a 6400×2160 frame is 55 MB of BGRA per frame on the CPU, which won't hold 60 fps.
- **Licence:** MS-PL for FFME itself, plus FFmpeg's.
- **Verdict:** no.

### 6.6 Our own FFmpeg decode, mixing chosen Tracks (fallback)

- **What it gives:** demux with libavformat, decode video with D3D11VA hardware decoding, decode and mix any set of AAC Tracks with libswresample, or with `amix` in libavfilter, then play through WASAPI. Present frames into the same `D3DImage` as §6.1.
  - It's the only candidate that **mixes several Tracks at once** natively.
  - It needs no Store codec extensions.
  - It has no Media Foundation fMP4 quirks, and it reuses FFmpeg 9 bindings we already pin.
- **Cost:**
  - We'd write a player: an audio clock with A/V sync, seeking and flushing, scrubbing, frame queueing, hardware-frame interop into D3D11, and end-of-stream and error states.
  - The App would also have to reference FFmpeg. Under AD-2 that's either a new App-side platform assembly or a player process. Either is a spine update.
  - Roughly 8–12 stories.
- **Verdict:** keep as the fallback if mixing several Tracks becomes a must-have and the cheap MediaPlayer mixing idea (§7, Q2) fails.

## 7. Open questions for the builder

1. **Where does the player live?** The Library is a list today, and "Open" hands the file to the shell's default player (`LibraryShell`). The options:
   - a detail pane beside the list;
   - a drill-in page inside the Main App, like the Settings drill-ins (`DrillInHeader`);
   - a separate player window that can go full screen.

   A drill-in page or a separate window is the cheapest, since the list layout is untouched.
2. **One Track at a time, or a mix?** Track 0 is already "Everything", so one at a time covers "hear it all" and "solo my mic".
   - Mixing two or more chosen Tracks is possible with MediaPlayer. Run one extra `MediaPlayer` per extra Track, each with its own `SelectedIndex`, all slaved to one `MediaTimelineController`, with only the first one rendering video. That's unverified (sync and decode cost) and would be its own spike.
   - Otherwise mixing needs §6.6.
3. **Per-Track volume or mute** in the player, or just a picker? Per-Track volume needs mixing (Q2).
4. **Which files does it open?** Clips only, or Recordings too? Recordings need the §5.2 fix, and Monitor Recordings are 6400×2160.
5. **Which controls?** Play and pause, a seek bar, frame step, playback rate, full screen, volume, keyboard shortcuts. And should the player remember the last Track chosen per game?
6. **Missing codec extension:** show a message with "Open in default player", or point at the Store page? This only matters off this PC.
7. **Is the player a place to trim or cut a Clip later?** That would change the seek-bar design now. Out of scope unless asked.

## 8. Recommendation

**Build on WinRT `MediaPlayer` with frame-server rendering into a `D3DImage`.**

- It's the least new code that meets every hard requirement: switching Tracks mid-play, all three codecs, fMP4 and huge Recordings, and themeable, airspace-free WPF.
- It adds no new runtime dependency, and switching is measured to work.
- Its two real gotchas have known fixes: force Track 0 at open (§5.1), and add `mehd` to Recordings (§5.2).
- If those turn out to cost more than expected, or a codec-extension-free player becomes a must, **LibVLCSharp.WPF** is the runner-up. Accept the ~90 MB package and the airspace overlay window.

## 9. Rough epic shape (epic 14, "In-app player"; ids illustrative)

| Story | Content | Notes |
|---|---|---|
| 14.1 Player surface | An App-side `VideoSurface` control. It holds a WinRT `MediaPlayer`, renders through the frame server into a D3D11 texture shared to D3D9Ex, shown in a `D3DImage`, scaled to the control's pixel size. Play and pause. Opens a Clip from the Library (Q1 decides where). Forces Track 0 at open. Shows a "can't decode" message plus "Open in default player" when `DecoderStatus` isn't supported. | The interop core; one App test plays a fixture and checks a frame arrived. A Heavy test for 6400×2160. |
| 14.2 Transport | Seek bar with time, scrubbing (throttled `Position`), frame step, rate, volume, keyboard, full screen if Q5 wants it. Fluent and Gaming dark styling over the video. | Uses theme brushes over the `D3DImage`. |
| 14.3 Track picker | Lists `AudioTracks` by `Name` (our Track names). Switches mid-play. Shows the selected Track; optional per-game memory (Q5). | One test: switch Track and assert `SelectedIndex` plus continued playback. The loopback audible check is optional and Heavy. |
| 14.4 Recordings play to the end | Engine: the Recording muxer finalizes with an `mehd` in `moov/mvex`, patched in place over `udta` (§5.2). App: opens Recordings; a Heavy test on a 6400×2160 Recording. | Touches Platform.FFmpeg; verify the patch on a real Recording first, so it starts with a small spike. |
| 14.5 (optional) Mixed Tracks | Spike, then story: extra muted-video `MediaPlayer`s on a `MediaTimelineController`, or §6.6. | Only if Q2 says mix. |

**Size:** 4 stories without mixing, and 5–6 with it.

## 10. Sources

- Microsoft, [Media items, playlists, and tracks](https://learn.microsoft.com/en-us/windows/uwp/audio-video-camera/media-playback-with-mediasource): `AudioTracks.SelectedIndex` switches the active Track; select after the item opens.
- Microsoft, [`MediaPlaybackAudioTrackList.SelectedIndex`](https://learn.microsoft.com/en-us/uwp/api/windows.media.playback.mediaplaybackaudiotracklist.selectedindex) and [`MediaPlaybackItem.AudioTracksChanged`](https://learn.microsoft.com/en-us/uwp/api/windows.media.playback.mediaplaybackitem.audiotrackschanged).
- Microsoft, [MediaPlayerElement for Windows Forms and WPF](https://learn.microsoft.com/en-us/windows/communitytoolkit/controls/wpf-winforms/mediaplayerelement): the XAML Islands wrapper, which has no full-screen support. [Microsoft.Toolkit.Wpf.UI.XamlHost 6.1.2](https://packages.nuget.org/packages/Microsoft.Toolkit.Wpf.UI.XamlHost) is from 2020.
- [LibVLCSharp.WPF on NuGet](https://www.nuget.org/packages/LibVLCSharp.WPF) (3.10.2, 2026-10-13); its [WPF README](https://github.com/radiolondra/libvlcsharp/blob/3.x/src/LibVLCSharp.WPF/README.md) on airspace; [LibVLC4Sharp.WPF](https://www.nuget.org/packages/LibVLC4Sharp.WPF/) (nightly only); [VideoLAN.LibVLC.Windows](https://www.nuget.org/packages/VideoLAN.LibVLC.Windows) (3.0.24; the 3.0.21 .nupkg is 89,989,754 bytes per nuget.org's flat container); [libvlc-nuget wiki](https://github.com/mfkl/libvlc-nuget/wiki) (LGPL-2.1+).
- [FFME.Windows 7.0.361-beta.1](https://nuget.org/packages/FFME.Windows/7.0.361-beta.1) (FFmpeg 7.0, published 2024-06-26) and [FFME releases](https://www.github.com/unosquare/ffmediaelement/releases) (4.4.350 for FFmpeg 4.x).
- Chromium: [Intent to ship: Media tracks](https://groups.google.com/a/chromium.org/g/blink-dev/c/Yk8u329AmIs) (2016) and [runtime_enabled_features.json5](https://chromium.googlesource.com/chromium/src/+/2c0f4a7f43a85ae0448647bd87d4e180ba3e3373/third_party/blink/renderer/platform/runtime_enabled_features.json5) (`AudioVideoTracks`, status experimental). Confirmed on 156 by measurement (§4.2).
- Our code: `src/ReplayBuffer.Platform.FFmpeg/FragmentedMp4Muxer.cs` and `ClipWriter.cs` (layout, titles, default flag); `Directory.Build.props` (AOT scope); `src/ReplayBuffer.App/Library/LibraryShell.cs` ("Open" uses the shell).
