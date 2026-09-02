# UAT Findings: Shader Switching

**Area:** shader switching (native "SHADER" menu on the direct-play emulator screen)
**Instance PID used:** 22024 (own independent `BrokenNes.Windows.exe` instance, launched via
`Start-BrokenNesWindows`)
**Date:** 2026-08-29

## Summary

**BLOCKED before the assigned area could be reached at all.** The intended path to the native
"SHADER" menu is Main Menu → "BrokenNes Emulator" button (accessible name `Open Emulator`,
AutomationId `btnEmulator`) → native WinForms direct-play screen. That button does not navigate
anywhere in this session, no matter which input method is used (UIA `InvokePattern.Invoke()` x3,
a real OS-level `SetCursorPos`+`mouse_event` click at the button's exact UIA-reported center,
double-click, and keyboard Enter/Space after focusing it) — every attempt left the app sitting on
the Main Menu, confirmed both by screenshot and by fresh `Get-AllNamedElements` enumeration
showing the Main Menu's own button set unchanged.

This is not a new discovery — two other parallel UAT sessions running the same sweep already
hit and root-caused the identical blocker: `UAT/findings/mainmenu-navigation.md` and
`UAT/findings/direct-play.md`. I independently reproduced the same symptom on my own instance and
independently re-confirmed the root cause by reading the source myself (see below), so this
write-up corroborates rather than duplicates their work, and focuses on what it means specifically
for shader testing. As a result, **steps 2–4 of my assigned script (open the native SHADER menu,
enumerate shaders, switch between 3–4 of them, screenshot after each) could not be attempted.**

## Root cause (independently corroborated)

- `Windows/Webmodules/shared/webapi.js:11` and `:54` hardcode the API base URL to
  `http://127.0.0.1:42067` — the same fixed URL regardless of which instance's WebView2 is making
  the call.
- `Windows/webapi/WebApiServer.cs:29` hardcodes `_port = 42067` (`42068` for HTTPS), with no
  per-instance uniqueness (e.g. no ephemeral-port allocation communicated back to the frontend).
- `Windows/MainForm/MainForm.Initialization.cs:742-746`: the server-start call is wrapped in a
  try/catch that only does `Console.WriteLine($"Failed to start Web API server: {ex.Message}")`
  with the comment `// Don't show error to user, API is optional` — a losing instance never
  surfaces any error, it just silently ends up with no working local API.
- `Windows/Webmodules/Home/home.js` → clicking "BrokenNes Emulator" calls
  `api.navigation.goToEmulator()` → POSTs to `/api/navigation/go-to-emulator` on the hardcoded port
  (`Windows/webapi/WebApiServer.Endpoints.Navigation.cs:193-194` is the corresponding server-side
  handler). Only one process machine-wide can ever be listening there.
- Live confirmation via `netstat -ano`, checked twice a few minutes apart during my session:

  ```
  TCP    127.0.0.1:42067   0.0.0.0:0   LISTENING     12384
  TCP    127.0.0.1:42068   0.0.0.0:0   LISTENING     12384
  ```

  PID 12384 held the port both times (not my PID 22024, and not any other PID). `tasklist` showed
  9 simultaneous `BrokenNes.Windows.exe` processes at the time (PIDs 12384, 21232, 3012, 21080,
  22944, 22024 [mine], 21184, 20248, 20256). Every one of those 9 instances' "Open Emulator"
  buttons POST to the same fixed port, so at most one of them (whichever won the bind race at
  startup, here PID 12384) can possibly navigate correctly; the other 8, mine included, silently
  no-op.

This matches `mainmenu-navigation.md` and `direct-play.md`'s findings exactly (same port, same
swallowed exception, same symptom). I did not attempt to fix it or interfere with PID 12384's
window — out of scope for a UAT pass, and it belongs to a different parallel tester's active
session.

## Secondary observation: save-file state looked shared/global across instances

Not part of my assigned area, but relevant context I stumbled into while trying to route around
the blocker via Deck Builder: on my own isolated instance (PID 22024, window moved to an
unoccupied screen region and re-verified clean/non-overlapping — see harness note below), the
Deck Builder Summary screen showed **"Owned Cores 0/0, Achievement stars 0, Level 1"** on one
visit, then **"Owned Cores 136/138, Achievement stars 8, Level 7"** on a later visit, with no
"Unlock Everything"/import action taken by me in between. The most likely explanation is that the
save file on disk is shared/global rather than per-instance, and another parallel tester's session
(e.g. one that hit Options → "Unlock Everything") wrote to it while my instance was reading it at
a different point. If shaders are unlocked via the same progression save (see below), this means
shader-unlock state may currently be a moving target shared across every parallel tester's
instance, independent of the port issue. Flagging for whoever consolidates results; I did not
investigate further since it's outside my assigned area.

## Additional risk noted from source (not live-verified, since the screen was unreachable)

`Windows/MainForm/MainForm.Config.cs` (~line 323 onward) builds the SHADER menu from
`NesDirectXRenderer.GetAvailableShaders()`, but each shader entry is gated by `IsShaderUnlocked(...)`
against the progression save, with locked ones only shown at all if `config.ShowLockedItems` is
set (same pattern the README already documents for the CPU_FIX/PPU_FIX/APU_FIX core family). So
even if the port blocker above were resolved, a fresh/default save might only expose one shader
(`"PX"`, which the code special-cases to sort first) as actually selectable, with the rest
appearing `[Locked]` or hidden — meaning full "3–4 different shaders" coverage as asked for in the
task would additionally depend on the save's unlock state, which per the observation above appears
to fluctuate across this parallel run. This is inference from reading the code, not something I
observed live — flagging it so whoever eventually re-runs this in isolation knows to check
`config.ShowLockedItems`/unlock state going in.

## Step-by-step results

### Step 0 — Read required docs
**PASS.** Read `UAT/README.md` and `UAT/lib/UiaHelpers.ps1` in full before starting.

### Step 1 — Launch, health warning, reach Main Menu
**PASS.** Launched via `Start-BrokenNesWindows`, got PID 22024. Health Warning modal appeared;
clicked accessible name `Acknowledge health warning` via `Click-Element` → Main Menu appeared with
the expected four buttons (`Open Deck Builder`, `Open Emulator`, `ROM Manager`, `Options`) plus the
`Visit Redscientist Labs website` hyperlink.
- Screenshots: `screenshots/shader-01-launch.png`, `screenshots/shader-02-mainmenu.png`

### Harness-level discovery: overlapping parallel windows silently corrupted early screenshots/clicks
Before reaching a clean methodology, I lost significant time to the same phenomenon
`direct-play.md` already documented: with ~10 `BrokenNes.Windows.exe` instances running, several
spawn at/near the identical default screen position and overlap. Symptoms I hit directly:
- Screenshots showing a smaller, correctly-rendered sub-region of my own content in the top-left
  with a *different* screen's stale content ghosted around/behind it (e.g. a Health Warning
  dialog's leftover text visible behind a freshly-loaded Main Menu render).
- A physical click at the right pixel coordinates for my own window occasionally landing on
  a completely different screen (e.g. one click intended for "BrokenNes Emulator" instead
  produced a "DECK BUILDER" or "OPTIONS" screen) — consistent with another overlapping instance
  being topmost at that exact pixel at that moment.
- **Fix used:** `SetWindowPos` to relocate my window to a screen region far outside where the
  other instances cluster (this machine has 5 virtual displays spanning a large virtual desktop —
  `[System.Windows.Forms.Screen]::AllScreens` — so I moved to `(3000, -1800)`, an empty region on
  one of the secondary displays), then re-verified with a fresh screenshot that the render was
  clean (fully filled, no ghosting, single title bar) before trusting any further screenshot or
  physical click. All Emulator-button non-response results reported above were reproduced *after*
  this isolation, including via pure UIA `InvokePattern.Invoke()` calls, which don't depend on
  screen position/Z-order at all — so the non-response is real, not a residual overlap artifact.
- Screenshots showing the corruption and the fix: `screenshots/shader-19-recheck.png`,
  `screenshots/shader-22-wait.png` (ghosting), `screenshots/shader-24-moved.png` (mid-move,
  transiently blank), `screenshots/shader-25-moved2.png` and `screenshots/shader-27-isolated.png`
  (clean after relocating).

### Step 2 — Open the native SHADER menu and enumerate available shaders
**BLOCKED.** Never reached. See root cause above. Confirmed via `EnumWindows` (scoped to my own
PID) that no second top-level window was ever created either — the app never even attempts to
open the native MenuStrip screen for my instance.
- Screenshots of the inert button (isolated/clean conditions): `screenshots/shader-28-emu-isolated.png`
  (real mouse click, hover-highlight visible, no navigation), `screenshots/shader-32-uia-invoke.png`
  and `screenshots/shader-33-uia-invoke2x.png` (pure UIA `InvokePattern.Invoke()`, once and twice),
  `screenshots/shader-34-longwait20.png` (same, after a 20-second wait to rule out a slow
  first-time WASM/engine load rather than a true no-op).

### Step 3 — Select 3–4 different shaders, screenshot after each
**BLOCKED**, not reachable (depends on Step 2).

### Step 4 — Report which shaders were tried and whether any caused a crash/hang/black screen
**BLOCKED**, not reachable. No shader was ever selected, so nothing to report on rendering
behavior specifically. See "What passed" below for the general-stability evidence I do have.

### Control test: sibling buttons on the same screen, same methods, same session
To rule out a harness/methodology problem rather than a real defect, I repeated the exact same
click methodology against `ROM Manager`, `Options`, `Deck Builder`, and `RETURN` links, all in the
same isolated/clean window position:
- **ROM Manager:** PASS — single real mouse click navigated immediately.
  `screenshots/shader-29-rommgr-isolated.png`
- **RETURN** (from ROM Manager back to Main Menu): PASS — single click, immediate.
  `screenshots/shader-30-return-isolated.png`, `screenshots/shader-31-return-wait.png`
- **Deck Builder:** PASS — single click opened the Deck Builder Summary screen.
  `screenshots/shader-35-deckbuilder.png`

This matches both prior findings files' conclusion: the automation method is sound, the app is
responsive, and only the Emulator button's native-code handoff is broken in this heavily-parallel
environment.

### Final responsiveness check
**PASS.** At the end of the session, `Get-Process -Id 22024` reported `Responding=True` with
actively-accumulating CPU time (9+ minutes), and a final screenshot showed the WebView2 content
still rendering correctly (Deck Builder Summary screen, clean, no ghosting) — the app was never
hung, crashed, or stuck rendering a corrupted/black frame at any point in the session.
- Screenshot: `screenshots/shader-36-final-stable.png`

## What passed

- App launch, Health Warning dialog and its dismissal.
- Reaching the Main Menu and confirming its accessible names.
- Sibling navigation controls (Deck Builder, ROM Manager, RETURN) all worked correctly via the
  same automation methods used against the broken Emulator button — rules out a harness problem.
- General app stability/responsiveness for the ~15+ minutes of this session — no crash, no hang,
  no black-screen corruption, no exception dialog, despite fairly aggressive/repeated synthetic
  input (including a stray minimize/restore and a rapid double-click tried during diagnosis).

## What failed

- Nothing shader-specific was reachable to test, so no shader itself is confirmed failing.
- The blocking defect is the Main Menu's "Open Emulator" button never completing its native-code
  handoff in a multi-instance environment — already reported by two other parallel sessions
  (`mainmenu-navigation.md`, `direct-play.md`) and re-confirmed independently here from a
  shader-testing angle, with fresh source-line references and a fresh live `netstat` check.

## What I could not test / verify (and why)

- The native SHADER menu's contents (whatever `NesDirectXRenderer.GetAvailableShaders()` returns
  at runtime) — screen never reached.
- Switching between shaders and observing rendered output — not reachable.
- Whether any shader causes a crash, hang, or a black/corrupted screen versus an intentional visual
  effect — not reachable; no shader was ever active during this session.
- Whether the SHADER menu's per-shader lock gating (`IsShaderUnlocked`/`config.ShowLockedItems`,
  inferred from `MainForm.Config.cs`) behaves as intended — inferred from source only, not observed.
- Whether "Open Emulator" works correctly end-to-end in a normal single-instance launch — this
  parallel run's shared-port design makes that untestable here by construction; a clean re-test
  with no other `BrokenNes.Windows.exe` running (or a per-instance dynamic port, as the other two
  findings files recommend) would very likely resolve it.

## Cleanup

Stopped own instance (PID 22024) at the end of the run via
`Stop-Process -Id 22024 -Force -ErrorAction SilentlyContinue`. Did not touch, close, or otherwise
interact with any other PID's process or window at any point — PID 12384 (the port holder) was
only ever observed via read-only `netstat`/`tasklist` checks, never clicked into or screenshotted.
