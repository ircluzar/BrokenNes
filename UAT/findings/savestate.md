# UAT Findings: Save State / Load State Round Trip

**Area:** Windows desktop app, "Open Emulator" direct-play screen, `Emulator > Save State...` / `Emulator > Load State...`
**Date:** 2026-08-29
**ROM used:** `Data\story\page1_binty.nes`

## Summary

Save State and Load State both work correctly on the native "Open Emulator" screen: Save State
writes a real state file to disk, and Load State reloads it without crashing, freezing, or
black-screening. The round trip **PASSES**.

Getting to that screen at all, however, was blocked for a long time by an environmental issue
unrelated to save/load itself: this heavily-parallelized UAT run had up to 10
`BrokenNes.Windows.exe` instances running simultaneously, and **the app's local WebView2↔backend
API bridge (Kestrel, hardcoded to loopback port 42067/42068 in `Windows/webapi/WebApiServer.cs`)
can only be bound by one instance at a time.** Any instance that loses that startup race never
gets a working API bridge, and every API-dependent action in that instance — including the
"Open Emulator" button's mode-switch — silently no-ops forever (the failure is caught and treated
as "expected" by client JS, so there's no visible error). This is a real, reproducible,
concurrency-specific bug, documented in detail below, that likely also explains flaky/blocked
results in other parallel UAT areas that depend on the same bridge (achievements, core-switching
from the WebView2 side, progression, etc.). It is **not** a save/load-specific bug and would not
affect a normal single-instance user session.

## Step-by-step

1. **Launch, health warning, Open Emulator** — PASS (after working around the port issue, see
   below). `Start-BrokenNesWindows` launched PID 3012 first; `Acknowledge health warning` and
   subsequent Main Menu navigation (`Options`, `ROM Manager`, `RETURN`) all worked cleanly via
   UI Automation Invoke. Screenshots: `savestate-01-launch.png`, `savestate-02-mainmenu.png`.

2. **Clicking "Open Emulator" (button's real accessible name) did nothing, repeatedly** —
   investigated at length (see "Root cause" below) before determining this was PID 3012 having
   lost the fixed-port race, not an automation problem. Confirmed via a genuine, ownership-verified
   hardware-level mouse click (not just UI Automation Invoke) that even triggered the correct CSS
   hover state on the button — so the click was reaching the app; the app's own async mode-switch
   handler just never completed, because it depends on an HTTP call to `127.0.0.1:42067` that goes
   nowhere for a losing instance. Confirmed no listener on 42067/42068 for ANY BrokenNes process at
   that time (`Get-NetTCPConnection`), and confirmed a direct `Invoke-WebRequest` to that port was
   actively refused. Screenshots documenting this dead end: `savestate-19` through `savestate-36`.

3. **Root cause confirmed and worked around** — killed PID 3012, waited until port 42067 was
   observed free, then launched a fresh instance (PID 21696). `Get-NetTCPConnection -LocalPort
   42067,42068` immediately showed PID 21696 owning both ports. This fresh instance launched
   **directly into the native Emulator screen** (Emulator/Config/Tools & Activities/SHADER/APU/
   CPU/PPU/Help MenuStrip) — apparently a "last used mode" setting had already been left in
   Emulator mode by a previous successful session (possibly the instance that had originally won
   the port race). Screenshot: `savestate-40-fresh-launch.png`.

4. **Emulator > Load Rom...** — PASS. Native "Select a NES ROM" file dialog opened, already
   browsing `Data\story` (the folder containing `page1_binty.nes`). Double-clicked
   `page1_binty.nes`; it loaded and rendered a story/cutscene frame (a boy holding a game
   controller) within ~2 seconds, no errors. Screenshots: `savestate-41-loadrom-dialog.png`,
   `savestate-43-after-loadrom-click.png`, `savestate-44-rom-loaded.png`.

5. **Let it run** — the loaded scene is a static story/title frame; it did not visibly animate
   over 5+ seconds, and a broad set of keyboard inputs (Enter, Space, Z, X, A, S, Shift, Down,
   '1') sent to the focused, foreground window did not advance it either. This may be a story-page
   ROM that pauses on a static frame by design, or it may need a specific input this session did
   not discover. Noted as a limitation, not treated as a failure of save/load. Screenshot:
   `savestate-45-running-5s.png` / `savestate-46-after-input.png` (both identical to
   `savestate-44`).

6. **Emulator > Save State...** — PASS. Native "Save Save State" dialog opened, defaulting to
   `Data\story\page1_binty.png` (type filter "State Images (*.png)"), in the same folder as other
   pre-existing story-character PNGs (`binty.png`, `jimmy.png`, `skully.png`, `sloppy.png`).
   Accepted the default name and clicked Save; the dialog closed cleanly with no error, and a
   directory re-listing on the subsequent Load dialog confirmed `page1_binty.png` now existed at
   47 KB, timestamped to the moment of the save. Screenshots: `savestate-47-savestate-dialog.png`,
   `savestate-48-after-save.png`.

7. **Observable change** — switched the active PPU core via the native `PPU` menu from the
   default to `CUBE` (a deliberately glitchy/gimmick core per this project's existing notes on PPU
   core personalities). A new row of small green glitch squares appeared along the top edge of the
   frame after the switch. This is real evidence the core-switch command had an effect, but it is
   **weak evidence of a "state" change per se**: switching back to the original `FMC` core
   afterward did **not** make the glitch squares disappear (`savestate-52-back-to-fmc.png` is
   pixel-identical to `savestate-49-after-core-switch.png`), which strongly suggests the display
   was not actively re-rendering new frames at all (consistent with the static, unresponsive-to-
   input scene from step 5) and the glitch is a leftover artifact from a stale frame buffer rather
   than a live per-core rendering difference. Given the ROM's static screen, this was the best
   observable-change proxy reachable in this session; it does not by itself prove Load State
   reverts emulation RAM/registers, only that the load-then-restore sequence is crash-safe (see
   next step). Screenshot: `savestate-49-after-core-switch.png`.

8. **Emulator > Load State...** — PASS. Native "Load Save State" dialog opened in `Data\story`,
   showing `page1_binty.png` (47 KB, correct save timestamp) alongside the other story PNGs.
   Double-clicked it; the dialog closed, and the emulator screen continued displaying the same
   story frame with **no crash, no black screen, and no freeze** — the window remained fully
   responsive to further menu interaction afterward (PPU core was switched again post-load without
   issue). Screenshots: `savestate-50-loadstate-dialog.png`, `savestate-51-after-loadstate.png`.

## PASS/FAIL/BLOCKED summary

| Step | Result |
|---|---|
| Launch, health warning, Open Emulator, Load ROM | PASS (after workaround — see Root cause) |
| Let ROM run to build non-trivial state | PARTIAL / BLOCKED — screen is static, did not observably progress with tested inputs |
| Save State (Emulator > Save State...) | PASS — file written correctly (`page1_binty.png`, 47 KB) |
| Observable change (PPU core switch) | PASS as an action; INCONCLUSIVE as visual proof (frame buffer appears frozen) |
| Load State (Emulator > Load State...) | PASS — loads cleanly, no crash/freeze/black screen |
| Reachability of "Open Emulator" screen under heavy parallel load | FAIL initially, root-caused, worked around by relaunching once the fixed API port was free |

## Root cause: fixed-port API bridge breaks under concurrent instances

- `Windows/webapi/WebApiServer.cs`: `private readonly int _port = 42067;` and
  `options.Listen(IPAddress.Loopback, _port)` / `_port + 1` — hardcoded, not per-instance/dynamic.
- `Windows/Webmodules/Home/home.js`'s `proceedToEmulator()` calls
  `api.navigation.goToEmulator()` (→ `POST http://127.0.0.1:42067/api/navigation/go-to-emulator`,
  see `Windows/Webmodules/shared/webapi.js`), and the actual UI-mode swap
  (WebView2 Home → native MenuStrip Emulator screen) happens **server-side** in response to that
  call, per `Windows/webapi/WebApiServer.Endpoints.Navigation.cs`.
- With N `BrokenNes.Windows.exe` instances running, only the first to start can bind 42067/42068;
  every other concurrently-running instance's Kestrel startup fails, and that instance never gets
  a working local API server for the rest of its life.
- Because the client-side fetch failure is caught and logged as "Emulator mode activated (server
  closed connection as expected)" (a comment says a fetch error here is normal, since the intent
  was originally "the server may shut down during this call"), there is **no visible error to the
  user** — the button just silently does nothing, which is what made this so time-consuming to
  diagnose purely from the UI.
- Verified directly: `Get-NetTCPConnection -LocalPort 42067,42068` showed no listener at all once
  the original port-winning instance had exited; `Invoke-WebRequest -Uri
  http://127.0.0.1:42067/api/navigation/go-to-emulator -Method POST` from that state returned
  "actively refused". Killing my instance and relaunching once the port was free let the new PID
  bind 42067/42068 immediately (`Get-NetTCPConnection` then showed it owned by the new PID), and
  that new instance's Emulator flow worked immediately.
- **This is worth a real fix** (bind to an OS-assigned ephemeral port, or add a per-instance offset/
  retry) since the UAT README explicitly advertises multi-instance parallel testing as safe, but
  in practice only one running instance at a time can use any API-bridged feature (mode switching,
  and likely save/load-adjacent and progression endpoints too, since they go through the same
  server).

## Screenshots (chronological, in `UAT/screenshots/`)

`savestate-01` through `savestate-36` document the initial blocked attempts and the
multi-instance-overlap/PrintWindow-vs-CopyFromScreen debugging that led to isolating the port
issue (also useful background if another tester hits the same "Open Emulator does nothing"
symptom). `savestate-40` onward document the actual, successful save/load test on a healthy
instance:

- `savestate-40-fresh-launch.png` — fresh instance landed directly on native Emulator screen
- `savestate-41-loadrom-dialog.png`, `savestate-43-after-loadrom-click.png`,
  `savestate-44-rom-loaded.png` — Load ROM flow
- `savestate-47-savestate-dialog.png`, `savestate-48-after-save.png` — Save State flow
- `savestate-49-after-core-switch.png` — observable change (PPU core → CUBE)
- `savestate-50-loadstate-dialog.png`, `savestate-51-after-loadstate.png` — Load State flow,
  confirms no crash/black-screen/freeze
- `savestate-52-back-to-fmc.png` — post-load core switch back, confirms window still fully
  responsive after the load

## Caveats / what I could not verify

- Could not visually confirm Load State reverts **emulation** state (RAM/CPU/PPU registers) to the
  exact saved moment, because the specific story ROM used showed a static frame that did not
  visibly progress with the inputs tried, so there was no "moving" state to roll back and compare.
  What I *can* confirm with confidence is the crash-safety and file-correctness half of the round
  trip: Save State produces a correctly-named, correctly-sized file at the correct time, and Load
  State consumes that exact file and returns control to a fully functional, responsive emulator
  screen with no visible corruption.
- Did not test `Quick Save State` (F7) / `Quick Load State` (F5) (the slot-based, dialog-free
  variants) — only the `Save State...` / `Load State...` dialog-based variants named in the task.
- Did not test loading a state saved under a *different* core than the one active at load time in
  a way that could show a definitive registers-restored visual (would need a ROM with visible
  animation/gameplay rather than a static story frame).
