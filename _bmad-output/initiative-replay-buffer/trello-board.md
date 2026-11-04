# Trello board

The Trello board that mirrors this ticket tree. The tree stays the source of truth; the coordinator moves cards as work advances.

- **Board:** [ReplayBuffer](https://trello.com/b/Q3TeryFd/replaybuffer), private, in the builder's Trello Workspace.
- **Board id:** `ari:cloud:trello::board/workspace/6ae9f97af5eb2665e52529f6/6ae9fa1c9105c93239e2ce44`
- **Card id form:** `ari:cloud:trello::card/workspace/6ae9f97af5eb2665e52529f6/<card id>`. A card opens at `https://trello.com/c/<card id>`.

## Lists

| List | Meaning | List id |
|------|---------|---------|
| Backlog | waiting on prerequisites | `6ae9fa34de1908c24b0712be` |
| Ready | prerequisites met, not started | `6ae9fa354bf769b2861f09a7` |
| In progress | a session is building it | `6ae9fa36873f9ca073071568` |
| In QA | READY FOR QA reported, QA running or fixes in flight | `6ae9fa379a75786dc5c08722` |
| Done | merged into develop and marked done | `6ae9fa383e33708cc8d74fb3` |

List ids take the form `ari:cloud:trello::list/workspace/6ae9f97af5eb2665e52529f6/<list id>`.

## Labels

| Color | Meaning | Label id |
|-------|---------|----------|
| red | high risk | `6ae9fa1c9105c93239e2ce69` |
| purple | needs the builder (HITL) | `6ae9fa1c9105c93239e2ce6a` |

Label ids take the form `ari:cloud:trello::label/workspace/6ae9f97af5eb2665e52529f6/<label id>`. Done cards carry no labels.

## Cards

| Ticket | Card id |
|--------|---------|
| 1.1 Tracer: game window and two audio devices to a playable fragmented MP4 | `6ae9fa9c5fbdc02044a98082` |
| 1.2 Path A measurement: FPS cost, BGRA encoder input and QPC stamps | `6ae9fa9d64481394fa3b4af2` |
| 1.3 NativeAOT proof for the engine | `6ae9fa9ee863c94bd0722045` |
| 1.4 Settings store, complete key catalog and Resolve | `6ae9fa9f321cf914fb4da139` |
| 1.5 IPC server and client with numbered events, snapshot and shared shapes | `6ae9faa0d3b99b79edd5753b` |
| 1.6 SessionOrchestrator and the capture-session API | `6ae9faa1027d3e6f63b90426` |
| 1.7 Main App shell and tray | `6ae9faa35529c55315e55c45` |
| 1.8 Supervision and single instance | `6ae9faa4e4e08d00adb22642` |
| 1.9 Hotkey registration in the engine | `6ae9faa526abb534fd5340e3` |
| 1.10 Output Settings and encoder probing | `6ae9faa62e0786d13f78505a` |
| 1.11 Fixed output resolution, frame pacer and color path | `6ae9faa72a16a41269bd2dcd` |
| 1.12 AudioDeviceHub port with QPC stamping and adaptive resampling | `6ae9faa884ff04eddd0d361f` |
| 1.13 Publish, install and Start with Windows | `6ae9faa91a52505906473334` |
| 1.14 FPS and drift benchmark, and the final stack decision | `6ae9faaa3c849ff3a09737b7` |
| 1.15 Refactor sweep | `6ae9faab22a3431cfc07cb22` |
| 2.1 Tracer: Global Track Layout to named Track streams in a capture | `6ae9fae369d1613df3927138` |
| 2.2 Track mixer: summing, alignment and limiter | `6ae9fae488dc5bb517610f34` |
| 2.3 Live device list over IPC | `6ae9fae5fbd331ea676726bb` |
| 2.5 Device dropout and rebind | `6ae9fae8a1927c4183aaba43` |
| 2.4 Mic Devices and mic mute | `6ae9fae74729aede0695d179` |
| 2.6 Track meters in the engine | `6ae9fae96050f840e8a8886d` |
| 2.7 Track Layout editor in the Main App | `6ae9faeabbfad2b266b3b950` |
| 2.11 Mic Devices and double-count warning in the editor | `6ae9faec85ac810ae9a05616` |
| 2.8 Live meters in the Track Layout editor | `6ae9faed82c7a0be9642350f` |
| 2.9 Named Tracks in players and editors, end to end | `6ae9faebc3883b91e9a9f24f` |
| 2.10 Refactor sweep | `6ae9faee968416a90633df80` |
| 3.1 Tracer: detect a borderless game and make it the Target Game | `6ae9faef231e8ea97179e9bd` |
| 3.8 Which Windows game list to read | `6ae9faf205b7350ec115d934` |
| 3.2 Detection sources and exclusions | `6ae9faf3f3f28b2d417087c2` |
| 3.3 Target Game rule | `6ae9faf4294cfad607f6bd63` |
| 3.4 Game Profile auto-creation and Ignored | `6ae9faf5036377b8ec5916b0` |
| 3.5 OutputPathAllocator | `6ae9faf138b9eb1bfed01d8b` |
| 3.6 Game Profiles page in the Main App | `6ae9faf60f6a21842d70fdee` |
| 3.7 Refactor sweep | `6ae9faf76db0f93befb9567d` |
| 4.1 Tracer: Save Hotkey to a multi-Track Clip from a Memory ring | `6ae9fb3e32fbbad7144b5df5` |
| 4.2 Exact save cut and gapless Reset | `6ae9fb3f551793c40777e283` |
| 4.3 Replay Buffer lifecycle: Target switch, exit drain and Ignored | `6ae9fb404583643aa098edf4` |
| 4.4 Save Cooldown, save events and two concurrent writers | `6ae9fb416a008c67e8af2451` |
| 4.5 Failed saves, retry and the file-being-written signal | `6ae9fb42707f5709e957eb08` |
| 4.6 Ring generations and Buffer Window resizing | `6ae9fb4346d11f9926c5eec7` |
| 4.7 Disk-mode ring | `6ae9fb44a6de44b7b00d6cfc` |
| 4.8 Memory-mode RAM margin | `6ae9fb450a62be56a50b5a55` |
| 4.9 Per-Game-Profile settings at the next Reset | `6ae9fb4607df8ea9480b3548` |
| 4.10 Replay Buffer and Output Settings page with the storage estimate | `6ae9fb47645979f6b6eb4e93` |
| 4.11 Game Profile overrides in the Main App | `6ae9fb4886ff16bc48c013a1` |
| 4.12 Hotkeys page in the Main App | `6ae9fb498a5f6e8f46687771` |
| 4.13 Refactor sweep | `6ae9fb4b69b4e420ae4d9f0b` |
| 4.14 Clips end to end on the builder's PC | `6ae9fb4cd47799c04bbf2f98` |
| 5.1 Tracer: capture state to the Overlay dot | `6ae9fb4dc9344e71bf58a0de` |
| 5.2 Q-4: the Overlay over exclusive fullscreen and its borderless FPS cost | `6ae9fb4ead03073e7f542e56` |
| 5.3 Saving icon through the Save Cooldown | `6ae9fb4f080e480564ed2a50` |
| 5.4 Feedback notices in the engine, with a dev simulator | `6ae9fb509fa40867e0eac852` |
| 5.5 Overlay message queue | `6ae9fb5146b28c46e1be427d` |
| 5.6 Feedback Sounds in the engine | `6ae9fb520f63130db531af95` |
| 5.7 Mic-muted icon and the disk-full red X | `6ae9fb539d8de66898705887` |
| 5.8 Overlay over exclusive fullscreen and the presentation path | `6ae9fb54056de09474aaeeda` |
| 5.9 Capture exclusion for every App window | `6ae9fb55ad15b24c8b31f8e3` |
| 5.10 Main App status line | `6ae9fb56e51fab5ec7d9e1c5` |
| 5.11 Feedback Sounds settings page | `6ae9fb574bf769b286208ba9` |
| 5.12 Benchmark with the Overlay on | `6ae9fb58d1ff81d4fa9da5cb` |
| 5.13 Refactor sweep | `6ae9fb5a53939009b9b4dae4` |
| 5.14 Overlay and Feedback Sounds end to end on the builder's PC | `6ae9fb5b0b019e206baf24a9` |
| 5.15 Adaptive Overlay dot | `6aeabc225526396a15b255cc` |
| 6.1 Tracer: Record Hotkey to a fragmented MP4 Recording beside the Replay Buffer | `6ae9fbb382e57754455e52a4` |
| 6.2 Crash-safe Recordings and recording-interrupted | `6ae9fbb4448044d3ce6b1bec` |
| 6.3 Record Hotkey modes and the Recordings switch | `6ae9fbb5a1f741b5f4d3a7f7` |
| 6.4 Recording lifecycle: game exit, Target hold, Ignored and Quit | `6ae9fbb6ed21e3722aae3266` |
| 6.5 Clips during a Recording on one encoder chain | `6ae9fbb72167fefd9733f981` |
| 6.6 One file however long, and forced encoder restarts | `6ae9fbb93fd8648aad04aca3` |
| 6.7 Disk full during a Recording | `6ae9fbbace73dba581a05e81` |
| 6.8 Recording notices and sounds | `6ae9fbbbd11d1484fdca6964` |
| 6.9 Monitor Recording by its own Hotkey | `6ae9fbbcaf9c7fe29ae8c31e` |
| 6.10 Desktop Duplication fallback for Monitor Recording | `6ae9fbbde0838b64ecb15c91` |
| 6.11 Recordings settings in the Main App | `6ae9fbbe12bd3bc012f36465` |
| 6.12 Refactor sweep | `6ae9fbbf7e4d164fdb5cd7a3` |
| 6.13 Recordings end to end on the builder's PC | `6ae9fbc024dc1e2584ffb19b` |
| 7.1 Tracer: a saved Clip appears in the Library within 2 s | `6ae9fbc1661ad848bb775f32` |
| 7.2 Changes outside the app, reconciliation and a corrupt index | `6ae9fbc291c9855405c27339` |
| 7.3 Length and thumbnails | `6ae9fbc3cacbb6def317641f` |
| 7.4 Favourite | `6ae9fbc429dfdfbcf53c2642` |
| 7.5 Group and filter by game, date and Favourite | `6ae9fbc63f14fc1f53b3cd99` |
| 7.6 Open, show in Explorer and copy for Discord | `6ae9fbc78e2e8a1040027d8e` |
| 7.7 Rename and delete, locked while being written | `6ae9fbc8844c5aede644ca67` |
| 7.8 Cleanup | `6ae9fbc995bec64fe9005c0b` |
| 7.9 Library and Cleanup settings page | `6ae9fbcaca27f2f5b2de6f82` |
| 7.10 Refactor sweep | `6ae9fbcb350464f70300bb2a` |
| 7.11 Library and Cleanup end to end on the builder's PC | `6ae9fbcd792fcc4d8d956222` |
| 8.1 Tracer: a soak harness and a 20-minute soak report | `6ae9fbceafb3955f9fb8a7f5` |
| 8.2 Engine crash recovery and the restart warnings | `6ae9fbce593b3ded44c64d28` |
| 8.3 GPU driver reset recovery | `6ae9fbcf2d69585f67092a52` |
| 8.4 Sleep, resume and the secure desktop | `6ae9fbd12e6a4aabfffc45d7` |
| 8.5 Monitor changes | `6ae9fbd284bcfdbb8f943112` |
| 8.6 Nothing to re-select after a reboot, a restart or a mixer restart | `6ae9fbd3dd8c7da4c7ef8b0e` |
| 8.7 Final conformance run | `6ae9fbd416363e9cbf5ece83` |
| 8.8 8-hour soaks in Memory and Disk mode | `6ae9fbd5f5eb2665e5290183` |
| 8.9 T-01 to T-25 conformance record | `6ae9fbd69a5adf775cd04eaa` |
| 8.10 Refactor sweep | `6ae9fbd7b25afea35f3d7f08` |
| 8.11 Hands-off operation on the builder's PC | `6ae9fbd939e972b1b489b2c0` |
| 9.1 Design pass: DESIGN.md and EXPERIENCE.md for both themes | `6aeb56781cc628b5e06a81fb` |
| 9.2 Theme infrastructure, live switching and a control gallery | `6aeb56795700216d33ecb918` |
| 9.3 Shell, navigation and status line | `6aeb567a2ad4dc996151b288` |
| 9.4 Library restyled | `6aeb567bc93bbb20384e2b28` |
| 9.5 Settings pages: General, Replay Buffer, Recordings, Hotkeys and Feedback Sounds | `6aeb567c4c0ca2fc05d39a1c` |
| 9.6 Settings pages: Track Layout, Game Profiles, and Library and Cleanup | `6aeb567dd30e8b465ebc5fff` |
| 9.7 Tray menu, dialogs and Overlay messages | `6aeb567e724bef294d1f3f3b` |
| 9.8 Refactor sweep | `6aeb567f4ba1943f56124875` |
| 9.9 Modern UI end to end on the builder's PC | `6aeb5680f8d5e704797db805` |

The legend card at the top of Backlog is `6ae9fa9bd7f70d53c9518fbf`.
