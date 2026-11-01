# Sega track C3a: picture bridges both ways (round 3)

Written 2026-10-09 when the wave was stopped early (usage cap). Branch `worktree-agent-a900359976202a680`. Plan: `sega-cores-plan.md` (sections 2, 6, 7, 8, 12); rules: `sega-tracks-brief.md`. **Read "Half-done" first.**

## What exists

| Piece | File (`Windows/NesEmulator/mix/` unless noted) | What it is |
|---|---|---|
| `PPU_SMS`, `PPU_MD` | `PPU_SMS.cs`, `PPU_MD.cs`, base `NesFrontedPpu.cs` | NES PPU cores (CoreRegistry ids `SMS`, `MD`; NES PPU menu "From the Master System" / "From the Genesis") whose picture is drawn by a real `SmsVdp` / `MdVdp`. A FIX-timed NES PPU stays in front (timing, NMI, sprite-0, registers: the game runs exactly as on PPU_FIX); at dot 320 of each line the base class records scroll / mask / control, at frame end the Sega chip replays the NES picture (tiles baked with their NES palette, per-line scroll table, 64 sprites in order, priority bit, 8x16) and draws it. |
| `SegaPictureSession` | `SegaPictureSession.cs` | Decorator `ConsoleSessions.CreateSega` puts around the Master System, Game Gear and Genesis sessions. The board keeps running untouched; a PPU menu entry `NES:<chip>` turns the VDP state into that NES-family chip's picture after each frame (in-place swap, no restart); the console's own chip is a pass-through; a chip not on the menu is refused, never replaced. `Inner` exposes the board session. |
| `SmsToNes`, `MdToNes` | `SmsToNes.cs`, `MdToNes.cs` | The Sega-to-NES translators, fed by a line capture taken through the board's own instruction hook (no board or VDP change). **Layered path** (PPU_FIXS and the Game Boy chips fronted by it): `ExtPicture` layers with the VDP's per-line scroll, locks, window, 2-cell column scroll (new `Layer.ColumnScroll` in `ppus/ExtPicture.cs`), tile priority, sprites in the VDP's order and ladder; 0 pixels differ from the VDP's own picture in the synthetic scenes. **Flat path** (every other NES chip, and through the NES the Game Boy / SNES / other Sega chips): one 33x30 nametable pair, one fine scroll per frame, the 255 most used tile variants (`FlatTilePacker`, the rest shown as the closest kept tile), 4 palettes of 3 colours, 64 sprite pieces. |
| Helpers | `BridgeCounters.cs`, `NesFixPalette.cs`, `SegaColour.cs`, `SegaQuantizer.cs`, `TilePool.cs`, `FlatTilePacker.cs` | Thread-safe loss counters (`IPictureBridgeInfo`); the FIX palette and nearest-colour search; the VDP colour reductions; exhaustive 3-medoid palette solver with warm start (no shimmer); tile slot allocator. |
| Catalog and apps | `systems/ConsoleSystems.cs`, `systems/ConsoleSessions.cs`, `Windows/webapi/WebApiServer.Endpoints.Console.cs`, `Windows/MainForm/MainForm.Consoles.cs`, `WebLite/LinkerConfig.xml` | Sega PPU menus list every NES-family chip as `NES:<chip>` with an honest label; NES PPU menu lists SMS / MD; `/api/console` status carries `pictureBridge`, `pictureBridgeCounters`, `pictureBridgeSummary`; Lite roots `PPU_SMS`, `PPU_MD`. |
| Workshop tools | `Workshop/Sega/PictureMetrics.cs`, `PictureBridgeCli.cs` (`--picture-bridge nes|sega|lockstep`), `PictureBridgeTestCli.cs` + `PictureBridgeNesTests.cs` + `PictureBridgeSegaTests.cs` (`--picture-bridge-test`), `BridgeSweepCli.cs` (`--bridge-sweep`, child `--bridge-sweep-one`) | Fidelity metric, lab, verifier, sweep. |

## Fidelity metric (reported per game)
Per run, every 10 frames: colour error % (mean per-pixel RGB distance), block error % (16x16 block mean colours), SSIM on luma (8x8 windows, stride 4), match % (pixels within 6 %); "gamut floor" = the reference with every pixel replaced by its nearest NES colour (the NES gamut is the main limit). Recognisable = SSIM >= 0.55 and block error <= 12. Every loss is a named counter (status line, API, sweep JSON).

## Verification (all run on this branch, main = c4862b0 unchanged)
| Verifier | Result |
|---|---|
| `--picture-bridge-test` with NES / SMS / GG / MD ROM directories | **207 passed, 0 failed** (catalog, nes2sega, sega2nes incl. exact colour per pixel on the layered path, session, classifier 15, real games: 5 SMS + 3 GG + 6 MD games on NES chips, 7 NES games including VRUN on PPU_SMS and PPU_MD, front identical to PPU_FIX in every case) |
| `--sega-contracts-test` | 80 / 80 |
| `--sega-bridge-test` | 251 / 251 (two checks updated: the Sega picture menus now list NES chips) |
| `--md-test` | 143 / 143 |
| `--md-golden` | 75 frames match, 0 changed |
| `--sega-test` | 65 / 65 (two checks updated for the NES chips in the picture menus and the in-place chip swap) |
| `--smstest --roms <sms dir>` | 214 pass, 0 fail, 18 skip (GG ROMs not in that dir) |
| NES golden suite (`UAT/run-suite.ps1`, this branch's Workshop exe, main's ROM roots) | 46 pass, 0 fail, 16 skip (= baseline) |
| `UAT\entrypoints\desktop-smoke.ps1 -Exe` | 9 / 9 |
| `UAT\entrypoints\sega-preview-smoke.ps1 -Exe` | 38 pass, **4 fail on this branch AND the same 4 on main c4862b0** (off_genesis_input_reaches_the_game, sms/gg_real_picture_is_still_without_input, md_input_reaches_session), rerun alone twice each: environmental (shared machine), not caused by this work |
| Plugin self-test (Native AOT build, `--rom vrun.nes`) | 38 pass, 2 warnings, 3 fail (`md`, `standalone-rack`, `realtime`: all "needs 5x real time", got 2.8x to 3.6x). **Main c4862b0 fails the same 3 back to back** (3.7x to 4.0x): machine load. Not re-run quiet. |
| Desktop over the HTTP API (my build, scripted) | 17 / 17: SMS game on NES:FIXS, NES:FIX, NES:DMG, NES:SNES and back; a chip not on the menu refused (400); Genesis game on NES:FIXS and NES:SMS; NES PPU list has SMS and MD; smb1 on PPU_SMS and PPU_MD; back to FIX; status shows the bridge and counters |
| Lite dev server of this worktree (port 5021) | NES PPU picker lists `MD:Genesis+Fix` and `SMS:Master System+Fix`; VRUN drew its title screen on PPU SMS and on PPU MD, no console error. Master System console: PPU menu lists From the NES / SNES / Game Boy / Genesis; Alex Kidd loaded and NES:FIXS selected, **no picture seen yet** (the interpreted dev build ran at 0.0 to 1.1 fps on the loaded machine; the Alex Kidd title needs ~100 frames). No console error. Not completed. |

Real-game numbers (sweep below and the verifier): Master System on NES:FIXS SSIM 0.82 to 0.99 (mean 0.92 over 10 games), on NES:FIX 0.74 to 0.98 (0.87); Game Gear FIXS 0.59 to 0.97 (0.85); Genesis FIXS 0.75 to 1.00 (0.89), FIX 0.40 to 0.99 (0.78); NES on PPU_SMS 0.92 to 1.00 (0.96), on PPU_MD 0.93 to 1.00 (0.97); Game Boy / SNES through the NES onto the Master System chip 0.91 to 1.00, onto the Genesis chip 0.98 to 1.00.

## Proofs that the verifiers can fail
Bugs injected by text replacement, built, group run, reverted with `git checkout` (harness in the scratchpad, not in the repo):
- `--picture-bridge-test`: 21 injections, every one flagged. PPU_SMS: palette bake (16 failed checks), scroll sign (3), behind-sprite priority bit (1), v-flip ignored (2), 8-per-line counter (1). PPU_MD: scroll sign (3), sprite priority inverted (1), sprites on the background palette (8), vertical fine scroll dropped (1). SmsToNes: scroll sign (2), sprite priority (1), sprite counter (1). MdToNes: vertical scroll sign (1), sprite ladder (2), window offset (2). Catalog: SMS removed from the NES menu (5). Session: unknown chip accepted (1).
- **The first run found 3 checks that could not fail** (SMS flat sprite counter, Genesis sprite-vs-plane ladder, Genesis window columns) and a Game Gear colour check ending in `|| true`; all fixed (new `ColourMismatch` exact check, new scenes) and re-flagged.
- The sweep's classifier: a bridge made to freeze after frame 100, to throw after frame 100, to loop forever, and to draw black were each run through a small sweep (NES on SMS and MD, Master System on NES:FIXS and NES:FIX, Genesis on NES:FIXS): 5 of 5 classified frozen, 5 of 5 crashed, 5 of 5 hung (60 s limit), 5 of 5 dead. A picture that stops part-way is also frozen now (`ChangesLate`), and a game whose own sound is only the Model 1 Genesis DAC's idle ripple (0.0033, Strider and Castle of Illusion: no key-on, no DAC, no PSG tone by `--md-run --sound-report`) is silent, not "dead for its bridge"; synthetic tests for both.

## Sweep (`--bridge-sweep`): 45 hand-picked games, 700 frames, 338 runs, every default combination plus the sound bridges
Run from the build before the last two classifier changes (`sweep_full2`; the final re-run with them was stopped at 294 of 338 by the wrap-up). Picture: **195 alive, 45 lossy, 0 refused, 0 frozen, 0 dead, 0 hung, 0 crashed**. Sound: 49 alive, 2 static (Game Gear Castle of Illusion and demo: the game is silent), 2 "dead" that the final classifier calls static (Strider, Castle of Illusion on the Genesis: silent games). Control runs (the game on its own chips) all ran. First sweep found VRUN "crashed" on every chip: the sweep started NES games on the Famiclone cores, which do not run VRUN's unofficial opcodes; fixed (FIX family, as the app).

| Route (mean SSIM; worst game) | Result |
|---|---|
| Master System -> NES:FIXS / NES:FIX / NES:MD / NES:SNES | alive 10/10; 0.92 (0.82) / 0.87 (0.74) / 0.86 (0.75) / 0.86 (0.74) |
| Game Gear -> same | alive 6/6; 0.85 (0.59) / 0.81 (0.63) / 0.80 / 0.79 |
| Genesis -> same | FIXS alive 10/10 0.89 (0.75); FIX 9 alive + Kid Chameleon lossy 0.40 (a title screen of ~54,000 unique tile variants, shown as the closest of 255), mean 0.78 |
| Sega -> NES:DMG / NES:DMGS (4 shades) | DMG lossy 26/26 (SSIM about 0, a 160x144 crop in 4 shades assigned by tile level, not by luminance: not comparable), DMGS 0.30 to 0.59, mixed |
| NES -> PPU_SMS / PPU_MD | alive 8/8 (VRUN included); 0.96 / 0.97 |
| Game Boy, Game Boy Color, SNES -> NES -> SMS / MD chips | alive 11/11 each (4 Game Boy, 2 Color, 5 SNES games); 0.91 to 1.00 |
| NES -> existing DMG / DMGS / SNES chips | alive 8/8 each (no reference, so no SSIM) |
Cost: bridged Master System runs 52 to 69 fps against 383 unbridged, the Genesis 28 to 40 against 121 (native, one core, shared machine); NES on PPU_SMS/MD 119 to 122 against 164.
`sweep.json`, `matrix.md` and the 158-game library sample (`lib.json`: 58 Master System, 40 Game Gear, 60 Genesis, seeded random from the user's libraries) were prepared; **the library sample and the all-menu-entries sweep were never run.**

## Limits (all counted at run time, none silent)
NES gamut (the floor above); flat path: one fine scroll a frame, 255 background tiles, 4 palettes of 3 colours, 8 sprite pieces a line, 64 pieces; TMS9918 modes, Genesis shadow/highlight, interlace not drawn; mid-frame VRAM/CRAM changes use the end-of-picture state (Genesis) or the frame's palette; Genesis H40 shows its middle 256 columns, 240 lines its middle 224; the DMG chip is monochrome and 160x144.

## Decisions
1. Always wrap Sega sessions (`SegaPictureSession`) so a chip swap is in place; own-chip is a pass-through. 2. Flat path for NES-faithful chips, `ExtPicture` layers where flat is too lossy; both stay. 3. No capability substitution: a chip that cannot be built makes the session refuse. 4. Sweep and tools start NES games on the FIX family. 5. `ExtPicture.Layer.ColumnScroll` is the only change to a shared picture core (default null: SNES/Game Boy paths unchanged; NES golden suite identical). 6. No third-party code taken (SSIM is the published formula); no ledger row needed. 7. Shared files touched, all small and additive: `ConsoleSystems.cs`, `ConsoleSessions.cs`, `Workshop/Program.cs` (4 routes), `WebLite/LinkerConfig.xml`, `ExtPicture.cs`, `SnesToNes.cs` (`Quantizer` made `internal`), `NES.cs` (`GetPictureBridge`), the status endpoint and `MainForm.Consoles.cs`; two older verifiers (`SegaBridgeTestCli`, `SegaBridgeMdTests`, `SegaFoundationTestCli`) updated for the new menu entries.

## Half-done (be honest)
- Lite: the Sega-on-NES-chip picture was not seen on screen (too slow here); NES-on-SMS/MD was.
- Final sweep, library sample and all-menu-entries sweep not completed; matrix above is from `sweep_full2`.
- Dashboard: not updated (see the report for suggested entries).
- Plugin real-time checks and 4 sega-preview-smoke checks not re-run on a quiet machine (they fail identically on main).
- Not done at all: optional `PPU_GG`; speed of the bridges on WASM.

## Next steps
1. On a quiet machine rerun `finalsweep.ps1` equivalents: `--bridge-sweep --manifest games.json --sound`, then the library sample (`--pictures "sms:NES:FIXS+NES:FIX;gg:...;md:..."`) and the all-menu-entries run; put the matrix here. 2. Lite: load a Master System game, pick `NES:FIXS`, wait for the title, look. 3. Rerun the plugin self-test and `sega-preview-smoke` quiet. 4. Dashboard: suites `picture-bridges` (207), `picture-bridges-proofs`, `bridge-sweep`; `phases/p3-track-c` gate "Picture bridges both ways" to done once 1 and 2 hold. 5. **C3b (plugin)**: nothing needed from pictures; a Sega ROM session in the plugin's ROM mode is now a `SegaPictureSession` (use `.Inner` if the concrete type is needed). **C3c (CPU walls)**: the sweep tool classifies any CPU x game x core combination the same way (add `DefaultCpus` and a `--cpu` argument to `RunOne`); `BridgeSweepCli.Classify` is pure and tested.
