---
title: 'Refactor sweep'
type: 'refactor'
ticket: '7'
created: '2026-11-05'
status: built
baseline_revision: 'c20c1a4'
route: 'quick'
risk: 'low'
---

# Story 14.7: Refactor sweep

Built on 14.6's tip `c20c1a4` (`feature/14.7-player-sweep` from `feature/14.6-cant-play`), at the builder's word: "continue the epic". Cleanup only. 14.3 (`mehd` at finalize, in the engine) is built separately; it isn't in this stack, and its cleanup belongs to its own branch.

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

## Verification

- `dotnet build ReplayBuffer.slnx -c Release`: 0 warnings, 0 errors.
- Default suites, 0 failed (started 16:42): Platform.Desktop 15 of 15; App 250, with 225 passed and 25 skipped (Explicit/Heavy).
- **Heavy, written and not run** (nights): the stack's `PlayerPlaybackTests` now cover a Clip (14.2), 6400×2160 HEVC, 200 opens, the heard Track and a paused switch (14.4), and a fragmented Recording (14.7).

  `dotnet test --project tests/ReplayBuffer.App.Tests -c Release --no-build --explicit only --filter-class "*PlayerPlaybackTests"`

## Log

- 2026-11-05: branch `feature/14.7-player-sweep` from `feature/14.6-cant-play`; built and verified by 16:43.
