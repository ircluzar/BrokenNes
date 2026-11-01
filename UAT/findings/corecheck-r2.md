# Core switching + FIX-lock live confirmation (round 2)

Follow-up to `UAT/findings/corecheck.md` (round 1), which never reached the native
menu because of the 12-instance port-42067 collision documented in `UAT/README.md`.
This round ran with **exclusive access** (verified clean before launch: no stray
`BrokenNes.Windows` process, port 42067 free) and reached the native menu
successfully on the very first "Open Emulator" click.

PID used: 21656 (fully exited at the end of the run, confirmed via `Get-Process`).

## Result summary

| Check | Result |
|---|---|
| Reach native Emulator/CPU/PPU/APU menu screen | PASS (live) |
| Load a ROM via Emulator > Load Rom... | PASS (live) |
| CPU menu: FIX absent | **CONFIRMED absent, live screenshot** |
| PPU menu: FIX absent | **CONFIRMED absent, live screenshot** |
| APU menu: FIX absent | **CONFIRMED absent, live screenshot** |
| CPU core switching (FMC, SPD, LOW, EIL) | PASS, all responsive |
| PPU core switching (LOW, SPD) | PASS, all responsive |
| APU core switching (FMC, LOW, SPD) | PASS, all responsive |

## Walkthrough

1. **Launch.** `Start-BrokenNesWindows` against the Release build. First dialog shown
   was actually an **"Audio Warning"** ("Audio initialization failed: BadDeviceId
   calling waveOutOpen — The emulator will run without sound."), not directly a
   Health Warning — this machine apparently has no usable audio output device.
   Dismissed via its native "OK" button (this is a plain WinForms MessageBox, not
   WebView2 content). See `screenshots/corecheck-r2-01-launch.png`.
2. Behind it, the WebView2 **Health Warning** splash appeared as expected
   (`corecheck-r2-02-mainmenu.png`), dismissed via its real accessible name
   `Acknowledge health warning` (visible text is just "OK" inside the styled box —
   another visible-text-vs-accessible-name mismatch worth noting for future runs).
3. Main Menu reached (`corecheck-r2-03-mainmenu.png`). Enumerated named elements
   first, per the helper file's guidance — confirmed accessible name is
   **"Open Emulator"** (visible button text "BrokenNes Emulator"), matching the
   README's documented gotcha exactly.
4. Clicked **Open Emulator**. This time, with truly exclusive access, the
   WebView2->native HTTP handoff worked on the first try — the native MenuStrip
   screen (Emulator/Config/Tools & Activities/SHADER/APU/CPU/PPU/Help) appeared
   immediately (`corecheck-r2-04-emulator.png`). This is the live confirmation the
   round-1 report was missing: **exclusive access is sufficient to make "Open
   Emulator" work reliably**, corroborating the README's port-collision diagnosis.
5. **Loaded the ROM** via Emulator > Load Rom... . Two automation-harness gotchas
   surfaced here, neither of which are product bugs (documented below for whoever
   runs this area next) — the ROM loaded correctly in the end
   (`corecheck-r2-11-afterload.png`, showing page1_binty.nes's title-screen art
   rendered full-window).
6. Opened the **CPU** menu. Enumerated items and confirmed live via toggle-pattern
   state which one is checked. Live screenshot `corecheck-r2-13-cpumenu-fmc-checked.png`
   shows the full item list with a checkmark next to the active core.
7. Selected **FMC → SPD → LOW → EIL** (skipping ULQ/Z80 as instructed — intentional
   gimmick cores). Screenshotted after each; window stayed responsive
   (`Get-Process ... Responding` = True) and re-rendered every time, no crash, no
   hang, no freeze. Re-opened the CPU menu afterward and used `TogglePattern` to
   positively confirm the checkmark tracks the last click (FMC showed `ToggleState=On`
   after selecting it; EIL showed `ToggleState=On` after selecting it later — see
   `corecheck-r2-14/15/16/17/18-*.png`).
8. Opened the **PPU** menu, enumerated it (`corecheck-r2-19-ppumenu.png`), selected
   **LOW** and **SPD**. Both responsive (`corecheck-r2-20/21-*.png`).
9. Opened the **APU** menu, enumerated it (`corecheck-r2-22-apumenu.png`), selected
   **FMC**, **LOW**, **SPD**. All responsive (`corecheck-r2-23/24/25-*.png`).
10. Final responsiveness check: process still `Responding=True`, `MainWindowTitle`
    still "BrokenNes", native menu bar still reachable via UIA after all the above.

## FIX-lock: live-confirmed absent from all three menus

Exact live-enumerated item lists (accessible Name, in on-screen order):

- **CPU**: `FMC, EIL, LOW, LW2, SPD, ULQ, Z80` — 7 items, no FIX.
- **PPU**: `FMC, BFR, CUBE, CUBEX, EIL, EXE, IMG, LOW, LQ, SPD, ULQ` — 11 items, no FIX.
- **APU**: `FMC, EIL, HI, HI2, HI2X, LOW, LQ, LQ2, MNES, QLOW, QLQ, QLQ2, QN, SPD, SPD2, ULQ, WF` — 17 items, no FIX.

Each list above is backed by both (a) a `Get-AllNamedElements`-style UIA descendant
enumeration of the open dropdown and (b) a `PrintWindow`-captured screenshot of that
same open dropdown (`corecheck-r2-13`, `-19`, `-22` for FMC/PPU-full/APU-full views
respectively, with `-15` showing the CPU dropdown post-selection as an extra
cross-check). This satisfies the round-1 report's outstanding requirement for a
live-screenshot-backed confirmation, not just source-code reasoning. The finding
from round 1 / the README (FIX exists as a core family but is locked out of the
in-game menus by the progression/ownership system with no unlock definition ever
added for it) is fully corroborated: FIX literally does not appear as a selectable
or even `[Locked]`-suffixed item in any of the three menus in their current default
state. (Toggling `config.ShowLockedItems` to check for a `[Locked]` FIX entry was
not attempted this round — out of scope for this pass, which was specifically about
confirming plain-menu absence live; the README's diagnosis already covers that
angle via source reading.)

## Harness/automation notes for future runs of this area (not product bugs)

- **A native MenuStrip dropdown is its own top-level popup window**, not a
  descendant of the main "BrokenNes" window. While a dropdown is open,
  `Get-AppRoot`/`Process.MainWindowHandle` can resolve to that popup instead of the
  main window (both share the same PID, and `RootElement.FindFirst` with only a
  PID condition doesn't guarantee which match comes back), which makes
  `Save-WindowScreenshot` return a black/empty image and makes `Get-NativeMenuBar`
  return null. Fix: send `{ESC}` (via `System.Windows.Forms.SendKeys`) to close the
  dropdown before re-resolving the app root, or resolve the specific popup hwnd
  directly (via `EnumWindows` filtered to the target PID, visible, and not the
  known main hwnd) and `PrintWindow` that hwnd instead when you specifically want a
  screenshot of an open dropdown.
- **`Emulator > Load Rom...` opens a real native `#32770` Open-File common dialog**,
  which is a *separate top-level window*, invisible to `Get-AppRoot`'s
  children-only PID search until you go looking with `EnumWindows`. It is barely
  UIA-navigable beyond its top-level named panes (`File name:`, the actual
  ComboBoxEx has an empty-named Pane sibling with AutomationId `1148` but exposes
  no `Edit`/`Button` `ControlType`, no supported patterns, and a full `Descendants`
  scan of the dialog or its `ShellView` list hangs for 100+ seconds because of the
  virtualized folder-view item count) — practically, the only reliable way found to
  drive it was `SetForegroundWindow` on the dialog's own hwnd followed by
  `SendKeys` typing the filename and `{ENTER}`, which worked cleanly.
- **This machine's actual physical/interactive display shows something else
  entirely** (in this run, the Claude Code IDE itself) — `CopyFromScreen` against
  the BrokenNes window's on-screen coordinate rect captures whatever is really
  showing there (not BrokenNes), even though `GetWindowRect` reports plausible
  on-screen coordinates for the BrokenNes window/dialog. `PrintWindow`-based
  capture (as `Save-WindowScreenshot` already does) is unaffected and remains the
  only reliable screenshot method — this is a stronger, generalized version of the
  README's existing CopyFromScreen warning: it's not just "some other window is on
  top", the whole visible desktop can belong to something unrelated. Similarly,
  **do not assume real mouse-click coordinates (a `computer`-tool style click) will
  ever land on BrokenNes** in this environment — only UIA `Invoke`/`Select`/`Toggle`
  patterns (as the existing `Click-Element` helper already uses) and `SendKeys`
  keyboard input reliably reach the app. `SendKeys` was confirmed to work (used
  successfully for `{ESC}` and for typing the ROM filename).
- Clicking a `MenuItem` via `Click-Element`'s `InvokePattern.Invoke()` can
  occasionally take a long time to return (one call ran past the 120s foreground
  timeout and had to finish in the background) even though the action underneath
  succeeds almost immediately — plan for this by not blocking later steps on that
  particular call's synchronous return if it can be avoided, or just tolerate the
  occasional backgrounding as this run did.

## Cleanup

`Stop-Process -Id 21656 -Force` issued at the end of the run;
`Get-Process -Id 21656` confirmed no such process afterward (see final tool output
below the findings write-up in the session transcript). Machine left clean for the
next agent.
