---
title: 'Transport: seek bar, time, volume and keys'
type: 'feature'
ticket: '5'
created: '2026-11-05'
status: done
baseline_revision: '27ebd41'
route: 'quick'
risk: 'low'
---

# Story 14.5: Transport: seek bar, time, volume and keys

Built before 14.4 at the builder's word ("the player is missing basic player controls", "all that exists is a play button"); the ticket order stays as planned ("we don't need to swap ticket order"). It's stacked on 14.2's tip `27ebd41` (`feature/14.5-transport` from `feature/14.2-player-tracer`), since 14.2 merges after tonight's Heavy run. App only; AD-27.

## The seek-while-paused finding (14.2's note), settled

A scratch probe drove the real `MediaPlayer` over the four-Track fixtures, through a shared stream and on a pumping WPF dispatcher, as the App does. Paused at 0, it sought to 3 s and then played.

| Case | Frame after the paused seek | Where Play resumed |
|---|---|---|
| No Track switch | yes, at 3.00 s | 3 s |
| A Track switch at open (index 2 to 0, as 14.2 did) | none, or at 3.00 s | **0 s** (14.2's proof) |
| Track 0 chosen as the Tracks are listed (`AudioTracksChanged`), so no switch | yes, at 3.00 s | 3 s |
| A switch while playing, then pause and seek | yes | 3 s |
| A switch while paused, then seek | yes | **0 s** |
| A switch while paused, then Play with the position set again | yes | 3 s |

So a paused seek works in itself. It's a Track switch while paused that makes Media Foundation's next Play resume from the start. `WindowsMediaPlayer` now:
- chooses the first Track in `AudioTracksChanged` while the file opens, so there's no switch at open. 14.2's `SelectedTrack = 0` is then a no-op, and the setter skips a no-change.
- after any Track switch while paused, sets the paused (or since sought) position again just after `Play()`. 14.4's picker relies on this, and its Heavy check should cover a paused switch followed by Play.

## Changes

- **`IPlayer`** gains `Volume` (0 to 1), `IsMuted` and `Seek(TimeSpan)`. In `WindowsMediaPlayer`, `Seek` sets `PlaybackSession.Position`; paused, that delivers the frame through the frame server, as above.
- **`PlayerPage`:**
  - `Position` is read by `RefreshPosition()`, which the view calls every 100 ms while playing and skips while dragging.
  - `PositionSeconds` is the seek bar's two-way value. A click seeks; while `IsScrubbing`, it only records the latest point, which `ApplyPendingScrub()` seeks once a rendered frame and on release.
  - `DurationSeconds`; `TimeText` ("0:04 / 0:10", h:mm:ss from an hour, the elapsed time alone with no known length).
  - `HasDuration`, `CanSeek` (a known length and a playable file) and `HasControls`.
  - `SeekTo` (clamped to the file) and `SeekBy` (Left and Right, 5 s).
  - `Volume` and `IsMuted`, with `MuteCommand`, `MuteGlyph` and `MuteName`, and `ChangeVolume` (Up and Down, 5 %, which unmutes). A `VolumeChanged` event lets the Library keep them.
  - At the end it stops on the last frame with the bar full, and Play seeks to 0 and plays.
- **`LibraryViewState`** gains `PlayerVolume` (1.0) and `PlayerMuted` (false), additive. An older file loads full and unmuted, and a volume out of range is clamped. `LibraryPage` passes them to each player page and saves on change.
- **`PlayerView`:**
  - The bar now runs across the bottom of the video, in both looks: Play or Pause, the time, the seek bar (`IsMoveToPointEnabled`, hidden with no known length), mute (a Segoe Fluent Icons glyph, named for screen readers) and a 96 px volume slider.
  - Thumb drag events turn `IsScrubbing` on and off, and apply the pending point on `CompositionTarget.Rendering`.
  - Keys are on the bubbling `KeyDown`, so a focused control's own keys win: the Play button's and mute's Space, and the sliders' arrows. The page's keys are Space, Left/Right and Up/Down; Esc stays the shell's (it closes the drill-in).
- **`LibraryTheme.xaml`:** `RbPlayerBarStyle` is stretched with 16 px margins, and `RbPlayerTimeStyle` (12 px, text-primary, tabular figures) is added.

## Tests

- **Default, fake player:**
  - `PlayerTransportTests`:
    - `TheSeekBarAndKeysSeekWithinTheFileAndADragSeeksOncePerFrame`: Right seeks 5 s, clamps at 30 s and at 0; a click seeks; a drag through 10, 11 and 12 s seeks once, to 12 s.
    - `PlayPausesAndAtTheEndStopsOnTheLastFrameThenStartsAgain`.
    - `TheVolumeAndMuteAreKeptAcrossRuns`, through a real view-state file; Up unmutes.
    - `AFileWithNoKnownLengthShowsTheElapsedTimeOnlyAndCantBeSought`, and the h:mm:ss format.
  - `PlayerViewThemeTests` (extended):
    - the seek bar, volume and `0:04 / 0:10` show in both looks;
    - the page's keys (Right, Down, Space), sent to the view, seek 5 s, lower the volume and play.
- **Not added:** a Heavy test for the paused seek through the real player. The probe above measured it. 14.2's `PlayerPlaybackTests` still run tonight, unchanged.

## Verification

- `dotnet build ReplayBuffer.slnx -c Release`: 0 warnings, 0 errors.
- Default suites, 0 failed (started 16:18): Platform.Desktop 15 of 15; App 239, with 216 passed and 23 skipped (Explicit/Heavy).
- PNGs for QA, beside the App test binary: `player-fluent.png` and `player-gaming.png` now show the transport, and `player-failed-gaming.png` the failure.

## Log

- 2026-11-05: branch `feature/14.5-transport` from `feature/14.2-player-tracer` at `27ebd41`; built and verified by 16:20.
