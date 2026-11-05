---
title: 'Track picker'
type: 'feature'
ticket: '4'
created: '2026-11-05'
status: done
baseline_revision: 'c7257a3'
route: 'quick'
risk: 'medium'
---

# Story 14.4: Track picker

Built on 14.5's tip `c7257a3` (`feature/14.4-track-picker` from `feature/14.5-transport`), at the builder's word: "continue the epic". The ticket order stays as planned. App only; AD-27.

## Changes

- **`PlayerTrack`** (new): a Track's index in file order and its name, the stream's `handler_name`. With no name it's "track n", counted from 1 as epic 10's glossary does ("track 1"). `IsPlaying` marks the one that plays.
- **`PlayerPage`:**
  - `Tracks` is filled from `PlayerMedia.TrackNames` when the file opens.
  - `SelectedTrack` is the first at open, and again on every reopen, since each open is a new page.
  - Choosing another sets `IPlayer.SelectedTrack` mid-play and marks it. The position doesn't move and there's no seek; Windows leaves its ~0.1 s gap.
  - A null or foreign choice, or one while the page is closed or failed, is refused, and the list shows the Track that plays again.
  - `HasTracks` shows the list once the file is open and playable.
- **`PlayerView`:** a ComboBox, "Audio track", in the bar between the seek bar and mute, from 120 to 200 px wide, listing the names and two-way on `SelectedTrack`. It works by mouse and keyboard in both looks. Its own arrow keys win while it's focused, since the page's keys bubble.
- **A Track switch while paused** works because of 14.5's `WindowsMediaPlayer`: Play sets the paused position again after a paused switch, and Track 0 is chosen as the Tracks are listed, with no switch at open.
- **14.5's Heavy harness** (fixed on 14.5 as `c7257a3`, under it here) now mutes through the page, and `MakeFixture` takes a length.

## Tests

- **Default, fake player:** `PlayerTrackTests`:
  - `TheListShowsTheTracksByNameWithTheFirstPlayingAndAgainOnReopen`: Game, Mic, Discord, Desktop, the first playing; a reopen asks the new player for index 0.
  - `ChoosingATrackWhilePlayingSwitchesWithoutMovingThePosition`: Discord gives `SelectedTrack` 2, no seek, the position kept and still playing; a cleared choice keeps the Track that plays.
  - `AnUnnamedTrackIsCountedFromOne`: "track 2", "track 3".
- **`PlayerViewThemeTests`:** the Track list shows "Game" in both looks, in `player-fluent.png` and `player-gaming.png`.
- **Heavy, written and not run** (nights only): `PlayerPlaybackTests.TheChosenTrackIsTheOneHeardAndAPausedSwitchResumesInPlace`.
  - It plays a 12 s four-tone fixture at volume 0.001 (inaudible).
  - New `ProcessLoopback` helper: Windows' process-loopback capture of the test's own audio, with a Goertzel check of each Track's tone. It checks the heard Track 450 ms after each of ten switches.
  - Then, paused at 3 s, a switch and Play must play on from past 3 s.

  `dotnet test --project tests/ReplayBuffer.App.Tests -c Release --no-build --explicit only --filter-class "*PlayerPlaybackTests"`

## Verification

- `dotnet build ReplayBuffer.slnx -c Release`: 0 warnings, 0 errors.
- Default suites, 0 failed (started 16:27): Platform.Desktop 15 of 15; App 243, with 219 passed and 24 skipped (Explicit/Heavy, one new).

## Log

- 2026-11-05: branch `feature/14.4-track-picker` from `feature/14.5-transport`; built and verified by 16:28.
