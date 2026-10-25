---
lens: versions-and-fit (web reality check)
target: architecture-replay-buffer.md (Stack table, AD-9, AD-12, AD-14, AD-15, Consistency Conventions)
date: 2026-10-25
method: WebSearch/WebFetch against nuget.org, Microsoft Learn, devblogs, ffmpeg.org, GitHub; local `dotnet --list-sdks` and the SDK's BundledVersions.props
files-modified: none (this review only)
---

# Review: versions and fit

## Verdict

The Stack table is mostly real and current, but two pins are stale (CsWin32, FFmpeg native), the minimum OS is out of support and contradicts the TFM, and the "AOT-compatible" claim covers the Windows SDK projection only. FFmpeg.AutoGen under NativeAOT and the CsWin32 AOT configuration are unverified, and the spine doesn't say how they'd be made to work.

## Stack table, row by row

| Row | Spine says | Reality on 2026-10-25 | Status |
| --- | --- | --- | --- |
| .NET runtime | 10.0.13 | 10.0.13, released 2026-10-13 (October servicing; .NET 10 is LTS, EOL 2028-11-14). Installed locally: WindowsDesktop 10.0.13. | OK |
| SDK band | 10.0.1xx | 10.0.13 ships SDKs **10.0.113** and **10.0.402** (latest). This machine only has **10.0.402**; no 1xx SDK is installed. | Mismatch. Pin it with `global.json`, or change the row to 4xx. |
| C# | 14 | C# 14.0 per the 10.0 releases.json | OK |
| Engine publish | NativeAOT win-x64 | Feasible, but see F3–F5 | Unverified |
| TFM | net10.0-windows10.0.26100.0 "(Windows SDK projection, AOT-compatible)" | 26100 is the newest platform version the SDK bundles: 10.0.402 maps it to `Microsoft.Windows.SDK.NET.Ref` **10.0.26100.57**. NuGet has 26100.87 and 28000.87, but those need an explicit `WindowsSdkPackageVersion`. CsWinRT says projections are AOT-compatible from projection revision **.38** (CsWinRT 2.1.1) onward, so .57 qualifies. | OK for the projection itself; see F4 for interop |
| Minimum OS | Windows 11 22H2 | **22H2 has no servicing left on any edition.** Home/Pro ended 2024-10-08, Enterprise/Education 2025-10-14, and Microsoft's table no longer lists it. 24H2 Home/Pro ended 2026-10-13 (Enterprise runs to 2027-10-12). Serviced now: 23H2 Ent (until 2026-11-10), 24H2 Ent, 25H2, 26H1, 26H2 (GA 2026-09-29, build 26300 — the builder's machine is 10.0.26300). | **Out of date.** See F1 |
| WPF | ships with .NET 10 | Yes (WindowsDesktop 10.0.13) | OK |
| Microsoft.Windows.CsWin32 | 0.3.264 | 0.3.264 exists (2025-12-13). **Latest is 0.3.367 (2026-10-22)**, with about 17 releases in between. Several touch COM and AOT: v0.3.275 fixes `CsWin32RunAsBuildTask`; v0.3.333 preserves coclass generic fields for AOT and changes COM output marshaling; v0.3.335 fixes the IInspectable-derived COM interface layout; v0.3.346 adds a safe COM out-pointer ABI; v0.3.363 fixes build-task LibraryImport generation. | **Stale.** See F2 |
| FFmpeg.AutoGen | 9.0.1.1 | Latest stable (2026-08-16). TFMs: netstandard2.0/2.1, net8.0, which work on net10. Binds FFmpeg 9.0.1 headers. | OK as a version; AOT fit unverified (F3) |
| FFmpeg native | 9.0.1, BtbN GPL shared win64 | FFmpeg 9.0 "Lei" GA 2026-08-04. **9.0.2 released 2026-09-18** (same sonames: avutil 61, avcodec 63, avformat 63, so ABI-compatible with the 9.0.1 bindings). BtbN publishes **release-branch heads, not tags** (today `ffmpeg-n9.0.2-35-g9a9b70bb8c-win64-gpl-shared-9.0.zip`). It keeps only the last 14 dailies and each month's last build (for 2 years). Branches built: master, 9.0, 8.1. | **Stale and not reproducible as written.** See F6 |
| H.NotifyIcon.Wpf | 2.4.1 | Latest stable (2025-12-01), targets net10.0-windows7.0 and net462. Only prereleases since (2.5.0-beta.3, 2026-09-12). | OK (10 months old, still current) |

## Fit questions

### Q1. Does the .NET 10 SDK's built-in Windows SDK projection support NativeAOT for Windows.Graphics.Capture?

Yes for the projected WinRT types. CsWinRT documents generated projections as AOT-compatible from CsWinRT 2.1.1 and projection `10.0.<sdk>.38`, and the 10.0.402 SDK bundles `10.0.26100.57`. The docs don't cover headless or console NativeAOT apps specifically.

WGC from an unpackaged process also needs COM interop that the projection doesn't supply:
- `IGraphicsCaptureItemInterop::CreateForWindow` / `CreateForMonitor`
- `CreateDirect3D11DeviceFromDXGIDevice`
- `IDirect3DDxgiInterfaceAccess`

Under AD-14 that interop goes through CsWin32. Under AOT the hand-off between the two stacks has to be raw ABI pointers converted with `MarshalInterface<T>.FromAbi` / `FromAbi`, not built-in COM. Nobody has shown this works yet. See F4.

### Q2. Does FFmpeg.AutoGen work under NativeAOT?

Unverified, and there's a specific risk.
- **How the default package binds:** the `FFmpeg.AutoGen` package (DynamicallyLoaded bindings) resolves every export with `Marshal.GetDelegateForFunctionPointer<T>`, called from `FunctionResolverBase.GetFunctionDelegate<T>`. It relies on runtime marshalling: strings are `[MarshalAs(UnmanagedType.LPUTF8Str)]` on net8+.
- **No AOT signals from upstream:** the package isn't marked AOT-compatible, and its repo has no AOT issues or docs. A community fork, FFmpeg_droidFix.AutoGen, advertises that it *adds* NativeAOT support, which suggests upstream doesn't claim it.
- **Community reports:** community posts say `GetDelegateForFunctionPointer` doesn't work for NativeAOT.
- **A safer option exists:** a DllImport-based package at the same version, `FFmpeg.AutoGen.Bindings.DynamicallyLinked` 9.0.1.1 plus `FFmpeg.AutoGen.Abstractions` 9.0.1.1. NativeAOT pregenerates stubs for DllImport and can make direct P/Invoke calls. See F3.

### Q3. Is WDA_EXCLUDEFROMCAPTURE available on the target OS?

Yes. It's supported from Windows 10 2004, so any Windows 11 qualifies; on older builds it falls back to `WDA_MONITOR`.

Constraints the spine doesn't state:
- It works only on **top-level windows owned by the calling process**, and only while DWM is composing. That's fine for the App's windows.
- The engine's message-only Hotkey window isn't visible, so it needs no affinity. "Every window the app creates" should say "every visible top-level window the App creates".
- The WPF Overlay is a layered window (`AllowsTransparency`). Confirm in epic 5 that the affinity call succeeds on it and that the Overlay is excluded from a WGC *monitor* capture (Monitor Recording). Window capture of the game never includes it anyway.

### Q4. Is Windows 11 22H2 still in support, and should the minimum move?

No, 22H2 isn't in support, and yes, the minimum should move. See F1.

## Findings

### F1 [high] Minimum OS: Windows 11 22H2 is out of support and contradicts what the spine relies on

**Problem:** 22H2 ended servicing for every edition by 2025-10-14. The memlog's reason for 22H2 ("WGC border removal ... rely on it") is also wrong in both directions:
- `IsBorderRequired` dates from build 20348 (UniversalApiContract v12), so 22H2 adds nothing for it.
- The WGC features that matter for a fixed-rate game capture pacer need **24H2 (build 26100, contract v19)**: `MinUpdateInterval` and `IncludeSecondaryWindows`.

The TFM already compiles against 26100, so builders will see those APIs. With a 22H2 floor, every call to them needs `ApiInformation` guards.

**Fix:**
- Set Minimum OS to **Windows 11 24H2 (10.0.26100)**, and set `SupportedOSPlatformVersion` = `10.0.26100.0` so the CA1416 analyzer enforces it. This is the lowest build that's still serviced (Enterprise until 2027-10-12) and matches the TFM. Use 25H2 instead if "still serviced for Home/Pro" is required: 24H2 Home/Pro ended 2026-10-13.
- Correct the memlog's reason for the floor.
- Note that borderless capture needs `GraphicsCaptureAccess.RequestAccessAsync(Borderless)`. The docs also mention a `graphicsCaptureWithoutBorder` manifest capability, so epic 1 should verify how this behaves in an unpackaged process.

### F2 [medium] CsWin32 0.3.264 is 10 months old and missing COM/AOT fixes; the AOT mode isn't named

**Problem:** 0.3.367 is current. The releases in between fix COM layout for IInspectable-derived interfaces (the WinRT interop interfaces in Q1), preserve coclass generics for AOT, and fix build-task LibraryImport generation. They also *change* COM output shapes (v0.3.333, v0.3.346, v0.3.358), so pick the version before epic 1 writes code.

Microsoft Learn says CsWin32's default output "uses the .NET runtime marshaller and is not automatically AOT-compatible". The AOT path is `CsWin32RunAsBuildTask=true` plus `DisableRuntimeMarshalling=true`, which generates `LibraryImport` and `GeneratedComInterface` code. The alternative is `"allowMarshaling": false`. The spine names neither.

**Fix:**
- Pin **0.3.367**.
- Add to AD-14: "`ReplayBuffer.Platform.*` uses CsWin32 in build-task mode (`CsWin32RunAsBuildTask`, `DisableRuntimeMarshalling`)".

### F3 [high] FFmpeg.AutoGen under NativeAOT: unverified, and the default package is the risky binding

**Problem:** The memlog lists this as an open question, but the Stack and AD-14 just say "FFmpeg.AutoGen". The default package binds through `Marshal.GetDelegateForFunctionPointer<T>` with runtime marshalling (Q2), and nothing upstream claims it works under NativeAOT.

**Fix:**
- In the Stack table, name `FFmpeg.AutoGen.Abstractions` + `FFmpeg.AutoGen.Bindings.DynamicallyLinked` **9.0.1.1** (DllImport) as the engine's binding, with the FFmpeg DLLs beside the exe.
- Make epic 1's tracer acceptance criterion: "`dotnet publish -p:PublishAot=true` with zero IL2xxx/IL3xxx warnings from Platform.FFmpeg, then a hardware encode plus an fMP4 mux runs from the AOT exe".
- Keep the framework-dependent fallback in Deferred.

### F4 [medium] "Windows SDK projection, AOT-compatible" holds for the projection, not for the WGC interop seam

**Problem:** Q1. The projection (26100.57) is AOT-ready. The CsWin32-COM ↔ CsWinRT hand-off for `IGraphicsCaptureItemInterop` and `IDirect3DDxgiInterfaceAccess` isn't.

**Fix:** Change the TFM note to "(projection AOT-compatible; WGC COM interop is proven by the epic 1 tracer)". Add a one-line AD-14 rule: the two interop stacks hand off only raw ABI pointers, converted with `MarshalInterface<T>.FromAbi` / `FromAbi`, never through built-in COM casts.

### F5 [low] SDK band 10.0.1xx doesn't match the machine, and nothing enforces it

**Problem:** Only 10.0.402 is installed. Both bands ship runtime 10.0.13. The bundled Windows SDK projection is set per SDK, so the band affects the projection revision.

**Fix:** Either add `global.json` pinning `10.0.113` with `rollForward: latestPatch` and install it, or change the row to `10.0.4xx` (`10.0.402`). The 4xx band is the simpler choice for a single-builder project.

### F6 [medium] FFmpeg native 9.0.1 BtbN: stale, and you can't reproduce it as pinned

**Problem:**
- FFmpeg **9.0.2** (2026-09-18) is the current 9.0 point release.
- BtbN builds branch heads, so there is no `9.0.1` artifact as such. The memlog's 2026-08-31 build only survives as "last build of the month" (kept 2 years). Dailies disappear after 14 days.

**Fix:**
- Pin to a specific BtbN asset name from a month-end release (for example the 2026-09-30 or 2026-10-31 `ffmpeg-n9.0.2-…-win64-gpl-shared-9.0.zip`) and record its SHA-256 in the repo (for example `native/ffmpeg/VERSION`).
- Keep FFmpeg.AutoGen on 9.0.1.1: the major sonames are identical, so the bindings still match.

### F7 [low] Logging: Microsoft.Extensions.Logging has no rolling-file provider

**Problem:** The Consistency Conventions table commits to "a rolling file" through `Microsoft.Extensions.Logging`. The framework doesn't ship a file provider, and the spine names neither a package nor a version. A NativeAOT engine also can't use reflection-based configuration binding for logging.

**Fix:** Either name the provider and its version (it must be AOT/trim-clean), or say "a small in-house `ILoggerProvider` in Platform.Windows". Also state that the engine uses source-generated `[LoggerMessage]`.

### F8 [info] Other committed tech that checks out

- `RegisterHotKey`, WASAPI event-driven shared mode with loopback, `IMMNotificationClient`, DXGI Desktop Duplication, named pipes, and source-generated System.Text.Json all have no version exposure and are all AOT-safe.
- WPF stays framework-dependent, which is correct: WPF doesn't support NativeAOT.
- H.NotifyIcon.Wpf 2.4.1 is the right stable pin.
- .NET 11 hit RC2 on 2026-10-13 (GA expected Nov 2026, STS). Staying on .NET 10 LTS is the right call. Don't move to `net11.0` mid-initiative.

## Sources

- .NET October 2026 servicing (10.0.13): https://devblogs.microsoft.com/dotnet/dotnet-and-dotnet-framework-october-2026-servicing-updates/
- .NET 10 release metadata (SDKs 10.0.113 / 10.0.402, C# 14.0, EOL): https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json
- Local check: `dotnet --list-sdks` (10.0.402 only for .NET 10); `C:\Program Files\dotnet\sdk\10.0.402\Microsoft.NETCoreSdk.BundledVersions.props` (`WindowsSdkPackageVersion="10.0.26100.57"`)
- CsWin32 on NuGet (0.3.367 latest; 0.3.264 = 2025-12-13): https://www.nuget.org/packages/Microsoft.Windows.CsWin32 , https://www.nuget.org/packages/Microsoft.Windows.CsWin32/0.3.264
- CsWin32 releases: https://github.com/microsoft/CsWin32/releases , https://github.com/microsoft/CsWin32/releases?page=2
- CsWin32 AOT guidance (build task mode, DisableRuntimeMarshalling, allowMarshaling): https://microsoft.github.io/CsWin32/docs/getting-started.html , https://learn.microsoft.com/en-us/windows/apps/develop/interop/call-win32-apis
- CsWinRT AOT/trimming (projection .38+, CsWinRT 2.1.1): https://github.com/microsoft/CsWinRT/blob/master/docs/aot-trimming.md
- Microsoft.Windows.SDK.NET.Ref versions: https://www.nuget.org/packages/Microsoft.Windows.SDK.NET.Ref
- Microsoft.Windows.CsWinRT versions (2.3.1 stable; 3.0 preview): https://www.nuget.org/packages/Microsoft.Windows.CsWinRT
- FFmpeg.AutoGen packages: https://www.nuget.org/packages/FFmpeg.AutoGen , https://www.nuget.org/packages/FFmpeg.AutoGen.Bindings.DynamicallyLinked , https://www.nuget.org/packages/FFmpeg.AutoGen.Abstractions
- FFmpeg.AutoGen binding code: https://raw.githubusercontent.com/Ruslan-B/FFmpeg.AutoGen/master/FFmpeg.AutoGen/FunctionResolverBase.cs , https://raw.githubusercontent.com/Ruslan-B/FFmpeg.AutoGen/master/FFmpeg.AutoGen/generated/Delegates.g.cs
- AOT fork claim: https://www.nuget.org/packages/FFmpeg_droidFix.AutoGen
- Community report on GetDelegateForFunctionPointer under NativeAOT: https://discourse.libsdl.org/t/my-new-wrapper-for-net-7-0-and-greater/46663
- Native AOT interop (DirectPInvoke): https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/interop
- FFmpeg downloads (9.0.2 = 2026-09-18; sonames): https://ffmpeg.org/download.html
- FFmpeg 9.0 release: https://www.phoronix.com/news/FFmpeg-9.0-Released
- BtbN builds and retention policy: https://github.com/BtbN/FFmpeg-Builds , https://github.com/BtbN/FFmpeg-Builds/releases
- H.NotifyIcon.Wpf: https://www.nuget.org/packages/H.NotifyIcon.Wpf
- Windows 11 release information (servicing table, updated 2026-10-13): https://learn.microsoft.com/en-us/windows/release-health/windows11-release-information
- SetWindowDisplayAffinity / WDA_EXCLUDEFROMCAPTURE: https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowdisplayaffinity
- WGC GraphicsCaptureSession, IsBorderRequired, MinUpdateInterval, IncludeSecondaryWindows: https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscapturesession , https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscapturesession.isborderrequired , https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscapturesession.minupdateinterval , https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscapturesession.includesecondarywindows
