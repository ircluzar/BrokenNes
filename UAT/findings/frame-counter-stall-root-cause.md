# AREA: Root-cause of the stuck frame counter / fps readout

## Verdict

**Reproduced, and root-caused. It is not a bug in BrokenNes.Web's code.** The counter freeze
is a direct, mechanical consequence of `requestAnimationFrame` (rAF) never firing for a browser
tab that isn't actually being composited to a real screen — which is exactly the situation an
unattended, headless-style browser-automation tool creates. `CLOCK_FMC`, the only clock this Web
build ships, paces its entire emulation loop through rAF with **zero fallback timer**, so when rAF
is starved, the loop doesn't slow down — it stops completely (0 frames, not just low fps). The
Blazor app, the WASM runtime, JS interop, and the UI data-binding are all functioning correctly
throughout; none of them is the cause.

## 1. Reproduction

Dev server: `dotnet run --project Web/BrokenNes.Web.csproj --urls http://localhost:5099`
(final port; see "Testing-environment notes" below for why the port changed mid-session).

Loaded the page in a fresh, isolated tab and, in one atomic JS execution against that tab
(`href` verified as `http://localhost:5099/` inside the same script), sampled every second for
15 real seconds while independently instrumenting three things at once: the on-page status text,
a hash of the actual canvas pixel buffer, a freshly-injected `requestAnimationFrame` probe, and a
control `setInterval` probe.

Result — all 10 one-second samples, verbatim:

```
t≈1..15s: status = "RUNNING 0.0 fps frame 0 demo.nes"   (unchanged, all 10 samples)
          canvasHash = 1097670149                        (unchanged, all 10 samples)
          rafTotal = 0                                    (never once fired)
          intervalTotal = 1,2,4,6,8,10,11,13,14,15        (fires throughout, on schedule)
          visibilityState = "hidden", hasFocus = false    (all 10 samples)
```

This is an exact, clean reproduction of the reported symptom: "frame 0", "0.0 fps", never
advancing even after several seconds — confirmed with the counter literally frozen for the full
15-second window in a single uninterrupted observation (no core-dependence checked further since
the loop never runs regardless of core selection, matching the original report).

## 2. Mechanism — why it stalls (with file:line evidence)

**How the frame counter is supposed to update**, tracing from the UI backward:

- `Web/Pages/Nes.razor:25` binds `frame @(emu?.FrameCount ?? 0)` and line 24 binds
  `@(emu?.Fps.ToString("F1") ?? "0.0") fps` — straightforward, correct bindings to
  `Emulator.FrameCount`/`Emulator.Fps`.
- Those fields are mutated in exactly one place: `Web/Emulation/Emulator.cs:283-313`
  (`BuildFrame()`) — `FrameCount++` at line 291, `Fps = ...` at line 301 (recomputed every
  0.5 real seconds of accumulated frames). `BuildFrame()` also calls
  `OnStateChanged?.Invoke()` (line 304) which is wired to `InvokeAsync(StateHasChanged)` in
  `Nes.razor:102` — **this reactivity path is correct**; every time `BuildFrame()` actually
  runs, the DOM does visibly update (confirmed independently mid-session, see below).
- `BuildFrame()` is invoked from exactly one path in this build: the JS-driven
  `[JSInvokable] FrameTick()` (`Emulator.cs:317`), called from `nesInterop.js`.
- `nesInterop.js:44-92` (`startEmulationLoop`) is the sole driver of that call, and it is
  **100% `requestAnimationFrame`-driven**: the `step` closure is only ever re-armed via
  `requestAnimationFrame(step)` (lines 67, 89, 91) — there is no `setInterval`/`setTimeout`
  fallback anywhere in this loop.
- This is confirmed as a deliberate, load-bearing design choice, not an oversight:
  `Web/BrokenNes.Web.csproj` (the clocks `<Compile>` block, ~line 111) explicitly **excludes**
  `CLOCK_CLR.cs` and `CLOCK_TRB.cs` from the Web build, with a comment explaining both would
  *permanently hang the tab* in single-threaded WASM (`Task.Run` doesn't get a real background
  thread there). `CLOCK_FMC` (`Windows/NesEmulator/clocks/CLOCK_FMC.cs`) is therefore the
  **only** clock this build can ship, and its own doc comment says so explicitly: "delegates
  cadence entirely to the JS requestAnimationFrame loop... it's the only one that belongs in a
  WASM build." So: rAF is not just *a* pacing mechanism here, it is architecturally the *only*
  one available to this build.

**What's actually happening**: `requestAnimationFrame` never fires for this tab. Verified
directly — a freshly-injected, completely independent `requestAnimationFrame` probe (added by my
own test script, unrelated to the app's code) registered **zero** callbacks across a 15-second
window on the same page, while a control `setInterval` on the same page fired 15 times almost
exactly on its 1 Hz schedule during that same window. Since `startEmulationLoop`'s `step` is
`requestAnimationFrame`-armed only, "rAF never fires" ⇒ "`step` never runs again after its first
scheduling" ⇒ `FrameTick`/`BuildFrame()` never run ⇒ `FrameCount`/`Fps` never change — while
everything else in the WASM app (timers, the DOM, the `IsRunning`/`RomName` bindings, console,
network) stays completely healthy. This is a real stall of the emulation loop itself, not a
`StateHasChanged()`/reactivity gap: the canvas's actual pixel buffer (read via
`getImageData` and hashed, not just eyeballed) was byte-for-byte identical across all 10 samples,
confirming `RunFrame()` itself never executed — not merely that the redraw was skipped.

**Why rAF never fires here**: the page consistently reported `document.visibilityState ===
"hidden"` / `document.hasFocus() === false` for the duration of the clean, final reproduction
above, even immediately after explicitly making that tab the "active" one via the browser tool's
own tab-selection call. Per the Page Visibility spec, browsers are free to suspend rAF entirely
for a hidden/non-composited page, and Chromium does. This UAT is being driven from an unattended,
headless-style browser-automation pane with no human present to actually look at a real screen,
so no tab in it is ever truly composited to a display — which is sufficient on its own for
Chromium to withhold rAF regardless of which tab the automation layer calls "active".

## 3. Loop-not-running vs. UI-not-refreshing — resolved

The task asked specifically which of these it is. Answer: **the loop is not running (a real
stall)**, not a UI refresh gap:

- Canvas pixel content never changes during the stall (hashed, not just visually inspected) —
  rules out "frames are computed but not drawn."
- `setInterval` keeps firing on schedule throughout — rules out a full WASM/JS freeze or an
  unhandled exception taking down the runtime (also corroborated by zero console errors and all
  `_framework/*.wasm` + `roms/demo.nes` network requests returning 200 OK throughout).
- Mid-session (before isolating the final clean repro above), I confirmed the reactivity path
  itself is sound: while a different tab happened to be genuinely visible/active for a stretch,
  `FrameCount`/`Fps` visibly climbed in the DOM in step with real elapsed time, with no code
  changes — i.e., when `BuildFrame()` *does* run, the bound UI text updates correctly every time.
  So there is no missing `StateHasChanged()`/binding defect layered on top; the entire chain
  downstream of `BuildFrame()` is correct. The only broken link is upstream of it:
  nothing is calling `BuildFrame()` at all, because nothing is calling `step`, because rAF isn't
  firing.

## 4. Is this a real BrokenNes.Web bug, or a testing-environment artifact?

Both, in different ways:

- **Not a code defect in the traditional sense.** A real user with the tab open, focused, and
  visible in an ordinary foreground browser window will get rAF exactly as designed, and per
  `Web/README.md`'s own measured numbers, a Debug/no-AOT build should reach roughly 13 fps (not
  60, but very much not "0 fps forever"). Nothing here indicates that path is broken.
- **This exact freeze is genuinely reachable by real users, though**, any time the tab is
  backgrounded — switched away from, or the window minimized — which is completely standard,
  intentional browser behavior for *any* rAF-driven site, not specific to BrokenNes. The app is
  even visibility-aware in principle: `Nes.razor:112` wires up `nesInterop.registerVisibility`,
  which calls `[JSInvokable] JsVisibilityChanged` (`Emulator.cs:376-378`) →
  `_activeClock?.OnVisibilityChanged(visible)`. But `CLOCK_FMC.OnVisibilityChanged`
  (`CLOCK_FMC.cs:28-31`) is a no-op with the comment "JS rAF is already visibility-friendly;
  nothing to do" — true for *pacing* (rAF throttling itself prevents runaway catch-up), but it
  means there is **no user-facing signal** that a frozen counter is "expected, tab is
  backgrounded" versus "the app has crashed." That ambiguity is a legitimate, low-cost UX gap
  worth considering (e.g., visibly showing "paused — tab not visible" instead of a live-looking
  "RUNNING 0.0 fps"), even though the underlying rAF suspension is correct, standard browser
  behavior and not something the app should try to defeat.
- **The specific instance the prior manual check (and this check) hit is almost certainly the
  automation harness, not a backgrounded end-user tab.** This UAT runs unattended, with no human
  ever looking at a real screen, so the browser-automation pane's tabs are never truly composited
  — the exact condition that starves rAF. That almost certainly explains why the "prior manual
  check" — very likely performed by another agent through this same kind of tool — saw the same
  permanently-stuck counter regardless of core selection: it isn't core-related because it has
  nothing to do with the emulator cores at all.

## 5. Fix attempted?

No. Per the task's own guidance, I only apply a fix if it's a trivial, obviously-safe one-liner
(e.g. a missing `StateHasChanged()`). That's not what this is — the actual fix would be adding a
non-rAF fallback pacing path (or at minimum a "backgrounded" UI state) to `nesInterop.js`'s
`startEmulationLoop`, which is a real design change to the pacing model, explicitly out of scope
here ("do not attempt a larger change"). No source files were modified.

## 6. Testing-environment notes (not a BrokenNes bug, but affects how to read this report)

This session ran in a browser-automation pane **shared across multiple parallel UAT agents**,
which caused some noisy, initially-confusing intermediate readings before I isolated the clean
repro above — noted here for transparency, not as findings about the app:

- My first `dotnet run --urls http://localhost:5023` instance actually **crashed at startup**
  with `AddressInUseException` (another process had already bound 5023 — likely a collision with
  a concurrent sibling agent despite the "pick a distinct port" guidance). I didn't notice this
  immediately because `curl`/the browser were transparently hitting some *other* already-running
  server on that same port, which produced several inconsistent/contradictory readings (including
  once observing a different ROM name, `test.nes`, that I never loaded). I later confirmed via
  `Get-CimInstance Win32_Process` that my own process for port 5023 was not among the running
  `dotnet` processes, moved to a fresh port (5099), and re-ran the full diagnosis cleanly on a
  server and tab I could positively verify end-to-end (`location.href` checked inside the same
  script that took the measurements).
- The browser tool's `tabId` targeting was not fully reliable under this shared, concurrent load:
  a couple of `computer`/`javascript_tool` calls executed against another agent's tab despite an
  explicit `tab-1` being requested, and my own tab was closed out from under me once by
  (presumably) another agent's session. All conclusions in this report are from calls where I
  positively verified the executing tab and origin inside the same script call that took the
  measurement, so this instability doesn't undermine the diagnosis above — but it's worth knowing
  about if other areas' findings from this same sweep look inconsistent.
- Dev server for this task was stopped (port 5099, confirmed via `curl` returning no response
  after stopping the process). No server was left running from this session.

## Files referenced

- `Web/Pages/Nes.razor` (status bindings, lines 23-26; boot sequence, `OnAfterRenderAsync`)
- `Web/Emulation/Emulator.cs` (`BuildFrame()` lines 283-313; `FrameTick` line 317;
  `JsVisibilityChanged` lines 376-378)
- `Web/wwwroot/lib/nesInterop.js` (`startEmulationLoop` lines 44-92)
- `Web/BrokenNes.Web.csproj` (clock `<Compile>`/`Exclude` block explaining CLR/TRB exclusion)
- `Windows/NesEmulator/clocks/CLOCK_FMC.cs` (rAF-only design, `OnVisibilityChanged` no-op)
- `Web/README.md` ("Performance and AOT" table — expected ~13 fps in Debug when rAF *does* fire)
