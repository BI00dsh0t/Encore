---
title: 'Capture exclusion for every App window'
type: 'feature'
ticket: '9'
created: '2026-11-03'
status: 'built'
baseline_revision: 'f2ff2fe2a20af05401b41727ae43dc7c37a58249'
route: 'full'
route_source: 'auto'
risk: 'low'
review: 'quick'
review_source: 'pinned'
lenses_ran: [quick]
review_loop_iteration: 0
followup_review_recommended: false
context: []
warnings: []
deferred: []
---

<intent-contract>

## Intent

**Problem:** Only windows that derive from `ShellWindow` and the tray's `ContextMenu` set `WDA_EXCLUDEFROMCAPTURE`. The Game Profiles page's native `OpenFileDialog`, WPF popups (ComboBox drop-downs, tooltips) and any future window that derives from plain `Window` would show in a capture (R6, PLAN.md §7.1).

**Approach:** One shared helper in `ReplayBuffer.Platform.Desktop` (AD-2): `CaptureExclusion.ExcludeNewWindows()` installs a process-wide WinEvent hook that excludes every top-level window of the App's process as it is created or shown. The App installs it first thing at start. `ShellWindow`'s handle-time call and the tray menu's explicit call stay, as the ticket asks, since they apply before the first frame.

## Boundaries & Constraints

**Always:** Win32 calls live in `ReplayBuffer.Platform.Desktop`. The hook callback never throws: a window that can't be excluded (a child or message-only window, or one already gone) is skipped. The hook delegate stays rooted for the hook's life. Disposing the returned handle unhooks.

**Never:** No Contracts change. No change to how the Overlay window is built (an Overlay redesign is another story). No Windows toast notifications. No new WGC monitor-capture code in `Platform.Capture`.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| WPF window not derived from ShellWindow | `new Window().Show()` with the hook on | excluded once the dispatcher pumps | No error expected |
| Popup / ContextMenu | popup opened | its HwndSource window excluded | No error expected |
| Native common dialog | `OpenFileDialog.ShowDialog` | the `#32770` dialog excluded | No error expected |
| Child or message-only window | create/show event for it | skipped | `SetWindowDisplayAffinity` failure swallowed |

</intent-contract>

## Code Map

- `src/ReplayBuffer.Platform.Desktop/CaptureExclusion.cs` -- keep `Apply`/`IsApplied`. Add `public static IDisposable ExcludeNewWindows()`: `SetWinEventHook(EVENT_OBJECT_CREATE, EVENT_OBJECT_SHOW, default, callback, (uint)Environment.ProcessId, 0, WINEVENT_OUTOFCONTEXT)` on the calling thread (it must pump messages, as the WPF UI thread does). Callback: only `event` CREATE or SHOW, `idObject == OBJID_WINDOW (0)`, `idChild == CHILDID_SELF (0)`, and `GetAncestor(hwnd, GA_ROOT) == hwnd`; then `SetWindowDisplayAffinity`, ignoring its result. The returned object keeps the delegate and calls `UnhookWinEvent` on Dispose. Add `public static IReadOnlyList<nint> VisibleWindows()`: `EnumWindows` over this process's visible top-level windows (`GetWindowThreadProcessId`, `IsWindowVisible`), for the tests.
- `src/ReplayBuffer.Platform.Desktop/NativeMethods.txt` -- add `SetWinEventHook`, `UnhookWinEvent`, `GetAncestor`, `EnumWindows` (and the constants CsWin32 needs: `EVENT_OBJECT_CREATE`, `EVENT_OBJECT_SHOW`, `WINEVENT_OUTOFCONTEXT`). `allowMarshaling` is false: use function-pointer or `[UnmanagedCallersOnly]` callbacks where CsWin32 generates them, or a static delegate with `Marshal.GetFunctionPointerForDelegate`.
- `src/ReplayBuffer.App/ShellApp.cs:~40` (`OnStartup`) -- install the hook before any window is made; keep it in a field and dispose it on exit, where `_tray`/`_overlay` are cleaned up.
- `src/ReplayBuffer.App/ShellWindow.cs` -- unchanged; update its doc to name the hook as the net.
- `src/ReplayBuffer.App/ShellApp.cs:106` (`CreateTrayMenu`) -- unchanged.
- `tests/ReplayBuffer.App.Tests/OverlayTests.cs` -- read only: `Pump`, `Shift`, `Raise`, `Notice` helpers and `AScreenCopyShowsNoneOfTheOverlay`'s screen-copy pattern. Copy what's needed; don't refactor it.
- `tests/ReplayBuffer.App.Tests/TrayTests.cs:46` -- read only: how the tray menu is opened off-screen.

## Tasks & Acceptance

**Execution:**
- [x] `src/ReplayBuffer.Platform.Desktop/NativeMethods.txt`, `CaptureExclusion.cs` -- `ExcludeNewWindows` and `VisibleWindows` -- the shared helper any window type can call.
- [x] `src/ReplayBuffer.App/ShellApp.cs`, `ShellWindow.cs` -- install the hook at start, dispose at exit; doc.
- [x] `tests/ReplayBuffer.Platform.Desktop.Tests/CaptureExclusionTests.cs` -- one test for the skip row: with `ExcludeNewWindows()` on, a WPF `Window` hosting a native child (`HwndHost` or a `HwndSource` with `WS_CHILD` parented to it) is shown and pumped; the top-level window is excluded and nothing throws. Keep the callback's filter in an `internal` method if that makes the test simpler (the project has `InternalsVisibleTo` via `AssemblyInfo.cs` if present; check).
- [x] `tests/ReplayBuffer.App.Tests/CaptureExclusionTests.cs` (new, `[Collection(nameof(WpfCollection))]`) -- default test `EveryWindowTheAppShowsIsExcludedFromCapture`: on an STA thread with the hook on, show off-screen (-32000) without activation the `MainWindow`, the `OverlayWindow` with a "Clip saved" notice, the tray menu from `ShellApp.CreateTrayMenu`, a plain `Window` and an open `Popup` (neither via ShellWindow); pump; assert `VisibleWindows()` is non-empty and every one is `IsApplied`. Close all in `finally`.
- [x] same file -- `[Fact(Explicit = true)]`, `[Trait("Category", "Heavy")]` `ADialogIsExcludedAndAScreenCopyShowsNoneOfTheAppsWindows`: wait while `%USERPROFILE%\Documents\repos\ReplayBuffer\.bench-running` exists (repo root found by walking up to `ReplayBuffer.slnx`), create it, delete it in `finally`. Show a lime helper window, then on top of it a solid-red plain `Window` (not ShellWindow) on screen; from a `DispatcherTimer`, open `OpenFileDialog.ShowDialog` and, once a visible `#32770` window of the process exists, assert every visible window is excluded and screen-copy the red window's rect: no red pixels, mostly lime. Close the dialog by posting `WM_CLOSE`.

**Acceptance Criteria:**
- Given the App's hook on, when the Main App, the Overlay with a message, the tray menu, a plain WPF window and a popup are shown, then every visible top-level window of the process has `WDA_EXCLUDEFROMCAPTURE`.
- Given the hook on and a native file dialog open, when the screen is copied over an App window, then none of the App's pixels appear and the dialog is excluded.

## Implementation Notes

- The hook callback is a static `[UnmanagedCallersOnly]` method passed as a function pointer (CsWin32 generates function-pointer signatures with `allowMarshaling: false`), so there is no delegate to root. The filter lives in `internal CaptureExclusion.NamesATopLevelWindow`; the Platform.Desktop test drives it through the real hook, so no `InternalsVisibleTo` was added.
- Disposing the hook calls `UnhookWinEvent` directly (no SafeHandle finalizer), since a hook must be removed from the thread that set it.
- `ShellApp` installs the hook first thing in `OnStartup` and disposes it in `QuitAsync` after the windows close.
- The default App test gives its Overlay a dispatcher-posting invoker: the "Clip saved" notice expires on a timer thread after the test, and a direct invoker crashed the test host.
- Verified: build 0 warnings; Platform.Desktop.Tests 12/12; App.Tests 98 passed, 10 skipped (explicit); the Heavy `ADialogIsExcluded...` passed at the console (not a remote session).

## Plan Change Log

- Heavy test: the lime helper is shown and pumped before the hook is installed, and the "every visible window is excluded" check skips it. With the hook on first, the hook excludes the lime helper too, and the screen copy could not see lime under the red window.

## Review Triage Log

### 2026-11-04 — Review pass
- verdicts: 5 findings — high 0, medium 0, low 5, false 0, maybe-false 0
- findings:
  - `[low]` `[patch]` The Desktop test's child check is vacuous: `VisibleWindows` never lists a `WS_CHILD` window — fixed: `InternalsVisibleTo` for the Desktop tests, and the test asserts `NamesATopLevelWindow` takes the window and rejects the child.
  - `[low]` `[patch]` `OnEnumWindow` is `[UnmanagedCallersOnly]` with no try/catch, so a throw would end the process — fixed: wrapped, and it stops the enumeration on a throw.
  - `[low]` `[reject]` The default test's Overlay shows on the real monitor, not at -32000 — it places itself top-right by design; it is excluded, click-through and never activated, and shows for 300 ms. Moving it would add a test-only placement seam.
  - `[low]` `[patch]` The default test's hook leaks if a window constructor throws before `try` — fixed: everything is built inside `try`, and `finally` is null-safe.
  - `[low]` `[patch]` `BenchLock.Take` checks then writes, so two runs could both take it — fixed: `FileMode.CreateNew` in a retry loop. No timeout added: the brief says to wait for the lock to clear.

## Design Notes

- The ticket asks for "a WPF class handler on Window.SourceInitialized". `SourceInitialized` is a CLR event, not a routed one, so `EventManager.RegisterClassHandler` can't target it; `ShellWindow.OnSourceInitialized` (from story 5.1) is the per-window form. The hook is what makes a new window impossible to miss, including the native file dialog and WPF popups, which no `Window` handler sees.
- The verify line names a WGC monitor capture. `Platform.Capture` has window capture only, and `WDA_EXCLUDEFROMCAPTURE` removes a window from GDI screen copies and WGC alike, so the Heavy test uses the screen copy story 5.1's Overlay test uses. Building WGC monitor capture is out of this story.
- An out-of-context hook delivers events while the hooking thread pumps, so a non-ShellWindow window may be on screen for a moment before it is excluded; `ShellWindow` and the tray menu stay to cover the App's own windows from their first frame.

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings, 0 errors
- `dotnet test --project tests/ReplayBuffer.App.Tests -c Release --no-build` -- expected: all pass
- `dotnet test --project tests/ReplayBuffer.Platform.Desktop.Tests -c Release --no-build` -- expected: all pass
- `dotnet test --project tests/ReplayBuffer.App.Tests -c Release --no-build --explicit only --filter-method "*ADialogIsExcluded*"` -- expected: pass (Heavy, at the console)

## Auto Run Result

- **Change:** `CaptureExclusion.ExcludeNewWindows()` (Platform.Desktop) is a process-wide out-of-context WinEvent hook that sets `WDA_EXCLUDEFROMCAPTURE` on every top-level window of the process as it is created or shown. That covers plain WPF windows, popups and the native file dialog. `ShellApp` installs it first in `OnStartup` and disposes it on quit. `ShellWindow` and the tray menu's explicit calls stay, so the App's own windows are excluded before their first frame.
- **Files:**
  - `src/ReplayBuffer.Platform.Desktop/CaptureExclusion.cs` — the hook, the filter and `VisibleWindows`.
  - `NativeMethods.txt` — the new Win32 imports.
  - `ReplayBuffer.Platform.Desktop.csproj` — `InternalsVisibleTo` for the Desktop tests.
  - `src/ReplayBuffer.App/ShellApp.cs` — installs and disposes the hook.
  - `ShellWindow.cs` — doc comment only.
  - `tests/ReplayBuffer.Platform.Desktop.Tests/CaptureExclusionTests.cs` — the hook test and the skip row.
  - `tests/ReplayBuffer.App.Tests/CaptureExclusionTests.cs` — the default every-window test, and the Heavy dialog and screen-copy test.
- **Review:** 4 low findings patched, 1 low rejected (the Overlay shows top-right, as designed), none deferred. Follow-up review recommended: false, since no high or medium finding was patched.
- **Verification:**
  - 0-warning Release build.
  - Platform.Desktop.Tests: 12 of 12 passed.
  - App.Tests: 98 passed, 10 skipped (the explicit tests).
  - The Heavy `ADialogIsExcludedAndAScreenCopyShowsNoneOfTheAppsWindows` passed at the console, holding `.bench-running`.
  - No engine code changed, so the NativeAOT publish was skipped.
- **Residual risks:**
  - A window that doesn't derive from `ShellWindow` can show for a moment before the hook acts on it.
  - The ticket's WGC monitor capture is replaced by a GDI screen copy. `WDA_EXCLUDEFROMCAPTURE` hides a window from both, and the code has no monitor capture.
