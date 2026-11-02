---
title: 'Named Tracks in players and editors, end to end'
type: 'feature'
ticket: '9'
created: '2026-11-02'
status: 'in-progress'
baseline_revision: 'e283cd20c40f061692f4919f4a41b5e96fa64b12'
route: 'full'
route_source: 'auto'
risk: 'low'
review: ''
review_source: ''
lenses_ran: []
review_loop_iteration: 0
followup_review_recommended: false
context: ['{project-root}/CLAUDE.md']
warnings: ['oversized']
deferred: []
---

<intent-contract>

## Intent

**Problem:** Nothing checks a real multi-Track capture as a player sees it: the Track names and order, Track 1 as default, sync between Tracks, a single-device Track at its source's level, and a mix of full-scale sources never above 0 dBFS (R3, R7). 2.1 and 2.2 cover these only in process or on 3 s files.

**Approach:** A new Bench command, `bench tracks`, plays known signals into two Output Devices, captures `--minutes` (default 5) of a four-Track layout through the engine's `debug-capture --track-layout` in a throwaway test instance, and checks the file with ffprobe, decoded audio and VLC's HTTP interface (the scripted player). It writes `tracks.json`, a section in `report.md`, and a pass or fail exit code. DaVinci Resolve stays a Human Check.

## Boundaries & Constraints

**Always:**
- The Track Layout is, in order: `Everything` = {A, B, Mic}, `Game` = {A}, `Chat` = {B}, `Mic` = {Mic}. A and B are Output Devices given by `--device-a` and `--device-b` (both required, distinct). Mic is `--input-device` (default: the default Input Device).
- Each of A and B plays a `TonePlayer` at `--tone-dbfs` (default 0, range −30 to 0). B starts so that its bursts fall about 0.5 s after A's. Each source then peaks at full scale, and A's burst plus B's tone exceeds it in `Everything`, so the limiter acts every second.
- The settings and data folders belong to a fresh `ReplayBuffer-Test-bench-<8 hex>` instance, deleted afterwards (also on failure). Never touch the Dev or installed instance.
- The analysis works on `tracks.mp4` plus `tracks-markers.json`, so `--reanalyze` reruns it without devices.
- VLC runs with `-I http` on 127.0.0.1, `--aout adummy --vout vdummy`, a random password and a free port. It makes no sound, and it is killed afterwards whatever happens.

**Never:**
- Change engine, Media or Platform code, Contracts or the settings schema.
- Change Windows settings, such as volume or default devices.
- Play into a device the builder didn't name.
- Script DaVinci Resolve.
- Measure the 60-minute drift (that belongs to epic-hardening).
- Change what `drift`, `playback`, `fps` or `hotkey` do.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Good file | 4 Tracks as configured | verdict `pass`, exit 0 | — |
| Wrong names or order | streams titled other than Everything, Game, Chat, Mic | `titles` check fails, verdict `fail`, exit 1 | — |
| Default flag | a stream other than the first audio is default, or the first isn't | `default` check fails | — |
| Track out of sync | Game shifted 5 ms against Everything | `sync` check fails (bound 1 ms) | — |
| Level off | Game's bursts 3 dB below `--tone-dbfs` | `level` check fails (bound ±0.5 dB) | — |
| Over full scale | Everything decodes above 1.05 | `fullScale` check fails | — |
| Short Track | an audio stream more than 0.1 s from the video's duration | `durations` check fails | — |
| Too few bursts | a single-device Track with under 90% of the expected onsets, or under 95% of them matched in Everything | `sync` check fails, with the counts | — |
| VLC absent | no vlc.exe found (`--vlc` or the Program Files locations) | `vlc` is `not-checked`; the verdict ignores it | — |
| VLC lists other names | VLC's audio stream descriptions differ from the titles | `vlc` check fails | — |
| Engine fails | nonzero exit or no file | `tracks.json` with verdict `failed` and the engine's error, exit 1 | instance folder still deleted |
| Bad options | `--device-a` missing, A equals B, a device not found | usage error exit 2, or failure exit 1 with the candidates (as `MarkedCapture`'s `ResolveDevice`) | — |

</intent-contract>

## Code Map

- `bench/ReplayBuffer.Bench/DriftBenchmark.cs`: reuse, don't change.
  - `MarkedCapture` is the pattern for the tone players, the marker window and resolving devices. Copy `ResolveDevice` or make it `internal static` and reuse it.
  - `EngineProcess.Run` runs the engine and saves its logs.
- `bench/ReplayBuffer.Bench/MediaAnalysis.cs`: `Run`/ffprobe helpers and `FfmpegTools`. Use them for probing and for decoding one audio stream (`-map 0:a:<i> -f f32le`), streamed rather than read into memory whole. Five minutes of stereo f32 is 115 MB per Track.
- `bench/ReplayBuffer.Bench/OnsetDetector.cs`: the burst onsets. Construct it with `--tone-dbfs`.
- `src/ReplayBuffer.Platform.Audio/TonePlayer.cs`:
  - Bursts are 3 kHz, 50 ms long, once a second from stream frame 48000.
  - The tone is 440 Hz at 10 dB below the burst level.
  - `BurstQpc(i)` and `BurstCount` give the stamps.
- `bench/ReplayBuffer.Bench/MarkerWindow.cs`: the window to capture (`MarkerWindow.Open(fps)`; flashing not needed).
- `bench/ReplayBuffer.Bench/PlayerScripts.cs`: add `FindVlc(string? given)` and `RunVlc(...)` here, beside Chrome and WMP.
  - Start vlc.exe as above, with the clip path.
  - Poll `http://127.0.0.1:<port>/requests/status.json` (basic auth, empty user) for up to the timeout until `information.category` holds the audio streams.
  - Each `Stream N` with `Type` `Audio` carries its title as `Description`. Order them by N.
  - Then send `?command=pl_stop` and kill the process tree.
  - Return the labels, or the error.
- `bench/ReplayBuffer.Bench/TracksCheck.cs` (new):
  - `bench tracks`: the capture, the throwaway instance, `tracks-markers.json`, the analysis call, the VLC check, `tracks.json`, the printed summary and the exit code.
  - Write settings.json as `SettingsDocument.Empty with { Global = ...SetItem(Keys.TrackLayout.Name, Keys.TrackLayout.ToJson(layout)) }`, serialized with `SettingsJsonContext.Default.SettingsDocument` into `Instance.ForTest(name).RoamingDataFolder/settings.json`. See `TrackLayoutCaptureTests` for the `LayoutDevice` and `Track` constructors.
  - The engine args: `debug-capture --instance <name> --track-layout --hwnd <marker> --seconds <n> --output tracks.mp4 --size --fps --bitrate`, with no device options.
- `bench/ReplayBuffer.Bench/TrackAnalysis.cs` (new): the checks, as pure functions over probe results and per-Track onsets, levels and peaks, so tests feed them without devices.
  - Records: `TracksReport` (in `tracks.json`) and `TrackFigures` per stream: title, default, duration, onsets, burst level dBFS, peak.
  - Checks: titles, default, durations, sync, level, fullScale, vlc. Each has a figure, a target and a mark (`DriftMath.Pass`/`Miss`, or `not-checked`).
  - Sync: match each Game onset to the nearest Everything onset within ±5 ms, and each Chat onset likewise. Report the matched fraction and the max and median |Δ| in ms. Bound 1 ms.
  - Level: per onset of a single-device Track, the RMS of samples onset+5 ms to onset+45 ms (mono average), ×√2, in dBFS. Take the median over bursts and compare with `--tone-dbfs`, ±0.5 dB.
  - fullScale: the peak |sample| of Everything ≤ 1.05 (AAC coding tolerance, as 2.2's test). Also report Everything's median burst level, which shows the limiter acting.
- `bench/ReplayBuffer.Bench/BenchOptions.cs`:
  - Add `tracks` with options `minutes` (5, 1 to 60), `device-a`, `device-b`, `input-device`, `tone-dbfs`, `size`, `fps`, `bitrate`, `vlc`, `reanalyze`, plus Common.
  - Add the usage text.
  - `output-device` doesn't apply to `tracks`.
- `bench/ReplayBuffer.Bench/Program.cs`: dispatch `tracks`. Update the header comment.
- `bench/ReplayBuffer.Bench/Report.cs`: load `tracks.json` and add a "Named Tracks" section (one row per check). Update the class doc.
- Tests in `tests/ReplayBuffer.Bench.Tests/`:
  - `OptionsTests.cs` and `ReportTests.cs` patterns.
  - `EndToEndAnalysisTests.cs` builds a lavfi file with ffmpeg; follow it. Skipped without ffmpeg.

## Tasks & Acceptance

**Execution:**
- [x] `bench/ReplayBuffer.Bench/TrackAnalysis.cs`: records and checks.
- [x] `bench/ReplayBuffer.Bench/PlayerScripts.cs`: `FindVlc` and `RunVlc`.
- [x] `bench/ReplayBuffer.Bench/TracksCheck.cs`: the command.
- [x] `bench/ReplayBuffer.Bench/BenchOptions.cs` and `Program.cs`: the `tracks` command and its options.
- [x] `bench/ReplayBuffer.Bench/Report.cs`: the Named Tracks section.
- [x] `tests/ReplayBuffer.Bench.Tests/TrackAnalysisTests.cs`: each matrix row from synthetic onsets, levels and probe data:
  - pass;
  - wrong names or order;
  - wrong default;
  - 5 ms shift;
  - −3 dB level;
  - peak 1.2;
  - a short Track;
  - too few onsets;
  - VLC absent, matching or mismatched.
- [x] `tests/ReplayBuffer.Bench.Tests/TracksEndToEndTests.cs`: ffmpeg lavfi makes a 20 s mp4 with h264 and four AAC streams titled Everything, Game, Chat, Mic (`-metadata:s:a:N title=...`, disposition default on the first). The signals mimic the layout:
  - Game: bursts at −6 dBFS;
  - Chat: bursts offset by 0.5 s;
  - Everything: their sum, limited to ≤ 1;
  - Mic: silence.

  `--reanalyze` on it, with a hand-written markers log, passes. A second file with Game delayed 5 ms fails `sync`. Skipped without ffmpeg.
- [x] `tests/ReplayBuffer.Bench.Tests/OptionsTests.cs` and `ReportTests.cs`: parsing of `tracks` (`device-a` required, `output-device` refused); the report section from a sample `tracks.json`.

**Acceptance Criteria:**
- Given two inaudible Output Devices on the builder's PC (e.g. the Steam Streaming ones), when `ReplayBuffer.Bench tracks --device-a <A> --device-b <B> --out measurements/bench-2.9` runs for 5 minutes, then it exits 0. `tracks.json`'s checks all pass: titles Everything, Game, Chat, Mic in order; only Track 1 default; durations within 0.1 s; sync within 1 ms; Game and Chat within 0.5 dB of the played level; Everything ≤ 1.05; VLC listing the same names in order. `report.md` shows the Named Tracks section.
- Given that run folder, when `tracks --reanalyze --out <same>` runs, then it reproduces the same verdict without playing or capturing.
- Given a file whose Tracks are misnamed or out of sync, when it is analysed, then the verdict is fail and the exit code 1.

## Implementation Notes

- `MarkedCapture.ResolveDevice` and `MediaAnalysis.Run`, `RunTool` and `PacketTimes` became `internal` for reuse; no behaviour change.
- Track durations and onset start times come from packet timestamps, as `drift` does. Titles are read from the `name` tag, then `title`, then `handler_name`.
- B's phase: B is started so its first burst lands 0.5 s into A's second grid (A's latency taken as B's), checked against B's first stamp and retried up to 3 times; outside 0.35–0.65 s it is a warning.
- VLC found but failing to list the streams is a `miss` (not `not-checked`); only an absent vlc.exe is `not-checked`.
- The throwaway instance's engine logs are copied to `tracks-instance-logs/` in the run folder before its folders are deleted.

## Plan Change Log

## Review Triage Log

## Design Notes

Why both sources play at full scale: each Track's source reaches 0 dBFS, which is R3's "two full-scale sources". A's burst (1.0) plus B's tone (0.32) would reach 1.32, so `Everything` exercises the limiter every second, and its peak shows the limiter held it. The bursts use 3 kHz and the tones 440 Hz, so the onset detector still finds both devices' bursts in the mix. B starts half a second after A, so their onsets don't fall within the detector's 200 ms re-arm.

Why the level is compared with the played level: a 1-minute probe on Steam Streaming Speakers (2026-11-02) read −6 dBFS bursts back at −5.9 dBFS and the −16 dBFS tone at −16.0. Loopback on that device carries the played level, so "a single-device Track at its source's level" is measurable to ±0.5 dB.

Why VLC through its HTTP interface: VLC 3's `status.json` lists each audio stream's title as its `Description`, in order. That is the name its Audio Track menu shows, read unattended with no sound or window. DaVinci Resolve isn't installed and can't be scripted in the free edition, so it is a Human Check.

## Verification

**GPU lock:** before every build, test or capture, check that `%USERPROFILE%\Documents\repos\ReplayBuffer\.bench-running` doesn't exist; wait while it does.

**Environment:** the worktree needs `native/ffmpeg` (copied from the main checkout).

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release`: expect 0 warnings and 0 errors.
- `dotnet test --solution ReplayBuffer.slnx`: expect all to pass. A known real-time flake gets one rerun.
- `pwsh tools/publish-engine.ps1`: expect 0 IL/AOT warnings.
- `bench/ReplayBuffer.Bench/bin/Release/.../ReplayBuffer.Bench.exe tracks --device-a "2- Steam Streaming Speakers" --device-b "Speakers (Steam Streaming Microphone)" --out measurements/bench-2.9 --repo .`: expect exit 0 and every check pass.
