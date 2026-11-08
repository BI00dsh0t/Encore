# Allow app screenshots and recordings

Builder request, 2026-11-08: “the app should be allowed to be recorded or screenshotted.” Ordinary implementation on `feature/allow-app-capture`; this overrides the original all-window exclusion portion of R6 / AD-15.

Remove the startup-wide `ExcludeNewWindows` hook, `ShellWindow`'s initial affinity application and the tray popup's source-change affinity application. Main window, creator/player, dialogs, tray/menu popups, native pickers and overlay now retain ordinary capturable display affinity. Overlay topmost/click-through/focus behavior, theme decoration, capture-engine target selection and sound routing are unchanged. The standalone low-level exclusion helper remains available to explicit callers/native utility tests but has no App production caller.

Update existing app-window, dialog, tray/overlay and Heavy screen-copy expectations to the new policy; the screen-copy case now expects the app's red test window to be visible rather than the helper behind it. These capture-specific and unrelated shell/overlay suites remain **unrun** under clip-creator-only clearance. Compile all updated tests, review all exclusion call sites, and run only authorized non-Explicit ClipCreatorTests as a shared-window/creator regression. No recording, actual screenshot/Heavy/GPU/HITL or broader suites are authorized here.

Do not change display/system settings, the installed app or running Dev App, user settings or Library; no push. A full restart of the updated App creates new ordinary windows; already-running old windows retain their old affinity until that process exits.

Verification: feature Release build **0 warnings/errors**; authorized non-Explicit ClipCreatorTests **25 passed, 0 failed/skipped**. Scoped ordinary review confirms there are no remaining App production calls to Apply/ExcludeNewWindows or exclusion-hook lifecycle fields. Updated native capture-specific expectations are compiled only; actual screenshot/recording fidelity remains unverified under standing clearance.
