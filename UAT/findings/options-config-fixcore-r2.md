# FIX-core progression lock - live screenshot confirmation (round 2)

Follow-up to `UAT/findings/options-config-fixcore.md` (round 1). Round 1 confirmed the
CPU_FIX/PPU_FIX/APU_FIX lock-out purely by reading source (`MainForm.Config.cs`,
`MainForm.Progression.cs`) - "Open Emulator" never actually navigated during that pass (blocked by
the 12-way-parallel port-42067 contention documented in `UAT/README.md`), so the live menu screenshot
showing `CPU_FIX [Locked]` was never captured. This round re-runs that single scenario with
exclusive access (no other `BrokenNes.Windows.exe` instance alive) specifically to capture it.

## Setup

- Verified clean machine state before launch: `Get-Process -Name 'BrokenNes.Windows'` returned
  nothing, `Get-NetTCPConnection -LocalPort 42067` returned nothing. Launched exactly one instance
  (PID 14324).
- Dismissed a native "Audio Warning" dialog (`Audio initialization failed: BadDeviceId calling
  waveOutOpen` - unrelated pre-existing environment quirk, not part of this test) via its "OK"
  button, then the WebView2 "Health Warning" splash via its real accessible name **"Acknowledge
  health warning"** (visible text is just "OK" - another visible-text-vs-accessible-name mismatch
  worth adding to the helpers' running list).
- Clicked "Open Emulator" from the Main Menu. Because this run had genuine exclusive access, the
  WebView2->native handoff worked on the first try and navigated straight to the native
  Emulator/Config/Tools & Activities/SHADER/APU/CPU/PPU/Help MenuStrip screen - reconfirming the
  README's diagnosis that exclusivity, not the feature itself, was what round 1 lacked.

## Harness note: capturing open native dropdown menus

`Save-WindowScreenshot` (PrintWindow-based) only captures the target HWND's own content. A
WinForms `MenuStrip` dropdown renders as a **separate top-level popup window**, so it is invisible
to a PrintWindow capture of the main window - screenshots taken this way while a menu was open came
back solid black or (worse) tiny/wrong-sized because `Process.MainWindowHandle` itself transiently
re-resolves to the popup window (empty title) while a menu is open, returning a bogus small
`BoundingRectangle`. Fix used here: read the main window's screen rect via UI Automation
(`Get-AppRoot(...).Current.NativeWindowHandle` + `DwmGetWindowAttribute`) **while no menu is open**,
cache that rect, then reopen the menu and do a plain `Graphics.CopyFromScreen` over the cached
rect. This is safe specifically because this run has exclusive access (no other instance around to
overlap on screen) - the exact condition the README says a `CopyFromScreen` approach needs. Worth
folding into `UiaHelpers.ps1` as a documented helper for any future menu-dropdown screenshot need.

Separately, `Click-Element` (Invoke/SelectionItem/Toggle/ExpandCollapse patterns) returned `False`
for the "Show locked items" `ToolStripMenuItem` and a `Find-ByName` re-query for it intermittently
returned nothing while the submenu was visibly still open on screen - the transient dropdown's UIA
subtree appears flaky/lazily-populated. Worked around with a raw `SetCursorPos` +
`mouse_event(LEFTDOWN/LEFTUP)` click at the item's on-screen coordinates (read from its
`BoundingRectangle` while it was briefly resolvable), which reliably registered.

## Steps and evidence

1. **CPU menu, before toggle** (`screenshots/fixcore-r2-04c-cpu-before.png`): FMC, EIL (checked/
   current), LOW, LW2, SPD, ULQ, Z80. No FIX entry anywhere - matches round 1's prediction.
2. Opened **Config -> Debug Tools** (`screenshots/fixcore-r2-05-debugtools-menu.png`) and confirmed
   the exact live menu item name: **"Show locked items"** (matches
   `MainForm.Initialization.cs:359`'s `new ToolStripMenuItem("Show locked items", ...)` - lives
   under Config > Debug Tools, not a top-level Config item).
3. Clicked it (see harness note above for how). Confirmed via screenshot the click registered
   (menu closed as expected for any WinForms menu-item click).
4. **CPU menu, after toggle** (`screenshots/fixcore-r2-09-cpu-after-toggle.png`) - **the key shot**:
   a new entry **"FIX [Locked]"** now appears between EIL and LOW, rendered in the disabled/greyed
   style WinForms uses for a non-clickable `ToolStripMenuItem`. This is the live-UI confirmation
   the round-1 finding was missing.
5. **PPU menu, after toggle** (`screenshots/fixcore-r2-10-ppu-after-toggle.png`): same pattern -
   **"FIX [Locked]"** appears (greyed out) between EXE and IMG, alongside the already-known gimmick
   cores BFR/CUBE/CUBEX/EIL/EXE/IMG/LOW/LQ/SPD/ULQ.
6. **APU menu, after toggle** (`screenshots/fixcore-r2-11-apu-after-toggle.png`): same pattern -
   **"FIX [Locked]"** appears (greyed out) between EIL and HI.

## Conclusion

Live-UI evidence now backs up the round-1 source-reading diagnosis exactly as predicted:
`CPU_FIX`/`PPU_FIX`/`APU_FIX` are absent from their respective menus under default settings
(`ShowLockedItems = false`, the shipped default) and appear as a disabled `"FIX [Locked]"` entry in
all three menus (CPU/PPU/APU) once "Config > Debug Tools > Show locked items" is turned on. The
cores are genuinely wired into the menu-population code and reachable in a locked/disabled display
- they are not silently dropped or absent under every settings combination. They remain
unselectable (no card/unlock definition exists to grant ownership), so this does not give players
or testers a way to actually play on FIX from the shipped Windows UI - it only confirms the gap is
exactly what round 1's source reading said it was: a missing unlock grant, not a missing/broken menu
wire-up. This closes out the open narrative question from the FIX-core-family effort this session.

## Cleanup

Closed the open APU dropdown (Escape), then `Stop-Process -Id 14324 -Force`. Confirmed via
`Get-Process -Id 14324` (post-stop) that the process was gone before finishing.
