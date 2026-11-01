# UAT Findings: Imagine / "Target the Beam" ML mode (Round 2)

**Round-1 file:** `UAT/findings/imagine.md` — fully blocked in round 1 by the port-42067 concurrency
bug (12 parallel instances contending for one HTTP API); "Open Emulator" never navigated, so
nothing past the main menu was ever tested. A sibling round-1 agent did confirm via
`Windows/Webmodules/ImagineBug/config.json` that the feature is labeled **"Target the Beam"** in
the Tools & Activities menu (not literally "Imagine").

**This round:** ran with exclusive access (sole `BrokenNes.Windows.exe` instance, verified port
42067 free before launch). Result: **"Open Emulator" worked flawlessly on the first try** — this
single-instance workaround is confirmed effective. The whole feature was exercised live, end to
end, including actually running ML inference. No crash or hang anywhere in the flow.

## Steps performed

1. Launched via `Start-BrokenNesWindows`, dismissed the "Audio initialization failed: BadDeviceId"
   dialog (OK) and then the in-app "Health Warning" splash (Acknowledge health warning) — two
   separate dismissals, not one.
2. Main Menu → clicked "Open Emulator" (accessible name; visible text "BrokenNes Emulator") →
   native MenuStrip screen loaded correctly (Emulator/Config/Tools & Activities/SHADER/APU/CPU/PPU/
   Help), confirming exclusive port ownership worked.
3. Emulator → Load Rom... → native "Select a NES ROM" common dialog opened, already pointed at the
   `Data\story` folder → selected and opened `page1_binty.nes`. ROM loaded and rendered correctly
   (a story-mode character portrait, matching the "binty" filename).
4. Tools & Activities → **Target the Beam** menu item present and clickable; opened a right-side
   panel. Confirmed via UIA the panel's internal WebView2 page title is **"Imagine a Bug"**
   (matches the `ImagineBug` config.json path a sibling agent found), while the user-facing menu
   label is "Target the Beam" — both names now tied together with live evidence.
5. Enumerated the panel: exactly two buttons — **"Create Savestate"** and **"Open Glitch
   Harvester"** — plus a "Target the Beam explanation" text block: *"Target the Beam is an
   experimental corruptor that lets you select the render scanlines to corrupt cpu instructions as
   they draw the pixels to the frame. Result may vary."* This matches round-1's enumeration exactly.
6. **Clicked "Create Savestate" live.** No crash, no hang, no error. It transitioned into a
   scanline-region-picking mode: an on-screen banner appeared reading **"Draw a shape to target the
   scanlines"**, and the "Create Savestate" button relabeled itself to **"Retry"**.
7. **Actually drew a region** by sending a synthetic mouse-drag (`WM_LBUTTONDOWN` →
   `WM_MOUSEMOVE`×N → `WM_LBUTTONUP`) directly to the WebView2 `Chrome_RenderWidgetHostHWND` child
   window (see "Automation gotcha" below for why this was necessary instead of real cursor
   movement). The drag was accepted: a lightened/washed-out horizontal band appeared across the
   game frame at the dragged scanline range — a live, visible demonstration of the advertised
   "corrupt CPU instructions as they draw scanlines" effect. Screenshots:
   `imagine-r2-13-targetbeam.png` (before) → `imagine-r2-14-aftersavestate.png` (savestate armed,
   prompt shown) → `imagine-r2-16-restored.png` (after the drag — visible corrupted band).
8. Clicked **"Retry"** — cleanly reset back to the pre-draw state (button relabeled back to "Create
   Savestate", corrupted band gone, target-icon restored). Full round-trip works with no residue or
   instability.
9. Clicked **"Open Glitch Harvester"** — navigated to the full Glitch Harvester panel (tabs: RTC /
   SAVE STATES / STASH / STOCKPILE / **IMAGINE**). This is the deeper ML UI the "Target the Beam"
   entry point feeds into.
10. Opened the **IMAGINE** tab directly. Contrary to round-1's "may fail gracefully if no ML model
    file is present" caveat, **a real model file is present and loaded**: byline *"what if we
    trained an AI on 6502 assembly and made the NES execute whatever it predicts"*, Model Settings
    (Load Model / Epoch=30 / Bytes=2 / Temp=0.40 / Top K=1), Targeted Imagine (Scanline Mode)
    checkboxes ("Enable Scanline Targeting", "Load on Imagine" — checked), a big **"Imagine a Bug"**
    button, and a status line already reading **"Status: Model loaded and ready"** before any click.
11. **Clicked "Imagine a Bug" live — twice.** Both times: no crash, no hang, process stayed
    `Responding: True` throughout. Each click visibly, dramatically corrupted the NES frame
    (scrambled scanlines/colors — the AI-predicted bytes actually executing as CPU instructions) and
    updated the status line with byte count and a bracketed hex-looking readout. Screenshots:
    `imagine-r2-20-afterimaginebug.png`, `imagine-r2-21-final.png`.
12. Cleanly exited: `Stop-Process -Id $myPid -Force`, confirmed both the specific PID and
    `Get-Process -Name BrokenNes.Windows` return nothing afterward.

## Bug found: malformed hex byte readout in "Imagine a Bug" status text

The status text after a successful inference is supposed to show the predicted bytes in hex, but
the characters produced are not valid hex digits:

- Run 1: `Status: Bug imagined! [0x0O 0x0K 0x0K 0x0=]`
- Run 2: `Status: Bug imagined! [0x0J 0x0R 0x0C 0x0=]`

(read via UIA accessible Name, not OCR — exact strings). Valid hex digits are `0-9A-F`; `O`, `K`,
`J`, `R`, `=` are not hex digits, and every run's fourth entry is literally `0x0=`. This looks like
the code is formatting each predicted byte as `"0x0" + someChar` where `someChar` comes from
treating the byte (or a masked nibble of it) as a raw ASCII/ANSI character rather than converting
it to a hex digit (e.g. calling something like `(char)(byteValue)` or indexing into the wrong
table instead of `byteValue.ToString("X2")`). It reproduced identically in shape (`0x0<char>`
four times, last one always `=`) across two independent runs with different underlying byte
values, which points at a deterministic formatting bug rather than a one-off fluke. Purely
cosmetic — it did not affect the actual corruption behavior (the frame visibly changed both times)
— but the displayed "bytes predicted" readout is not trustworthy for a user or a follow-up agent
trying to read what the model actually predicted.

Confirmed **not** a crash, not a hang, not a silent no-op — the feature is fully live and working
end-to-end; this is a narrow, reproducible display/formatting defect worth a real fix.

## Automation gotcha discovered this round (harness note, not a product bug)

After the native "Tools & Activities" menu interaction, a transient, empty-titled, class-`SysShadow`
top-level window appeared sharing the same PID as `BrokenNes.Windows.exe` (Windows creates these
as drop-shadow chrome for certain popups/menus). Two of `UiaHelpers.ps1`'s assumptions broke
because of it:

- **`Get-AppRoot`'s `FindFirst`-by-PID can silently return the `SysShadow` pane instead of the real
  `ControlType.Window`** when both share the same PID at query time — every subsequent
  `Get-ContentPane`/`Get-NativeMenuBar` call against that "root" then legitimately returns null
  (there's nothing wrong with those calls; they were handed the wrong root). Fix used here: filter
  the PID-matched top-level elements to `ControlType.Window` explicitly before using the result as
  root.
- **`Save-WindowScreenshot`'s `$proc.MainWindowHandle` can also point at the `SysShadow` window**
  instead of the real window once this happens (confirmed live: `MainWindowHandle` returned the
  shadow's handle, with an empty title, while `GetForegroundWindow()` correctly returned the real
  "BrokenNes" window). This is why several screenshots came back solid black in this session even
  though `PrintWindow` reported success — it was successfully capturing the (invisible, empty)
  shadow window, not a real failure. Fix used here: resolve the real HWND once via the
  `ControlType.Window`-filtered UIA element's `NativeWindowHandle` and call `PrintWindow` against
  that handle directly rather than trusting `.MainWindowHandle` after any native menu interaction.
- **This session's on-screen real estate is shared with the Claude Code terminal window, and it
  sits on top of BrokenNes at the coordinates `GetWindowRect` reports** (`WindowFromPoint` at those
  coordinates returned the "Claude" window, not BrokenNes, even immediately after a successful
  `SetForegroundWindow` on the BrokenNes window). This means **real synthetic mouse input
  (`SetCursorPos` + `mouse_event`) silently lands on whatever window is physically topmost on
  screen, not necessarily the target app** — it worked for the native file-open dialog (which
  legitimately raises itself above everything as a modal) but not for BrokenNes's own main window.
  The reliable workaround, used successfully here for the scanline-region drag, is to bypass
  screen-coordinate input entirely and post `WM_LBUTTONDOWN`/`WM_MOUSEMOVE`/`WM_LBUTTONUP` directly
  to the specific child HWND (found via `EnumChildWindows`, class `Chrome_RenderWidgetHostHWND` for
  the WebView2 render surface) with client-relative coordinates — this is delivered via the message
  queue and is unaffected by on-screen z-order/overlap, matching why UIA's `Invoke()`-based clicks
  (also message/COM-based, not coordinate-based) were reliable all session while raw mouse
  simulation was not.
- Separately, note for future agents: **`Click-Element`'s `Invoke()` call on a menu item that opens
  a modal dialog (e.g. "Load Rom...") blocks synchronously until that dialog is dismissed** — it is
  not a hang, but a script that doesn't know to handle the now-open dialog will sit there until the
  tool-call timeout. Structure automation of any ROM/file-load flow as: fire the Invoke in one step,
  expect the call to only return after the dialog closes, and handle the dialog in between via a
  separate window (found by class `#32770` via `EnumWindows`) rather than waiting on the same call.

## Screenshots (in `UAT/screenshots/`, chronological)

`imagine-r2-01-launch.png`, `-02-mainmenu.png`, `-03-mainmenu-postwarning.png`,
`-04-openemulator.png`, `-08-realwindow.png` (recovery after the SysShadow mixup),
`-09-filedialog.png`, `-11-selected2.png`, `-12-romloaded.png`, `-13-targetbeam.png`,
`-14-aftersavestate.png`, `-16-restored.png` (post-drag, corrupted band visible),
`-17-afterretry.png` (reset), `-18-glitchharvester.png`, `-19-imaginetab.png`,
`-20-afterimaginebug.png`, `-21-final.png`.

## Summary

Area is **fully unblocked and functional**, contrary to round 1's total block. With exclusive
access, "Open Emulator" worked on the first attempt, confirming the port-42067 concurrency
diagnosis from round 1 and its prescribed workaround (run any `Open Emulator`-dependent area
sequentially/exclusively). Every control in "Target the Beam" and the deeper Glitch Harvester
"IMAGINE" tab was exercised live: ROM load, savestate-then-draw-scanline-region corruption
(with a full create → draw → visible corruption → retry → reset cycle), and actual ML model
inference via "Imagine a Bug" (model was present and loaded, ran twice, visibly corrupted the
frame both times, no crash). The only defect found is cosmetic: the predicted-bytes hex readout
in the Imagine status message renders invalid, non-hex characters instead of proper hex digits,
reproduced consistently across two separate inference runs.
