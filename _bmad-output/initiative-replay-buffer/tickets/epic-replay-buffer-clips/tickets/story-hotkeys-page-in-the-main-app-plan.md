---
title: 'Hotkeys page in the Main App'
type: 'feature'
ticket: '12'
created: '2026-11-03'
status: 'built'
baseline_revision: 'cd38af2df1edd7bc8dc77deaba516011c0958400'
route: 'full'
route_source: 'auto'
risk: 'low'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
followup_review_recommended: false
context: ['{project-root}/CLAUDE.md']
warnings: ['oversized']
deferred: []
---

<intent-contract>

## Intent

**Problem:** Every `HotkeyAction` and `recordHotkeyMode` has a settings key, and the engine registers them, reports `hotkey.status` and refuses duplicates (`hotkey-duplicate`), but no Main App page sets them or shows which failed (R9).

**Approach:** A "Hotkeys" Settings page (after Replay Buffer) lists the six actions with their binding and the engine's status for each, sets a binding by pressing a combination, clears one, and switches the Record mode. Each change sends one Global op at once; a duplicate is refused in the page before sending. No engine or Contracts change: the engine already applies `hotkeys` immediately.

## Boundaries & Constraints

**Always:**
- Rows in `HotkeyAction` order with labels: Save Clip, Mic mute, Record (toggle), Record start, Record stop, Monitor Recording (toggle).
- The bindings edited are the document's Global `hotkeys` when present and valid; otherwise the bindings in `state.Hotkeys` (the engine's effective ones: the catalog default in the installed instance, none in Dev/Test, AD-16).
- A change sends the whole `HotkeySettings` with one field changed, via `SettingsValues.SetGlobal(Keys.Hotkeys, …)`, at the rendered revision.
- A duplicate is the engine's rule (`HotkeyPlan.SameCombination`: same Vk, same modifiers ignoring `NoRepeat`, any mode). It is refused with a `Message` naming the other action and nothing is sent; the engine's own refusal still surfaces through `SendAsync`'s Failed branch.
- Capture: "Set" puts a row in capture mode; the next non-modifier key with the held Ctrl/Alt/Shift/Win becomes the binding; Escape cancels. `Key.System` uses `SystemKey` (Alt combos). Pure translation in a static method so it's testable without input.
- Status per row from `state.Hotkeys`: registered → "Working"; `hotkey-taken` → "Failed: another app holds this combination"; `hotkey-duplicate` → "Failed: another Hotkey has this combination"; `hotkey-failed` → "Failed to register"; `hotkey-inactive` → "Not used in this Record mode"; `hotkey-unbound` → "Not set". A failed row is marked (red).
- Lean tests; tests that inject real input or need focus are Heavy + Explicit (none planned).

**Never:** an engine, Core or Contracts change; App referencing Core; a "Save" button (edits commit at once, as on the Replay Buffer page); per-profile Hotkeys (`hotkeys` isn't overridable).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Rebind | Save row captures Ctrl+Shift+F5 | one `hotkeys` op, Save = (0x74, Ctrl\|Shift), others unchanged | — |
| Duplicate | MicMute captures the Save combination | nothing sent; `Message` "…already the Save Clip Hotkey" | — |
| Taken | status Save `hotkey-taken`, MicMute registered | Save "Failed: another app…" marked failed; MicMute "Working" | — |
| Mode | Separate selected | one `recordHotkeyMode` op `"separate"` | — |
| Clear | Clear on Record stop | `hotkeys` op with RecordStop null | — |

</intent-contract>

## Code Map

- `src/ReplayBuffer.App/Settings/HotkeysPage.cs` (new) -- `internal sealed class HotkeysPage(SettingsSender sender) : SettingsPage(sender)`, modelled on `ReplayBufferSettingsPage` (`Refuse`, `SendAsync`, `OnRender`). `Title` "Hotkeys". `Rows` (`IReadOnlyList<HotkeyRow>`, built once). `IsToggleMode`/`IsSeparateMode` (radio pair; only a check acts, like `IsMemory`/`IsDisk`). `ShowStatus(HotkeyStatus status)` called from `MainViewModel.Refresh` before `page.Render`. Public methods `Capture(HotkeyAction, HotkeyBinding)`, `Clear(HotkeyAction)`; `static HotkeyBinding? FromKey(Key key, Key systemKey, ModifierKeys modifiers)` (null for modifier-only keys; VK via `KeyInterop.VirtualKeyFromKey`); `static string Describe(HotkeyBinding?)` e.g. "Ctrl+Shift+F5", "Alt+F10", "Not set" (keys via `KeyInterop.KeyFromVirtualKey`; `D0`–`D9` shown as digits).
- `HotkeyRow : Observable` (same file) -- `Action`, `Label`, `BindingText`, `StatusText`, `IsFailed`, `IsCapturing`; `SetCommand` (starts capture on this row, ends it on others), `ClearCommand` (`RelayCommand`).
- Contracts used as is: `HotkeySettings.For`, `HotkeyBinding`, `HotkeyModifiers`, `HotkeyAction`, `RecordHotkeyMode`, `Keys.Hotkeys`, `Keys.RecordHotkeyMode`, `HotkeyStatus`/`HotkeyBindingStatus`, `ErrorCodes.Hotkey*`. Write a private `With(HotkeySettings, HotkeyAction, HotkeyBinding?)` switch in the page.
- `src/ReplayBuffer.App/Settings/HotkeysView.xaml(.cs)` (new) -- styled like `ReplayBufferSettingsView.xaml`: Record mode radios, then an `ItemsControl` of rows (label, binding text or "Press a combination…" while capturing, Set, Clear, status in DarkRed when `IsFailed`). The view's `PreviewKeyDown` hands the key to the capturing row's page (`FromKey` then `Capture`, Escape cancels), marks it handled.
- `src/ReplayBuffer.App/Settings/SettingsPage.cs` -- add `new HotkeysPage(sender)` after `ReplayBufferSettingsPage`.
- `src/ReplayBuffer.App/MainViewModel.cs` -- `Hotkeys` property (`Pages.OfType<HotkeysPage>().Single()`), `Hotkeys.ShowStatus(state.Hotkeys)` beside `ReplayBuffer.ShowState(state)`.
- `tests/ReplayBuffer.App.Tests/TrackLayoutPageTests.cs:472` -- page order adds "Hotkeys" after "Replay Buffer".
- Reuse: `ReplayBufferSettingsPageTests` (send capture through `FakeEngineSession.Reply`, `Create()`, the `Sta.Run` binding-error and PNG render test).
- Already proven in Engine (no change): `HotkeyServeTests.ACombinationAnotherProcessHoldsIsTakenAndTheOthersRegister` (the ticket's helper-process check) and `ARebindFreesTheOldCombinationAndADuplicateUpdateIsRefused`.

## Tasks & Acceptance

**Execution:**
- [x] `src/ReplayBuffer.App/Settings/HotkeysPage.cs` -- page, rows, capture translation, status text -- R9 in the App
- [x] `src/ReplayBuffer.App/Settings/HotkeysView.xaml`, `HotkeysView.xaml.cs` -- the view and key capture -- the builder's page
- [x] `src/ReplayBuffer.App/Settings/SettingsPage.cs`, `src/ReplayBuffer.App/MainViewModel.cs`, `tests/ReplayBuffer.App.Tests/TrackLayoutPageTests.cs` -- register the page, feed status, page order
- [x] New `tests/ReplayBuffer.App.Tests/HotkeysPageTests.cs` -- (1) `FromKey(F5, Ctrl|Shift)` then `Capture` on Save sends one Global `hotkeys` op at the rendered revision with only Save changed; a duplicate sends nothing and sets `Message`; Clear on Record stop sends it null; Separate sends `recordHotkeyMode`; (2) a status with Save `hotkey-taken` and MicMute registered shows Save failed and MicMute "Working"; (3) the view renders without binding errors and writes `hotkeys-settings.png`

**Acceptance Criteria:**
- Given the Hotkeys page, when a combination is captured for an action, then one `settings.update` op sets `hotkeys` with only that action changed.
- Given a combination another action already has, when it is captured, then nothing is sent and the page says which action has it.
- Given a `hotkey.status` with one action `hotkey-taken`, when the page renders, then that row shows failed and the others show their own status.
- Given the Record mode radios, when Separate is chosen, then `recordHotkeyMode` is sent as `separate`.
- Given the default run, when the 0-warning Release build and App.Tests run, then all pass.

## Implementation Notes

- `HotkeysPage` keeps the edited `HotkeySettings`: the document's when present and readable, else built from `state.Hotkeys` bindings; `ShowStatus` refreshes the fallback when no document value exists. The first edit marks it as from the document.
- Capture state lives on the rows (`IsCapturing`); `HotkeysPage.CapturingRow` is what the view's `PreviewKeyDown` checks. `BindingText` shows "Press a combination… (Esc cancels)" while capturing.
- The duplicate check mirrors `HotkeyPlan.SameCombination` privately (App can't reference Core).
- Clear test uses a document with RecordStop bound, since the catalog default leaves it null (clearing an already-null binding sends nothing).
- A combination the engine already holds (or another app holds) reaches the engine, not the page, so it can't be captured on another row; the in-page duplicate check covers keys that reach the page and the engine's `hotkey-duplicate` refusal still shows. A fix would need the engine to pause its Hotkeys during capture (an engine change; not done).

## Plan Change Log

## Review Triage Log

### 2026-11-04 — Review pass
- verdicts: 5 findings — high 0, medium 1, low 3, false 1, maybe-false 0
- findings:
  - `[medium]` `[patch]` `HotkeysPage.Capture`: a combination with no modifier (plain A, Space, Enter) was sent and the engine would register it system-wide — confirmed, `Keys.IsValidHotkeys` accepts modifiers 0. Fixed: no-modifier bindings other than F1–F24 are refused with a message; a test assert covers plain A.
  - `[low]` `[patch]` `HotkeysView`: capture mode survived leaving the page, so a later key in the view bound a stale row — confirmed. Fixed: `Unloaded` and losing keyboard focus within cancel capture.
  - `[low]` `[reject]` `FromKey` drops `Key.ImeProcessed` silently — real, but the builder doesn't type Hotkeys through an IME; the fix adds a branch for a case not met in everyday use.
  - `[low]` `[patch]` `HotkeysPageTests.Status()`: null bindings reported `hotkey-inactive`, which the engine never sends — confirmed against `HotkeyPlan.For`. Fixed: RecordStart bound (Alt+F11) and inactive, RecordStop null and unbound; asserts the inactive binding carries over.
  - `[false]` `[reject]` a first edit before the first `hotkey.status` could wipe the catalog defaults — `EngineHost` registers the Hotkeys before the pipe accepts connections, so the App's first snapshot already carries the status, and `MainViewModel.Refresh` renders nothing before a snapshot.

## Design Notes

The page edits the document's `hotkeys` but falls back to `state.Hotkeys`' bindings when the document has none, because the engine (not the App) knows whether the catalog default applies (installed instance) or nothing is bound (Dev/Test); the first edit then writes the full set the builder sees.

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings
- `dotnet test --project tests/ReplayBuffer.App.Tests -c Release --no-build` -- all pass
- `dotnet test --project tests/ReplayBuffer.Engine.Tests -c Release --no-build --filter-class "*HotkeyServeTests"` (or the matching filter syntax) -- the existing helper-process check passes

**Manual checks (if no CLI):**
- Human Check row: the builder rebinds the Save Hotkey, is refused a duplicate, saves a Clip with the new binding in a fullscreen game, closes the Main App and saves again; checks the page by eye.

## Auto Run Result

- **Summary:** R9's Hotkeys page. A "Hotkeys" Settings page (after Replay Buffer) lists the six `HotkeyAction`s with their binding and the engine's `hotkey.status` text (failed ones in red), sets a binding by pressing a combination (Esc cancels), clears one, and switches `recordHotkeyMode`. Each change sends one Global op at once. A duplicate, or a plain key other than F1–F24, is refused before sending.
- **Contracts:** no change. **Engine/Core:** no change.
- **Files:**
  - App: new `Settings/HotkeysPage.cs` (page and `HotkeyRow`), `Settings/HotkeysView.xaml(.cs)`; `Settings/SettingsPage.cs` (page order), `MainViewModel.cs` (`Hotkeys`, `ShowStatus`).
  - Tests: new `HotkeysPageTests` (4); the page order in `TrackLayoutPageTests`.
- **Review:** quick lens, 5 findings: 3 patched (1 medium, 2 low), 1 low rejected (IME), 1 false.
- **Follow-up review recommended:** false (one medium patched).
- **Verification:** after the patches and the develop merge, the Release build has 0 warnings. App.Tests: 88 passed, 7 Heavy skipped. One run of five had a single failure that didn't recur in four reruns and wasn't captured (likely an existing timing-sensitive App test). Before the merge, `HotkeyServeTests` passed 4 (the helper-process `hotkey-taken` check among them; 1 Heavy skipped). Engine was not rerun after the merge, because no engine code changed.
- **Residual risks:** real key capture with focus is untested (Human Check). A combination the engine or another app already holds can't be captured on the page (see Implementation Notes). `hotkeys-settings.png` is written to the App test output folder for the HITL record.

