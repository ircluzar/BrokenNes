# UAT Findings: RTC (Real-Time Corruptor)

**Area:** RTC / Real-Time Corruptor (native "Tools & Activities" menu on the direct-play emulator
screen)
**Instance:** own independent `BrokenNes.Windows.exe`, PID 21232
**Date:** 2026-08-29

## Summary

**BLOCKED before the assigned area could be reached at all.** The only documented path to RTC is
Main Menu → "BrokenNes Emulator" button (accessible name `Open Emulator`) → native WinForms
direct-play screen → Tools & Activities menu → RTC. The "Open Emulator" button never completes
that navigation on this instance, no matter which input method was tried (UI Automation
`InvokePattern.Invoke()`, real OS-level `SetCursorPos`+`SendInput`/`mouse_event` clicks at the
button's exact UIA-reported center, and keyboard Enter after focusing it) — every attempt left the
app sitting on the Main Menu.

This is **not a new discovery** — three other parallel UAT sessions covering different areas
(`UAT/findings/mainmenu-navigation.md`, `UAT/findings/direct-play.md`,
`UAT/findings/shader-switching.md`) already hit and root-caused the identical blocker. I
independently reproduced the same symptom on my own instance and independently confirmed the root
cause is live and still in effect for my PID specifically (see below), so steps 2–4 of the RTC
script (open Tools & Activities, find RTC, exercise its controls) **could not be attempted.** The
app itself stayed fully responsive throughout — this is a real, previously-reported product defect
that happens to surface only under heavy parallel-instance load, not a crash/hang and not a
methodology problem on my end.

## Root cause (corroborated, not rediscovered)

Per `shader-switching.md`'s source-level analysis (which I did not need to re-derive, but did
verify is still live): `Windows/Webmodules/shared/webapi.js` and `Windows/webapi/WebApiServer.cs`
hardcode the local API to a fixed port (42067/42068) with no per-instance uniqueness. Clicking
"BrokenNes Emulator" in the WebView2 front end POSTs to that fixed port to ask the native host to
switch views; only one `BrokenNes.Windows.exe` process machine-wide can ever be bound to it, so
every other simultaneously-running instance's click silently no-ops (the failure is swallowed —
`MainForm.Initialization.cs`'s comment: "Don't show error to user, API is optional").

I confirmed this is still true for my own session:

```
netstat -ano | grep -E "42067|42068"
TCP    127.0.0.1:42067   0.0.0.0:0        LISTENING     12384
TCP    127.0.0.1:42068   0.0.0.0:0        LISTENING     12384
```

PID **12384** holds both ports — not my PID (21232). `tasklist` showed 8 simultaneous
`BrokenNes.Windows.exe` processes at the time of my check (12384, 21232 [mine], 3012, 21080, 22944,
20248, 20256, 11480), consistent with a large parallel UAT sweep across many areas. Only whichever
instance won the startup bind race can navigate past the Main Menu's "Open Emulator" button; mine
is one of the seven that cannot.

## Step-by-step results

### Step 0 — Read required docs
**PASS.** Read `UAT/README.md` and `UAT/lib/UiaHelpers.ps1` in full before starting.

### Step 1 — Launch, health warning, reach Main Menu, load ROM
**PARTIAL / BLOCKED at the ROM-load sub-step.**
- Launched via `Start-BrokenNesWindows`, got PID 21232.
- Health Warning dialog appeared as expected. Screenshot: `rtc-01-launch.png`.
- Clicked accessible name `Acknowledge health warning` → Main Menu appeared correctly.
  Screenshot: `rtc-02-mainmenu.png`.
- Enumerated Main Menu via `Get-AllNamedElements`: confirmed `Open Deck Builder`, `Open Emulator`,
  `ROM Manager`, `Options`, `Visit Redscientist Labs website` — matches the documented
  visible-text/accessible-Name mismatch (visible "BrokenNes Emulator" → accessible `Open
  Emulator`).
- Clicking `Open Emulator` never navigated anywhere (see Root cause above), so the game/ROM never
  actually loaded and "let it run a couple seconds" could not be performed. **BLOCKED.**

### Step 2 — Open native "Tools & Activities" menu, find RTC entry
**BLOCKED.** Never reached — this menu only exists on the native direct-play screen, which was
never displayed for this PID. Confirmed via `EnumWindows` (scoped to my own PID) that no second
top-level window was ever created for my instance either.

### Step 3 — Open RTC, exercise its controls (domain, blast type/intensity, manual apply,
auto-corrupt toggle)
**BLOCKED**, not reachable — depends on Step 2.

### Step 4 — Report responsiveness
**PASS** (the one part of the assigned area I could actually complete): the app process itself
never crashed or hung despite ~35+ minutes of repeated interaction and a large amount of synthetic
input while diagnosing the blocker. `Get-Process -Id 21232` reported `Responding=True` at the end
of the session (with ~1194s of accumulated CPU time, itself evidence of the heavy multi-instance
load causing the port race and general system contention). A final screenshot shows the Main Menu
still rendering cleanly and correctly, un-corrupted. Screenshot: `rtc-27-final-responsive-check.png`.

## Control test (rules out a harness/methodology problem on my end)

Before concluding this was a real blocker rather than my own mistake, I repeated the exact same
`Click-Element`/`InvokePattern.Invoke()` call against the Main Menu's `Options` button:

- **Options:** PASS — single `Click-Element` call navigated to the Options screen within 6 seconds,
  confirmed via a fresh `Get-AllNamedElements` enumeration showing `OPTIONS`, `DeckBuilder Save`,
  `Clear the save`, `Unlock Everything`, `Open Credits`, `Input configuration`, `RETURN`, etc.
- **RETURN** (from Options back to Main Menu): PASS — single click, immediate, confirmed via the
  same enumeration technique.

This matches all three prior findings files' conclusion: the automation method is sound and the
app is generally responsive to UI Automation; only the `Open Emulator` button's native-code
handoff is broken for any instance that loses the fixed-port bind race — which, with this many
parallel testers, is nearly every instance except one.

I also polled the live accessibility tree (PID-scoped, immune to the cross-window screen-overlap
artifacts the other findings files describe) every 4-5 seconds for up to 60 seconds straight after
several separate `Open Emulator` click attempts, specifically to rule out a slow first-time
load/JIT/WASM-warmup explanation rather than a true no-op — the Main Menu's button set never
changed at any point across any of these waits.

## What passed

- App launch, Health Warning dialog and its dismissal.
- Reaching the Main Menu and confirming its accessible names.
- Sibling navigation controls (`Options`, `RETURN`) navigate correctly via the same automation
  method used against the broken `Open Emulator` button — rules out a harness problem on my side.
- General app stability/responsiveness for the full ~35-minute session — no crash, no hang, no
  black-screen/corrupted rendering, no exception dialog, despite fairly aggressive repeated
  synthetic input (mouse clicks, keyboard, window-foreground changes) while diagnosing the
  blocker.

## What failed

- Nothing RTC-specific was reachable to test, so no RTC control itself is confirmed
  failing/passing.
- The blocking defect is the Main Menu's `Open Emulator` button never completing its native-code
  handoff when more than one `BrokenNes.Windows.exe` instance is running system-wide (hardcoded,
  non-unique local API port) — already reported independently by three other parallel sessions and
  re-confirmed live for this specific instance/PID via a fresh `netstat` check.

## What I could not test / verify (and why)

- The native "Tools & Activities" menu's existence/contents, and specifically locating an RTC /
  Real-Time Corruptor entry — direct-play screen never reached.
- Loading `Data\story\page1_binty.nes` and letting it run — never reached (no native
  Emulator > Load ROM menu ever appeared).
- RTC's memory-domain picker, blast type/intensity controls, manual "apply once" action, and any
  auto-corrupt/every-frame toggle — none of these controls were ever rendered for this instance.
- Whether a manual or auto corruption pass produces the expected visible glitching without
  crashing the process — not observable without reaching RTC.
- Whether "Open Emulator" (and therefore RTC) works correctly in a normal single-instance launch —
  this parallel run's shared-fixed-port design makes that untestable here by construction, exactly
  as the three prior findings files also concluded. A clean re-test with no other
  `BrokenNes.Windows.exe` running (or a code fix giving each instance a unique/ephemeral port)
  would very likely let RTC be exercised normally.

## Files

- Findings: `C:\Users\philt\OneDrive\Documents\PROJECTS\BrokenNes\UAT\findings\rtc.md`
- Screenshots (all under `C:\Users\philt\OneDrive\Documents\PROJECTS\BrokenNes\UAT\screenshots\`):
  `rtc-01-launch.png`, `rtc-02-mainmenu.png`, `rtc-03-emulator.png` through `rtc-26-*.png`
  (numerous click/wait/diagnostic attempts against `Open Emulator`, all showing the Main Menu
  unchanged), `rtc-27-final-responsive-check.png` (final proof the app was not hung).
- Related corroborating findings from other parallel sessions:
  `UAT\findings\mainmenu-navigation.md`, `UAT\findings\direct-play.md`,
  `UAT\findings\shader-switching.md`.

## Cleanup

Stopped own instance (PID 21232) at the end of the run via
`Stop-Process -Id 21232 -Force -ErrorAction SilentlyContinue`. Did not touch, close, or otherwise
interact with any other PID's process or window at any point — PID 12384 (the current port holder,
presumably another parallel tester's active session) was only ever observed via read-only
`netstat`/`tasklist` checks, never clicked into or screenshotted.
