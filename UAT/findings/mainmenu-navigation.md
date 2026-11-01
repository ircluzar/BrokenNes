# UAT Findings: Boot + Main Menu Navigation

**Area:** boot + main menu navigation
**Instance PID used:** 19944 (own independent `BrokenNes.Windows.exe` instance, launched via `Start-BrokenNesWindows`)
**Date:** 2026-08-29

## Summary

Boot, health-warning acknowledgment, and 3 of the 4 main-menu destinations (Deck Builder, ROM
Manager, Options) all opened cleanly, round-tripped back to the main menu correctly, and never
crashed or hung. The 4th destination, "Open Emulator" (visible text "BrokenNes Emulator"), could
not be positively verified end-to-end in this session — not because the button/app is broken, but
because of a confirmed, root-caused environmental collision from running 10 parallel
`BrokenNes.Windows.exe` instances at once (this heavily-parallelized UAT pass): the app's
WebView2-to-native-UI handoff depends on a same-machine loopback API server hardcoded to a fixed
port (`127.0.0.1:42067`), and only one of the 10 running instances can ever bind that port. See
Step 4 for full detail — this is a real, source-confirmed finding, not a shrug.

Also worth flagging up front for whoever reads UAT results across all parallel areas this run:
that same fixed-port design underlies most of this app's WebView2<->native-code plumbing (progression
reads/writes, navigation, core/shader selection, ROM loading, etc.), so **other parallel agents'
areas that exercise those API-backed actions may be silently cross-talking with instance PID 12384**
(the instance that happens to hold port 42067 in this run), not their own instance. This is worth
the project owner's attention independent of this specific findings file.

## Step-by-step results

### Step 1: Launch + Health Warning modal
**PASS.** Launched via `Start-BrokenNesWindows`, got PID 19944. `Get-AllNamedElements` on the
initial content pane immediately showed the Health Warning modal text and a button with accessible
Name **"Acknowledge health warning"** (visible text is "OK" — another visible-text-vs-accessible-Name
mismatch beyond the ones already documented in `UiaHelpers.ps1`). Clicked it via `Click-Element`
(`InvokePattern`), succeeded.
- Screenshot: `screenshots/mainmenu-01-healthwarning.png`

### Step 2: Main menu enumeration
**PASS.** `Get-AllNamedElements` on the main menu returned exactly the accessible names named in
the task: `Open Deck Builder`, `Open Emulator`, `ROM Manager`, `Options`, and hyperlink
`Visit Redscientist Labs website` (not clicked, per instructions). Visible text again differs from
accessible Name for two of them (visible "Deck Builder" → accessible "Open Deck Builder"; visible
"BrokenNes Emulator" → accessible "Open Emulator").
- Screenshot: `screenshots/mainmenu-02-mainmenu.png`

### Step 3: Open Deck Builder → screenshot → back to Main Menu
**PASS**, with one nested-navigation wrinkle worth documenting. Clicking "Open Deck Builder" lands
on a **Deck Builder Summary** sub-screen first (Owned Cores 136/138, Achievement stars 8, Level 7,
with hyperlinks "Continue Deck Builder" / "Watch Story Again" / "View Unlocked Cores" / "RETURN").
After a short delay it auto-advances to the full **Build Console** screen (CPU/PPU/APU/SHADER
selection + Cartridges list + START/RESET GAME + controller ports). The Build Console's "RETURN"
goes back to the Summary screen, not straight to Main Menu; the Summary screen's own "RETURN" is
what actually returns to Main Menu. Both screens re-use the same outer WebView2 pane Name
("(DECK BUILDER|Deck Builder) - BrokenNes" case varies), so `Get-ContentPane`/`Get-AllNamedElements`
correctly reflect live state each time — the only gotcha was needing 2 RETURN clicks, not 1, and
figuring that out required actually reading the enumerated names at each step rather than assuming.
App stayed responsive and `Get-Process -Id $myPid` resolved throughout. Confirmed back at Main Menu
by final enumeration (buttons `Open Deck Builder`/`Open Emulator`/`ROM Manager`/`Options` present).
- Screenshots: `screenshots/mainmenu-03d-deckbuilder-forced.png` (Build Console),
  `screenshots/mainmenu-04-back-to-menu.png` (mid-transition Loading/new-round state, an artifact of
  my first RETURN click landing on the Build Console's own RETURN),
  `screenshots/mainmenu-06-back-at-mainmenu.png` (confirmed back at Main Menu)

### Important harness-level discovery (not an app bug): screenshot cross-contamination between parallel instances
`Save-WindowScreenshot`'s `CopyFromScreen` grabs whatever is on screen at the target window's
rectangle **coordinates**, not that window's actual content — it does not force the window to be
topmost first. With 10 parallel `BrokenNes.Windows.exe` instances open simultaneously (confirmed via
`Get-Process -Name BrokenNes.Windows`, which listed 10 PIDs, several with window handles allocated
within moments of each other, i.e. likely near-identical default spawn position), a screenshot taken
without first forcing the target window to the foreground can silently capture a **different
instance's window** if it happens to be on top at that screen location. I hit this directly:
`Get-AllNamedElements` (PID-scoped, unaffected by Z-order) reported "DECK BUILDER" content, while
the very next `Save-WindowScreenshot` call for the same PID captured what turned out to be the
Main Menu or Options screen of an overlapping window instead. Fix used for the rest of this run:
call `BringWindowToTop` + `SetForegroundWindow` on my own PID's `MainWindowHandle` immediately
before every `Save-WindowScreenshot` call, in the same PowerShell invocation (PowerShell tool
sessions don't persist variables between calls). Flagging this for whoever consolidates all parallel
areas' findings — any screenshot evidence from this heavily-parallel run that didn't explicitly
force-foreground first should be treated as unverified.

### Step 4a: ROM Manager → screenshot → back to Main Menu
**PASS.** Opened cleanly on first click, showed the RetroAchievements-compatible game list (Kirby's
Adventure, Super Mario Bros., Mega Man 2, etc., all "Not imported yet" — consistent with
`UAT/README.md`'s note that this needs real user-imported ROMs). "RETURN" hyperlink took it straight
back to Main Menu in one click. App responsive throughout.
- Screenshots: `screenshots/mainmenu-07-rommanager.png`, `screenshots/mainmenu-08-back-from-rommanager.png`

### Step 4b: Options → screenshot → back to Main Menu
**PASS.** Opened cleanly on first click (DeckBuilder Save: Clear the save / Unlock Everything / Open
Credits; Input configuration: Controller 1 / Controller 2). "RETURN" hyperlink took it straight back
to Main Menu in one click. App responsive throughout.
- Screenshots: `screenshots/mainmenu-09-options.png`, `screenshots/mainmenu-10-back-from-options.png`

### Step 4c: Open Emulator
**BLOCKED** (root cause identified, not a generic "couldn't test"). Clicking the "Open Emulator"
button (`Click-Element` InvokePattern — 2 attempts — and a genuine OS-level `SetCursorPos` +
`mouse_event` click directly on the button's screen coordinates, after force-foregrounding the
window) never changed my instance's UI: it stayed on the Main Menu every time, across waits up to
~11 seconds total, with the process confirmed alive and `Responding: True` throughout (CPU time was
actively increasing, ruling out a hang). No native MenuStrip ever appeared for PID 19944 (`Get-NativeMenuBar`
only ever found the OS-default "System Menu Bar" with a single "System" item — not the app's own
Emulator/Config/Tools & Activities/SHADER/APU/CPU/PPU/Help bar). `EnumWindows` confirmed no second
top-level window was created either, so this genuinely never transitioned.

Root cause, confirmed via source + live process inspection:
- `Windows/Webmodules/Home/home.js`'s `onEmulatorClick` → `proceedToEmulator()` calls
  `api.navigation.goToEmulator()`, which POSTs to a **hardcoded** `http://127.0.0.1:42067/api/navigation/go-to-emulator`
  (`Windows/Webmodules/shared/webapi.js` line 11).
- `Windows/webapi/WebApiServer.cs` line 29 hardcodes `_port = 42067` (and `42068` for HTTPS) with no
  per-instance uniqueness.
- `Windows/MainForm/MainForm.Initialization.cs`'s `EnsureWebApiServerRunningAsync()` wraps
  `webApiServer.StartAsync()` in a try/catch that explicitly swallows any bind failure: `catch
  (Exception ex) { Console.WriteLine(...); // Don't show error to user, API is optional }` — so a
  losing instance never crashes or shows any visible error, it just silently has no working local API.
- Live confirmation: `netstat -ano` showed port 42067/42068 LISTENING under PID **12384**, not my
  own PID 19944 — and a `netstat` filter for PID 19944 showed **zero** listening ports at all (no
  fallback port either). So my instance's own backend never started, and my WebView2 frontend's
  fixed-URL fetch to port 42067 — if it landed anywhere at all — would have hit **PID 12384's**
  backend instead of my own, telling a completely different window (not mine) to switch modes.
  PID 12384 was independently confirmed still running afterward (a plain `Get-Process -Id 12384`
  existence check only, no window interaction) — I did not screenshot or otherwise inspect it, since
  it belongs to a different parallel test session, but flagging here in case my repeated clicks
  caused a visible mode-switch side effect on that other agent's window.
- By contrast, Deck Builder / ROM Manager / Options all navigate via plain client-side
  `window.location.href` changes inside the same already-loaded WebView2 page (confirmed in
  `home.js`'s `onOptionsClick`/`onRomManagerClick`) — no dependency on the loopback API — which is
  exactly why those three worked reliably in this same parallel environment while only the one
  destination requiring an actual native-UI handoff did not.
- Screenshots showing the click had no effect: `screenshots/mainmenu-13-openemulator-check.png`,
  `screenshots/mainmenu-14-openemulator-longwait.png`, `screenshots/mainmenu-15-openemulator-realclick.png`

**This is a legitimate design concern beyond just "blocked my test":** the app has no single-instance
lock (confirmed, and by design per `UAT/README.md`), so nothing stops a real user from launching two
copies of `BrokenNes.Windows.exe` — at which point the second copy's "Open Emulator" (and likely
several other API-backed actions — core/shader selection, ROM loading via the API path, save-state
quick-save/load, progression claims) would silently do nothing or, worse, silently act on the FIRST
copy's window instead, with zero visible error. Recommend either a per-instance dynamic port (e.g.
`0` to let the OS assign one, communicated to the WebView2 side via a query string/injected
variable at startup) or a single-instance lock, whichever fits the product's intent.

## What I could not verify

- Whether "Open Emulator" actually works correctly end-to-end (reaches the native
  Emulator/Config/Tools & Activities/SHADER/APU/CPU/PPU/Help MenuStrip screen without crashing) —
  blocked specifically by the port collision above, which is an artifact of this session running
  10 parallel instances simultaneously, not something intrinsic to a normal single-instance launch.
  A clean re-test with no other `BrokenNes.Windows.exe` instances running (or with the port made
  configurable per-instance) would very likely resolve this and is the natural follow-up.
- Whether my own attempted clicks caused a visible side effect on PID 12384's window (the instance
  that owns the shared port) — I deliberately did not inspect that window further to avoid
  interfering with whatever other parallel agent owns that session.

## Cleanup

Stopped own instance (PID 19944) at the end of the run via
`Stop-Process -Id 19944 -Force -ErrorAction SilentlyContinue`. Did not touch any other PID's process
or window at any point (only a read-only `Get-Process -Id 12384` existence check, no UI interaction).
