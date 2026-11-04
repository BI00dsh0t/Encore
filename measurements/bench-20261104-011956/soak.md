# ReplayBuffer soak, 2026-11-04 01:40

**Verdict: miss.** 19 of 19 Clip(s) pass; 8 of 10 bound(s) met.

- Run: 20 min in memory mode, a 5-minute warm-up, 121 samples (every 10 s), 19 presses of ctrl+alt+shift+f16 (every 60 s)
- Buffer Window 30 s; Tracks Everything; 60 fps
- Instance `ReplayBuffer-Test-soak-0d59e9d7`; App `%USERPROFILE%\Documents\repos\ReplayBuffer\.claude\worktrees\festive-lamarr-1763e6\src\ReplayBuffer.App\bin\Release\net10.0-windows10.0.26100.0\ReplayBuffer.App.exe`; engine `%USERPROFILE%\Documents\repos\ReplayBuffer\.claude\worktrees\festive-lamarr-1763e6\src\ReplayBuffer.App\bin\Release\net10.0-windows10.0.26100.0\ReplayBuffer.Engine.exe`
- GPU load: 2560x1440 on NVIDIA GeForce RTX 5090, 80.5 fps (target 80)
- Run folder: `%USERPROFILE%\Documents\repos\ReplayBuffer\.claude\worktrees\festive-lamarr-1763e6\measurements\bench-20261104-011956`

## Bounds

| Process | Counter | Value | Limit | Result |
|---|---|---|---|---|
| app | privateBytes | private bytes +11.9 MB ≤ 20 MB since warm-up (82.3 MB) | 20 | pass |
| app | privateBytesSlope | slope 59.43 MB/h ≥ 2 MB/h | 2 | **fail** |
| app | handles | handles +0 ≤ 50 | 50 | pass |
| app | gdi | gdi +0 ≤ 50 | 50 | pass |
| app | user | user +1 ≤ 50 | 50 | pass |
| engine | privateBytes | private bytes -0.57 MB ≤ 26.54 MB since warm-up (530.8 MB) | 26.54 | pass |
| engine | privateBytesSlope | slope 4.28 MB/h ≥ 2 MB/h | 2 | **fail** |
| engine | handles | handles -8 ≤ 50 | 50 | pass |
| engine | gdi | gdi +0 ≤ 50 | 50 | pass |
| engine | user | user -1 ≤ 50 | 50 | pass |

## Clips

| Press | At (s) | Outcome | Video (s) | Audio (s) | Tracks | Frames / expected | Result |
|---|---|---|---|---|---|---|---|
| 1 | 60 | saved | 29.283 | 29.269 | Everything | 1757 / 1757 | pass |
| 2 | 120 | saved | 29.983 | 29.973 | Everything | 1799 / 1799 | pass |
| 3 | 180 | saved | 29.017 | 29.011 | Everything | 1741 / 1741 | pass |
| 4 | 240 | saved | 29.017 | 29.026 | Everything | 1741 / 1741 | pass |
| 5 | 300 | saved | 29.983 | 29.973 | Everything | 1799 / 1799 | pass |
| 6 | 360 | saved | 29.983 | 29.973 | Everything | 1799 / 1799 | pass |
| 7 | 420 | saved | 29.017 | 29.011 | Everything | 1741 / 1741 | pass |
| 8 | 480 | saved | 29.983 | 29.973 | Everything | 1799 / 1799 | pass |
| 9 | 540 | saved | 30 | 29.989 | Everything | 1800 / 1800 | pass |
| 10 | 600 | saved | 29.017 | 29.013 | Everything | 1741 / 1741 | pass |
| 11 | 660 | saved | 29.983 | 29.973 | Everything | 1799 / 1799 | pass |
| 12 | 720 | saved | 30 | 29.995 | Everything | 1800 / 1800 | pass |
| 13 | 780 | saved | 29.033 | 29.032 | Everything | 1742 / 1742 | pass |
| 14 | 840 | saved | 29.967 | 29.967 | Everything | 1798 / 1798 | pass |
| 15 | 900 | saved | 30 | 29.989 | Everything | 1800 / 1800 | pass |
| 16 | 960 | saved | 29.017 | 29.013 | Everything | 1741 / 1741 | pass |
| 17 | 1020 | saved | 29.983 | 29.973 | Everything | 1799 / 1799 | pass |
| 18 | 1080 | saved | 29.017 | 29.013 | Everything | 1741 / 1741 | pass |
| 19 | 1140 | saved | 29.983 | 29.973 | Everything | 1799 / 1799 | pass |

Passing Clips are deleted after their check; a failing one stays in the run folder's `clips` folder.
