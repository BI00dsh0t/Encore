# Overlay presentation options: keeping a borderless game in independent flip

Research for story 5.8, 2026-11-03. Written on `feature/5.8-overlay-perf-research`, cut from 5.8's tip `50cba20`.

**Decision (builder, 2026-11-03): option E, the adaptive dot (§5), built on this branch.** The experiments in §7 haven't run.

**Status when written:** research only. The builder paused the work and asked for no builds, tests, benchmarks or prototypes. The doc proposes experiments (§7) instead of running them. One read-only capability probe and a `dxdiag /t` dump ran before the pause; they are the only new measurements here, and they are labelled as such.

## 1. The question

Spike 5.2 measured, at 13:12 on 2026-11-03, that the Overlay's always-shown dot kept the synthetic borderless game in **Hardware Composed: Independent Flip** at no FPS cost. Story 5.8 re-ran the same stand-in after the PC rebooted at 20:01, with NVIDIA Overlay, Discord and Steam running and two monitors attached. With the dot shown, the borderless load dropped to **Composed: Flip** and lost about 7.5 % FPS:

| Run (5.8, 20:12–20:30) | Borderless PresentMode, hidden / shown / hidden again | FPS cost |
|---|---|---|
| The App, dot and messages | Hardware Composed: Independent Flip / Composed: Flip / Hardware Composed: Independent Flip | 7.57 % |
| The App, `--dot-only` | same | 7.85 % |
| The spike's stand-in | same | 7.39 % |

In the same 5.8 runs, the **fullscreen** load (a flip swap chain made DXGI fullscreen, which Windows runs with fullscreen optimizations) kept Hardware Composed: Independent Flip with the dot shown (−0.2 %). Legacy exclusive kept Hardware: Legacy Flip (0.08 %).

FR-6 wants the dot shown for as long as capture runs. The epic's fallback shows it only for 3 s after each state change, which deviates from FR-6. The builder asked for better options before choosing.

## 2. How Windows decides (sources)

- Microsoft names three presentation paths for a flip-model swap chain:
  - DirectFlip, where the buffers match the screen.
  - DirectFlip with panel fitters.
  - DirectFlip with multi-plane overlay (MPO), where DWM can "reserve a dedicated hardware scanout plane for your application".
  - Once a swap chain is DirectFlipped, DWM sleeps; this is independent flip.
  - When other desktop content comes on top, Microsoft says DWM has three choices: go back to composition, "reverse compose" the content on top into the app's buffers before flipping, or use MPO to stay in independent flip. ([For best performance, use DXGI flip model](https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/for-best-performance--use-dxgi-flip-model))
- PresentMon's labels, as the community reads them ([Blur Busters PresentMon thread](https://forums.blurbusters.com/viewtopic.php?p=88397)):
  - "Hardware Composed: Independent Flip": the app has been granted a hardware overlay plane.
  - "Composed: Flip": DWM composes the app's surfaces.
- The same community saw it happen with something other than our window: the Windows "Activate Windows" watermark leaves borderless games in Composed: Flip "unless the display has active MPO planes". ([Blur Busters](https://forums.blurbusters.com/viewtopic.php?p=102497))
  - Composed: Flip also rules out tearing.
  - So any topmost content over a borderless game needs a free MPO plane to keep independent flip. That matches what 5.2 and 5.8 saw.
- MPO is optional for display drivers. Where it exists, planes blend with premultiplied alpha, and "at least one plane must support shrinking and stretching". ([Multiplane overlay hardware requirements](https://learn.microsoft.com/en-us/windows-hardware/drivers/display/multiplane-overlay-hardware-requirements))
- An app can ask DXGI, per output, what the hardware can do:
  - [`IDXGIOutput2::SupportsOverlays`](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_3/nf-dxgi1_3-idxgioutput2-supportsoverlays) answers whether the output has MPO at all.
  - [`IDXGIOutput3::CheckOverlaySupport`](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_3/nf-dxgi1_3-idxgioutput3-checkoverlaysupport) answers per format.
  - [`IDXGIOutput6::CheckHardwareCompositionSupport`](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_6/nf-dxgi1_6-idxgioutput6-checkhardwarecompositionsupport) answers whether windowed and fullscreen swap chains can be composed by hardware.
  - All three are documented and need no privileges.
- DirectComposition content (`CreateSwapChainForComposition`, premultiplied alpha) is the surface type DWM can promote to its own MPO plane. That is how video gets a plane. Direct scanout and independent flip for composition content need displayable surfaces and WDDM 3.0. ([Composition swapchain programming guide](https://learn.microsoft.com/en-us/windows/win32/comp_swapchain/comp-swapchain))
  - Nothing public lists the DWM rules for promoting a DirectComposition visual: size, transforms, opacity, or how many planes a desktop window may hold.
- NVIDIA reports only community evidence; nothing official was found:
  - MPO planes vanish when any display runs 10 or 12 bpc.
  - RTX HDR, RTX Video Super Resolution or NVIDIA Image Scaling being on disables MPO.
  - Multi-monitor setups often show `MPO MaxPlanes: 1`.
  - Sources: [Steam / Lossless Scaling thread](https://steamcommunity.com/app/993090/discussions/0/594008672803255485), [r/nvidia MPO thread](https://lr.eu.psf.lt/r/nvidia/comments/qffxcz/mpo_multiplaneoverlays_are_amazing_you_can_play), [MPO-disable gist](https://gist.github.com/douglasparker/9439aa211c3f163e9390ba84db7f18bf).

## 3. What the PC reports now (read-only, before the pause)

- **`dxdiag /t`, 2026-11-03 about 21:50.** For both NVIDIA outputs it reports `MPO MaxPlanes: 1`, `MPO Caps: Not Supported`, `MPO Formats: Not Supported`.
  - The two outputs are 2560×1440 at 240 Hz (primary) and 3840×2160 at 144 Hz. Display topology is Extend. Driver 32.0.16.2101, with hardware scheduling on.
  - A "Meta Virtual Monitor" adapter is present, with no mode.
- **The capability probe** (`ReplayBuffer.Bench.Overlay.exe --probe`, prototype code, uncommitted):

  ```
  adapter NVIDIA GeForce RTX 5090
  output \\.\DISPLAY1 2560x1440 at 0,0 bpc=8 colorspace=DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709 hwcomposition=0x3 (FULLSCREEN|WINDOWED) SupportsOverlays=False overlay(BGRA8)=0x2 (SCALING)
  output \\.\DISPLAY2 3840x2160 at 2560,0 bpc=8 colorspace=DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709 hwcomposition=0x0 (none) SupportsOverlays=False overlay(BGRA8)=0x1 (DIRECT)
  ```

- **Registry, read only:** no `OverlayTestMode` or `DisableMPO` under `HKLM\SOFTWARE\Microsoft\Windows\Dwm`, so MPO isn't switched off by hand.
- **Overlay apps running since login:** NVIDIA Overlay (20:01), Steam (20:02), Discord (20:02).

**Reading:** the game's monitor has **no MPO overlay planes right now**. Both outputs are 8 bpc SDR, so the 10/12-bit cause is ruled out. Spike 5.2's own "risk to watch" said exactly this. The candidates for why MPO is gone are:

- The second monitor: mixed refresh rates, NVIDIA's multi-monitor plane limits.
- An NVIDIA App feature: RTX HDR, Video Super Resolution or Image Scaling.
- Driver state after the reboot.

Whether MPO was on during the spike at 13:12 wasn't recorded. Experiment E1 settles it.

**Consequence:** with no plane, DWM has only composition or reverse composition for anything drawn over a borderless game. No window type the App could choose (WPF layered, DirectComposition, GDI) gets a plane that doesn't exist. Changing the Overlay's presentation technology can't fix this PC as it is now.

**Open puzzle:** the fullscreen load kept independent flip with the dot shown. Two explanations fit:

- DWM reverse-composes the dot into a fullscreen-optimized swap chain.
- Reverse composition can't serve a load that presents with `ALLOW_TEARING`. The borderless load tears; the fullscreen load doesn't.

If the second is right, a borderless game with vsync on, or with a frame cap and no tearing, might keep independent flip without MPO. Experiment E2 tests it.

## 4. The options

Rated on FPS cost, latency, staying out of capture, focus, anti-cheat safety (NFR-3), exclusive fullscreen, and code size.

| # | Option | FPS cost on this PC (no MPO) | FPS cost with MPO | FR-6 (always-shown dot) | Out of capture / focus / NFR-3 | Exclusive fullscreen | Code |
|---|---|---|---|---|---|---|---|
| A | Keep today's WPF layered window, always shown | ~7.5 % borderless (measured) | none (spike 5.2) | yes | yes / yes / yes | unchanged (unseen, harmless) | none |
| B | Rewrite the Overlay as a DirectComposition window with a flip-model, premultiplied swap chain (`WS_EX_NOREDIRECTIONBITMAP`) | expected the same as A: there's no plane to promote to (E3 would confirm) | likely none, as A already achieves | yes | `WDA_EXCLUDEFROMCAPTURE` works on any top-level window; same styles; yes | same as A | large: about 300–500 lines with D2D/DirectWrite for the icons and message text, replacing the WPF view |
| C | Dot on a monitor other than the game's | none expected: nothing covers the game's output (E4) | none | partly: always shown, but not where the player looks; impossible with one monitor | yes | visible, since another output isn't affected | small: one placement choice (`Monitors`) |
| D | Show on change: the dot for 3 s after each state change (epic fallback) | ~7.5 % for 3 s per change only | none | no (deviation) | yes | same | small: a timer in `OverlayViewModel` |
| E | **Adaptive:** always-shown dot where the game's output reports hardware overlays (`SupportsOverlays`), show on change where it doesn't | 3 s windows only | none, FR-6 kept | yes where it's free; deviation only on PCs without MPO | yes | same | small: one DXGI query in `Platform.Desktop`, plus D's timer |
| F | Tray icon state (hollow, solid, saving) as a second always-on cue | none (taskbar is hidden under a borderless game) | none | no; a supplement only | tray isn't captured with window capture | works | small; the App already has a tray |
| G | Sounds only (5.6's Feedback Sounds) | none | none | no; covers events, not a steady state | n/a | works | none; already built |
| H | Inject or hook into the game (Steam, the old Discord overlay, presumably ShadowPlay's indicator) | none | none | yes | **fails NFR-3**, anti-cheat risk | works | out of scope |

Notes on the options:

- **Size, position, opacity, update rate.** Any visible pixel of a topmost window over the game's output needs a plane or composition, so making it smaller doesn't help.
  - A corner position doesn't matter either. Only another output does (option C).
  - A static dot redraws nothing, but DWM still composes every game frame while it is visible, so update rate doesn't matter.
  - A layered window at alpha 0 is effectively hidden, which is the same as D.
- **What others do without injecting.**
  - Discord's revamped overlay stopped hooking games, works only in windowed and borderless, and claims fewer anti-cheat conflicts ([Neowin](https://www.neowin.net/amp/discord-unveils-brand-new-overlay-for-pc-promises-better-game-support-and-performance/), [Gizmodo](https://gizmodo.com/discords-overlay-update-may-finally-convince-your-friends-to-turn-on-video-chat-2000580388), [Discord known issue](https://support.discord.com/hc/en-us/articles/25289844838551--Known-Issue-Game-Overlay)). It is then subject to the same plane rules as our window; nothing public says how Discord handles it.
  - Xbox Game Bar's pinned widgets float over games as separate windows; no public data on their flip impact was found.
  - The Steam overlay injects into the game's graphics API at launch ([bugnet](https://bugnet.io/blog/how-to-fix-steam-overlay-not-showing-in-unity)).
  - Nothing public describes how ShadowPlay draws its indicator.
  - OBS shows no in-game indicator at all.
  - None of these documents a technique that keeps independent flip without MPO.
- **Latency.** Every option except H shows the dot within a frame or two of the App's window update. D and E keep FR-6's "appears within 1 s of the change".

## 5. Recommendation

**Option E, adaptive presentation, keeping the current WPF window.**

- Show the dot always (FR-6 as written) when `IDXGIOutput2::SupportsOverlays` is true for the output holding the Target Game's window.
  - Re-check it on display changes (`WM_DISPLAYCHANGE`) and when the Target Game changes.
- When it is false, show the dot for 3 s after each state change.
- The saving icon, the red X, the mic-muted icon and the queued messages show as they do now. They are transient, and the cost is paid only while they are on screen.
- Add one setting, `overlayDot: auto | always | onChange` (default `auto`), so the builder can accept the FPS cost or avoid it outright.
- Keep the tray icon's capture state (option F) as the always-available glance in every mode.

Why E:

- It keeps FR-6 everywhere the dot is free. Spike 5.2 showed free is the normal state when MPO is on.
- It pays the 7.5 % only in 3 s windows on a PC without planes.
- It is small: one DXGI query in `Platform.Desktop`, a timer and a setting in the App. No Overlay rewrite.

A DirectComposition rewrite (B) is the expensive option, and the evidence says it can't help without planes.

Its weakness: `SupportsOverlays` says planes exist, not that one is free. Another overlay app could hold the last plane. E5 checks how well the query predicts PresentMode; if it predicts badly, `auto` should mean on-change.

**Fallback: option D everywhere.** Show the dot for 3 s after each state change, with the same setting to turn the always-shown dot back on. This is the epic's existing fallback, the FR-6 deviation it already names.

**Not recommended:**

- B: large, and no expected gain on this PC.
- C as the default: not visible where the player looks, and not available on one monitor. It could be a later `overlayMonitor` setting if E4 shows it is free.
- H: breaks NFR-3.

## 6. What the builder needs to decide

1. Accept a deviation from FR-6 on PCs without MPO: the dot shows on change, not always (E, or D as the fallback). The other choice keeps the always-shown dot and accepts about 7.5 % FPS in borderless on such PCs.
2. Whether to find out why MPO is off on this PC (E1). If MPO comes back, this PC gets FR-6 for free under E, and the deviation only matters on other setups.
3. Whether to approve the experiments below. E1, E2 and E5 are the ones that change the decision.

## 7. Proposed experiments (not run; each needs approval)

All run under `.bench-running`, at the console, one at a time.

- **E1, why MPO is off (Human Check; this one changes Windows or app settings, so only the builder can do it).**
  1. Run `ReplayBuffer.Bench.Overlay.exe --probe` (or `dxdiag /t`) as things are now.
  2. Run it again after each of these, one at a time: disconnect or disable the second monitor; turn off RTX HDR, RTX Video Super Resolution and NVIDIA Image Scaling in the NVIDIA App; quit NVIDIA Overlay, Discord and Steam.
  3. Once `SupportsOverlays` is true, re-run `bench overlay --modes borderless` (stand-in and `--host app`).
  - **Shows:** which of these removes the planes, and whether the always-shown dot is free again on this PC, as at 13:12.
- **E2, the tearing hypothesis (Bench only).**
  - Run `bench overlay --modes borderless,borderless-notear` with the stand-in. `borderless-notear` is the uncommitted prototype's mode: the same load without `DXGI_PRESENT_ALLOW_TEARING`.
  - **Shows:** whether DWM keeps a non-tearing borderless game in independent flip with the dot shown and no MPO (reverse composition). If it does, the cost only hits games that tear. That narrows the deviation further, but the App can't see whether a game tears, so it doesn't change E's design.
- **E3, a DirectComposition dot (Bench only).**
  - Run `bench overlay --modes borderless --overlay-kind dcomp`, then `dcomp-nolayered`, then `gdi-opaque`, in this no-MPO state and again after E1.
  - **Shows:** whether any window type keeps independent flip without planes (expected: no), and whether a DirectComposition swap chain gets its own plane where WPF's window doesn't (expected: no difference, since WPF already keeps it with MPO).
  - If both come out as expected, B is closed.
- **E4, the other monitor (Bench only).**
  - Run `bench overlay --modes borderless --overlay-kind wpf-other`.
  - **Shows:** whether a dot on the second monitor leaves the game's output in independent flip with no MPO. Expected: yes.
- **E5, `SupportsOverlays` as a predictor (Bench plus E1).**
  - In each state E1 produces, record the probe's `SupportsOverlays` for the game's output beside `bench overlay`'s borderless PresentMode with the dot shown.
  - **Shows:** whether `auto` can trust the query. A state where the query says true but the game drops to Composed: Flip (a plane taken by another app) means `auto` should default to on-change.
- **Human Check, re-measure with the overlay apps closed** (as the coordinator asked): quit NVIDIA Overlay, Discord and Steam, then repeat 5.8's `bench overlay --host app --modes borderless`. This is part of E1's step 2.

## 8. Prototype code

Written before the pause and **left uncommitted** in this worktree (`adoring-morse-f6f531`). A patch copy is in this session's scratchpad. Only the stand-in Overlay project was built once, and the probe above ran once; nothing else was built or run.

- `bench/ReplayBuffer.Bench.Overlay`:
  - `--probe`: `MpoProbe.cs`.
  - `--kind wpf|wpf-other|dcomp|dcomp-other|dcomp-nolayered|gdi-opaque`: `NativeDots.cs`, `Program.cs`, `OverlayHostWindow.cs`.
  - CsWin32 setup: `NativeMethods.*` and the csproj.
- `bench/ReplayBuffer.Bench.GpuLoad`: `--tearing on|off`.
- `bench/ReplayBuffer.Bench`: `bench overlay --overlay-kind` and the `borderless-notear` mode.
- Nothing has a test yet. Any committed version needs the 0-warning build and Heavy, Explicit tests for its GPU runs.
