# Corruption Slop — UAT Findings

**Area:** Tools & Activities > Corruption Slop (automated corrupt/restore/rerun loop)
**Date:** 2026-08-29
**Build under test:** `Windows\bin\Release\net10.0-windows\win-x64\BrokenNes.Windows.exe`
**ROM used:** `Data\story\page1_binty.nes`

## Methodology note (important context for reading the evidence below)

This session hit a real, reproducible screenshot-capture bug: the harness's documented
`Save-WindowScreenshot` (DWM extended-frame-bounds rect + `CopyFromScreen`) intermittently captured
the **wrong window's pixels** or a stale/partially-composited frame for this specific app — most
likely because BrokenNes.Windows hosts a WebView2 control alongside a D3D-rendered emulator surface
inside the same native window, which `CopyFromScreen` doesn't always composite correctly, and because
several parallel UAT instances (and native owned dialogs the app creates but keeps hidden behind the
main window, e.g. "Player 1/2 Controller Configuration") were on-screen at once. Screenshots
`corruptionslop-01` through `-15` in this pass are unreliable for that reason and are **not** used as
evidence below.

Switching to the Win32 `PrintWindow` API (with `PW_RENDERFULLCONTENT`) against the specific target
HWND produced consistent, verifiably-correct captures (cross-checked against live UI Automation state,
e.g. presence/absence of the native menu bar). **All evidence cited below is from `PrintWindow`
captures (`corruptionslop-16` onward), which I verified to be trustworthy.** This capture-reliability
issue is a UAT-harness/environment finding, not a claim about the shipped app.

I also want to flag for whoever automates this screen next: on the WebView2-hosted **Main Menu**,
clicking/UIA-invoking "Open Emulator" was flaky under synthetic input — it frequently focused the
button (visible yellow focus ring, confirmed via `AutomationElement.FromHandle` on the real "BrokenNes"
top-level HWND, which is the reliable way to reach this app's UIA tree — `Get-AppRoot`'s
top-level-children search returned an empty/unwoken tree most of the time in this session) without
firing navigation, across mouse (`mouse_event` and `SendInput`), keyboard (`Enter`/`Space` after
confirmed focus), and UIA `InvokePattern` — retried many times on a second, freshly-launched instance
with no success. It eventually succeeded on the first instance. I could not isolate a deterministic
trigger; I'm reporting it as an automation-reliability observation, not a confirmed app defect, since
a real mouse user was not tested.

## Steps and results

1. **Launch, health warning, Open Emulator, load ROM** — PASS
   Launched an independent instance, acknowledged the health warning (native UIA click on
   `Get-ContentPane`'s "Acknowledge health warning" element — this worked reliably), reached the
   "Open Emulator" native screen, and loaded `Data\story\page1_binty.nes` via `Emulator > Load Rom...`
   (native menu, 100% reliable throughout via `Get-NativeMenuBar`/`Open-NativeMenu`) → standard
   Explorer file-open dialog → typed the full path into the filename field → Enter.
   Evidence: `corruptionslop-16-printwindow.png` (Open Emulator screen, idle shader), `-18` through
   `-21` (file dialog flow), `-22-romloaded.png` (ROM loaded and rendering the story page art).

2. **Open Tools & Activities > Corruption Slop** — PASS
   The native `Tools & Activities` menu reliably lists `Corruption Slop` alongside `Deck Builder`,
   `Target the Beam`, `Time Jump Challenge`, `Hex Editor`, `ROM Manager`, `RTC + Glitch Harvester`.
   Invoking it via UIA `InvokePattern` succeeds (the call itself completes normally, unlike
   `Load Rom...` which times out client-side because it blocks on a modal dialog — Corruption Slop's
   invoke returns immediately since it doesn't open a modal).
   Evidence: menu enumeration in-session; `corruptionslop-23-opened.png`.

3. **Start it and observe for ~10–15 seconds** — FAIL
   On invoke, Corruption Slop correctly hides the native menu bar and shows its own compact overlay
   (a red square "Exit Corruption Slop" button top-right, and an "Adjust until funny" intensity knob
   bottom-right, matching `Windows\Webmodules\CorruptionSlop\index.html`). However, in **every one of
   three separate attempts**, this overlay disappeared and the native menu bar reappeared within
   roughly **1.5–3 seconds** of starting — well short of the requested 10–15 second observation window
   — and the emulator display never visibly went through a restore/corrupt/rerun cycle. It looked like
   a one-frame flash of "starting up," not a running loop.
   - Attempt 1: overlay visible at ~0.5s (`corruptionslop-23-opened.png`), gone by ~3s
     (`corruptionslop-24-check.png`, back to static unmodified art + full menu bar).
   - Attempt 2: overlay visible at `corruptionslop-26-a.png` (0.5s) and `-26-b.png` (1.5s), gone by
     `-26-c.png` (3s) and `-26-d.png` (5s) — same pattern, fully reproduced.
   - Attempt 3 (the formal 2s/7s/12s capture requested by the task): by the time of the first capture
     at t=2s the overlay was **already gone** and the native menu bar was already back
     (`corruptionslop-30-t2s.png`). The t=7s and t=12s captures (`-30-t7s.png`, `-30-t12s.png`) are
     **pixel-identical** to t=2s — i.e. nothing was cycling; the display was simply frozen on a static
     (and visually broken — see below) frame for the entire window. So rather than "cycling," what I
     observed across the requested 10–15s window was: already-stopped, then unchanging.
   - Reading `Windows\Webmodules\CorruptionSlop\corruption-slop.js`: `runLoop()` is wrapped in a
     `.catch(error => { ...; stopAndExit(); })`. `stopAndExit()` is the only path that both stops the
     loop *and* calls `api.ui.showMenu()` to restore the native menu — which is exactly what I
     observed reappearing. That strongly suggests an unhandled exception/rejection is thrown very
     early (during `configureRtc()`/`createBaseState()`, or on the very first `runCycle()`), causing
     an immediate silent self-abort rather than the intended long-running loop. I did not have a way
     to capture the WebView2 devtools console in this harness to confirm the exact exception.

4. **App stayed responsive throughout** — PASS
   Even after Corruption Slop's silent self-abort (all 3 attempts) and after the frozen/broken display
   noted below, the native menu bar remained fully interactive: I successfully reopened
   `Tools & Activities` and `Emulator` menus via UIA afterward every time
   (`corruptionslop-31-responsivecheck.png` confirms a menu reopened cleanly after the third failed
   attempt). No hang, no crash, no unresponsive-window state at any point.

5. **Manual Stop button** — BLOCKED
   Because the loop never sustained a visibly-running state for more than ~1.5–3 seconds across three
   attempts, I was never able to click the overlay's own "Exit Corruption Slop" stop square while it
   was actually running — it had already self-stopped and removed itself before I could act. I cannot
   report on whether the manual stop control itself works correctly in isolation.

6. **Secondary observation — broken split-canvas rendering** (found while investigating, related but
   not separately confirmed as a distinct bug): After the second Corruption Slop attempt and some
   manual exploration of the sibling `RTC + Glitch Harvester` panel (opened from the same
   `Tools & Activities` menu, confirmed independently functional — its RTC tab with Blast
   Type/Intensity/Manual Blast/Let It Rip! controls rendered correctly, `corruptionslop-27-rtcgh.png`),
   the third Corruption Slop attempt left the emulator display in a visually broken state: the
   character art appeared **tripled horizontally** with plain blue gaps between copies, and this exact
   broken frame was static for the full ~10 second observation window (`-30-t2s/t7s/t12s.png`,
   pixel-identical). I did not isolate whether this rendering glitch is inherent to Corruption Slop
   alone or a side effect of switching between the RTC+GH panel and Corruption Slop in the same
   session (both are WebView2 modules overlaying the same emulator canvas). Flagging it since it's a
   visibly broken frame either way, but not marking it PASS/FAIL on its own since I could not isolate
   a clean single-cause repro.

## Summary

Corruption Slop's menu entry, launch, and menu-hide/restore mechanics all work, and the app never
hangs or becomes unresponsive because of it. But the actual advertised behavior — "an automated loop
that repeatedly restores, corrupts, and reruns content for rapid glitch generation," sustained over
time — did not occur in any of three attempts in this session: the loop self-terminated (silently,
gracefully, menu restored) within roughly 1.5–3 seconds every time, before any visible corrupt/restore
cycle, on the specific test ROM (`Data\story\page1_binty.nes`) used per the task instructions. This
looks like a real functional defect (an early unhandled failure inside `start()`/`runLoop()`'s
try/catch in `corruption-slop.js`, per source inspection) rather than a test-methodology artifact,
since it reproduced identically 3/3 times using the reliable `PrintWindow` capture method, but I was
unable to capture the underlying JS exception/console output to pin down the exact cause — flagging
for someone with WebView2 devtools access to confirm.

## Screenshots (evidence), in order

- `corruptionslop-16-printwindow.png` — Open Emulator screen reached (ground-truth via PrintWindow)
- `corruptionslop-18-filedialog.png` / `-19` / `-20-pathtyped2.png` / `-21-pathtyped3.png` — Load ROM file dialog flow
- `corruptionslop-22-romloaded.png` — ROM loaded successfully
- `corruptionslop-23-opened.png` — Corruption Slop overlay visible at start (attempt 1)
- `corruptionslop-24-check.png` — overlay gone, menu restored ~3s later (attempt 1)
- `corruptionslop-26-a.png` / `-26-b.png` — overlay visible (attempt 2, 0.5s/1.5s)
- `corruptionslop-26-c.png` / `-26-d.png` — overlay gone, menu restored (attempt 2, 3s/5s)
- `corruptionslop-27-rtcgh.png` / `-28-manualblast.png` / `-29-savestates.png` — sibling RTC+Glitch Harvester panel, tested for context
- `corruptionslop-30-t2s.png` / `-30-t7s.png` / `-30-t12s.png` — attempt 3, the formal spaced captures requested by the task; already stopped by t=2s and frozen/broken (identical) through t=12s
- `corruptionslop-31-responsivecheck.png` — app responsiveness confirmed after failure (menu reopens cleanly)

All screenshots are in `C:\Users\philt\OneDrive\Documents\PROJECTS\BrokenNes\UAT\screenshots\`.
