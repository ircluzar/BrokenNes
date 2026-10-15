# Track A4: Master System, Game Gear, SG-1000, round 4 (2026-10-15)

Record of the fourth round on the Z80 consoles. Earlier records: `sega-track-a.md`, `-a2.md`, `-a3.md`. Provenance of code and outside facts: `Windows/NesEmulator/sega/THIRD_PARTY_NOTICES.md` (nothing was ported this round; the oracle, Mesen 2.1.1, was only run, black box, its code never read). Branch `worktree-agent-a314e5c62d8920ea5`, from `main` = `5c974b2`, not merged, not pushed. The shared bar is in `sega-tracks-brief.md`, "Round 4 additions".

**Summary.** All 40 roster games (20 Master System, 20 Game Gear) were played with scripted input that reaches gameplay and compared with Mesen frame by frame; the whole of both libraries was swept again and every non-ok game that is not a tagged bad dump or a noisy scene was run against the oracle; sound was compared at the event level (the chips' registers at every frame end); every core slot of both consoles was hot-swapped mid-game through the apps' session; save states were round-tripped on all 40 games; four of the five battery games were saved by their own save command and restarted from the save, every picture and the cartridge RAM equal to the oracle's in both passes; UAT cases exist and pass. **What is not done:** the in-game battery save and reload is proven for four of the five roster games with a battery (Phantasy Star, Golden Axe Warrior, Penguin Land, Defenders of Oasis) and not for Shining Force II (Game Gear), whose save is in the church after a first battle that a script of button taps does not win; and one boot-time interrupt difference in Shining Force II is understood only partly. Where the picture differs from the oracle the cause is named below with its evidence; where it is not established it says so. No Z80, VDP or PSG source was changed this round (the Z80 gate was therefore not needed; the other gates are listed under "Verifiers").

## What exists (new this round)

| Area | What | Where |
|---|---|---|
| Hot swapping | `SmsSession.TrySwapCore` takes only ids the console's menus offer (it accepted any CPU id, and a Game Gear took `SMS-FM`, which its menu does not list); suite `swap` swaps every id of every slot mid-game | `sms/SmsSession.cs`, `Workshop/Sega/SwapSuite.cs` |
| Save states on the roster | suite `rosterstate`: all 40 roster games, F7 / F5 path, round trip and sound-unit refusals | `Workshop/Sega/RosterSuite.cs` |
| UAT | case kind `sega-run` (`--console-run` through the apps' session factory), 17 cases and 4 in-game save round trips (`roundtrip`) | `UAT/lib/SegaCases.ps1`, `UAT/run-suite.ps1`, `UAT/cases/sega.json` |
| Roster play scripts | the 40 games with the pad scripts that play them (looked at picture by picture) | `Workshop/Sega/mesen/roster-scripts.tsv` |
| Oracle tooling | the oracle's PSG registers and YM2413 register file at every frame end (`capture.lua -Psg`), its cartridge RAM (`-Dump` writes `.sram.bin`); `--sms-run --sound-regs / --fm-regs-file`, `--vdp-flags-at`, the comparison against the oracle's first run alone; `mesen-compare.ps1 -Sound -Apu`; fix for long-named games the oracle "could not capture" | `Workshop/Sega/mesen/`, `Workshop/Sega/SmsRunCli.cs` |
| In-game saves | `roster-saves.tsv` (the pad scripts of the four games) and `mesen-saves.ps1` (both passes against the oracle: pictures and the cartridge RAM); `mesen-capture.ps1 -SaveIn` (the oracle starts from a save file), `mesen-compare.ps1 -Dump` and a battery column in its list | `Workshop/Sega/mesen/` |
| Input scripts | `--input @file` for a script too long for a command line | `Workshop/Sega/SmsRunCli.cs` |
| Console run | `--console-run` takes `.7z` ROMs (`archive.7z#text`), passes the ROM's file name to the session (a `(Europe)` dump is PAL as in the app), prints colours and audio peak, `--battery-out` | `Workshop/ConsoleRunCli.cs` |
| Sweep | `--sms-sweep` had crashed on every game since the picture wrapper was merged (an `InvalidCastException`: it cast the app's session to `SmsSession`); it also crashed at the end on the class name `audio-bug?` (GDI+ cannot save a file with a `?`). Both fixed; `--sms-sweep --selftest` recognises every class | `Workshop/Sega/SmsSweepCli.cs`, `SmsRunCli.cs` (`SmsRomIo.Concrete`) |

Shared files touched, each small: `UAT/run-suite.ps1` (a dot-source of `lib/SegaCases.ps1`, one kind in the switch, `%ENV%` expansion in roots, the `.7z#` selector in the existence check), `Workshop/ConsoleRunCli.cs` (above), `Workshop/Sega/SmsTestCli.cs` (two suites, `--write-swap-programs`). No contract file of `sega/` was changed.

## Verifiers run (and each shown able to fail)

| Verifier | Result | Proof it can fail |
|---|---|---|
| `--smstest --only swap --roms` | 159 / 159 | a session whose `TrySwapCore` says yes to every id and silences the PSG on a sound swap: 97 fail |
| `--smstest --only rosterstate --roms` | 40 / 40 (81 s) | a work RAM byte the load did not restore: 30 of 40 fail |
| `UAT\run-suite.ps1 -Filter sega*` | 21 / 21, each recorded twice (91 s) | 17 plain cases: frame interrupt 55 master clocks late (`VintFlagMc` 5 to 65, built apart and reverted): 13 of 17 fail. 4 save round trips: a save import that copies nothing (built apart, reverted): 4 of 4 fail, each with the restart equal to the no-save control; one that copies only the first 1 KB: 3 of 4 fail (Penguin Land's stage is inside the first KB) |
| `mesen-saves.ps1 -RomDir <roster>` | 4 games x 2 passes x 2 timing modes: pictures and cartridge RAM identical to Mesen at every checkpoint (377 s) | the same script with the build whose save import copies nothing: pass 2 pictures and cartridge RAM DIFFER, exit 1 |
| `--sms-sweep --selftest` | every class recognised | `--sms-sweep --selftest --poison`: 5 of 6 wrong |
| `--sega-test` 65/65, `--sega-contracts-test` 80/80, `--sega-bridge-test` 320/320, `--md-test` 144/144, `--z80test --selftest` 57/57 | on the merged tree (`c22c0e6`) | their own proofs (A1-A3, B and the Z80 track) |
| `--smstest --prove --roms` | 586 clean passes, 0 failures, in 20 suites; each suite fails when poisoned | the run is itself the proof; the table under "Gate" has each suite's poison |

### Gate: `--smstest --prove --roms <the user's library>` on the merged tree (`c22c0e6`, 2026-10-15)

Every suite is run clean, then again with its poison; the run ends "prove: every suite failed when poisoned". Clean 586 pass, 0 fail.

| Suite | Clean | Poisoned (what the poison does) |
|---|---|---|
| vdptest | 55 / 55 | 50 pass 5 fail (frame and line interrupt flags 55 master clocks late) |
| irqphase | 8 / 8 | 4 pass 4 fail (Z80 / VDP phase moved by 4 cycles) |
| psg | 23 / 23 | 14 pass 9 fail (a sound unit 6 % fast with no stereo port) |
| fm | 39 / 39 | 13 pass 19 fail (FM data port dropped, `$F2` read-back wrong in bit 0) |
| state | 21 / 21 | 9 pass 12 fail (a work RAM byte left unrestored after the load) |
| sessionstate | 68 / 68 | 50 pass 18 fail (a VRAM byte left unrestored) |
| swap | 159 / 159 | 62 pass 97 fail (`TrySwapCore` says yes to every id, PSG silenced on a sound swap) |
| rosterstate | 40 / 40 | 10 pass 30 fail (a work RAM byte the load did not restore) |
| eeprom | 11 / 11 | 5 pass 6 fail |
| bios | 8 / 8 | 5 pass 3 fail |
| known | 5 / 5 | 3 pass 2 fail |
| refresh | 11 / 11 | 9 pass 2 fail |
| pal | 7 / 7 | 5 pass 2 fail |
| sg1000 | 26 / 26 | 17 pass 9 fail |
| save | 6 / 6 | 5 pass 1 fail (a corrupted save) |
| mapper | 14 / 14 | 10 pass 4 fail |
| session | 18 / 18 | 17 pass 1 fail |
| smoke | 12 / 12 | 10 pass 2 fail |
| golden | 54 / 54 | 47 pass 7 fail (frame interrupt 55 master clocks late) |
| speed | 1 / 1 | 0 pass 1 fail (a target no machine reaches) |

Earlier on the branch (before the sound-font exemption in `swap`): 577 pass, 9 fail, all 8 of the `NES:WF` swap checks and one throughput check, on a machine that has a MIDI synth (`APU_WF` plays through it and makes no samples; those checks now skip visibly, `SmsCheck.Skipped`, instead of failing). The `fm` suite's throughput check (the YM2413 at 25 times real time) measured 24 times under load once and passes on a quiet machine; it is a timing check and says so.

Not run: `--z80test` against the vector files (no Z80 source changed this round).

## The roster against Mesen 2.1.1, played through

Method: each game played with its script (`roster-scripts.tsv`: the sweep tool's pad pattern, or a menu script found by looking at the pictures) for 2,399 to 3,599 frames; the oracle run twice headless (its power-on line is random: frames where its two runs differ are "oracle disagrees with itself" and count as matched when this emulator equals one of them), this emulator replaying the oracle's power-on line; three modes: the app's own Z80 timing ("default"), the oracle's Z80 instruction lengths (`OracleZ80Timing`, a tool setting; A2) and those plus the oracle's R register not counting in HALT (`OracleHaltNoRefresh`, A3). The table gives frames equal out of the frames compared (frames 2 to N). "PSG" is the number of frames on which the oracle's PSG registers (three tone periods, four attenuations, noise register, Game Gear stereo mask) at the end of the frame equal this emulator's.

| # | Game | frames | default timing | oracle's Z80 lengths + R | PSG default | PSG with lengths |
|---|---|---|---|---|---|---|
| SMS 1 | Astro Warrior (USA) | 2399 | 2395 (4 differ) | 2396 (3 differ) | 2400/2400 | 2400/2400 |
| SMS 2 | Fantasy Zone (World) (v1.2) | 2399 | 2393 (6 differ) | 2396 (3 differ) | 2400/2400 | 2400/2400 |
| SMS 3 | Alex Kidd in Miracle World (USA, Europe) (v1.1) | 2399 | 2395 (4 differ) | 2397 (2 differ) | 2400/2400 | 2400/2400 |
| SMS 4 | Black Belt (USA, Europe) | 2399 | 642 (1757 differ) | 1037 (1362 differ) | 905/2400 | 915/2400 |
| SMS 5 | Columns (USA, Europe) | 2399 | 304 (2095 differ) | 2389 (10 differ) | 1767/2400 | 2400/2400 |
| SMS 6 | Bubble Bobble (Europe) | 2399 | 1655 (744 differ) | 2388 (11 differ) | 2400/2400 | 2400/2400 |
| SMS 7 | Penguin Land (USA, Europe) | 2399 | 342 (2057 differ) | 2338 (61 differ) | 2400/2400 | 2400/2400 |
| SMS 8 | Wonder Boy (USA, Europe) | 2399 | 2395 (4 differ) | 2395 (4 differ) | 2400/2400 | 2400/2400 |
| SMS 9 | Psycho Fox (USA, Europe) | 2399 | 2393 (6 differ) | 2394 (5 differ) | 2400/2400 | 2400/2400 |
| SMS 10 | Shinobi (USA, Europe) | 2399 | 2399 (0 differ) | 2399 (0 differ) | 2400/2400 | 2400/2400 |
| SMS 11 | Out Run (USA, Europe) | 2399 | 2387 (12 differ) | 2371 (28 differ) | 2400/2400 | 2274/2400 |
| SMS 12 | Space Harrier (USA, Europe) | 2399 | 299 (2100 differ) | 299 (2100 differ) | 2399/2400 | 2398/2400 |
| SMS 13 | Sonic The Hedgehog (USA, Europe) | 2399 | 2365 (34 differ) | 2380 (19 differ) | 2400/2400 | 2400/2400 |
| SMS 14 | R-Type (USA, Europe) | 2399 | 2399 (0 differ) | 2399 (0 differ) | 2400/2400 | 2400/2400 |
| SMS 15 | Phantasy Star (USA, Europe) (v1.3) | 2399 | 2399 (0 differ) | 2399 (0 differ) | 2400/2400 | 2400/2400 |
| SMS 16 | Golden Axe Warrior (USA, Europe) | 2399 | 2395 (4 differ) | 2397 (2 differ) | 2400/2400 | 2400/2400 |
| SMS 17 | Wonder Boy III - The Dragon's Trap (USA, Europe) | 2399 | 2381 (18 differ) | 2399 (0 differ) | 2400/2400 | 2400/2400 |
| SMS 18 | Sonic The Hedgehog 2 (Europe) | 2399 | 2355 (44 differ) | 2399 (0 differ) | 800/2400 | 2400/2400 |
| SMS 19 | Micro Machines (Europe) | 2799 | 2796 (3 differ) | 2797 (2 differ) | 1322/2800 | 2800/2800 |
| SMS 20 | Streets of Rage II (Europe) | 2399 | 2390 (9 differ) | 2391 (8 differ) | 2400/2400 | 2400/2400 |
| GG 1 | GG Demo by Charles MacDonald (PD) | 2399 | 2398 (1 differ) | 2398 (1 differ) | 2400/2400 | 2400/2400 |
| GG 2 | Columns (U) [!] | 2399 | 522 (1877 differ) | 2399 (0 differ) | 2400/2400 | 2400/2400 |
| GG 3 | Fantasy Zone Gear (JU) [!] | 2399 | 2389 (10 differ) | 2393 (6 differ) | 2400/2400 | 2400/2400 |
| GG 4 | Bubble Bobble (U) [!] | 2399 | 2212 (187 differ) | 2220 (179 differ) | 2400/2400 | 2400/2400 |
| GG 5 | Pac-Man (J) | 2399 | 2397 (2 differ) | 2399 (0 differ) | 2400/2400 | 2400/2400 |
| GG 6 | Wonder Boy (E) [!] | 2399 | 2396 (3 differ) | 2399 (0 differ) | 2400/2400 | 2400/2400 |
| GG 7 | Castle of Illusion Starring Mickey Mouse (U) [S][!] | 3199 | 3199 (0 differ) | 3199 (0 differ) | 3200/3200 | 3200/3200 |
| GG 8 | Land of Illusion Starring Mickey Mouse (U) [!] | 3199 | 3199 (0 differ) | 3199 (0 differ) | 3200/3200 | 3200/3200 |
| GG 9 | Sonic The Hedgehog (U) (V1.1) [!] | 2399 | 2387 (12 differ) | 2393 (6 differ) | 2389/2400 | 2400/2400 |
| GG 10 | Dragon Crystal (U) [!] | 2399 | 576 (1823 differ) | 2396 (3 differ) | 2070/2400 | 2400/2400 |
| GG 11 | GG Shinobi (E) [!] | 2399 | 2399 (0 differ) | 2399 (0 differ) | 2400/2400 | 2400/2400 |
| GG 12 | Shinobi II - The Silent Fury (U) [!] | 2399 | 2397 (2 differ) | 2397 (2 differ) | 2400/2400 | 2400/2400 |
| GG 13 | Sonic Chaos (U) [!] | 2399 | 2399 (0 differ) | 2399 (0 differ) | 2387/2400 | 2400/2400 |
| GG 14 | Sonic The Hedgehog 2 (U) [!] | 2399 | 1726 (673 differ) | 2399 (0 differ) | 2066/2400 | 2399/2400 |
| GG 15 | Tails' Adventures (U) [!] | 3599 | 2276 (1323 differ) | 3599 (0 differ) | 1976/3600 | 3598/3600 |
| GG 16 | Defenders of Oasis (U) [!] | 2599 | 2596 (3 differ) | 2599 (0 differ) | 2600/2600 | 2600/2600 |
| GG 17 | Ristar the Shooting Star (U) [!] | 2399 | 2345 (54 differ) | 2399 (0 differ) | 2385/2400 | 2400/2400 |
| GG 18 | Micro Machines (E) | 2399 | 2396 (3 differ) | 2398 (1 differ) | 1434/2400 | 2400/2400 |
| GG 19 | Wonder Boy - The Dragon's Trap (E) [!] | 2599 | 2598 (1 differ) | 2599 (0 differ) | 1342/2600 | 2600/2600 |
| GG 20 | Shining Force II - The Sword of Hajya (U) [!] | 2599 | 2551 (48 differ) | 2551 (48 differ) | 2598/2600 | 2600/2600 |

Totals over the 40 games: **84,837 of 99,760 frames equal by default (85.0 %), 95,773 with the oracle's lengths (96.0 %), 95,894 with lengths and R (96.1 %).** 28 of 40 games are within 5 frames of identical with the best mode, 34 within 12.

What the scripts reach (all pictures looked at): Master System: Galaxy Zone (Astro Warrior), round 1 and the game over of Fantasy Zone, Alex Kidd's level, Black Belt's street, Columns, Bubble Bobble's levels, Penguin Land's stage, Wonder Boy's forest, Psycho Fox, Shinobi's street, Out Run's road, Space Harrier's field, Green Hill (Sonic), R-Type stage 1 with a death and restart, Phantasy Star's story, Camineet in first person and the dungeon map, Golden Axe Warrior's name entry, story and overworld, Wonder Boy III's castle with monsters, Sonic 2's Underground Zone, Micro Machines' Challenge qualifying race (select screen: **Left and button 1 pressed together** picks Challenge; a plain button 1 does nothing there), Streets of Rage II's first street. Game Gear: the graphics demo, Columns, Fantasy Zone Gear's shop, Bubble Bobble, Pac-Man, Wonder Boy's forest, Castle of Illusion's first scene (**no input for the first 1,250 frames**: a Start during the logo or the text leaves the game on a text screen in both emulators), Land of Illusion's world map, a stage and its score screen, Sonic 1's Green Hill, Dragon Crystal's dungeon, GG Shinobi's truck stage, Shinobi II's first stage, Sonic Chaos' player select (**both emulators then fade to a dark screen**; the first zone was compared in A2: 898 of 898), Sonic 2's first zone, Tails' Adventures' world map (**Up picks the forest**) and first level, Defenders of Oasis' menu script, Ristar's stage, Micro Machines' race, Wonder Boy: Dragon's Trap's castle (CONTINUE / NEW GAME: Down, then **Start or button 2**, not button 1), Shining Force II's intro dialogue and first road scene.

### What the remaining differences are

1. **The oracle's Z80 is not the Zilog one** (A2): ten games gain 40 frames or more from the oracle's lengths (Columns Master System 304 to 2,389 and Game Gear 522 to 2,399, Bubble Bobble Master System 1,655 to 2,388, Penguin Land 342 to 2,338, Dragon Crystal 576 to 2,396, Sonic 2 on the Master System 2,355 to 2,399 and on the Game Gear 1,726 to 2,399, Tails' Adventures 2,276 to 3,599, Ristar 2,345 to 2,399, Black Belt 642 to 1,037); the others move by a few frames. The app keeps the manual's table (it passes z80test's hardware CRCs); the lengths are a tool setting. Out Run is the other way round (2,387 by default, 2,371 with the lengths: the lengths are an approximation).
2. **The oracle disagrees with itself** (its power-on line is random and a game's state follows it): **Black Belt** (its two runs differ on 1,758 of 2,399 frames, from frame 151: the same frame at which this emulator first leaves the first run, by 11 to 14 pixels on a few lines while the game clears the name table during the picture; the next frames are equal again, then the games part), **Space Harrier** (2,085 frames from frame 212: of the 314 frames where the oracle's runs agree, 292 are equal, 299 counting the frames equal to one of its runs), **Penguin Land** (the 61 frames left with the oracle's lengths), Ultimate Soccer (Europe) in the library (704 of 1,499). With only one oracle run these games cannot be compared beyond where its runs agree: this is the limit of the comparison, not a verdict on the emulation.
3. **A boot-time difference of one interrupt: Shining Force II (Game Gear)**, 48 frames. A work RAM counter ($143B) is one behind the oracle's from the first frames on; at the game's first `EI` (after it sets registers 0 to 10 with the display off) the oracle accepts an interrupt after the next instruction (its CPU interrupt input is already high) and this emulator does not; the traces diverge there (`004C` then `0038` in the oracle, `004D` in this emulator). It is phase dependent in the oracle: another run of the same game with another power-on line does not take it. Probe programs that enable the line interrupt late, with the display on and off and R10 untouched, set to $FF, 62, 0 or 10, and that write R10 after the enable, give the same first interrupt line in both emulators (29 probe runs, all equal), so the line counter and the enable logic are right; what sets the oracle's pending flag in this game is not established. The visible effect is a 38-pixel text cell from frame 1,087 for 48 frames. Not fixed.
4. **Colour-RAM dots: Bubble Bobble (Game Gear)**, 179 frames, each 1 to 3 pixels (a dot of the colour written while the display is off; A1's item 2). Invisible in play.
5. **Isolated frames** (1 to 19 per game: a band of lines of a few dozen pixels). Black Belt's frame 151 was taken apart: the game rewrites the name table through the whole frame (1,312 VRAM writes in frame 151), RAM and VRAM are identical at the frame's end, and the picture differs on a few lines, so the effect is a write landing a little earlier or later against the beam. **A scan of every VDP write-slot parameter against the oracle** (slot start 112 to 144 master clocks, period 56 to 72, slots per line 4 to 16, write latency -9 to +40; 12 games, 1,499 frames each, frames equal to the oracle's first run): the default is at the optimum (15,511 frames; no other setting reaches it, the best is 15,506 with a write latency of +3; a slot start of 112 loses 2,389, other settings 5 to 40), and latching each background tile once, with the name-table fetch 0 to 48 pixels ahead of its pixels (tried, then reverted), gains at most 7 frames of 15,500 and does not touch Black Belt's frame. So the difference is not a parameter of the model: it is the VRAM access queue A3 decided not to model. Nothing found here changes that decision.

## The whole libraries (item 3)

`--sms-sweep` (2,400 frames per game, scripted pad, the app's own session), on the fixed tree:

| Library | Files | ok | not ok | Of the not ok |
|---|---|---|---|---|
| Game Gear (`X:\EMULATION\Sega Game Gear`) | 665 | 562 | 103: no-picture 33, noisy 24, silent 19, hung 17, blank 7, static 3 | 41 are tagged bad dumps (`[b]`, `[o]`, `[a]`, `[h]`: black or garbage by their nature); 19 untagged noisy |
| Master System (`X:\EMULATION\Sega Master System`) | 460 (342 archives) | 438 | 22: noisy 8, silent 5, static 4, hung 2, audio-bug? 2, blank 1 | 3 tagged bad dumps (BIOS images) |

The same totals as the result file A3 left for its third pass (562 / 665 and 438 / 460): the code behind the numbers did not move; what changed is that every class is now accounted for.

* **noisy** (19 Game Gear untagged, 8 Master System): the contact sheet of the Game Gear ones shows 24 of 24 as ordinary game scenes (a soccer pitch, a platform level, an RPG map, a card table, dithered title art): the metric (57 to 83 percent of neighbouring pixels differ) misfires on detailed pictures. The 8 Master System ones were run against the oracle at 6 checkpoints: 6 identical in both timing modes, Tecmo World Cup '93 identical with the oracle's lengths, Ultimate Soccer (Europe) is the oracle's chaos above.
* **The other 57 non-ok games** that are neither tagged bad dumps nor noisy (37 Game Gear, 20 Master System) were run against the oracle (pictures at 6 checkpoints in two timing modes, PSG registers on all 2,400 frames): **49 are identical to the oracle**: it hangs, shows black or plays no sound at exactly the same places. That covers: all 12 MOD2PSG players (black by design), the Simple Text Demo (text, then a halt), the GG Hi-Res demo, Frogs, the Nibbles, Hicolor, Twisty, Tween, Windows and Chaos 89 demos (no PSG writes by design), Mortal Kombat (U) (180 PSG writes, all silence: the oracle is silent too), the Super Tetris (Korea) dumps and the GG2SMS hacks (Frogger x3, Pop Breaker V1.1: black in the oracle too; a hack with a Game Gear header is run as a Game Gear by this emulator and as a Master System by the oracle, the header rule of Phase 0), the Marksman Shooting and Gangster Town light-gun games (silent without a gun), Lucky Dime Caper, Promocao Especial, Monopoly (the pattern's input does not start the game), Micro Machines (the select screen: see the roster), Wonder Boy III and Turma da Monica (the two `audio-bug?` games: with the menu script both play sound, 1,019 and 1,018 of 1,500 frames audible; the sweep's pad never starts the game). The eight not identical: two BIOS images (the oracle runs them as cartridges, this emulator as BIOS images by their name), Excellent Dizzy Collection (Master System) at one checkpoint (frame 300; identical from 600), GG Nibbles V4 (at most 7 pixels with the oracle's lengths), Jang Pung II (4 pixels on one line at frame 900 with the oracle's lengths; A3's "unresolved" game: it is the Z80 lengths class, not a mapper), Mortal Kombat (J) (3 pixels at frame 900), Pop Breaker (GG2SMS V1.0) (console detection above: identical when forced to a Master System), Sonic Chaos (3 PSG frames).
* **Sonic Chaos (U) "blank"**: the game's player select fades to a dark screen under the sweep's pad; the oracle does the same on all 2,399 frames compared.

## Sound at the event level (item 4)

The oracle's port-write callbacks never fire (its SMS port space is not reachable from its Lua API), so the record is the **chips' registers at the end of every frame**: `capture.lua -Psg` writes `frame tone0 tone1 tone2 att0 att1 att2 attNoise noise stereo` and, with the FM chip on, the YM2413's 64 registers and the audio control; `--sms-run --sound-regs` / `--fm-regs-file` compares them with this emulator's frame by frame. Results (with the oracle's Z80 lengths; by default the streams are the same notes one frame apart in timing-sensitive games):

* **PSG and Game Gear stereo**: identical on every frame in **35 of the 40 roster games**; the five others: Black Belt 915 of 2,400 and Space Harrier 2,398 (the oracle's chaos), Out Run 2,274 (the lengths approximation), Sonic 2 (GG) 2,399 of 2,400, Tails' Adventures 3,598 of 3,600. The stereo mask is the ninth field: identical on all Game Gear games. Over the 57 screened library games the PSG is identical on 2,400 of 2,400 frames in all but two (Monopoly (Europe) by default timing, Sonic Chaos 3 frames).
* **YM2413**: the 64 registers and the audio control equal on **all 1,800 frames of all 28 Japanese FM games tried** (Alien Syndrome, Borgman, Kujaku Ou, Megumi Rescue, Argos, Phantasy Star, Ancient Ys, Hoshi wo Sagasite, Alex Kidd BMX Trial, Fantasy Zone II, Kenseiden, Aleste, Opa Opa, Captain Silver, Space Harrier 3-D, Thunder Blade, Maze Walker, Super Racing, Lord of Sword, Doki Doki Penguin Land, Super Wonder Boy, Galactic Protector, SDI, Family Games, Nekkyuu Koushien, Shinobi (Japan), Rampage, Zillion II) with the unit `SMS-FM` and the oracle's FM chip on; with the app's own timing six of them are one frame off in places (Aleste 1,354 of 1,800, BMX Trial 607, Maze Walker 933, Nekkyuu 1,003, Rampage 1,191, Zillion II 776).
* **Levels are not compared** (the oracle's test runner has no audio output and its Lua API no audio call). Nobody has heard any of it (no playback device on this machine).

## Hot swapping (item 5)

`--smstest --only swap` (159 checks, on assembled programs and, with `--roms`, on Shinobi, Wonder Boy, GG Wonder Boy and GG Columns at frame 520). Through the session the apps build: every id of the CPU slot (Z80), the picture slot (the console's own chip and each of 13 NES chips, the SNES, three Game Boy chips and the Genesis chip drawn through the NES) and the sound slot (own unit, `SMS-FM` on the Master System, 19 NES chips) is swapped after 90 frames of a program with a steady 440 Hz tone and a scrolling picture, and 90 frames run on. After each swap the work RAM, VRAM, colour RAM, Z80 PC and cycle count equal a run with no swap (a swap never disturbs the machine), the picture is the no-swap picture pixel for pixel on the console's own chip and a real (non-blank, moving, 256 x 240) picture on a NES chip, the tone keeps the PSG's pitch within 2 percent and is audible (**NES:QLQ plays the tone an octave up and at half level on a fresh session too**, so the suite names that exception; the plugin's self-test already calls QLQ "an octave sharp on purpose"). An id off the menu (`BOGUS`, `NES:NOPE`, `MD`, `NO-SUCH-UNIT`, `SMS-FM` on the Game Gear, an empty id) is refused and nothing changes; every picture and sound id of the menus also builds a session of its own (the path the apps take when a swap cannot be done in place); chains (own unit to SMS-FM to NES:FIX to NES:FIXS and back; the picture through five chips; all three slots interleaved) and a swap of both slots on each of 60 frames. **No slot refuses a menu id: every swap is in place.** A unit that comes back from a NES chip takes over the PSG's registers but not the phase of its square waves, so its first cycle starts afresh (not sample for sample the control's, which the suite says). It found a real bug (see "What exists"). The desktop's own handling (`MainForm.SetConsoleCore`: restart the game when the session says false) was read, not driven.

## Saves (item 2)

* **Save states** (F7 / F5): the suite `rosterstate` takes a state at frame 1,000 of each of the 40 games' scripts through the apps' own session factory and loads it into a fresh session: the next 120 frames equal the session that carried on (picture hash of every frame, every audio sample, work RAM, PC, cycle count), 40 of 40; a session on another sound unit (a fresh `SMS-FM` / `NES:FIX` session, and the same session after a sound hot swap) refuses the state with the machine unchanged.
* **In-game saves, played twice and compared with Mesen: four of the five battery games.** Pass 1 plays the game with a pad script up to its own save command; pass 2 is a NEW session that starts from the save file pass 1 wrote (the apps write the same bytes at close and read them at start) and walks the game's own CONTINUE / LOAD to the saved place. At 6 to 10 checkpoints per pass the pictures are identical to Mesen's (it is given the same file in its Saves folder, `mesen-capture.ps1 -SaveIn`) and this emulator's save equals the oracle's cartridge RAM over all 32 KB, in the app's Z80 timing and with the oracle's lengths: pass 1 and pass 2 of every game (`mesen-saves.ps1`, `roster-saves.tsv`). The picture of pass 2 comes from the save file, not from the script: the control run with no file ends on another picture (the UAT cases check that).
  * **Phantasy Star** (USA, Europe) v1.3: the intro to the command menu; **button 2 chooses and button 1 CANCELS** (an earlier attempt pressed button 1 on SAVE, which closed the menu and wrote nothing in either emulator: the game was doing what it should). SAVE, 'WILL SAVE THE GAME IN PROGRESS', slot 1 of 5, 'IS THAT O.K.', YES, 'WELL, SAVING 1': the cartridge RAM goes from 280 to 300 non-zero bytes (flag at `$0201`, the 1 KB slot at `$0400`). Pass 2: START / CONTINUE, 'CONTINUE GAME - YES', slot 1, 'CONTINUING', Camineet's map.
  * **Golden Axe Warrior**: the save is at the **inn keeper** ('You can save your game data here', in the village left of the graveyard where the game starts; the text is in the ROM at `$0CD78`): name ABC, out of the graveyard (Up one row, then Left), into the first house, up to the old man, YES (**button 2 chooses; button 1 only moves the cursor**), file 1: 'Your game data is saved', file 1 shows ABC (270 bytes change: the name and state at `$0400`, flag `$0030`). Pass 2: the title goes straight to 'SELECT A FILE TO LOAD', file 1 loads the hero in front of the inn.
  * **Penguin Land**: its battery is the **stage editor's** SAVE-01 / LOAD-01 (the code at `$4084` also keeps a round-select value at `$9C20` once the round counter reaches 31: read from the code, not reached). EDITOR on the title, button 2 opens the bar, the brick tool is chosen with button 2, bricks are placed with button 1, nine steps right to SAVE-01, button 2: 128 bytes change (an `OK` mark at `$0008`, the 449-byte stage, flag `$1C00`). Pass 2: the editor, the bar ten steps right to LOAD-01, button 2: the bricks are back on the empty field.
  * **Defenders of Oasis** (Game Gear): the game **saves by itself** when the throne-room conversation ends (frame 3,053 of the script that earlier stopped at 2,600, before it): two blocks of 1,280 bytes (`$0300` and `$0A00`) go from the `$FF` fill to the same record (a main copy and a backup, by the look of it; 6,231 non-zero bytes in all). Pass 2: Start through the title, the menu offers New Game / Continue / Copy Game / Delete Game with Continue chosen, 'Which game will you continue from?' shows Game 1, LV 1, Chap. 1, button 2 loads: the prince stands in the throne room with control.
  * **Shining Force II** (Game Gear): **not reached.** Its save is a church command (RAISE / CURE / PROMOTE / SAVE, in the ROM at `$9257`), after the first battle. A script of button 2 taps passes the whole opening (about 16,000 frames of dialogue) and reaches the first battle, where it stays for 90,000 frames (the units STAY; nobody dies, nothing is won). The cartridge RAM there is working storage (10,795 non-zero bytes at frame 600, 13,932 in the battle) with no slot structure to read a save from. Winning the battle needs real play.
* The pad scripts differ in what the buttons mean (in Phantasy Star and Golden Axe Warrior button 2 chooses; in Defenders button 2 talks and chooses, button 1 attacks, Start opens the menu; in Penguin Land's editor button 2 chooses and button 1 places): each was found by looking at the pictures.
* What was already verified and is unchanged: the save file round trip (A1's `save` suite), a battery program that survives two app runs (`sega-preview-smoke.ps1`), `ImportSave` copying a shorter file into the RAM (A2 compared the oracle's 8 KB files).
* A 24C01 EEPROM and the BIOS-image mode are A3's; unchanged.

## UAT (item 6)

`UAT/cases/sega.json` runs under `UAT\run-suite.ps1 -Filter sega*` with the new case kind `sega-run` (`UAT/lib/SegaCases.ps1`): `--console-run` through `ConsoleSessions.Create` with the cores the case names, optionally a hot-swap schedule (`swap`), recording the picture hash, size, distinct colours, audio peak, battery flag and the number of swaps the session refused. 21 cases: 4 in-game save round trips (Phantasy Star, Golden Axe Warrior, Penguin Land's editor, Defenders of Oasis: pass 1 saves in the game and writes the save file, pass 2 is a new session from that file walking the game's own CONTINUE / LOAD, a control run has no file; recorded: the save's size, non-zero bytes and SHA1 and the three picture hashes, and that the restart differs from the control) and 17 plain cases: 9 Master System (Astro Warrior, Sonic 1, Alex Kidd, **Micro Machines PAL**, Phantasy Star on **SMS-FM**, Wonder Boy III, Sonic drawn by **NES:FIXS**, Sonic's PSG on **NES:FIX**, Sonic with **every slot hot-swapped**), 5 Game Gear (Sonic 1, Columns, Wonder Boy, Shining Force II, Sonic with every slot hot-swapped), 1 **SG-1000** program, 2 tone programs; 1,200 to 3,300 frames of play each, 90 s for the suite. The ROMs are the user's library (the cases skip on a machine without it); the SG-1000 and tone programs are written by the Workshop (`--smstest --write-sg-programs / --write-swap-programs`) into a temp folder: no ROM of any kind is in the repository. Recorded twice each (a case whose two runs disagree is not written), so the goldens are reproducible; 13 of the 17 plain cases fail with the frame interrupt 55 clocks late; the 4 save cases fail when the save import copies nothing. `UAT\entrypoints\sega-preview-smoke.ps1`: run on the merged tree (`c22c0e6`), built by the script itself, alone on the machine: exit 0, 115 s, every check ok (the console menu, a Master System and a Game Gear program through RomDetect, input reaching the session, quick save / quick load, a Genesis program, and the battery program that survives two app runs: red then green, the count 1 then 2 in `SmsSaves\<sha1>.sav` and `GgSaves\<sha1>.sav`).

## About the oracle and the tools (for whoever uses them next)

* The oracle's Lua script fails silently when a path passes 260 characters; `mesen-compare.ps1` now cuts the tag to 28 characters, and a shallow scratch folder (`C:\bn_a4`) is needed. That, not the game, is why 8 long-named games "could not be captured" in A3.
* `Start-Process -ArgumentList @(..., '-Modes', 'a=b c')` splits the value at spaces; pass a single `-Command` string.
* The oracle's start phase file now also records its VDP counter state at the first instruction: always counter 255, latch 0, no flag pending, whatever the power-on line.
* The pad flips within a frame at exactly the same Z80 cycle in the oracle and here (a probe that samples port $DC in a 47-cycle loop: three power-on lines, equal sample index of the change), so input timing is not a source of difference.
* The oracle's `emu.memType.smsCartRam` reads the cartridge RAM (32 KB).

## Not tested / open

* Shining Force II's in-game save (above): the church after the first battle. The desktop app's own close and start were driven only for the synthetic battery program (`sega-preview-smoke.ps1`); the four games' saves went through the same session (`ExportSave` / `ImportSave`) headless.
* Shining Force II's boot interrupt (above), Black Belt and Space Harrier beyond the oracle's own agreement, Bubble Bobble (GG) colour dots.
* Lite and the plugin were not touched or run (C4a and C4b); the desktop's menu path for a swap was not driven (only the session's side).
* The one `[BIOS]` pair and `Excellent Dizzy Collection` at one checkpoint were not explained further.
* Nobody has listened to anything.

## Decisions

* TrySwapCore refuses what the menu does not offer instead of accepting it (a silent label for the CPU slot, a unit the Game Gear lacks); the apps then restart the game, which says so.
* The oracle's Z80 lengths, R-in-HALT behaviour and the VRAM access queue stay as decided in A2 / A3 (tool settings only / not modelled): this round's scans found nothing that argues for changing them.
* A `.sms` file with a Game Gear header stays a Game Gear (Phase 0's contract test says the header decides); the oracle decides by the extension; the only affected library files are broken GG2SMS hacks.
* UAT goldens are recorded while the games are played, not at their titles; they are regression guards, not accuracy claims.

## Next steps

1. Shining Force II's in-game save: win the first battle with a script (a unit at a time, looking at the pictures, or a replay recorded in the oracle) and reach the first church, then add the game to `roster-saves.tsv` and to a UAT save case. The two passes are then `mesen-saves.ps1` and `run-suite.ps1 -Filter sega-save*`.
2. Shining Force II: find what raises the oracle's interrupt input before the first `EI` (dump `emu.getState()` at the exec callback of `$0045` for several power-on lines: `cpu.activeIrqs` and the VDP flags), then reproduce it in the line-counter model.
3. Black Belt frame 151 / the VRAM access queue: build a probe that rewrites a name-table row during the picture at swept phases and compare the lines the oracle shows the new tiles on with this emulator's.
4. Run the whole gate and `sega-preview-smoke.ps1` on a quiet machine after merging `main`.
