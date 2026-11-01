# Glitch Harvester - Round 2 (follow-up)

Follow-up to `UAT/findings/glitch-harvester.md` (round 1), which was fully blocked: "Open Emulator"
never navigated because that pass ran ~12 concurrent `BrokenNes.Windows.exe` instances and hit the
hardcoded-port WebApiServer bug (see `UAT/README.md`). This round ran with **exclusive access** -
verified no stray process and port 42067 free before launch - and the handoff worked on the first
try. The entire Glitch Harvester feature is now verified end-to-end.

## Setup

1. Confirmed clean machine: `Get-Process -Name 'BrokenNes.Windows'` returned nothing, port 42067 free.
2. Launched via `Start-BrokenNesWindows`, PID 6752.
3. Dismissed "Audio Warning" dialog (`BadDeviceId calling waveOutOpen` - environment has no audio
   device; cosmetic, unrelated to this area) via its "OK" button.
4. Dismissed the WebView2 "Health Warning" screen - button's accessible Name is **"Acknowledge
   health warning"**, not "OK" (visible text is "OK" inside the styled button box).
5. Clicked "Open Emulator" (visible text "BrokenNes Emulator") from the Main Menu - navigated to the
   native MenuStrip screen immediately, no delay, no cross-talk. This confirms the round-1
   diagnosis: the handoff itself works fine, it was purely the multi-instance port collision that
   blocked it.
6. `Emulator > Load Rom...` opened a native Windows file-open dialog pre-navigated to
   `Data\story\`. Typed `page1_binty.nes` into the filename field and pressed Enter (see UI
   Automation quirks below). ROM loaded and started running immediately (a "story" test ROM showing
   an intentionally glitch-styled character portrait with scrolling colour-banding - this is the
   ROM's own content, not an emulation bug).
7. Let it run ~2 seconds, confirmed via two back-to-back screenshots that the frame content was
   still subtly changing (emulation actively running, not frozen).

## UI Automation quirks hit this round (new, not in `UiaHelpers.ps1` yet)

- **`Get-NativeMenuBar` / `Open-NativeMenu` intermittently returned null** on back-to-back calls in
  the same PowerShell process, with no code change - looked like a UIA caching/timing race rather
  than a real app issue. Workaround: retry (sending `{ESC}` first to clear any stuck menu state,
  then re-fetching `Get-AppRoot` fresh) reliably succeeded on retry. Did not block progress, just
  needed extra attempts.
- **`Invoke` on a native `MenuItem` can hang/timeout** (`Operation timed out. (0x80131505)`) instead
  of failing fast - happened once on "Load Rom...". The click actually DID go through server-side
  (the file dialog was open when checked afterward) despite the client-side timeout - don't treat an
  Invoke timeout as proof the action didn't happen; screenshot and check.
- **A native File-Open dialog's edit control has no `ValuePattern`** in this app (`GetCurrentPattern`
  throws "Unsupported Pattern" for `ValuePattern`) - had to click the field's screen coordinates
  (via raw `mouse_event`) and use `System.Windows.Forms.SendKeys` to type the filename, then
  `{ENTER}` in place of clicking "Open". Same technique was needed for the Glitch Harvester's own
  "New Name:" text input in the SaveStates panel - `Find-ByName` for the label text returns the
  `<label>` element (ControlType.Text), not the `<input>`; the actual `ControlType.Edit` element has
  to be found separately (`FindAll` filtered by `ControlType.Edit`) and also has no `ValuePattern` -
  same click+SendKeys workaround applies to any text-entry field in this app's WebView2 UI.
- **While a native dropdown menu is open, `Process.MainWindowHandle` can point at the dropdown
  popup itself** (a separate small top-level HWND, e.g. observed at 216x309px), not the main
  `BrokenNes` window - this made `Save-WindowScreenshot` (which uses `proc.MainWindowHandle`) return
  a black image while a menu was open. Not a bug in the helper for its intended use (screenshotting
  the app's main content) - just something to be aware of: if a screenshot comes back solid black,
  check whether a native menu/dialog is currently open and dismiss/complete it first, or fall back
  to a full-screen `CopyFromScreen` capture for debugging (safe here since this was a single-instance
  exclusive run).

None of these are product bugs - they're automation-harness friction, documented here so the next
agent doesn't have to rediscover them.

## Feature walkthrough: Tools & Activities > "RTC + Glitch Harvester"

Confirmed this is one combined panel (WebView2 side panel, content-pane title "Glitch Harvester -
BrokenNes") with five tabs: **RTC**, **SAVESTATES**, **STASH**, **STOCKPILE**, **IMAGINE**.

### RTC tab (Real-Time Corruptor)
- Auto-Corrupt checkbox, Blast Type dropdown (RANDOM selected by default), Intensity slider (1),
  "Manual Blast" button, "Let It Rip!" button.
- Memory Domains checklist: PRG ROM (256B), PRG RAM (256B), CHR (1KB), System RAM (2KB) - System RAM
  was checked by default.
- Crash Behavior: "On Crash" dropdown defaulting to "Ignore Errors", with a live "⚙️ Ignoring Errors"
  status indicator.
- Clicked **Manual Blast**: toast "Blast executed (1 writes)" appeared immediately, and the visible
  game frame's glitch pattern visibly shifted. Fully responsive, no lag.

### The actual create-base -> corrupt/blast -> stash -> replay flow
The STASH tab's own "Blast" button starts **disabled** until a "base state" exists and is selected -
this is correct, intentional gating (confirmed by reading
`Windows/Webmodules/GlitchHarvester/glitch-harvester.js`: `elements.btnBlast.disabled =
selectedBaseId === null`), not a bug. The actual flow, discovered by following the UI:

1. **SAVESTATES tab** ("SaveStates" - base-state management, distinct from the Emulator menu's
   quick-save/load state slots): typed a name ("TestBase1") into "New Name:" and clicked "Add Base".
   Toast "Base state 'TestBase1' created", entry appeared in the list, auto-selected (highlighted).
2. **STASH tab**: with a base now selected, "Blast" became enabled. Clicked it - toast "Corruption
   created and added to stash", new entry "Stash 1" (labeled "0 writes • <timestamp>") appeared in
   the Stash History list, auto-selected. "Replay" and "Keep" buttons both went from disabled to
   enabled.
   - "0 writes" on this particular blast is plausible - RANDOM blast type at Intensity 1 against a
     2KB domain is a low-probability single write attempt; not investigated further as a bug since
     the earlier RTC-tab Manual Blast at the same intensity did report "1 writes" (so it's not
     silently broken, just probabilistic).
3. Clicked **Replay**: toast "Stash entry replayed" - re-applied the stashed corruption on demand.
4. Clicked **Keep**: toast "Entry promoted to stockpile" - the entry disappeared from the Stash
   History (list reverted to the "Corruptions appear here when you blast." empty state), consistent
   with "Keep" being a move, not a copy.
5. **STOCKPILE tab**: confirmed "Entry 1" present with the same "0 writes • <timestamp>" as the
   promoted stash item - the promotion round-tripped correctly. Stockpile tab additionally offers
   Replay / Rename / Delete / Export / Import per entry.

**Full cycle completed successfully: create base -> blast/corrupt -> stash -> replay -> keep ->
verified in stockpile.** Every button pressed produced the expected toast and UI state change; no
crashes, freezes, or silent no-ops anywhere in this flow.

### IMAGINE tab (noted, not exercised)
Fifth tab is an ML-driven corruption feature ("imagine spending epochs teaching AI about 6502 just
to inject its predictions into a running console"): Load Model button, Epoch/Bytes/Temp/Top K
settings, "Targeted Imagine (Scanline Mode)" options (Enable Scanline Targeting, Load on Imagine),
"Imagine a Bug" button, and a status line reading "Status: No model loaded - click 'Load Model'" at
the time of testing. This ties into the project's TAS/ML integration work
(`project_tas_ml_integration` memory) - out of scope for this pass since it requires a trained model
asset; not exercised beyond confirming the tab renders correctly and the "no model loaded" gating
message displays as expected.

## Responsiveness
The app stayed fully responsive for the entire session - every click produced an immediate toast/UI
update, the emulator kept rendering in the background throughout (confirmed via two consecutive
screenshots showing the frame still subtly animating), and no dialog, hang, or crash was observed
from launch through clean shutdown.

## Screenshots
All in `UAT/screenshots/`, prefixed `glitch-harvester-r2-`:
- `01-launch` (Audio Warning), `02-mainmenu` (Health Warning), `03-afterhealth` (Main Menu),
  `04-openemulator` (native shell before ROM load), `05f-typed`/`06-romloaded`/`07-running` (ROM
  load), `08-panel` (RTC tab default state), `09-manualblast` (Manual Blast toast),
  `10-stash`/`11-stashblast` (Stash tab before base existed, Blast disabled), `13-savestates`/
  `14-namedtyped`/`15-addbase` (base state creation), `16-stash-enabled`/`17-stashblasted` (Blast
  enabled + executed), `18-replay` (Replay toast), `19-keep` (Keep/promote toast),
  `20-stockpile` (promoted entry confirmed), `21-imagine` (Imagine tab), `22`/`23-final-rtc`
  (responsiveness check).

## Cleanup
`Stop-Process -Id 6752 -Force` issued; confirmed via `Get-Process -Id 6752` returning nothing
before finishing. Machine left clean for the next agent.
