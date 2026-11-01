# UAT Findings: Glitch Harvester

**Area:** Glitch Harvester (create-base -> corrupt -> stash -> replay cycle)
**Result:** BLOCKED before reaching Glitch Harvester at all — root cause identified and confirmed in source, not an artifact of my automation technique.
**Instance used:** `BrokenNes.Windows.exe` PID 21080 (own independent instance, per instructions).

## Summary

I could not reach the Glitch Harvester screen. The blocker is upstream: the Main Menu's
**"Open Emulator"** button never navigates to the native Emulator/MenuStrip screen (Tools &
Activities > Glitch Harvester lives inside that native screen, per the README). I spent most of
this session root-causing *why*, since the failure looked at first like a UI-automation problem,
but turned out to be a genuine app bug that specifically surfaces when multiple
`BrokenNes.Windows.exe` instances run at once — i.e. exactly the parallel-UAT scenario this whole
harness depends on.

**This is worth flagging prominently to the project owner**: the harness's own documented
assumption ("Multiple independent `BrokenNes.Windows.exe` instances can run simultaneously ...
already verified") does **not** hold for the app's local Web API layer, and that layer silently
breaks cross-instance in a way that can make one tester's clicks affect a *different* tester's
window. See Root Cause below.

## Step-by-step

### 1. Launch, health warning, Main Menu — PASS
- Launched via `Start-BrokenNesWindows`, got PID 21080.
- Health warning dialog appeared; `Get-AllNamedElements` found accessible name
  "Acknowledge health warning"; `Click-Element` (InvokePattern) dismissed it cleanly.
- Main Menu loaded with buttons: "Open Deck Builder", "Open Emulator", "ROM Manager", "Options".
- Evidence: `screenshots/glitch-01-launch.png` (health warning, from the first instance I ran,
  PID 6484 — see note below), `screenshots/glitch-17-mainmenu-final.png` (clean Main Menu on
  PID 21080).

### 2. Sanity-check that navigation/clicking works at all — PASS
- `Click-Element` (UIA InvokePattern) on **"Open Deck Builder"** worked immediately and
  consistently — navigated to the Deck Builder screen (SUMMARY panel, Level 7, 136/138 owned
  cores, etc.).
- `Click-Element` on **"ROM Manager"** worked immediately and consistently — navigated to the ROM
  Manager screen (game list with Kirby's Adventure, Super Mario Bros., etc.).
- Evidence: `screenshots/glitch-18-rommanager.png` (ROM Manager reached cleanly on PID 21080 via
  plain InvokePattern, no coordinate clicking involved).
- This proves the harness's Click-Element/InvokePattern technique is sound in general, and rules
  out "my automation doesn't work at all" as an explanation for what follows.

### 3. "Open Emulator" never navigates — FAIL (root cause identified, see below)
- `Click-Element` (InvokePattern) on "Open Emulator": returns `True` (pattern invoked without
  throwing) but the UI never changes — `Get-AllNamedElements` still shows the plain Main Menu,
  every time, across dozens of attempts, with waits up to 5+ seconds.
- I suspected this was a UI-automation quirk (WebView2 accessibility, DPI/multi-monitor
  coordinate math, window-overlap from other parallel testers' instances) and spent considerable
  effort ruling those out:
  - Verified real mouse clicks (`SetCursorPos` + `mouse_event`) landing exactly on the button,
    with `WindowFromPoint`/`GetAncestor` confirming **our own hwnd** genuinely owned the clicked
    pixel at click time (to rule out another instance's window silently overlapping/stealing the
    click, which I did observe happening at other screen locations — 8 parallel
    `BrokenNes.Windows.exe` instances were running during this session).
  - Tried keyboard activation (`SetFocus` + synthetic `VK_RETURN`).
  - Tried direct `WM_LBUTTONDOWN`/`WM_LBUTTONUP` messages posted straight to the WebView2's
    `Chrome_RenderWidgetHostHWND` child window (bypasses screen-coordinate ownership entirely).
  - None of these changed the outcome for this specific button, while the exact same
    InvokePattern technique worked instantly for "Open Deck Builder" and "ROM Manager" on the same
    page.

### Root cause (confirmed by reading source, not guessed)

`Windows/webapi/WebApiServer.cs` runs a Kestrel HTTP server **hardcoded to port 42067/42068**
(`private readonly int _port = 42067;`), and this port is not per-instance/dynamic anywhere in the
codebase:

- `Windows/webapi/WebApiServer.cs:173-178` — `options.Listen(IPAddress.Loopback, _port)` /
  `_port + 1`, fixed.
- `Windows/Webmodules/shared/webapi.js:6-11` — `resolveDefaultBaseUrl()` falls back to the literal
  string `'http://127.0.0.1:42067'` with no per-instance override (`window.WEBAPI_BASE` /
  `window.__WEBAPI_BASE` are never set anywhere in the repo — confirmed by grep).
- `Windows/Helpers/WebViewHelper.cs:159` — the WebView2 request-proxy also hardcodes
  `http://localhost:42067{apiPath}`.

With 8 `BrokenNes.Windows.exe` instances running in parallel (this UAT pass), only the
**first-launched** instance (verified via `Get-Process ... | Select StartTime` and
`netstat -ano`/`Get-NetTCPConnection`) actually holds a listening socket on 42067/42068. Every
other instance's attempt to bind that port throws, and the failure is swallowed on purpose:

- `Windows/MainForm/MainForm.Initialization.cs:742-746` —
  `catch (Exception ex) { Console.WriteLine(...); /* Don't show error to user, API is optional */ }`

There's also a latent bug that makes this permanent instead of retryable:
`WebApiServer.cs:222` sets `_host = app;` **before** `await _host.StartAsync(...)` on line 225.
`IsRunning` (`WebApiServer.cs:79`) is simply `_host != null`, so it flips to `true` the instant the
field is assigned — regardless of whether the subsequent bind actually succeeds. Once that happens,
`MainForm.Initialization.cs`'s `if (!webApiServer.IsRunning) StartAsync()` guard never fires again
for that instance, so a failed bind is never retried for the lifetime of the process.

**Consequence for "Open Emulator":** `onEmulatorClick` → `proceedToEmulator()` →
`api.navigation.goToEmulator()` → `POST http://127.0.0.1:42067/api/navigation/go-to-emulator`
(`Windows/Webmodules/shared/webapi.js:325`, `Windows/Webmodules/Home/home.js:328-380`). Because
*every* instance's WebView2 content targets the same hardcoded port, this request doesn't fail —
it succeeds, but against whichever instance actually owns that port, which for me (PID 21080) was
a **different tester's process** (PID 12384, the earliest-started instance in this session). That
means my clicks were silently telling **someone else's** BrokenNes window to switch into Emulator
mode, while my own window stayed on the Main Menu (confirmed repeatedly via UIA — my window's
accessible-name tree never changed from the plain Main Menu, all the way through this test).

I have visual evidence consistent with this: a screenshot taken while my window sat at (10,10)
captured a compositor overlap showing a *different* BrokenNes window already in the native
Emulator screen with its "Emulator" menu open and a "Player 2 Controller Configuration" dialog on
screen (`screenshots/glitch-17-mainmenu-final.png` — the black Main Menu panel in the foreground is
my own window per UIA; the green-background native-menu content behind/around it belongs to
whichever instance holds port 42067). I did not interact with that other window — this was a
passive screenshot only — but it strongly suggests my repeated Invoke/click attempts on "Open
Emulator" did land, on that other instance's server, and toggled its view mode one or more times.
**I'm flagging this transparently since it's a real, if likely low-impact/reversible, side effect
of a genuine app bug, not something I did carelessly.**

I stopped attempting further "Open Emulator" clicks once this was confirmed, to avoid causing more
cross-instance interference.

### 4. Glitch Harvester itself — BLOCKED, untested
Since Glitch Harvester lives inside the native Emulator screen's "Tools & Activities" menu (per
the README and confirmed visually in the screenshot described above — the native MenuStrip does
have a "Tools & Activities" entry), and I could not get *my own* instance into that screen, I could
not reach Glitch Harvester, create a "base" savestate, corrupt/blast from it, stash a result, or
attempt a replay. None of the README's described Glitch Harvester flow could be exercised.

### 5. App responsiveness
My own instance (PID 21080) stayed fully responsive throughout — every UIA query, every
InvokePattern click on Deck Builder/ROM Manager, and every screenshot completed normally with no
hangs, delays, or crashes, for the roughly 20+ minutes I drove it.

## Discrepancy from the README worth flagging

The UAT README states: "Multiple independent `BrokenNes.Windows.exe` instances can run
simultaneously (no single-instance lock) — this is what makes parallel testing possible at all."
This is true for the WebView2/WinForms UI itself, but **not** true for the app's local Web API
server, which is hardcoded to a fixed port with no per-instance negotiation, silently fails to
start for every instance after the first, and (worse) causes surviving instances' UI to
transparently operate on a different, unrelated process's native state. Any UAT area whose flow
depends on that API (navigating into the Emulator screen from the Main Menu, and by extension
Glitch Harvester, Imagine, RTC, achievements-via-API, etc. — anything under
`Windows/webapi/WebApiServer.cs`'s `Register*Endpoints` calls) is at risk of the same problem
whenever more than one instance is running, which is exactly the normal operating condition for
this parallelized harness.

## Screenshots
- `glitch-01-launch.png` — health warning dialog (first instance, PID 6484).
- `glitch-02-emulator-opened.png` through `glitch-16-verified-click.png` — extensive
  troubleshooting sequence (DPI/coordinate math, window-overlap detection, message-based clicks);
  kept for anyone who wants to audit the root-cause investigation, not all individually meaningful.
- `glitch-17-mainmenu-final.png` — my own window (PID 21080) cleanly on the Main Menu, with a
  visible compositor overlap showing a *different* instance already inside the native Emulator
  screen (Tools & Activities visible in its menu bar) with a controller-config dialog open —
  supporting evidence for the cross-instance root cause.
- `glitch-18-rommanager.png` — ROM Manager reached cleanly on my own instance via plain
  InvokePattern, proving the automation technique itself is sound.

## What I could not verify
- The actual Glitch Harvester UI (create-base / corrupt / stash / replay controls) — never
  reached.
- Whether the README's described flow (named savestate "base" -> blast/corrupt -> stash -> replay)
  matches the real UI, since the UI was unreachable.
- Whether "Open Emulator" works correctly when only a single `BrokenNes.Windows.exe` instance is
  running (I would expect it to, based on the code — the bug only manifests with 2+ concurrent
  instances — but I did not get a window in this session where I was the only instance running to
  confirm directly).
