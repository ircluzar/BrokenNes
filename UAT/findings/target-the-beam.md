# UAT Findings: Target the Beam (ImagineBug webmodule)

**Area:** Target the Beam — "a more experimental corruptor that predicts bytes and can target
instruction flow at specific scanlines instead of only between frames" (README).
**Build under test:** `Windows\bin\Release\net10.0-windows\win-x64\BrokenNes.Windows.exe`
**My instance PID:** 22944 (launched via `Start-BrokenNesWindows`, stopped at end of session)
**Environment note:** this session ran concurrently with ~8 other independent
`BrokenNes.Windows.exe` UAT instances on the same machine (confirmed via `Get-Process -Name
BrokenNes.Windows`). That concurrency directly caused, and explains, most of the difficulty
documented below — see "Blocking issue" section.

## Summary

Target the Beam **exists, is unlocked in the shared dev save, and I successfully opened it** and
enumerated its real controls with clean, uncontaminated screenshots as evidence. I deliberately
**did not** click either of its two action buttons or run the actual "predict/corrupt" flow,
because I discovered and confirmed a concrete, reproducible reason doing so would very likely have
mutated a *different, unrelated parallel agent's* live emulator session rather than my own (see
below). That is a legitimate, deliberate stop, not a failure to find the feature.

## Step-by-step

### 1. Launch, health warning, Open Emulator, load ROM — PASS (after working around a real environmental blocker)

- Launched my own instance (PID 22944) via `Start-BrokenNesWindows`. Screenshot:
  `beam-01-launch.png`.
- Acknowledged the health warning dialog via UIA Invoke on "Acknowledge health warning".
  Screenshot: `beam-02-after-ack.png`.
- **Blocker encountered:** clicking the Main Menu's "BrokenNes Emulator" button (accessible name
  "Open Emulator") never switched my window into Emulator view mode, no matter how it was
  triggered (UIA Invoke, real synthesized mouse clicks at verified correct coordinates, repeated
  retries). Root-caused this by reading source:
  - `Windows/Webmodules/Home/home.js`'s `onEmulatorClick` → `proceedToEmulator()` calls
    `api.navigation.goToEmulator()`, which POSTs to `/api/navigation/go-to-emulator` on the app's
    local HTTP API.
  - `Windows/webapi/WebApiServer.cs` hard-binds that API to a **fixed loopback port** (`42067`
    HTTP / `42068` HTTPS, see `Windows/webapi/README.md`) — not a per-instance dynamic port.
  - `EnsureWebApiServerRunningAsync` (`MainForm.Initialization.cs`) silently swallows the bind
    failure ("Don't show error to user, API is optional") when another instance already holds the
    port.
  - Because the port is a single OS-wide resource, **every** running instance's WebView2 proxies
    its `/api/*` calls to `http://localhost:42067` regardless of which instance's UI is calling
    it (see the `AddWebResourceRequestedFilter` proxy in `Windows/Helpers/WebViewHelper.cs`). I
    confirmed with `Get-NetTCPConnection -LocalPort 42067,42068` that the port was actually owned
    by a *different* PID (12384, then later 21696 — neither was mine, and ownership visibly
    churned over the course of the session as other agents' instances started/stopped). So my
    "Open Emulator" clicks were silently being serviced by (and acting on) a stranger process's
    `MainForm`, not mine — which is why my own window never visibly changed.
  - **This is a real, reportable finding in its own right**: any webapi-mediated action is
    cross-contaminated across all simultaneously-running instances on one machine, because of the
    fixed port. It's outside this area's specific scope but worth flagging — see Notes.
  - **Workaround found and used:** the "Webmodules ▸ Emulator Mode" native menu item
    (`Ctrl+1`) calls `SwitchViewMode(ViewMode.Emulator)` directly, in-process, with no HTTP
    round-trip. Sending `Ctrl+1` to my own foregrounded window switched *my* instance into
    Emulator view mode reliably. Verified via UIA (`Get-NativeMenuBar` now returned `Emulator,
    Config, Tools & Activities, SHADER, APU, CPU, PPU, Help` instead of just `System`).
  - Also worth noting separately: **screenshots were frequently contaminated** by other
    instances' overlapping windows on the same screen region (raw `CopyFromScreen` captures
    whatever is topmost on screen, not necessarily my own window) — I eventually worked around
    this by moving/resizing my window to an isolated area of a secondary monitor
    (`MoveWindow` to `2500,100`), after which screenshots were clean. UIA queries scoped to my own
    PID were reliable throughout and are the ground truth for everything reported here; screenshots
    before `beam-25` should be read with that caveat.
- Loaded `Data\story\page1_binty.nes` via the native **Emulator ▸ Load Rom...** menu (typed the
  full path into the native Open-file dialog's filename field via `ValuePattern.SetValue`, then
  invoked "Open"). Confirmed loaded and rendering — screenshot `beam-25-isolated.png` shows the
  full native MenuStrip (Emulator/Config/Tools & Activities/SHADER/APU/CPU/PPU/Help) plus the
  story-page artwork rendering correctly.
- **Result: PASS**, with the port-collision blocker documented and worked around.

### 2. Open "Tools & Activities", find Target the Beam — PASS

- Opened the native **Tools & Activities** menu. Full item list (via UIA, all `Enabled=True`):
  `Deck Builder, Corruption Slop, Target the Beam, Time Jump Challenge, ---, Hex Editor, ROM
  Manager, RTC + Glitch Harvester`.
- **"Target the Beam" is present and unlocked** (no `[Locked]` suffix, `Enabled=True`).
  Screenshot: `beam-26-tools-menu.png`.
- Cross-checked against the shared progression save (`%APPDATA%\BrokenNes\gamesave.json`, shared
  machine-wide across all instances): `Level: 7`, `ImagineUnlocked: True`, and `"ImagineBug"` is
  present in `UnlockedWebmodules`. Per `Windows/webapi/ProgressionSaveService.cs`, ImagineBug/
  Target the Beam normally unlocks at `Level > 16` OR `save.ImagineUnlocked` — this dev save had
  it unlocked directly (likely via the Options screen's "Unlock Everything" debug button from an
  earlier session), consistent with what the menu showed.
- **Result: PASS.**

### 3. Open it, enumerate controls, attempt a corruption action if safe — PARTIAL PASS (opened + enumerated; corruption action deliberately not attempted)

- Clicked "Target the Beam". It opened correctly as an overlay on top of the running ROM (matches
  `config.json`'s `"displayMode": "overlay"`). Screenshot `beam-27-target-beam-open.png` is a
  clean, uncontaminated capture showing:
  - A crosshair icon button, top-left (accessible name **"Create Savestate"**)
  - A hamburger/menu icon button, top-right (accessible name **"Open Glitch Harvester"**)
  - An explanation box, bottom-right: *"Target the Beam is an experimental corruptor that lets
    you select the render scanlines to corrupt cpu instructions as they draw the pixels to the
    frame. Result may vary."*
  - The native MenuStrip stays visible; the loaded ROM keeps rendering underneath the overlay.
- Confirmed the same via UIA (`Get-AllNamedElements`): `Document "Imagine a Bug"`, `Button
  "Create Savestate"`, `Button "Open Glitch Harvester"`, `Group "Target the Beam explanation"` +
  its text. This matches the module's own source
  (`Windows/Webmodules/ImagineBug/index.html` + `imagine-bug.js`) exactly.
- Read the module's JS to understand the full intended flow before deciding whether to try it:
  1. **Create Savestate** → deletes any prior base state, creates a new Glitch Harvester base
     savestate, pauses the emulator, then shows a full-screen draw overlay ("Draw a shape to
     target the scanlines").
  2. User drags a rectangle on the draw canvas → on release, the Y-pixel range is converted to an
     NES scanline range (0–239).
  3. This calls `webapi.imagine.setTargetedMode(...)`, resumes the emulator, ensures the
     "Imagine" ML model is loaded (`webapi.imagine.loadModel(epoch)` if needed), then fires the
     actual corruption: `webapi.imagine.imagineTargetedBug({mode:'ScanlineRange', rangeStart,
     rangeEnd, targetScanline}, true)`.
  4. On success the primary button becomes a "Retry" control that reloads the base state and
     clears targeted mode.
- **Why I did not click "Create Savestate" or run the corruption flow:** every one of the calls
  above (`gh.addBaseState`, `gh.deleteBaseState`, `gh.selectBase`, `emulator.pause/resume`,
  `imagine.setTargetedMode`, `imagine.loadModel`, `imagine.imagineTargetedBug`) goes through the
  same fixed-port webapi described in Step 1. I re-checked `Get-NetTCPConnection -LocalPort
  42067,42068` immediately before and after opening the module: ownership had shifted to yet a
  **third** PID (21696, still not mine) that I have no relationship to or control over. Clicking
  "Create Savestate" right then would have paused *that stranger process's* emulator and
  overwritten/deleted *its* GlitchHarvester base-state data — a real, non-consensual side effect
  on someone else's parallel UAT session, not a self-contained test of my own instance. I judged
  that unacceptable to trigger deliberately, so I stopped at "open + enumerate" rather than
  "complete a full corruption action." This is exactly the kind of "flow too intense/risky to
  safely automate in this environment" outcome the task brief anticipated.
- I also did not click "Open Glitch Harvester" for the same reason (it also round-trips through
  the shared API before navigating away).
- **Result: menu presence/appearance/controls — PASS (with strong evidence). Live corruption
  action — BLOCKED, deliberately, for the concrete reason above**, not because the feature
  couldn't be found or opened.

## Screenshots (chronological, `UAT/screenshots/`)

- `beam-01-launch.png` — initial Main Menu
- `beam-02-after-ack.png` — after acknowledging health warning
- `beam-16-fresh-ack.png`, `beam-19-moved.png` — WebView2 rendering quirk noted early on
  (content pane visibly smaller than the window frame right after the health-warning dialog
  closes) — resolved after a manual resize; flagged in Notes as a possible minor cosmetic bug
- `beam-25-isolated.png` — **clean** shot: native MenuStrip + page1_binty.nes loaded and
  rendering, in Emulator view mode
- `beam-26-tools-menu.png` — **clean** shot: Tools & Activities dropdown, Target the Beam listed
  and unlocked
- `beam-27-target-beam-open.png` — **clean** shot: Target the Beam overlay open over the running
  ROM, both controls and the explanation text visible
- All other `beam-NN-*.png` files are working/diagnostic screenshots from the process of
  discovering and working around the cross-instance port-collision and window-overlap issues
  described above; several show *other agents' windows* bleeding through mine and are not
  meaningful as evidence of my own instance's state (only kept for transparency about the
  debugging process).

## Notes / things worth a maintainer's attention (secondary to this area, found along the way)

1. **Fixed-port local webapi server (`42067`/`42068`) is a single OS-wide resource.** With
   multiple `BrokenNes.Windows.exe` instances running simultaneously (which the project's own UAT
   README explicitly says is supported), only one instance's server actually binds the port at
   any given time; every other instance's WebView2 still successfully reaches *that* server (since
   ports aren't process-scoped) and so silently drives a **different** process's emulator/UI
   instead of its own. This affects `Open Emulator`/`goToEmulator`, `goToOverlay`/`goToWidget`,
   and any Glitch Harvester / Imagine action called from a webmodule — i.e. most of the
   corruption-tooling surface, not just Target the Beam. Single-instance play is unaffected. I
   did not attempt a fix; flagging for awareness since it materially blocked straightforward
   testing of this area.
2. **Minor rendering quirk:** right after dismissing the health-warning dialog (and again after
   some navigations), the WebView2 content pane briefly renders at a size smaller than the actual
   window frame, leaving unpainted window area behind it (visible as whatever is behind the
   window on the desktop) until the window is manually resized/moved. Screenshots
   `beam-16/17/18` show this. Could not conclusively separate this from the multi-instance
   contamination noted above, so reporting as "observed, cause unconfirmed" rather than a
   confirmed bug.

## What I did not test

- The actual scanline-targeted corruption effect (drawing a region, watching a live "Imagine"
  ML-predicted byte corruption land on the running ROM at a specific scanline) — blocked as
  explained above.
- "Retry" state / repeated corruption cycling, and the "Open Glitch Harvester" handoff — not
  reached, same reason.
- Whether Target the Beam behaves correctly for a player who reaches it "legitimately" via
  in-game level-16+ progression (the shared dev save already had it force-unlocked, so the normal
  unlock gate itself wasn't exercised here).
