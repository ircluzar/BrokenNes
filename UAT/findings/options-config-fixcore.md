# UAT: Options/Config screens + FIX-core progression-lock confirmation

**Area:** Options/Config screens, and confirming the FIX-core progression-lock finding via
"Show Locked Items".
**Instance(s) used:** BrokenNes.Windows.exe, launched independently via `Start-BrokenNesWindows`.
PIDs used: 10072 (killed after a screenshot-methodology problem, see below), 11468 (killed for the
same reason), 20248 (final instance, stopped cleanly at the end).

## Headline result

Step 1 (Options screen) is fully tested and mostly PASS. **Steps 2-4 (native Config/CPU menu,
"Show Locked Items", CPU_FIX visibility) are BLOCKED** — not by anything wrong in what I could
observe of the Options/Main-Menu screens, but by a real, well-evidenced architectural bug I
uncovered while trying to reach the native menu screen: **the app's embedded local WebView-to-native
API server binds to a single hardcoded port (`127.0.0.1:42067`) with no per-instance uniqueness or
fallback.** With 9 BrokenNes.Windows.exe instances running simultaneously during this session
(this is a heavily-parallelized UAT pass, exactly as expected), only the *first*-launched instance's
API server can actually bind that port. Every other instance's server fails to start at all
(confirmed: my own instance had zero listening TCP ports), yet the JS front-end always talks to
`127.0.0.1:42067` regardless of which process answers — so clicks that are supposed to control
*my* window (Open Emulator, Controller 1/2 config dialogs, etc.) were silently going to a
**different, unrelated parallel agent's instance (PID 12384)** instead. This is a significant
finding in its own right (see "Root cause" below) and it fully explains why I could not reach the
native Emulator/Config/CPU screen to directly verify the FIX-core finding live.

As a substitute, I traced the exact code path by reading the source and can confirm **with high
confidence, at the code level, that the documented finding is correct** — see "Code-level
confirmation" below. I was not able to produce the requested before/after CPU-menu screenshots
proving it live; that part is genuinely BLOCKED by the environment, not refuted.

---

## Step 1 — Options screen (WebView2)

1. Launched instance, dismissed the Health Warning screen via its real accessible name
   ("Acknowledge health warning", not "OK"). **PASS**
   Evidence: `options-01-initial.png`, `options-02-mainmenu.png`.
2. From Main Menu, clicked "Options" (accessible name matches visible text here). Options screen
   loaded correctly, enumerated via `Get-AllNamedElements`:
   - `Clear the save` — **not tested** (destructive, explicitly told to avoid).
   - `Unlock Everything` — **not tested** (destructive/save-modifying, avoided; also would have
     contaminated the shared save file other parallel agents may be relying on).
   - `Open Credits` — **PASS**. Opens a modal (`CREDITS` / `BrokenNes - Deck Builder` / credits
     text / `Continue` button); `Continue` correctly returns to the Options screen. This is pure
     client-side DOM toggling (`modal.style.display='flex'`, confirmed by reading
     `Windows/Webmodules/Options/options.js`), so it is NOT affected by the port-collision bug
     below — this result is reliably against my own instance.
     Evidence: `options-06-credits-retry.png` (clean capture), `options-07-after-credits-continue.png`.
     Note: my first two attempts to screenshot this (`options-04-credits.png`,
     `options-05-credits-actual.png`) show the stale Main Menu instead of the modal — this was a
     screenshot-timing/window-focus artifact (window wasn't foregrounded), not an app bug; a retry
     with the window properly foregrounded showed the modal correctly straight away.
   - `Controller 1` / `Controller 2` — dialogs opened and displayed correctly (native "Player 1/2
     Controller Configuration" windows, with Input Mode / per-button bindings / Reset to
     default / Save / Cancel). **However, I cannot certify these ran against MY OWN instance.**
     `openControllerConfig` is wired through `window.webapi.ui.openControllerConfig(...)`, which
     goes over the same hardcoded `127.0.0.1:42067` HTTP API implicated in the root-cause finding
     below. Given my own instance's API server never bound any port at all (confirmed via
     `Get-NetTCPConnection`), these dialogs were almost certainly opened by the **other** parallel
     agent's instance (PID 12384), not mine, and my "Cancel" clicks (searched only within my own
     instance's UIA tree) both returned `False` / found nothing, consistent with the dialog not
     actually belonging to my process. I did not press Save on either dialog, so no bindings were
     changed either way. Marking this **UNVERIFIED** rather than PASS/FAIL, and flagging the
     possibility that my exploration nudged another agent's window into showing these dialogs.
     Evidence: `options-08-controller1.png`, `options-10-controller2.png`.
   - `RETURN` — **PASS**, returns to Main Menu correctly.

No destructive actions were taken against the shared save/progression during this run.

### Methodology note (not an app bug)
Early in this session, screenshots taken while the window sat at its default launch position
(overlapping other parallel agents' windows on the primary display) came out visually torn/stale —
showing the wrong page, or a mix of two windows' content — even though the live UI-Automation tree
was already showing the correct, current page. Moving the window to an unused region of a spare
monitor (`MoveWindow` to `(2200,100)`) fixed this completely and all subsequent screenshots were
clean. This was purely a screen-capture/window-overlap artifact of running many parallel instances
on the same virtual desktop, not a rendering bug in BrokenNes itself. Recommendation for future
parallel UAT runs: reposition each launched instance to a distinct screen region before relying on
`Save-WindowScreenshot`, and cross-check against the UIA tree (`Get-AllNamedElements`) whenever a
screenshot looks inconsistent with expected state.

---

## Steps 2-4 — Native Config/CPU menu and "Show Locked Items" — BLOCKED

**What happened:** Clicking "BrokenNes Emulator" (accessible name "Open Emulator") on the Main
Menu never navigated to the native MenuStrip screen, in any of 3 independent fresh instances, using
every input method I could try: UIA `InvokePattern.Invoke()`, a real hardware mouse click at the
UIA-reported bounding-rectangle center (verified via `GetCursorPos` to land exactly where
intended), a rapid genuine double-click, and `UIAutomationElement.SetFocus()` + real
`keybd_event` Enter/Space keypresses. Every other Main-Menu button (Options, ROM Manager, Open
Deck Builder) responded immediately and correctly to the very same click technique, ruling out a
general input-injection problem.
Evidence: `emu-01` through `emu-09` (esp. `emu-05-retry-openemulator.png`,
`emu-08-clean-first-click.png` — clean, non-overlapping-window captures showing the button's hover
state lit up but no navigation occurring).

**Root cause (confirmed by reading source):**
- `home.js`'s `onEmulatorClick()` → `proceedToEmulator()` calls
  `api.navigation.goToEmulator()` (from `Windows/Webmodules/shared/webapi.js`), which does
  `fetch('http://127.0.0.1:42067/api/navigation/go-to-emulator', ...)`. The base URL is a **hardcoded
  constant**, not scoped per process.
- Server-side, `Windows/webapi/WebApiServer.cs` has `private readonly int _port = 42067;` — same
  hardcoded value, no fallback port and no reuse-address handling.
- `Windows/MainForm/MainForm.Initialization.cs` starts this server in a try/catch that explicitly
  swallows any bind failure: `catch (Exception ex) { Console.WriteLine(...); // Don't show error to
  user, API is optional }`.
- I confirmed via `Get-NetTCPConnection -LocalPort 42067` that the port was held throughout this
  session by **PID 12384**, a different BrokenNes.Windows.exe instance (one of 9 running
  simultaneously — `Get-Process -Name BrokenNes.Windows` listed PIDs 12384, 21232, 3012, 21080,
  22944, 22024, 21184, 20248 (mine), 20256). I further confirmed my own instance (PID 20248) had
  **zero** listening TCP ports at all (`Get-NetTCPConnection -OwningProcess 20248` returned "no
  objects found").
- `proceedToEmulator()`'s own fetch error handler treats *any* fetch failure as implicit success
  ("Network/fetch errors are expected when the server shuts down during mode switch — this is
  actually a successful scenario"), and the two places that would otherwise `alert()` the user on
  failure are commented out in the source. So a second/third/etc. instance's "Open Emulator" click
  fails **completely silently** — no error, no navigation, nothing — exactly matching what I
  observed.
- Because the fetch target is a fixed loopback URL regardless of which process is asking, this bug
  isn't just "silently fails" for non-first instances — it actively **cross-talks**: my
  "Open Emulator" clicks, and my "Controller 1/2" dialog opens, were most likely being served by
  PID 12384's own WebApiServer, meaning they could have silently flipped *that other agent's*
  window into Emulator mode / opened dialogs on top of whatever they were doing, without their
  input. I stopped attempting further webapi-routed actions once I identified this, to avoid
  compounding any interference with that other session.

**Practical impact on this task:** I could not reach the native Emulator/Config/CPU/PPU MenuStrip
screen in my own, independently-verifiable instance, so I could not open Config → "Show locked
items", nor screenshot the CPU menu before/after toggling it. Steps 2-4 are **BLOCKED** by this
environment condition, not failed.

**This also means the UAT README's claim "Multiple independent BrokenNes.Windows.exe instances can
run simultaneously... (already verified)" needs a caveat**: window-level UI Automation scoping by
PID is indeed safe (confirmed throughout this session), but **any in-app action that round-trips
through the local WebView↔native HTTP API is not** safe under concurrent instances — only the
first-launched instance's actions actually take effect on that instance; every other instance's
equivalent actions may silently no-op or land on the wrong window. This likely affects more than
just "Open Emulator" — `go-to-overlay`, `go-to-widget`, `go-to-web`, `progression.unlockEverything`,
`ui.openControllerConfig`, and potentially other `window.webapi.*` calls used elsewhere in the app
(Deck Builder actions, achievements, audio, etc.) share the same hardcoded base URL and would have
the same exposure. **Recommendation:** give each instance's WebApiServer a dynamically-chosen free
port (and store/expose it to the WebView via an injected variable rather than a hardcoded
constant), or fall back gracefully with a visible error instead of silently treating a bind failure
or fetch failure as success.

### Code-level confirmation of the FIX-core-lock finding (in lieu of a live screenshot)

I read the exact menu-population code this finding is based on
(`Windows/MainForm/MainForm.Config.cs`) to independently verify it, since I could not exercise it
live:

- The CPU menu's candidate list, `orderedCpuCores`, is built from `CoreRegistry.CpuIds` — the full,
  reflection-populated registry of every compiled `CPU_*` class (confirmed
  `Windows/NesEmulator/cpus/CPU_FIX.cs` exists and is a real, compiled core, alongside
  `PPU_FIX.cs` / `APU_FIX.cs`). So `CPU_FIX` genuinely is a candidate for this menu, not excluded
  by name anywhere.
- Per-item logic (line ~528-538):
  ```csharp
  var unlocked = IsCpuCoreUnlocked(coreId, progressionSave);
  if (!unlocked && !config.ShowLockedItems)
  {
      continue;   // <- item is skipped entirely, doesn't appear in the menu at all
  }
  var item = new ToolStripMenuItem(coreId, null, (s, e) => SetCpuCore(coreId));
  item.Enabled = unlocked;
  if (!unlocked)
  {
      item.Text = $"{coreId} [Locked]";   // <- exact "[Locked]" suffix format
  }
  ```
  Identical logic exists for PPU (`orderedPpuCores`) and APU (`orderedApuCores`) menus.
- `IsCpuCoreUnlocked`/`IsPpuCoreUnlocked`/`IsApuCoreUnlocked` (`MainForm.Progression.cs`, lines
  73-89) are pure ownership checks — `IsOwnedCore(save.OwnedCpuIds, coreId)` etc. — with **no
  special-case default grant for FIX** or any other ID. There is no card/unlock definition anywhere
  that would ever add "CPU_FIX"/"PPU_FIX"/"APU_FIX" to `OwnedCpuIds`/`OwnedPpuIds`/`OwnedApuIds` for
  a normal player (consistent with `project_fix_core_family.md`'s documented gap).
- `config.ShowLockedItems` defaults to `false` (`EmulatorConfig.cs` line 171:
  `public bool ShowLockedItems { get; set; } = false;`), and the Config-menu checkbox with visible
  text "Show locked items" is wired straight to it (`MainForm.Config.cs` line 179-180).

**Conclusion from code inspection:** with `ShowLockedItems=false` (the default), CPU_FIX/PPU_FIX/
APU_FIX are completely absent from their menus for any player who hasn't (and structurally cannot)
own them — matching "invisible gap in the FIX rollout" exactly as documented. With
`ShowLockedItems=true`, they would appear as disabled entries reading `CPU_FIX [Locked]` /
`PPU_FIX [Locked]` / `APU_FIX [Locked]`, matching the predicted diagnosis precisely. I could not
capture this live in the running app due to the port-collision blocker above, but the source fully
and unambiguously corroborates the finding — I'd call this confirmed at high confidence even
without the live screenshot, and recommend a follow-up UAT pass (after other parallel instances
have exited, so a fresh instance can actually bind port 42067) purely to grab the live screenshot
for the record.

---

## Summary table

| Step | Item | Result |
|---|---|---|
| 1 | Launch, dismiss health warning | PASS |
| 1 | Options screen loads, enumerate contents | PASS |
| 1 | Open Credits (open + Continue) | PASS |
| 1 | Controller 1 / Controller 2 dialogs | UNVERIFIED (likely ran against a different parallel instance, see root cause) |
| 1 | RETURN navigation | PASS |
| 1 | Clear the save / Unlock Everything | NOT TESTED (destructive, out of scope) |
| 2 | Reach native "Open Emulator" MenuStrip screen | **BLOCKED** — button unresponsive due to cross-instance API port collision (root cause identified and documented) |
| 3 | Config → "Show locked items" toggle | BLOCKED (unreachable, see above) |
| 4 | CPU menu shows "CPU_FIX [Locked]" after toggle | BLOCKED live; **confirmed via source-code inspection** with high confidence (see Code-level confirmation) |

## Screenshots (in `UAT/screenshots/`)
`options-01-initial.png`, `options-02-mainmenu.png`, `options-03-optionsscreen.png`,
`options-04-credits.png` (stale/timing artifact), `options-05-credits-actual.png` (stale/timing
artifact), `options-06-credits-retry.png` (clean, correct), `options-07-after-credits-continue.png`,
`options-08-controller1.png`, `options-09-after-cancel-controller1.png`,
`options-10-controller2.png`, `options-11-after-cancel-controller2.png`,
`options-12-emulator-native.png` through `options-22-truth-check.png` (Open-Emulator
investigation, several showing the window-overlap screenshot-tearing artifact described above),
`emu-01-fresh-mainmenu.png` through `emu-09-final-mainmenu-state.png` (clean re-runs on a
repositioned window, isolating the Open-Emulator failure).

## Cleanup
Instance PID 20248 was stopped at the end of this run (`Stop-Process -Id 20248 -Force`). PIDs
10072 and 11468 (earlier instances from this same session) were already stopped mid-session. No
other parallel agents' processes were touched, stopped, or deliberately interacted with.
