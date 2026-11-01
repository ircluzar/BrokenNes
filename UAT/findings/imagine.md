# UAT Findings: Imagine (ML-based mode)

**Area owner instructions recap:** launch, get past health warning, "Open Emulator", load
`Data/story/page1_binty.nes`, open Tools & Activities, find and open the Imagine entry, and report
whether it loads, fails gracefully, or causes a real problem (crash/hang/unresponsive UI).

**Result: BLOCKED before Imagine could be reached at all.** The Main Menu's "BrokenNes Emulator"
button (accessible Name "Open Emulator") does not respond to clicks — no navigation, no visible
change, no error, nothing. This is the sole gateway to the native "Open Emulator" screen that hosts
the Tools & Activities menu (and therefore Imagine), so steps 2-4 of this area could not be executed
at all. Details, reproduction, and evidence below.

## Environment note (read this first)

This pass ran while many other parallel UAT testers' `BrokenNes.Windows.exe` instances were also
running and, apparently, launching at overlapping default screen positions. Early screenshots
(`imagine-01-launch.png`, `imagine-03-emulator.png`) show visual content that on closer inspection
came from a **different agent's overlapping window**, not mine (`Save-WindowScreenshot`'s
`CopyFromScreen` captures whatever is topmost on screen at that rectangle, regardless of which
window actually owns it). I caught this via `UIA` queries scoped strictly to my own PID
disagreeing with what the screenshot showed, then confirmed it by moving my window
(`MoveWindow`) to an isolated, unoccupied region of a secondary monitor (approx. `1500,-900`)
before continuing. **All findings from `imagine-04-isolated.png` onward are verified against my own
PID's UI Automation tree, not just a screenshot**, so they are not subject to this artifact.
Anyone else running this harness in parallel should be aware `Save-WindowScreenshot` can silently
show a different process's window if screen positions overlap — worth a note back to whoever owns
`UiaHelpers.ps1`.

## Steps

### 1. Launch, health warning, "Open Emulator" — **FAIL** (blocked on last part)

- Launched instance (PID 21184) via `Start-BrokenNesWindows`. **PASS** — Main Menu appeared with
  the "Health Warning" overlay already present (text + "Acknowledge health warning" button both
  found via `Get-AllNamedElements`). Screenshot: `imagine-01-launch.png` (note: right-hand portion
  of this shot is another agent's window, see note above).
- Clicked "Acknowledge health warning" via `Click-Element` (`InvokePattern`). **PASS** — warning
  overlay gone, clean Main Menu with "Open Deck Builder", "Open Emulator", "ROM Manager", "Options"
  buttons all present. Screenshot: `imagine-02-mainmenu.png`.
- Clicked "Open Emulator" (visible text "BrokenNes Emulator"). **FAIL** — nothing happened. See full
  investigation below.

### 2-4. Tools & Activities → Imagine ("Target the Beam") → observe outcome — **BLOCKED**

Cannot reach the native "Open Emulator" screen (and its Tools & Activities menu, per
`UAT/README.md`'s own note that this menu only exists on that screen) because the button that leads
there does not work. Never got to load the ROM, open Tools & Activities, or open Imagine.

For what it's worth: by reading `Windows/Webmodules/ImagineBug/config.json`
(`"title": "Target the Beam"`, `"showInToolsMenu": true`, `"description": "Standalone Imagine mode
with scanline targeting overlay"`), the actual Tools & Activities menu item for this feature is
labeled **"Target the Beam"**, not "Imagine" — worth knowing for whoever picks this back up.

## Investigation: why "Open Emulator" does nothing

Reproduced identically on **two independent, freshly-launched instances** (PID 21184 and PID
11480), so this is not one-off flakiness or state left over from my own window manipulation.

Things tried against the button, all with the same (non-)result:
- `Click-Element` (UIA `InvokePattern.Invoke()`) — repeated 3x in a row with 500ms gaps.
- A genuine OS-level synthetic mouse click (`SetCursorPos` + `mouse_event` down/up) at the
  button's own `GetClickablePoint()`, after `SetForegroundWindow` and `SetProcessDPIAware` to rule
  out coordinate/focus mismatches.
- Pressing `Enter` via `SendKeys` while the button visibly had focus.
- Waiting up to 15 seconds after the click in case it was a slow/deadlocked operation rather than
  an instant no-op.

Every attempt produced: no visible change (still sitting on Main Menu — see
`imagine-12-tripleclick.png`, `imagine-14-fresh-emulator-attempt.png`,
`imagine-15-after-longwait.png`), no new top-level window (`EnumWindows` scoped to the PID showed
the same window set before and after), and — most tellingly — **zero new lines** in the app's own
hidden diagnostic console window (a `ConsoleWindowClass` window that exists per-instance and logs
`[WebModuleManager]`/`[LoadWebModule]` activity; I diffed its full text content before and after
each click attempt via UIA's `TextPattern` and it was byte-identical every time, ending at
`[LoadWebModule] Successfully loaded web module: Home`).

**Ruled out as a general app hang/resource-contention artifact:** on the very same instance, in the
very same session, clicking "ROM Manager" and "Open Deck Builder" (via the identical
`Click-Element` mechanism) navigated correctly and immediately, both before and after the failed
"Open Emulator" attempts. So the click-delivery mechanism itself works, the app isn't frozen, and
this is specific to the one button.

**Plausible root cause, from reading the shipped source (matches the deployed
`Windows/bin/Release/.../Webmodules/Home/home.js` byte-for-byte, so this isn't a stale-build
mismatch):**
- `Windows/Webmodules/Home/home.js`'s `onEmulatorClick()` either shows a one-time "Emulator
  Warning Modal" (`shouldShowEmulatorWarning()` — true for any fresh save under level 5, which a
  brand-new instance always is) or calls `proceedToEmulator()` directly.
- I found **neither** outcome occurred: the modal never appeared (I searched the full UIA
  descendant tree by name for its buttons — "Let me go to Emulator Mode anyway" / "Go back to main
  menu" — and found nothing at all beyond the "Open Emulator" button itself), and no navigation
  happened either.
- `proceedToEmulator()` depends on a global `api` helper object (`api.navigation.goToEmulator()` →
  `POST /api/navigation/go-to-emulator`, handled server-side in
  `Windows/webapi/WebApiServer.Endpoints.Navigation.cs`) with **no synchronous fallback** — contrast
  with the Deck Builder button's handler, which falls back to a plain `window.location.href` if
  its own API call fails, which is presumably why Deck Builder still works. If `api` isn't ready, or
  the fetch/local API call fails for any reason, `proceedToEmulator()`'s catch block only does a
  `console.error(...)` with no user-visible fallback or alert (the alert is present in the source
  but commented out) — which would look exactly like what I observed: a silently inert button.
- I could not go further than this: the harness has no way to see the WebView2 page's own DevTools
  console (only the app's native `Console.WriteLine` output is exposed via the hidden console
  window, and I confirmed that channel never carries any `[Home]`-tagged JS console output at all,
  before or after — so its silence doesn't actually prove the JS handler never ran, only that
  nothing reaches that particular log).

## Assessment

This is not the "ML model missing, fails gracefully" scenario the task anticipated — it's a dead
end one step earlier, at the Main Menu, before any Imagine-specific code would even run. It also
isn't a crash or a hang in the traditional sense (no exception dialog, no "Not Responding" window
state, other buttons keep working) — it's a **silently inert primary navigation button**, which
from a user's perspective is arguably worse: no feedback at all that anything went wrong. I'd
flag this as a real, high-priority bug independent of the Imagine investigation, since "open the
emulator" is one of the four top-level Main Menu options and is currently non-functional in this
Release build.

## What I could not verify

- Whether Imagine ("Target the Beam") itself loads, fails gracefully, or crashes — never reached
  it.
- Whether the missing-ML-model scenario the task description anticipated is handled gracefully —
  never reached the code path that would exercise it.
- Whether the "Open Emulator" bug is specific to this Release build / this machine's WebView2
  runtime, or would reproduce for an end user outside this heavily-parallelized test environment.
  I have no reason to think parallelism caused it (see "ruled out" above), but I can't rule out
  something specific to this build/runtime environment without a from-scratch investigation outside
  UAT scope.

## Screenshots (in `UAT/screenshots/`)

- `imagine-01-launch.png` — initial launch (right side is another agent's overlapping window, see
  note above).
- `imagine-02-mainmenu.png` — Main Menu after acknowledging health warning.
- `imagine-04-isolated.png` — Main Menu, window moved to an isolated screen region, clean baseline.
- `imagine-05-emulator.png`, `imagine-07-realclick.png`, `imagine-12-tripleclick.png` — successive
  click attempts on "Open Emulator" (UIA invoke, then genuine OS mouse click, then 3x rapid
  clicks); button shows focus outline but the screen never changes.
- `imagine-13-freshlaunch.png` — second, completely independent fresh instance, health warning
  screen.
- `imagine-14-fresh-emulator-attempt.png`, `imagine-15-after-longwait.png` — same failure
  reproduced on the fresh instance, including after a 15-second wait to rule out a slow/deadlocked
  operation.

## Steps tested summary

1. Launch app — PASS
2. Get past health warning — PASS
3. Click "Open Emulator" to reach the direct-play screen — FAIL (button is inert; see investigation
   above)
4. Load `Data/story/page1_binty.nes` — BLOCKED (no ROM-loading UI reachable without step 3)
5. Open Tools & Activities — BLOCKED (native menu only exists on the screen from step 3)
6. Find and open the Imagine ("Target the Beam") entry — BLOCKED
7. Observe/report Imagine's load behavior — BLOCKED
