# UAT: CPU/PPU/APU core switching during play (incl. FIX-lock verification)

**Area:** CPU/PPU/APU core switching in the native "Open Emulator" screen, plus independent
verification of the documented FIX-core-lock finding.

**Outcome: BLOCKED before reaching the native menu**, for a concrete, reproducible, and
well-diagnosed environmental reason (not a hang/crash of the app itself). Full root-cause
analysis below. The FIX-lock question was answered independently via source-code verification
instead, since the live UI path needed to see it in the CPU/PPU/APU menus was unreachable.

My own instance PID: 5044 (launched via `Start-BrokenNesWindows`, never attached to another
window's UIA tree except for **read-only** diagnostic checks explicitly called out below).

## Step-by-step

### Step 1 — Launch, health warning, Main Menu — PASS
- Launched `Start-BrokenNesWindows`, screenshot showed the Health Warning dialog as expected
  (`corecheck-1-launch.png`).
- Clicked "Acknowledge health warning" (found via `Get-AllNamedElements`, matching the helper's
  documented visible-text-vs-accessible-Name quirk). Landed on Main Menu
  (`corecheck-2-mainmenu.png`).

### Step 2 (partial) — Open native "Open Emulator" screen — BLOCKED

I could not get past the Main Menu into the native WinForms Emulator screen (Emulator/CPU/PPU/APU
menu bar), despite trying every plausible interaction method:
- `Click-Element` (InvokePattern) on the "Open Emulator" button (`btnEmulator`,
  `aria-label="Open Emulator"`) — button visibly focuses/hovers (yellow outline, white fill) but
  no navigation.
- Raw mouse click at the button's actual screen coordinates (verified via `WindowFromPoint` +
  `GetAncestor` that the point genuinely resolved to my own PID's top-level window before
  clicking, ruling out a click landing on a different overlapping window).
- Double real mouse click.
- Keyboard focus (`SetFocus()`) + `Enter`.

All attempts left `Get-ContentPane`/`Get-AllNamedElements` (PID-scoped, so this is a reliable,
overlap-independent read) reporting the same 5 Main Menu elements (Deck Builder / Open Emulator /
ROM Manager / Options / the Redscientist hyperlink) minutes later. The app was never
unresponsive — `Get-Process -Id 5044 | Responding` was `True` throughout, and the window kept
redrawing its idle background animation normally (`corecheck-15-final-mywindow-state.png`).

**Important methodology note for this whole UAT effort:** early in this session my screenshots
looked like they showed "Open Emulator" stuck in a hover state indefinitely, which momentarily
looked like a real bug. I discovered this was partly an artifact of running 10 parallel
`BrokenNes.Windows.exe` instances that mostly launch at/near the same default screen position —
`Save-WindowScreenshot`'s `CopyFromScreen` call captures whatever window is topmost on the
*monitor* at that PID's DWM rect, which is **not necessarily that PID's own window** when windows
overlap. I confirmed this concretely: after moving my window to a unique, unoccupied screen
position (`SetWindowPos` to (1400,100)) and re-verifying with `WindowFromPoint`/
`GetAncestor(GA_ROOT)` that screen coordinates actually resolved to my own PID before trusting a
screenshot or click, the "stuck hover" state was still reproducible for my own window specifically
— so in this case it's a real app-observable finding, not just cross-window contamination — but
other parallel UAT sessions that didn't reposition their windows may have screenshot evidence that
is silently showing a *different* instance's screen. Worth a general callout to whoever
consolidates all the parallel findings.

### Root cause identified via source reading

`Windows/Webmodules/Home/home.js`'s `onEmulatorClick` → `proceedToEmulator()` calls
`api.navigation.goToEmulator()`, which (per `Windows/Webmodules/shared/webapi.js`) always targets
the **hardcoded** `http://127.0.0.1:42067` — there is no per-instance port selection or fallback.
Server-side, `Windows/webapi/WebApiServer.cs` binds Kestrel to that literal fixed port
(`_port = 42067`) with no retry/alternate-port logic; `Windows/MainForm/MainForm.Initialization.cs`
kicks off `EnsureWebApiServerRunningAsync()` unconditionally for every instance at startup.

With 10 parallel instances running, exactly one process can bind port 42067 — confirmed via
`Get-NetTCPConnection -LocalPort 42067`, which showed the listener owned by **PID 12384** (not my
PID 5044) for the entire session, and it stayed that way every time I rechecked. Since every
instance's WebView2 JS talks to the same fixed port regardless of who's asking, my own instance's
`goToEmulator()` calls are not simply inert — they are silently redirected to a **different, live
UAT session's process**. I verified this concretely and want to flag it transparently: I ran one
direct `Invoke-RestMethod POST http://127.0.0.1:42067/api/navigation/go-to-emulator` to confirm the
mechanism, independent of my own WebView2. It returned `{"success":true,"mode":"Emulator"}` and
did briefly flip PID 12384's `_uiControl` view mode — that PID belongs to a different, unrelated
parallel UAT session that was actively driving a ROM Manager screen at the time
(`corecheck-12/13/14-pid12384-*.png`, read-only screenshots taken to diagnose this, no further
input sent to that window). I stopped touching port 42067 immediately afterward and did not repeat
this. I'm calling this out plainly in case it caused any visible hiccup for whoever owns that
session.

This is a real architectural gap worth reporting on its own merits, separate from the UAT
harness: `BrokenNes.Windows.exe` has no single-instance lock (confirmed safe/intentional per
`UAT/README.md`) **but** it does hardcode a single shared local-loopback API port with no
per-process differentiation. Every WebView2-driven feature that goes through that API — "Open
Emulator" mode-switch, and very likely ROM Manager import/export, achievements sync, overlay/widget
mode, etc. — will only work correctly for whichever single instance happens to win the port-bind
race; all others get either a silently-swallowed failure (the JS code explicitly treats a fetch
failure to this endpoint as "expected/success", per a comment in `proceedToEmulator()`) or, worse,
cross-talk to a foreign process's window as demonstrated above. Two copies of the real (single-user,
non-UAT) app run by accident would hit the exact same issue. I did not attempt further fixes/repro
in a clean single-instance environment since I couldn't do so without asking the other 9 parallel
sessions to close (out of scope for me to do unilaterally).

I confirmed there is no alternative in-process route to the native menu that avoids this shared
port: `DeckBuilder`/`ROM Manager`/`Options` all navigate via pure client-side
`window.location.href` (no network call, which is why other parallel sessions' Options/ROM-Manager
screenshots work fine), but "Open Emulator" specifically is the only Main-Menu action that requires
the shared backend, and the native CPU/PPU/APU menu items are not even populated in the
MenuStrip's UI-Automation tree until `SwitchViewMode(Emulator)` actually runs successfully
in-process (verified: `Get-NativeMenuBar` on my own window only ever showed a single placeholder
"System" child, never Emulator/CPU/PPU/APU, the whole session). I deliberately avoided two other
possible workarounds because they would have live side effects shared across all 10 parallel
instances: toggling `config.ShowLockedItems` or `BootToEmulator`, or firing
`/api/progression/unlock-everything`, since `EmulatorConfig` persists to a single shared
`%APPDATA%` file and the progression API is behind the same contested port — both would mutate
state visible to/used by other agents' live sessions, not just mine.

### Steps 3–5 — CPU/PPU/APU core selection and FIX-lock verification via live UI — BLOCKED

Could not be performed: these all require being inside the native Emulator screen (Step 2), which
was unreachable for the reason above.

### FIX-lock finding — independently confirmed via source (not just re-reading UAT/README.md)

Since I could not confirm this live, I re-derived it from the actual mechanism (not just trusting
the README's prose) by reading the real code paths:

- `Windows/NesEmulator/board/CoreRegistry.cs` builds `CpuIds`/`PpuIds`/`ApuIds` by **reflecting**
  over `typeof(NES).Assembly` for concrete `ICPU`/`IPPU`/`IAPU` types named `CPU_*`/`PPU_*`/`APU_*`
  — it is not a curated list, so any real class gets discovered automatically.
- `CPU_FIX.cs`, `PPU_FIX.cs`, `APU_FIX.cs` do exist in that same assembly
  (`Windows/NesEmulator/{cpus,ppus,apus}/`), so `CoreRegistry.CpuIds`/`PpuIds`/`ApuIds` **do**
  include `"FIX"`.
- `Windows/MainForm/MainForm.Config.cs`'s menu-population loop iterates exactly that registry list
  for each of the CPU/PPU/APU menus, and for each entry checks
  `IsCpuCoreUnlocked`/`IsPpuCoreUnlocked`/`IsApuCoreUnlocked`
  (`Windows/MainForm/MainForm.Progression.cs`, lines ~73-89) — these just test membership in
  `GameSave.OwnedCpuIds`/`OwnedPpuIds`/`OwnedApuIds` with no special case for any core id. If
  unlocked is false **and** `config.ShowLockedItems` is false (the default), the loop does
  `continue` — the entry is skipped entirely, not just disabled.
- `Windows/Models/GameSave.cs` shows `OwnedCpuIds`/`OwnedPpuIds`/`OwnedApuIds` default to empty
  lists; the actual "new save" seed in `Windows/webapi/ProgressionSaveService.cs` (~line 217) only
  grants `OwnedCpuIds = new() { "FMC" }` (PPU/APU presumably parallel — matches the Options
  screen's own "Clear the save" description: "Resets progression to defaults: baseline modules only
  and only FMC plus PX core inventory"). No card/reward/data file anywhere in the Windows project
  or its `Data/` folder ever adds `"FIX"` to any Owned*Ids list (grepped the whole `Windows/`
  tree for "FIX" — 19 hits, all either the FIX core implementation files themselves or unrelated
  identifiers like `IDmcDmaSchedulable`/`InstructionTracer`; none are unlock/card definitions).

**Independent conclusion: CONFIRMED.** FIX is a real, registry-discoverable core family, but it is
absent (not merely disabled) from the CPU/PPU/APU menus for a normal player's save, because no
default save or unlock path ever grants ownership of it. This matches `UAT/README.md`'s documented
finding exactly, re-derived from the mechanism rather than re-reading the same prose.

One nuance worth flagging: the Options screen's "Unlock Everything" debug button
(`/api/progression/unlock-everything`) passes `CoreRegistry.CpuIds`/`PpuIds`/`ApuIds` — the full
reflection list, which *does* include `"FIX"` — directly into `UnlockEverythingAsync`. So the one
existing live way to reveal FIX is not only `config.ShowLockedItems` (shows it as `[Locked]`,
per the README) but "Unlock Everything" would actually grant real ownership of it too. This is
consistent with, not contradictory to, the README's claim, since "Unlock Everything" is an
explicit debug/reset tool, not normal progression.

## Summary

| Step | Result |
|---|---|
| 1. Launch → health warning → Main Menu | PASS |
| 2. Reach native Emulator screen via "Open Emulator" | BLOCKED — environmental (shared hardcoded port 42067 across 10 parallel instances; port owned by a different, unrelated parallel session's PID) |
| 2. FIX absent from CPU menu (live) | BLOCKED (needs step 2) |
| 2. FIX absent from CPU/PPU/APU menus (independent source verification) | CONFIRMED via code reading — see above |
| 3. Switch 3-4 CPU cores, verify responsive | BLOCKED (needs step 2) |
| 4. Switch PPU/APU cores similarly | BLOCKED (needs step 2) |
| 5. Report core-switch results + FIX presence/absence | FIX presence/absence answered (absent by default, confirmed independently); core-switch behavior not observable this session |

## Screenshots (in `UAT/screenshots/`, `corecheck-*.png`)
- `corecheck-1-launch.png` — health warning
- `corecheck-2-mainmenu.png` — Main Menu after acknowledging
- `corecheck-3*.png`, `corecheck-4*.png` — various click/invoke/keyboard attempts on "Open
  Emulator" from the (contaminated, default-position) window, before I diagnosed the screenshot
  overlap issue
- `corecheck-5-moved-true-state.png` — my window moved to a unique screen position, confirmed
  un-clicked true state
- `corecheck-6..11` — Invoke/real-click/double-click attempts against my own, now
  overlap-verified, window — all show the same focus/hover-but-no-navigate result
- `corecheck-12/13/14-pid12384-*.png` — read-only diagnostic screenshots of the actual port-owning
  process (a different, unrelated parallel UAT session), used only to confirm the root cause; no
  further input sent to that window after diagnosis
- `corecheck-15-final-mywindow-state.png` — final proof my own instance is still fully responsive
  and unharmed, just parked on Main Menu

## Recommendation

Retest this area once BrokenNes.Windows either (a) gains a per-instance/dynamic API port or a
single-instance lock, or (b) can be retested with exclusive/solo access to the machine (no other
parallel `BrokenNes.Windows.exe` holding port 42067). Separately, the hardcoded-port-with-no-
fallback design is worth a real backlog item independent of UAT, since it silently breaks "Open
Emulator" (and likely other web-API-backed features) the moment a second instance of the real app
is ever running.

My own instance (PID 5044) was stopped cleanly at the end of this session.
