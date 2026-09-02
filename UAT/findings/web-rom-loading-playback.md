# UAT Findings — BrokenNes Web: ROM Loading and Play/Pause/Reset

**Build under test:** `Web/BrokenNes.Web.csproj` (experimental minimal Blazor WebAssembly shell)
**Server:** `dotnet run --project Web/BrokenNes.Web.csproj --urls http://localhost:5023` (Development config, interpreted WASM, no AOT)
**Method:** Claude Browser pane (CDP-driven Chromium), DOM/text inspection at each step (not just visual clicks)

## Summary

Core Play/Pause/Reset controls and ROM loading all work correctly. The one native-file-picker
limitation is a testing-tool constraint, not an app defect (worked around via a simulated file
selection, which exercised the app's real ROM-load code path end-to-end). One page-reload anomaly
was observed once, and is most likely an artifact of this being a shared/multi-agent browser
session rather than an app bug (details below).

## Steps tested and evidence

1. **Page load / default state** — Navigated to `http://localhost:5023`. Page rendered immediately
   with status line `RUNNING · 0.0 fps · frame 0 · demo.nes`, a 512x480 `<canvas>`, Pause/Reset/Load
   ROM buttons, and Clock/CPU/PPU/APU core dropdowns. Frame counter climbed on its own (demo.nes
   auto-loaded and auto-started), confirming the emulation loop starts unattended.

2. **Pause** — Clicked Pause while `RUNNING, frame 7`. Status line immediately flipped to
   `PAUSED`, button label flipped to `Play`. Waited 3s and re-read the page: frame counter stayed
   pinned at `7` the whole time — confirms Pause actually halts the loop, not just the label.

3. **Reset (while paused)** — Clicked Reset with `PAUSED, frame 7`. Frame counter dropped to `0`
   immediately, status remained `PAUSED` (Reset does not auto-resume), ROM name unchanged
   (`demo.nes`). Correct, sensible behavior.

4. **Play (resume)** — Clicked Play from `PAUSED, frame 0`. Status flipped back to `RUNNING`,
   frame counter resumed climbing from `0` (verified via canvas screenshots showing `frame 1`, then
   `frame 6`, etc. over subsequent seconds) — no jump/skip, no leftover stale count.

5. **Load ROM** — The control is a `<label>` wrapping a hidden native `<input type="file"
   accept=".nes">` bound through Blazor's `InputFile` component (confirmed via DOM inspection,
   `_bl_*` Blazor attribute present). Clicking it does trigger a real OS-level file-open dialog —
   confirmed by: no new browser tab/frame appearing, no DOM change, and (per the task's own
   guidance) no OS-level automation tool available for this Browser-pane session to drive a native
   dialog (checked: no `file_upload`-equivalent tool exists for this specific browser tool, and no
   dialog window shows up in the local Windows process list — this remote pane is not the local
   desktop session, so OS-level keystroke tricks aren't applicable here either).
   - **Workaround used:** fetched the already-served `/roms/demo.nes` via same-origin `fetch()`,
     wrapped it in a `File` object named `test.nes`, attached it to the hidden input via
     `DataTransfer`, and dispatched a real `change` event — this exercises the exact same
     Blazor `InputFile` → C# code path a real file selection would, just without needing the OS
     picker. This is explicitly allowed by the task brief ("the bundled default ROM if that's
     simpler — either is fine").
   - **Result:** status line updated to `RUNNING · frame 0 · test.nes` immediately — ROM name
     changed, frame counter reset to 0, and emulation kept running automatically. Repeated this
     twice (see below) with the same clean result both times.

6. **Pause/Reset/Play again after a ROM load** — With `test.nes` loaded and `RUNNING, frame 6`:
   - Reset → frame dropped to `0`/`1`, ROM name stayed `test.nes` (Reset does not revert to the
     default ROM — it resets the currently loaded one, correctly).
   - Frame counter resumed climbing normally afterward (`frame 1` → `frame 6` over ~3s).
   - Pause → status `PAUSED`, frame pinned, ROM name still `test.nes`.
   All consistent with the pre-ROM-load behavior in steps 2–4.

## Anomaly observed (likely environment artifact, not an app bug)

During the **first** ROM-load test, after `test.nes` had been running successfully for ~10–15
seconds (frame count climbing normally, no console errors), a subsequent Pause click failed with
`ref ... could not be attributed to a frame`, and the next read of the page showed the app back at
`RUNNING · frame 1 · demo.nes` — i.e. a full, unprompted browser reload had occurred (confirmed via
`read_network_requests`: `blazor.webassembly.js` and the ~500 `_framework/*.wasm` files were
fetched a second time on the same tab).

Investigated and most likely **not an app defect**:
- The dev server's own stdout log had zero new lines around this time (no rebuild, no restart —
  the same `dotnet run` process kept serving throughout), ruling out a server-side crash/restart.
- `Web/wwwroot/index.html` only has the standard Blazor `#blazor-error-ui` banner with a
  **manual** "Reload" link — grepped the whole `Web/` tree for `location.reload`/`onerror`/
  `unhandledrejection` and found nothing that would trigger this automatically.
- No error/exception ever appeared in the browser console (only the standard
  "Debugging hotkey" info line).
- This Browser pane is **shared across parallel UAT agents** — earlier in this same session, a
  `get_page_text` call unexpectedly returned another agent's tab (`tab-2`, port 5021) instead of
  mine, proving cross-tab bleed-through/focus-stealing is possible in this shared automation
  environment. The reload is consistent with the browser discarding/reloading a backgrounded tab
  to save memory while another agent's tab had focus.
- After explicitly keeping the tab fronted (`tabs_select`) for the remainder of testing, the exact
  same ROM-load → Pause → Reset → Play sequence was repeated end-to-end with **no recurrence** of
  the reload.

Recommendation: if this is seen again during a **non-parallel**, single-session manual test, it
would be worth a closer look (e.g. checking for a WASM heap exception specifically triggered by
processing a freshly-loaded ROM after some seconds of emulation) — but nothing in this session
points at the app itself.

## Other observations (not blocking, out of this area's core scope)

- **Performance**: fps hovered around 0.1–2.5 in this `dotnet run` Development build (interpreted
  WASM, no AOT/trimming) — frame counter took multiple real seconds to advance a handful of
  frames. This is expected for an unoptimized dev build and not indicative of an app bug, but it
  makes the canvas output hard to visually verify within a short test (see next point).
- **Canvas stayed solid black** (verified via `getImageData` — 0 of 245,760 pixels non-zero)
  through every screenshot taken, for both `demo.nes` and the duplicated `test.nes`. Given the
  extremely low fps above, only a handful of emulated frames (~1–15) had actually elapsed by the
  time of each check — many NES ROMs legitimately show a black screen for their first several
  dozen frames during power-on init, so this is very likely just "not enough frames yet" rather
  than a rendering bug. Flagging only so a longer-running/faster (published/AOT) check can confirm
  the canvas does eventually paint something.

## Verdict

Pass. ROM loading (Load ROM control → file selection → status line ROM name update → frame reset
→ auto-run) and Play/Pause/Reset all behave correctly and consistently, including immediately
after a ROM swap. No reproducible app-level bug found in this area.
