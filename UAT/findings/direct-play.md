# UAT Findings: Direct-Play ROM Loading and Basic Gameplay

**Area:** direct-play ROM loading and basic gameplay
**Instance:** own independent `BrokenNes.Windows.exe`, PID 15636
**Date:** 2026-08-29

## Summary

Blocked before the assigned area could really begin. The Main Menu's "BrokenNes Emulator"
button (accessible name `Open Emulator`, AutomationId `btnEmulator`) — the only documented
entry point to the native-menu direct-play screen — does not navigate anywhere when
activated, no matter which input method is used. Every sibling control on the same screen
(Deck Builder, ROM Manager, the Options screen's Return link) navigates correctly via the
exact same methods, tested back-to-back on the same running instance, which rules out a
methodology/harness problem and points at a real, reproducible defect (or at minimum a
dead/disconnected button) isolated to that one control. Steps 2–7 of the assigned script
(native MenuStrip, ROM loading, input sanity check, Pause/Reset) could not be attempted as a
result.

A significant side-discovery, documented below, is that with many parallel UAT instances of
this app running simultaneously, multiple `BrokenNes.Windows.exe` top-level windows can spawn
at/overlap the exact same screen coordinates. That silently breaks both screen-position-based
mouse clicks and `CopyFromScreen`-based screenshots (they can act on / capture whichever
instance is topmost at that pixel, not necessarily your own PID) unless you explicitly detect
and correct for it. This cost most of the session's time and is worth folding into the shared
harness notes for other parallel testers.

## Step-by-step

**Step 0 — Read required docs.** PASS. Read `UAT/README.md` and `UAT/lib/UiaHelpers.ps1` in
full before starting.

**Step 1 — Launch, health warning, reach Main Menu, identify "Open Emulator".** PASS.
- Launched via `Start-BrokenNesWindows`, got PID 15636.
- Health Warning dialog appeared as expected (screenshot `direct-play-01-launch.png`).
- Clicked accessible name `Acknowledge health warning` → Main Menu appeared
  (`direct-play-02-mainmenu.png`).
- Enumerated Main Menu via `Get-AllNamedElements`: confirmed the visible "BrokenNes Emulator"
  button's accessible Name is indeed `Open Emulator` (AutomationId `btnEmulator`), matching
  the README's documented quirk. Other buttons: `Open Deck Builder` (btnDeckBuilder),
  `ROM Manager` (btnRomManager), `Options` (btnOptions).

**Step 2 — Open the "Emulator" native menu and enumerate items.** BLOCKED. Never reached
this screen — see below.

**Step 3 — Load `Data\story\page1_binty.nes`.** BLOCKED — no native menu ever appeared to
load a ROM from.

**Step 4 — Before/after screenshots of ROM load.** BLOCKED, not reachable.

**Step 5 — Basic input sanity check (arrows, Z, X, Enter).** BLOCKED, not reachable.

**Step 6 — Pause/Reset menu items.** BLOCKED, not reachable.

**Step 7 — Report.** This document.

## The blocker, in detail

Clicking/activating "Open Emulator" was attempted via every method below, each verified
clean (own PID only, own window confirmed topmost/foreground at the moment of the attempt),
and **none of them navigated away from the Main Menu**:

1. `Click-Element` (tries Invoke/SelectionItem/Toggle/ExpandCollapse patterns) — Invoke
   pattern reports success (no exception), no navigation.
2. Direct `InvokePattern.Invoke()` call on the freshly-fetched element — same result,
   repeated 3 times across the session, always landing back on
   `BrokenNes - Main Menu` per `Get-ContentPane`/`Get-AllNamedElements` (not just
   screenshot — the actual UIA document name never changes).
3. Real OS-level mouse click (`SetCursorPos` + `mouse_event` LEFTDOWN/UP) at the button's
   exact UIA-reported center, with the window explicitly verified topmost at that screen
   pixel via `WindowFromPoint`/`GetAncestor` immediately beforehand — visible hover/pressed
   highlight appears (confirms the click physically lands on the button), but no navigation.
   Repeated as a single click, a double-click, and at multiple offsets within the button's
   hit rectangle (left/top/right/bottom edges) — same non-result every time.
4. Keyboard activation (Enter, Space, `z`) sent after legitimately focusing the button via
   `AutomationElement.SetFocus()` and confirming `GetForegroundWindow()` matched our window's
   handle — no navigation.

**Control test, same instance, same methods, immediately before/after:** `Open Deck Builder`,
`ROM Manager`, and the Options screen's `RETURN` hyperlink all navigate correctly and
immediately on the very first `Click-Element`/`InvokePattern.Invoke()` call, and Deck Builder
also responds correctly to a real mouse click. This was re-confirmed as the very last action
of the session (after all Emulator-button attempts) to prove the app was never globally hung —
`Open Deck Builder` still worked instantly at that point. So the automation approach is sound
and the app is responsive; only the Emulator button itself is inert.
- `IsEnabled=True`, `IsOffscreen=False` on the button throughout — it is not a disabled/
  locked control by any UIA-visible signal.
- Only one matching element exists in the tree (`FindAll` by name returned count=1) — not a
  stale-duplicate-element artifact.

Screenshots (`UAT/screenshots/`): `direct-play-08-directinvoke.png`,
`direct-play-11-truetopmost.png` through `direct-play-20-final-clean-invoke.png`,
`direct-play-18-romanager-control-test.png` (control test proving the method works on a
sibling button), `direct-play-21-app-still-responsive.png` (final proof the app was not
hung). All show the Main Menu unchanged after the Emulator button was actuated, versus the
control tests which show real navigation.

I could not determine *why* the button doesn't fire from outside the app (no dev console
access in this Release build) — it could be a broken/missing click handler, an unhandled
exception during navigation setup that's silently swallowed, or some precondition check that
fails without visible feedback. I did not attempt to fix it — out of scope for a UAT pass —
but flag it as the highest-priority thing to look at, since it fully blocks the
"Open Emulator" direct-play path this whole test area was built around.

## Side-discovery: overlapping parallel instances break position-based automation

While investigating the above, I found that with ~10 `BrokenNes.Windows.exe` instances
running in parallel (confirmed via `Get-Process`), several of their top-level windows spawn
at/near the identical default screen position and overlap. This has two consequences the
existing `UiaHelpers.ps1` doc comments don't mention:

- `Save-WindowScreenshot` uses `CopyFromScreen` against the target PID's DWM window rect —
  but if another instance's window is topmost over that same screen region, the screenshot
  silently captures the *other* window's content instead. I verified this directly with
  `WindowFromPoint` + `GetAncestor(..., GA_ROOT)`: at my own window's reported bounding
  rectangle, the topmost window at a given pixel sometimes resolved to a completely different
  PID/HWND than my own instance.
- Real OS-level input (`mouse_event`, `SendKeys`) goes to whatever window is physically
  topmost/foreground at that screen location — not necessarily the PID you scoped your UIA
  queries to. Early in this session, my very first click attempt appears to have landed on a
  *different* tester's instance (it unexpectedly showed the Options screen, which no
  subsequent same-PID UIA query could reproduce until I fixed the positioning).

**Workaround I used:** move the window to a unique offset via `SetWindowPos` (based on
its own PID so parallel testers land at different spots) and pin it `HWND_TOPMOST`, then
verify with `WindowFromPoint`/`GetAncestor` that the target PID's window is actually on top
at the coordinates of interest before trusting a screenshot or sending real input. Pure UIA
calls (`Get-AppRoot -ProcessId $myPid`, `InvokePattern.Invoke()`, etc.) are unaffected by
this since they operate on the accessibility tree directly rather than screen pixels/OS
input focus — that's what let me eventually isolate the Emulator-button issue as real rather
than an artifact of window overlap. Worth adding to `UAT/lib/UiaHelpers.ps1` or the README
for other parallel testers, though I didn't edit those shared files myself to avoid
conflicting with sessions actively using them.

## What passed

- App launch, health warning dialog and its dismissal.
- Reaching the Main Menu and confirming its accessible names (`Open Emulator` etc. match
  the README's documented visible-text/accessible-Name mismatch).
- Deck Builder navigation (Summary screen: Owned Cores 136/138, Achievement stars 8,
  Progress Level 7 — pre-existing shared save data, not something I created).
- ROM Manager navigation (shows its "Loading ROMs..." list screen, Import ROMs / Search ROMs
  controls, Return works).
- Options screen navigation and its Return link.
- General app responsiveness/stability throughout ~20 minutes of repeated interaction — no
  crash, no hang, no exception dialog observed at any point.

## What failed

- The Main Menu's "BrokenNes Emulator" (`Open Emulator`) button does not navigate to the
  native-menu direct-play screen under any input method tried (see above). This blocks the
  entire remainder of the assigned test area.

## What I could not test / verify (and why)

- Native MenuStrip enumeration (Emulator/Config/Tools & Activities/SHADER/APU/CPU/PPU/Help) —
  screen never reached.
- ROM loading via `Data\story\page1_binty.nes` and the file-open-dialog-by-typing-path
  technique — screen never reached, dialog never invoked.
- Before/after rendering comparison for ROM load — not reachable.
- Keyboard input sanity check against actual gameplay (arrows/Z/X/Enter) — not reachable;
  I did confirm keyboard delivery mechanics work in general (Enter/Space/Z all correctly
  reached the app window per `GetForegroundWindow` checks), just not against a loaded game.
- Pause/Reset menu items — not reachable.

## Files

- Findings: `C:\Users\philt\OneDrive\Documents\PROJECTS\BrokenNes\UAT\findings\direct-play.md`
- Screenshots: `C:\Users\philt\OneDrive\Documents\PROJECTS\BrokenNes\UAT\screenshots\direct-play-*.png`
  (21 numbered steps plus lettered sub-steps for the timed-wait test)
