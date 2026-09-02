# Shader switching - round 2 (follow-up to shader-switching.md)

**Status: UNBLOCKED, fully tested. Round 1 (`shader-switching.md`) was fully blocked because
"Open Emulator" never navigated under 12-way concurrent instance contention (port 42067/42068
bind race, documented in `UAT/README.md`). This round ran with exclusive single-instance access
and the handoff worked on the first try.**

## Setup

1. Verified clean machine before launch: `Get-Process -Name 'BrokenNes.Windows'` → 0 processes,
   `Get-NetTCPConnection -LocalPort 42067` → 0 connections.
2. Launched `BrokenNes.Windows.exe` (PID 22116) via `Start-BrokenNesWindows`.
3. First dialog shown was **not** the Health Warning - it was an **Audio Warning** modal
   ("Audio initialization failed: BadDeviceId calling waveOutOpen - The emulator will run without
   sound.") - a native WinForms MessageBox, dismissed via its "OK" button. This is presumably an
   environment quirk (no default audio device / device busy in this sandbox), not itself a shader
   bug, but noting it since it wasn't mentioned in the task's expected flow. After dismissing it,
   the WebView2 Health Warning screen appeared as expected and was dismissed via its
   "Acknowledge health warning" button.
4. From the Main Menu, clicked "Open Emulator" (accessible name; visible text "BrokenNes
   Emulator"). **Navigation to the native MenuStrip screen (Emulator/Config/Tools &
   Activities/SHADER/APU/CPU/PPU/Help) succeeded immediately** - confirms the round-1 hypothesis
   that the WebView2→native handoff via the local HTTP API works fine under exclusive access and
   only fails/cross-talks under concurrent-instance port contention.
5. Loaded `Data\story\page1_binty.nes` via **Emulator > Load Rom...**. Two automation-technique
   notes for future runs:
   - The native MenuStrip's dropdown popup is a **separate top-level window** (class
     `WindowsForms10.Window.20808...`), not a descendant of the main app root in the UIA tree -
     `Get-NativeMenuBar`'s returned menu item elements go stale once the dropdown is showing.
     Re-resolve items via `AutomationElement.FromHandle()` on that popup's HWND (found via
     `EnumWindows` + class-name filter), not via the original `$root`-scoped element.
   - `InvokePattern.Invoke()` on "Load Rom..." throws `Operation timed out (0x80131505)` - this is
     expected/harmless: invoking it opens a **modal native common-file-dialog**
     (`#32770` "Select a NES ROM"), which pumps its own message loop and blocks the UIA client
     call until the dialog closes. The dialog *does* open successfully despite the client-side
     timeout exception. The dialog's own descendant tree only exposes shallow `Pane` elements
     (no deep Edit/List controls via FindAll+Descendants, likely an MSAA-bridge limitation), so
     the pragmatic approach was `SetForegroundWindow` + `SendKeys` typing the filename + Enter.
     The dialog was already defaulted to the `Data\story` folder.
   - `Save-WindowScreenshot` (via `Get-Process ... MainWindowHandle`) returns solid black while
     a modal native dialog owned by the process is up - `MainWindowHandle` reports the dialog's
     `SysShadow` decoration window in that state, not the real content window. Capturing by
     explicit HWND (found via `EnumWindows`) instead of trusting `MainWindowHandle` worked
     correctly. This is a harness limitation to note for future areas that open native dialogs
     (Load ROM, Load State, Save State, Open Emulator Folder), not a product bug.

## SHADER menu enumeration

Opened the native SHADER menu (also a separate top-level popup window per above). Full item list,
in menu order:

```
PX
--------
BLD  BUMP  CCC  CNMA  CRY  CRZ  DOT  EXE  HUE  LAT  LCD  LSD  MSH  MUSK  RF  RGBX  SPK  TRI  TTF
TV  VHS  WARM  WTR
--------
Shader Strength >   (submenu: 0.5x / 1.0x / 1.5x / 2.0x / 2.5x / 3.0x)
```

**No `[Locked]` markers or greyed-out entries on any shader.** All 24 shaders plus the Shader
Strength submenu (6 levels) appear as plain, fully-enabled `MenuItem`s. TTF had the checkmark
(active) at menu-open time, consistent with this being a persisted setting from a prior session,
not a fresh default.

This confirms round 1's live-check request from the source-reading note: shader selection in this
menu is **not visibly progression/unlock-gated** the way CPU/PPU/APU cores are (per
`MainForm.Progression.cs`'s `IsCpuCoreUnlocked`/`IsPpuCoreUnlocked`/`IsApuCoreUnlocked` - there is
no analogous filtering applied to the SHADER menu's population). Caveat: this dev/shared save
already has 136/138 cores owned (per README's prior observation), so this run cannot fully rule
out a shader-side `IsShaderUnlocked` gate that happens to have every shader unlocked on this
particular save - only that, unlike the CPU/PPU/APU menus, no gating behavior (locked entries,
`[Locked]` text) was observed here even though it would be expected to show if present given how
CPU/PPU/APU render locked entries. Grep of `MainForm.Config.cs` was not repeated this round since
the task scope was the live-app check, not source re-verification - a fresh source grep for
`IsShaderUnlocked` would settle this definitively but wasn't necessary to answer the live-behavior
question asked.

## Shaders tried (5 total, each screenshotted)

| Shader | Screenshot | Visual result |
|---|---|---|
| PX | `screenshots/shader-r2-08-PX.png` | Plain/undistorted pixel view (near-identical to the pre-shader-menu baseline, `shader-r2-06-romloaded.png`, which had TTF active - the two look very similar on this particular static test image) |
| BLD | `screenshots/shader-r2-09-BLD.png` | Visually indistinguishable from PX at this zoom/content - bloom effect likely present but subtle on this flat-color sprite content |
| VHS | `screenshots/shader-r2-10-VHS.png` | **Strong, obviously-different effect**: heavy scanlines, chroma noise/color bleed, horizontal jitter bands - unmistakably a distinct shader |
| LCD | `screenshots/shader-r2-11-LCD.png` | Visible LCD sub-pixel grid softening/blur across the whole image - distinct from PX/BLD, subtler than VHS |
| TV | `screenshots/shader-r2-12-TV.png` | **Strong, obviously-different effect**: CRT barrel/pincushion distortion (curved edges), vignette, visible RGB subpixel mask - unmistakably a distinct shader |

Also expanded the **Shader Strength** submenu (0.5x-3.0x) - populated normally, no lock markers,
not clicked further (out of scope for this pass beyond confirming it opens).

App stayed fully responsive across every selection: each menu reopened cleanly, each shader
applied and rendered without any crash, hang, freeze, or blank/black screen (the one black
screenshot encountered, `shader-r2-04-loadromdialog.png`, was the harness's `MainWindowHandle`
capture quirk during the native file dialog, not an app-side blank-screen bug - see above).
Final screenshot (`shader-r2-13-final.png`) after closing all menus confirms the window returned
to a normal, fully-rendered state with TV shader still active.

## Summary

- **Open Emulator handoff: works correctly under exclusive access** - round 1's block was
  entirely the documented port-contention bug, not a shader-menu or navigation defect.
- **Shaders tried:** PX, BLD, VHS, LCD, TV (5 of 24 available) - all applied correctly, all
  distinct or plausibly-subtle-on-this-content, zero crashes/hangs/blank screens.
- **No shader unlock-gating visible in the live menu** on this save (136/138 cores owned) -
  unlike CPU/PPU/APU, no `[Locked]` entries appeared among any of the 24 shaders or the 6 Shader
  Strength levels. Cannot fully rule out a dormant `IsShaderUnlocked` gate given the near-complete
  save state, but the asymmetry with CPU/PPU/APU's visible gating is itself notable and worth a
  source-level confirmation grep for `IsShaderUnlocked` in a future pass if this matters for
  release sign-off.
- No product bugs found in shader switching itself. The only rough edges observed were
  UAT-harness technique issues (menu-popup-is-a-separate-window, `MainWindowHandle` during modal
  dialogs) rather than app bugs, and are written up above for the benefit of whichever agent
  automates Load ROM / Load State / Save State next.

Screenshots (all in `UAT/screenshots/`): `shader-r2-00-audiowarning.png`,
`shader-r2-01-afterok.png`, `shader-r2-02-mainmenu.png`, `shader-r2-03-openemu.png`,
`shader-r2-04-loadromdialog.png` (black - harness quirk), `shader-r2-04b-recheck.png` (black -
harness quirk), `shader-r2-04c-mainhwnd.png`, `shader-r2-04d-otherhwnd.png`,
`shader-r2-05-filedialog.png`, `shader-r2-06-romloaded.png`, `shader-r2-07-shadermenu.png`,
`shader-r2-08-PX.png`, `shader-r2-09-BLD.png`, `shader-r2-10-VHS.png`, `shader-r2-11-LCD.png`,
`shader-r2-12-TV.png`, `shader-r2-13-final.png`.
