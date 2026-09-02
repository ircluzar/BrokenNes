# UAT Findings: Target the Beam — Round 2 (finishing the corruption action)

**Area:** Target the Beam — "Imagine a Bug" webmodule, the scanline-targeted CPU-instruction
corruptor.
**Build under test:** `Windows\bin\Release\net10.0-windows\win-x64\BrokenNes.Windows.exe`
**My instance PID:** 22324 (launched via `Start-BrokenNesWindows`, stopped at end of session)
**Environment:** **exclusive access** — verified before launch that no other `BrokenNes.Windows`
process was running (`Get-Process -Name BrokenNes.Windows` → empty) and that ports 42067/42068 were
free (`Get-NetTCPConnection` → "No MSFT_NetTCPConnection objects found" for both). No other instance
was launched at any point during this session.
**Continuity:** this is a follow-up to `UAT/findings/target-the-beam.md` (round 1), which reached
and enumerated this screen via a `Ctrl+1` native-menu workaround but *deliberately declined* to
click "Create Savestate" because the shared webapi port (42067) was owned by an unrelated concurrent
agent's instance at that moment, and mutating it would have hit a stranger's session. This round
had genuine exclusive ownership of the port throughout, so that concern does not apply, and the
full flow was completed.

## Summary

**The full Target the Beam corruption flow completed successfully, end to end, with no crash, hang,
or missing-model failure.** Create Savestate → draw a scanline region → targeted CPU-instruction
corruption fired and visibly corrupted the running frame → the button correctly became "Retry" →
clicking Retry correctly restored the pre-corruption frame and reset the button back to its
"Create Savestate" state. "Open Glitch Harvester" was also confirmed as a working handoff button
from this screen, landing cleanly on the full RTC/Save States/Stash/Stockpile/Imagine panel.

Also confirmed, as the task asked: **with genuine exclusive port access, "Open Emulator" worked
directly on the first click** — no `Ctrl+1` workaround needed this time. This positively confirms
round 1's port-collision diagnosis (documented in `UAT/README.md`): the WebView2→native handoff
works fine when nothing else is contending for port 42067.

Along the way, this session surfaced a separate, real **UI-Automation-only** reliability problem
with the native `Emulator ▸ Load Rom...` menu item specifically — see "Notes" below. It did not
block progress (a same-effect webapi call and, later, patience with the native dialog both worked),
but it is worth a maintainer's attention as a testability/robustness gap, and possibly hints at a
genuine WinForms responsiveness issue while the emulator is actively rendering.

## Step-by-step

### 1. Launch, Audio/Health warnings, Open Emulator (direct, no workaround), load ROM — PASS

- Launched PID 22324. First dialog encountered was **"Audio Warning"** ("Audio initialization
  failed: BadDeviceId calling waveOutOpen — The emulator will run without sound.") — this is a
  different dialog than round 1's Health Warning gate, presumably because this dev machine has no
  usable audio output device right now; harmless, dismissed via its "OK" button.
  Screenshot: `beam2-01-launch.png`.
- After dismissing Audio Warning, the WebView2 content pane briefly rendered fully black
  (screenshot `beam2-02-after-audio-ok.png`) even though UIA already reported the Health Warning
  text/button in the tree underneath — the same "WebView2 pane needs a moment before it paints"
  quirk round 1 flagged as a minor cosmetic issue. Clicking "Acknowledge health warning" via UIA
  Invoke worked immediately despite the black screen, and the Main Menu then rendered correctly
  (`beam2-03-after-health-ack.png`).
- **Clicked "Open Emulator" (accessible name; visible text "BrokenNes Emulator") once, directly —
  no `Ctrl+1` workaround.** The native MenuStrip (`Emulator, Config, Tools & Activities, SHADER,
  APU, CPU, PPU, Help`) appeared immediately and the emulator's default/no-ROM screen (an animated
  Mandelbrot-pattern placeholder framebuffer + "Continue?" prompt) rendered correctly.
  Screenshot: `beam2-04-after-open-emulator.png`. **This confirms round 1's diagnosis: the
  WebView2→native HTTP handoff (port 42067) works correctly and immediately when the calling
  instance has uncontended, exclusive ownership of the port.**
- Loaded `Data\story\page1_binty.nes`. This step took a couple of tries due to a UI-Automation-only
  wrinkle (see "Notes" below for the full story) but ultimately succeeded via the **actual native
  Open-file dialog** (typed nothing — double-clicked the file in the already-navigated `story`
  folder), confirming the real end-user file-picker flow works. Screenshot: `beam2-16-after-dialog-load.png`
  shows `page1_binty.nes` rendering correctly under the full native MenuStrip.
- **Result: PASS.** Open Emulator's direct (non-workaround) path is confirmed working under
  exclusive access, as the task asked me to verify.

### 2. Tools & Activities ▸ Target the Beam — PASS

- Opened via a real synthesized mouse click on the menu bar (see Notes for why mouse-click was used
  instead of UI Automation `Invoke` for menu navigation) and captured the open dropdown with a raw
  screen-region capture — safe and accurate here specifically *because* this session has exclusive
  access (no other instance's window can bleed into the capture, unlike the round-1 all-instances
  scenario that required `PrintWindow`). Screenshot: `beam2-17-toolsmenu-dropdown.png` shows the
  full menu: `Deck Builder, Corruption Slop, Target the Beam, Time Jump Challenge, ---, Hex Editor,
  ROM Manager, RTC + Glitch Harvester` — matches round 1's enumeration exactly, "Target the Beam"
  present and enabled.
- Clicked "Target the Beam". It opened as an overlay over the running ROM, exactly as round 1
  described: crosshair icon top-left, hamburger icon top-right, explanation text box bottom-right.
  Screenshot: `beam2-18-target-beam-open.png`.
- **Result: PASS.**

### 3. Click "Create Savestate" and run the full targeted-corruption flow — PASS (full completion, no crash/hang/missing-model failure)

This is the step round 1 explicitly stopped short of. With genuine exclusive access this round, I
proceeded through it completely:

1. **Clicked "Create Savestate"** (real mouse click on the crosshair icon). Result: a full-screen
   draw overlay appeared immediately with the prompt **"Draw a shape to target the scanlines"**,
   and the emulator's frame froze (paused) on the currently-displayed still frame. Screenshot:
   `beam2-19-after-create-savestate.png`. This matches round 1's JS-source-derived prediction
   exactly (base savestate created, emulator paused, draw overlay shown).
2. **Dragged a rectangle** on the draw canvas (a real synthesized drag: mouse-down, 10 interpolated
   move steps, mouse-up — not a single teleport — to make sure the page's pointer-move-based drag
   handler saw a real gesture) roughly across the vertical middle of the frame.
3. **The result landed immediately**: the character image visibly corrupted — clean rows near the
   top, then a band of glitched/scrambled pixel blocks and color corruption starting around the
   dragged region and continuing to the bottom of the frame (consistent with "corrupt CPU
   instructions as they draw scanlines, with effects propagating to subsequent scanlines/frames").
   Screenshots: `beam2-20-after-drag.png` and `beam2-21-settled.png` (identical two seconds apart,
   confirming the emulator holds on the corrupted frame rather than continuing to run/crash-loop).
4. **Confirmed via UI Automation** (`Get-AllNamedElements`) that the primary button's accessible
   name changed from **"Create Savestate"** to **"Retry"**, exactly as round 1's source-reading
   predicted: `Button "Retry"`, `Button "Open Glitch Harvester"` both present and enabled.
5. **Clicked "Retry"** (same coordinates, now the "Retry" icon — a circle instead of a crosshair).
   Result: the frame **immediately reverted to the clean, pre-corruption image** and the button
   icon reverted back to the crosshair ("Create Savestate") state. Screenshot:
   `beam2-22-after-retry.png`. This confirms Retry correctly reloads the base savestate and clears
   targeted mode, and that the tool is safely re-usable for another corruption attempt without
   restarting the ROM.
- **No missing-ML-model failure was encountered** — the "Imagine" model needed for byte-prediction
  loaded/ran transparently as part of step 3 with no visible error, spinner-stuck-forever state, or
  exception dialog.
- **No crash, freeze, or hang of the application itself occurred at any point in this flow.** The
  process (`Get-Process -Id 22324`) reported `Responding = True` continuously throughout, including
  immediately after the corruption fired.
- **Result: PASS — full corruption flow completed successfully.**

### 4. "Open Glitch Harvester" handoff — PASS

- From the Target the Beam overlay (after the Retry-reset in step 3), clicked the hamburger icon
  (top-right, accessible name "Open Glitch Harvester").
- Result: navigated cleanly to the full Glitch Harvester panel — tabs `RTC / SAVE STATES / STASH /
  STOCKPILE / IMAGINE`, the RTC tab's controls (Auto-Corrupt checkbox, Blast Type dropdown,
  Intensity slider, "Manual Blast"/"Let It Rip!" buttons, Memory Domains checkboxes for PRG ROM/PRG
  RAM/CHR/System RAM, Crash Behavior "Ignore Errors" dropdown) all rendered and the still-loaded ROM
  frame is visible on the left. Screenshot: `beam2-23-glitch-harvester.png`.
- **Result: PASS.** The handoff button works correctly and lands on a fully-functional destination
  screen, not a broken/blank one.

## Screenshots (chronological, `UAT/screenshots/`)

- `beam2-01-launch.png` — Audio Warning dialog on launch
- `beam2-02-after-audio-ok.png` — transient black WebView2 render (cosmetic quirk, resolves itself)
- `beam2-03-after-health-ack.png` — Main Menu, rendering correctly
- `beam2-04-after-open-emulator.png` — **Open Emulator worked on the first direct click**, no
  workaround; default placeholder framebuffer + native MenuStrip visible
- `beam2-05*/06/07/08/09` — working screenshots from the Load Rom automation difficulty (see Notes)
- `beam2-10b-alt-e-menu.png`, `beam2-11-after-downenter.png` — keyboard-mnemonic attempt (did not
  successfully drive the native menu — WebView2 likely intercepts/absorbs the accelerator; abandoned
  in favor of mouse clicks)
- `beam2-13-rom-loaded.png` — ROM loaded via direct webapi call (used only as an intermediate
  sanity check, superseded by the real dialog flow below)
- `beam2-15-dropdown-screencap.png` — **the actual native "Select a NES ROM" Open-file dialog**,
  discovered still open (already browsed to `Data\story`) from an earlier slow/queued Invoke
  attempt — raw screen capture, safe under exclusive access
- `beam2-16-after-dialog-load.png` — **clean** shot: `page1_binty.nes` loaded via the real native
  file-picker flow, full MenuStrip visible
- `beam2-17-toolsmenu-dropdown.png` — **clean** shot: Tools & Activities dropdown open, Target the
  Beam listed
- `beam2-18-target-beam-open.png` — **clean** shot: Target the Beam overlay open, crosshair +
  hamburger + explanation box all visible
- `beam2-19-after-create-savestate.png` — **clean** shot: "Draw a shape to target the scanlines"
  overlay after clicking Create Savestate
- `beam2-20-after-drag.png`, `beam2-21-settled.png` — **clean** shots: the corrupted frame after the
  targeted scanline corruption fired
- `beam2-22-after-retry.png` — **clean** shot: frame restored to clean state after clicking Retry
- `beam2-23-glitch-harvester.png` — **clean** shot: Glitch Harvester panel reached via "Open Glitch
  Harvester" handoff

## What I did not test

- Other `Blast Type`/scanline-region shapes or sizes (only one rectangular drag was tried) — the
  module's core targeted-corruption mechanism is confirmed working, but its full parameter space
  (different drag shapes, edge-of-screen regions, multiple corruptions in a row without Retry in
  between) was not exhaustively exercised.
- What happens if "Create Savestate" is clicked a second time without an intervening Retry (round
  1's source reading says it "deletes any prior base state" first, so this is presumably safe, but
  not directly observed this round).
- Whether Target the Beam behaves correctly for a player who reaches it "legitimately" via in-game
  level-16+ progression rather than the shared dev save's force-unlocked state (same caveat as round
  1 — not this area's concern to fix, just not exercised).

## Notes / things worth a maintainer's attention (found along the way)

1. **UI-Automation `Invoke()` on the native `Emulator ▸ Load Rom...` MenuItem is highly unreliable
   as an automation target while the emulator is actively rendering — likely testability-only, but
   worth understanding.** Multiple attempts to invoke this specific menu item via
   `InvokePattern.Invoke()` (the same technique that worked instantly and repeatedly for the
   `Emulator` and `Tools & Activities` top-level menus, and for WebView2 buttons throughout this
   session) either hung the calling automation client for the full tool timeout (2 minutes) with no
   dialog ever becoming detectable, or appeared to return `$menu`/`$loadRom` as `null` on immediate
   retry (as if the previous attempt had left the MenuStrip in an inconsistent/mid-transition
   state). Eventually, a **native "Select a NES ROM" Open-file dialog was found already open**
   (`beam2-15-dropdown-screencap.png`) — already navigated to the `Data\story` folder — apparently
   the delayed result of one of the earlier "hung" attempts finally completing, just far slower than
   the 2-minute tool timeout and completely invisible to `PrintWindow`-based screenshots the whole
   time (WinForms `ToolStripDropDown`/common-dialog popups are separate top-level HWNDs, not
   children of the main window, so `PrintWindow(mainHwnd, ...)` never captures them — a real gap in
   this harness's screenshot strategy for *any* native popup/dialog, not just this one). Also
   observed once: `Get-NativeMenuBar`'s `FindFirst(Descendants, ControlType=MenuBar)` matched the
   window's hidden system-menu ("System") instead of the app's own MenuStrip when both exist as
   sibling `MenuBar`-type elements — a latent fragility in the helper itself (it should pick the
   `MenuBar` with the most children, the same disambiguation trick `Get-ContentPane` already uses
   for panes). I could not conclusively separate "real WinForms responsiveness problem while a
   Chromium-accelerated WebView2 is actively repainting a NES framebuffer at 60fps" from "quirk of
   this specific automation client/COM setup" — flagging as observed-but-unconfirmed, similar to
   round 1's WebView2-repaint-lag note. **Practical workaround used:** mouse-click automation (real
   `SetCursorPos` + `mouse_event`) for all menu navigation instead of `Invoke()`, plus (since this
   session has exclusive access) plain `CopyFromScreen` region captures to see open dropdowns/popups
   that `PrintWindow` cannot show. This worked reliably every time it was tried.
2. There is also a documented `POST /api/emulator/load-rom` webapi endpoint (loads a ROM from a
   disk path directly, no dialog) which I used briefly as an intermediate sanity check while
   diagnosing the above — confirmed working (`{"success":true,"path":"...page1_binty.nes"}`) — but
   the write-up above centers on the real native-dialog flow since that is what an actual user
   drives.
3. Minor cosmetic-only repeat of round 1's note: the WebView2 content pane can render fully black
   for roughly a second right after a native dialog is dismissed, before the underlying page paints.
   Purely cosmetic; UIA-driven interaction through it works fine regardless.
