---
title: 'Refactor sweep'
type: 'refactor'
ticket: '7'
created: '2026-11-05'
status: built
baseline_revision: 'c7f7013'
route: 'quick'
risk: 'low'
---

# Story 14.7: Refactor sweep

Built on 14.6's tip `c20c1a4` (`feature/14.7-player-sweep` from `feature/14.6-cant-play`), at the builder's word: "continue the epic". Rebased onto develop `c7f7013` once 14.2 to 14.6 had merged. Cleanup only. 14.3 (`mehd` at finalize, in the engine) is built separately; it isn't in this stack, and its cleanup belongs to its own branch.

## Scope, from what the epic deferred

| Item | Source | Outcome |
|---|---|---|
| A paused seek that resumed from 0 | 14.2's Follow-ups | **Closed in 14.5.** Its cause was a Track switch while paused, fixed in `WindowsMediaPlayer`. |
| The Track list reads `TrackNames` with no seam change | 14.2's Follow-ups | **Closed in 14.4.** |
| Interop helpers left in the App | 14.7's ticket | **None.** The App has no `DllImport`, `LibraryImport`, `ComImport` or `unsafe` (grepped). All the D3D interop is in `Platform.Desktop.VideoSurface`. |
| A player view model grown across stories | 14.7's ticket | **Trimmed.** `PlayerPage` gets one `FileName` for its three log lines, and `Fail` no longer raises `Message` and the Store link's properties itself, since the state change raises them. `PlayerPage` stays one class: the transport, Tracks and failure share its state and its tests. |
| The volume written on every slider step | found in this sweep | **Fixed.** `LibraryPage` holds the volume and mute as they change, and writes the view state once, when the player page closes (Back, a settings page, Quit). The 14.5 test now closes the page before reopening. |
| `FakePlayer` inside `LibraryPlayerTests.cs` | found in this sweep | **Moved** to `FakePlayer.cs`; five test classes use it. |
| The no-picture timer had no default test | 14.6's plan | **Closed.** `PlayerView.NoPictureTimeout` is an internal property a test shortens. Writing the test showed the view's own presents (the media arriving, a resize) counted as a picture, so a page that never gets one never failed. **Fixed:** only frames the player delivers count. New test: `PlayerViewThemeTests.APageWithNoPictureInTimeSaysTheFileCantBePlayed`. |
| 14.7's verify: a Recording still plays, switches Track and seeks | 14.7's ticket | **Added, Heavy:** `PlayerPlaybackTests.ARecordingFixturePlaysSeeksAndSwitchesTrack` on a fragmented fixture (AD-9's flags). It seeks to 5 s, switches to track 3, plays on past 5 s, and the picture shows. |

## QA notes folded in (from the 14.5, 14.4 and 14.6 reviews)

| Note | Outcome |
|---|---|
| 14.5: test that an old view file, without `PlayerVolume` and `PlayerMuted`, loads with the defaults | **Test added:** `PlayerTransportTests.AViewFileFromBeforeThePlayerLoadsFullAndUnmuted`. |
| 14.5: `ChangeVolume` wrote the view file twice; round to 2 dp | **Fixed.** `Volume` rounds to 2 dp and raises `VolumeChanged` once per change; `ChangeVolume` is just `Volume += delta`. The file is written only when the page closes (the sweep above). Test: `TheVolumeStepsWithoutDriftAndAnyChangeUnmutes` (0.333333 is 0.33; steps reach exactly 0 and 1). |
| 14.5: the volume slider dragged while muted should unmute, as the keys do | **Fixed.** Any change to `Volume` unmutes. Same test. |
| 14.5: stop the scrub before `_page` is cleared on detach | **Fixed.** `Attach` calls `StopScrub()` first, so the page's `IsScrubbing` is cleared. |
| 14.5: the Fluent seek thumb is grey on grey | **Noted for 14.8.** Under Fluent the slider takes the Windows accent, which is grey on this PC (`#4C4A48`), by design (R37). No token fix; 14.8's ticket now asks the builder whether it's visible enough or should be a fixed colour. |
| 14.4: Left and Right with the Track list focused | **Decided: the list's own keys win.** A probe showed a focused, closed ComboBox handles all four arrows: Down and Right pick the next Track, Up and Left the one before. That's standard Windows behaviour and the page's rule. In `PlayerViewThemeTests` they pick tracks 1, 2, 1 then 0, with no seek and no volume change. |
| 14.4: render the open drop-down in both looks | **Added:** `player-tracks-fluent.png` and `player-tracks-gaming.png`, from the ComboBox's popup, in `PlayerViewThemeTests`. |
| 14.6: pause the no-picture timer while the front buffer is gone | **Fixed.** `PlayerView.FrontBufferChanged(false)` stops the timer, and `true` restarts it in full if no player frame has come yet, before redrawing. Test: `APageWithNoPictureInTimeSaysTheFileCantBePlayed` holds the front buffer away for 700 ms against a 300 ms timeout without failing, then fails once it's back. |

## Verification

- `dotnet build ReplayBuffer.slnx -c Release` on develop `c7f7013` plus 14.7: 0 warnings, 0 errors (16:53).
- **Tests deferred, per the builder** ("don't run any tests until later. make a backlog of tests to run"). QA is code review only.
  - 14.7's default tests have never run. They're in the test backlog's row for 14.7 (§1). They are:
    - `PlayerTransportTests`: `AViewFileFromBeforeThePlayerLoadsFullAndUnmuted`, `TheVolumeStepsWithoutDriftAndAnyChangeUnmutes`, and `TheVolumeAndMuteAreKeptAcrossRuns` (changed: it closes the page before reopening);
    - `PlayerViewThemeTests`: `APageWithNoPictureInTimeSaysTheFileCantBePlayed` (new), and `ThePlayerPageShowsInBothLooksAndTheHiddenListIgnoresItsKeys` (now with the Track list's keys and the drop-down renders).
  - The last default run before the stop, at `dc22314`, passed: Platform.Desktop 15 of 15; App 250, with 225 passed and 25 skipped. A run after the QA notes went in reached the Track list's arrow-key check, which led to the decision above; it was stopped before the rest finished.
- **Heavy** (the backlog, §2 row 8): `PlayerPlaybackTests`, including the new `ARecordingFixturePlaysSeeksAndSwitchesTrack`.

## Log

- 2026-11-05: branch `feature/14.7-player-sweep` from `feature/14.6-cant-play`; built by 16:43.
- 2026-11-05: rebased onto develop `c7f7013` after 14.2 to 14.6 merged; the QA notes folded in; built at 16:53, tests deferred to the backlog.

## Code review round 1 fixes (2026-11-05)

The review of `fd4a77a` confirmed the view file saving on close (Quit included), the scrub stop, the count of player frames only, and the timer's threading. It asked for four fixes, with no tests run:

1. **[regression] Unmute on any request:** `Volume` unmuted only when the value changed, so Up at 1.0 or Down at 0 while muted, or the slider at the same value, left it muted. Any set now unmutes when muted. `TheVolumeStepsWithoutDriftAndAnyChangeUnmutes` adds Up at 1.0 while muted.
2. **The slider snaps to the rounded value:** `Volume` rounds before comparing and always raises PropertyChanged, so a slider at 0.333 is told the 0.33 kept. `VolumeChanged` is raised only when the volume or the mute changed. The test counts one PropertyChanged for the 0.333 set.
3. **[risk] The no-picture wait for media already open:** `PlayerView.Attach` starts the wait (`StartNoPictureWait`) when the page has its media and hasn't failed, as the `Media` change does. A fast open, or `FakePlayer`, no longer leaves the timer unstarted.
4. **One front-buffer signal:** `_frontBufferAvailable`, set from `D3DImage` at construction and by `FrontBufferChanged`, is what the present, the `Media` handler, `Attach` and the timer restart all read. The test's `FrontBufferChanged(false)` and the real event now mean the same thing.

- `dotnet build ReplayBuffer.slnx -c Release`: 0 warnings, 0 errors (16:58). The tests stay deferred to the backlog.
