# UAT Findings — BrokenNes Web: Boot + Core Switching (incl. FIX)

**Build under test:** `Web/BrokenNes.Web.csproj` (experimental minimal Blazor WebAssembly shell,
"no shaders, no progression/corruption features")
**Server:** `dotnet run --project Web/BrokenNes.Web.csproj --urls http://localhost:5021`
(Development config, interpreted WASM, no AOT)
**Method:** Claude Browser pane (CDP-driven Chromium). DOM/JS inspection (`querySelectorAll`,
dispatched real `input`/`change` events on the `<select>` elements, `getImageData` on the canvas)
was used throughout, not just visual screenshots, per the task's own guidance.

## Summary

Boots cleanly with zero console errors. **FIX is present and selectable in all three core
dropdowns (CPU/PPU/APU)** — confirmed not locked, matching the task's expectation that FIX isn't
gated behind the (absent, in this build) progression system. Every core combination tested —
FIX/FIX/FIX, and three other arbitrary combos — was selectable with correct DOM state afterward
and **produced zero console errors or exceptions**. The UI (Pause/Play toggle, Reset) stayed fully
responsive under every combination tested, including all-FIX.

One thing I could **not** fully resolve either way: whether the emulation loop itself keeps
ticking at a normal rate after selecting FIX/FIX/FIX, because this shared/parallel-agent Browser
pane throttles background tabs so aggressively that even the **untouched default core combo**
exhibited the same near-total frame-counter freeze after enough time hidden. Details and reasoning
below — I'm flagging this transparently rather than either asserting a hang bug I can't confirm,
or silently dropping a real signal.

## Steps tested and evidence

### 1. Boot

Navigated to `http://localhost:5021`. Page rendered immediately: title "BrokenNes web", status
line `RUNNING · 0.0 fps · frame 0 · demo.nes`, 512×480 `<canvas>`, Pause/Reset/"Load ROM" controls,
and four core dropdowns (Clock/CPU/PPU/APU). `read_console_messages` showed only the standard
`[info] Debugging hotkey: Shift+Alt+D` line — **no errors** on boot, repeated across four separate
fresh page loads during this session (all clean).

Default selections observed: Clock=`FMC` (only one clock core exists), CPU=`SPD`, PPU=`FMC`,
APU=`FMC` — i.e. not all-`FMC` as I initially assumed; worth knowing if anyone else assumes an
all-`FMC` default.

### 2. Dropdown enumeration — FIX present in all three

```
CPU: EIL, FIX, FMC, LOW, LW2, SPD, ULQ, Z80
PPU: BFR, CUBE, CUBEX, EIL, EXE, FIX, FMC, IMG, LOW, LQ, SPD, ULQ
APU: EIL, FIX, FMC, HI, HI2, HI2X, LOW, LQ, LQ2, MNES (desktop only), QLOW, QLQ, QLQ2, QN,
     SPD, SPD2, ULQ, WF (desktop only)
```

**FIX confirmed present and selectable (not disabled/locked) in CPU, PPU, and APU**, re-verified
after today's accumulated changes. `MNES` and `WF` are labeled "(desktop only)" in their option
text but are still present in the `<option>` list on Web — selecting `WF` for APU (see combo 4
below) did not throw or otherwise break anything, it's simply cosmetically labeled as intended for
desktop.

### 3. FIX/FIX/FIX (CPU+PPU+APU all set to FIX)

Set via `element.value = 'FIX'` + dispatched real `input` and `change` events on all three
`<select>`s (confirmed necessary — a raw DOM `.value` set without a dispatched `change` event does
**not** reach Blazor's bound state, verified by the dropdown visually appearing changed while the
underlying value silently reverted to the old selection on the next Blazor re-render). After
dispatching properly:
- All three dropdowns' `.value` read back as `FIX` — confirmed via direct `querySelectorAll`, not
  just visual state.
- `read_console_messages` — **zero errors**, only the standard debug-hotkey info line.
- Clicked Reset: frame counter dropped to `0`, status stayed `RUNNING`, ROM name unchanged
  (`demo.nes`).
- Clicked Pause then Play: label toggled `Pause → Play → Pause` correctly and immediately both
  times — confirms the Blazor UI thread itself is not deadlocked, this is a synchronous click
  handler responding normally.
- Repeated the whole FIX/FIX/FIX selection + Reset on a **second, completely fresh tab** (to rule
  out any state carried over from prior interaction) — identical result: clean DOM state, zero
  console errors.

### 4. Other combos tried

- **SPD / CUBE / HI2** (CPU/PPU/APU) — selected cleanly, zero console errors, Pause/Play toggle
  confirmed still responsive immediately after.
- **LW2 / EXE / QLQ2** — selected cleanly, zero console errors.
- **Z80 / BFR / WF** (including the "desktop only"-labeled APU core) — selected cleanly, zero
  console errors, canvas still a valid 512×480 2D-context surface afterward (no context loss).

No combination tried — including all three at once, and the desktop-only-labeled WF — produced a
console error, an unhandled exception, a blank/white page, or an unresponsive Pause/Reset button.

## Frame-counter / "does it hang" caveat (environment limitation, not a clean pass/fail)

While testing FIX/FIX/FIX I initially saw what looked like a clear hang: after Reset, the frame
counter stayed pinned at `0`/a fixed value for 10–60+ seconds straight (status still said
`RUNNING`), across multiple repeats and even after giving the canvas an explicit user-gesture
click. That looked like a strong, reproducible signal.

However, running the identical protocol against the **default, untouched core combo** on the same
tab — after the tab had been sitting backgrounded for a while — produced the *same* symptom: frame
counter frozen at `0` for 30+ seconds. Checking `document.visibilityState`/`document.hidden`
confirmed the tab reports `hidden: true` even immediately after this tool's own `tabs_select`
"front" call, because (per this tool's own status line) *"The Browser pane is currently hidden"* —
no human is watching this session, and the pane is additionally shared with other parallel UAT
agents whose tabs intermittently take foreground focus (I directly observed my tab's reported
origin get shuffled/hijacked between agents' ports mid-session, and closed/reopened tabs to
recover). Chrome's background-tab timer/rAF throttling gets more aggressive the longer a tab has
been hidden, which lines up with: fresh reloads ticked a little (frame 0→14 over ~20s early in the
session), then later in the same session even the default combo produced zero ticks for 30s+.

So I cannot cleanly separate "FIX/FIX/FIX genuinely stalls the emulation loop" from "this
particular shared, never-foregrounded automation pane throttles every core combo's `rAF` loop into
near-zero fps given enough backgrounded time." Everything that **isn't** gated behind `rAF`
(console errors, Blazor click handlers, dropdown state) was unaffected and consistently clean
across every combo. This exact class of artifact was also independently flagged in this same UAT
round by the sibling `web-rom-loading-playback.md` report (shared-pane tab reload/focus-stealing).

**Recommendation:** if anyone wants to fully close this out, re-run just the FIX/FIX/FIX vs.
default comparison in a normal, single, actually-foregrounded browser window (not this shared
automation pane) and watch the frame counter for ~10 real seconds each — that removes the
confound entirely. I'd treat it as a "recheck," not a known bug, based on what I could verify here.

## Verdict

**Pass**, with one flagged-but-unconfirmed item. Boot is clean, FIX is present and selectable in
all three core dropdowns as expected (not locked), every core combination tried (including
all-FIX) is selectable with correct resulting DOM state and **zero console errors**, and the UI
stays responsive throughout. The only open question — whether FIX/FIX/FIX's emulation loop ticks
at a normal rate versus just appearing frozen like every other combo did under this session's
heavy background-tab throttling — could not be resolved with confidence in this shared, headless,
never-foregrounded pane, and is called out above for a quick manual recheck rather than reported
as a confirmed defect.
