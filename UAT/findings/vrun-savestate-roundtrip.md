# GUI savestates on VRUN: they work, and the API path is config-gated

Date: 2026-09-04. ROM: `game.nes` (mapper 30, self-flashing UNROM-512). Cores CPU_FIX/PPU_FIX/APU_FIX.
Driven over the HTTP control API against a real desktop instance.

## Result: quick-save/quick-load round-trips correctly in live gameplay

| step | app_state | room | x | y | hp | score |
|---|---|---|---|---|---|---|
| entered gameplay | 1 | 0 | 224 | 192 | 5 | 0 |
| A - played left, then **quick-save** | 1 | 0 | **176** | 192 | 5 | 0 |
| B - played right (state genuinely moved) | 1 | 0 | **224** | 192 | 5 | 0 |
| C - **quick-load** | 1 | 0 | **176** | 192 | 5 | 0 |

C matches A on every field, B differs from A (so the test was not vacuous), and the process
survived. The spawn reading (224,192) hp 5/5 matches the documented spawn, so the RAM oracle is
reading the right addresses on this build.

Savestates carry the mapper-30 flash overlay (`Mapper30.GetState`/`SetState`), which matters
because flash is currently the game's only in-cartridge persistence and it is not written to disk -
so savestates are, today, the only way VRUN progress survives anything.

## The trap: `success:false, gated:true` does not mean progression-locked

`POST /api/emulator/quick-save-state` and `/quick-load-state` return `{"success":false,"gated":true}`
out of the box even on a fully-unlocked save. The cause is not progression:
`MainForm.SaveStates.cs` `QuickSaveStateFromApi()`/`QuickLoadStateFromApi()` return false unless
`config.EnableWebmoduleSavestateDebugShortcuts` is set, and it defaults to false. The endpoint then
reports `gated = !success`, which reads as "the player has not unlocked savestates" and sent an
earlier pass looking at the progression system.

**To exercise savestates over the API**, set `"enableWebmoduleSavestateDebugShortcuts": true` in
`%APPDATA%\BrokenNes\config.json` and restart. Two related gotchas found the same way:

- `gated` is computed as `!success` at the endpoint, so *every* failure is labelled "gated" -
  including a genuine capture failure or no ROM loaded. Do not read it as a specific cause.
- The app finishes loading its embedded `test.nes` a few seconds after the HTTP server starts
  answering. `Wait-BrokenNesApi` returns before that, so a ROM loaded immediately afterwards gets
  clobbered - the stdout shows `ROM Loaded: game.nes` followed by `ROM Loaded: test.nes`. Wait a
  few seconds after the API comes up before loading a ROM, and verify with
  `GET /api/emulator/current-rom`.

## Not covered

The physical F-key path (`QuickSaveState_Click`) was not driven with real keystrokes; this
exercises `TryQuickSaveState`/`TryQuickLoadState`, which is the same code the key handler calls,
minus the key handler itself.
